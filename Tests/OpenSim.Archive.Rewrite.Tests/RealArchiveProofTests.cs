/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using OpenSim.Archive.Inspection;
using Xunit;
using static OpenSim.Archive.Rewrite.Tests.RewriteKit;

namespace OpenSim.Archive.Rewrite.Tests;

/// <summary>
/// Runs the rewrite on a real archive. The archive is named by the environment variable OARX_REAL_OAR and the
/// folder for the rewritten copies and the proof record by OARX_PROOF_DIR; the test does nothing when either is not
/// set, so the normal run stays fast and needs no archive.
/// </summary>
[Collection("scene")]
public class RealArchiveProofTests : OpenSim.Tests.Common.OpenSimTestCase
{
    private static string Sha(string path)
    {
        using FileStream f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    /// <summary>Compares two archives entry by entry, in order, by name and by the bytes of the body.</summary>
    private static (long entries, List<string> differing, List<string> nameOrder) CompareEntries(string a, string b)
    {
        using GZipStream ga = new GZipStream(File.OpenRead(a), CompressionMode.Decompress);
        using GZipStream gb = new GZipStream(File.OpenRead(b), CompressionMode.Decompress);
        TarStreamReader ra = new TarStreamReader(ga), rb = new TarStreamReader(gb);
        List<string> differing = new List<string>();
        List<string> order = new List<string>();
        long n = 0;
        while (true)
        {
            TarEntryHeader ha = ra.ReadNext(), hb = rb.ReadNext();
            if (ha == null || hb == null)
            {
                if (ha != hb)
                    order.Add("one archive has more entries");
                break;
            }
            n++;
            if (ha.Name != hb.Name || ha.TypeFlag != hb.TypeFlag && !(ha.TypeFlag == '\0' && hb.TypeFlag == '0'))
                order.Add(n + ": " + ha.Name + " vs " + hb.Name);
            if (!ra.ReadBody().AsSpan().SequenceEqual(rb.ReadBody()))
                differing.Add(ha.Name);
        }
        return (n, differing, order);
    }

    /// <summary>Lists the parts the archive's object files hold, read with a plain XML parser (independent of the rewriter).</summary>
    private static List<(Guid id, string group, int link, string name, bool hasItems)> ArchiveParts(string path)
    {
        List<(Guid, string, int, string, bool)> parts = new List<(Guid, string, int, string, bool)>();
        using GZipStream gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        TarStreamReader tar = new TarStreamReader(gz);
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
        {
            if (!h.IsFile || !h.Name.StartsWith("objects/") || !h.Name.EndsWith(".xml"))
                continue;
            XDocument doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(tar.ReadBody()).TrimStart('﻿'));
            List<XElement> sops = doc.Descendants("SceneObjectPart").ToList();
            if (sops.Count == 0)
                continue;
            string group = (string)sops[0].Element("Name");
            foreach (XElement sop in sops)
            {
                XElement u = sop.Element("UUID");
                string text = (string)u?.Element("UUID") ?? (string)u?.Element("Guid");
                if (!Guid.TryParse(text, out Guid id))
                    continue;
                int.TryParse((string)sop.Element("LinkNum"), out int link);
                parts.Add((id, group, link, (string)sop.Element("Name"), sop.Element("TaskInventory")?.HasElements == true));
            }
        }
        return parts;
    }

    [Fact]
    public void RealArchive_IdentityAndRename_LoadToTheSameLinksets()
    {
        string source = Environment.GetEnvironmentVariable("OARX_REAL_OAR");
        string dir = Environment.GetEnvironmentVariable("OARX_PROOF_DIR");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dir))
            return;

        Directory.CreateDirectory(dir);
        string hashBefore = Sha(source);
        string identity = Path.Combine(dir, "identity.oar");
        string renamed = Path.Combine(dir, "renamed.oar");

        // identity rewrite
        RewriteSummary idSummary = new ArchiveRewriter().Rewrite(source, identity);
        File.WriteAllText(Path.Combine(dir, "identity-summary.json"), idSummary.ToJson());
        var idCompare = CompareEntries(source, identity);

        // load both and compare every linkset
        List<PartRecord> original, rewritten;
        using (FileStream f = File.OpenRead(source)) original = LoadAndSnapshot(f);
        using (FileStream f = File.OpenRead(identity)) rewritten = LoadAndSnapshot(f);

        // rename one part that sits in a linkset (link number above 1), preferring one with contents, and that the
        // loaded scene tells apart from every other part by group name, link number and name
        var candidates = ArchiveParts(source)
            .Where(p => p.link > 1 && original.Count(r => r.GroupName == p.group && r.LinkNum == p.link && r.Name == p.name) == 1)
            .OrderByDescending(p => p.hasItems).ThenBy(p => p.id).ToList();
        Assert.NotEmpty(candidates);
        var chosen = candidates[0];
        PartRecord target = original.Single(r => r.GroupName == chosen.group && r.LinkNum == chosen.link && r.Name == chosen.name);
        string newName = "Renamed by the rewrite test <x> & co";
        ArchiveRewriter rw = new ArchiveRewriter();
        rw.AddPatch(new PartPatch(chosen.id, ElementEdit.SetText("Name", newName)));
        RewriteSummary renSummary = rw.Rewrite(source, renamed);
        File.WriteAllText(Path.Combine(dir, "rename-summary.json"), renSummary.ToJson());
        var renCompare = CompareEntries(source, renamed);

        List<PartRecord> patched;
        using (FileStream f = File.OpenRead(renamed)) patched = LoadAndSnapshot(f);
        List<PartRecord> expectedPatched = original.Select(r => r == target ? r with { Name = newName } : r)
            .OrderBy(r => r.GroupName, StringComparer.Ordinal).ThenBy(r => r.Position, StringComparer.Ordinal).ThenBy(r => r.LinkNum)
            .ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        string hashAfter = Sha(source);

        File.WriteAllText(Path.Combine(dir, "proof.json"), JsonSerializer.Serialize(new
        {
            source,
            hashBefore,
            hashAfter,
            identityEntries = idCompare.entries,
            identityDifferingBodies = idCompare.differing.Count,
            identityOrderProblems = idCompare.nameOrder,
            parts = original.Count,
            linksets = original.Select(r => (r.GroupName, r.Position)).Distinct().Count(),
            identityLoadEqual = original.SequenceEqual(rewritten),
            renamedPart = target,
            renamedPartId = chosen.id,
            renamedPartHasContents = chosen.hasItems,
            renameNewName = newName,
            renameEntries = renCompare.entries,
            renameDifferingBodies = renCompare.differing,
            renameOrderProblems = renCompare.nameOrder,
            renameLoadEqualToExpected = expectedPatched.SequenceEqual(patched),
            identityOutputBytes = new FileInfo(identity).Length,
            sourceBytes = new FileInfo(source).Length
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(hashBefore, hashAfter);
        Assert.Empty(idCompare.differing);
        Assert.Empty(idCompare.nameOrder);
        Assert.True(idSummary.ControlFileFirst);
        Assert.Equal(idSummary.SourceEntries, idSummary.EntriesCopied);
        Assert.Equal(original, rewritten);
        Assert.Empty(renCompare.nameOrder);
        Assert.Equal(1, renSummary.ObjectFilesPatched);
        Assert.Single(renCompare.differing);
        Assert.Equal(expectedPatched, patched);
    }
}
