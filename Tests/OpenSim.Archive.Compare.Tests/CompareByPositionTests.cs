/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Xunit;
using static OpenSim.Archive.Compare.Tests.CompareKit;

namespace OpenSim.Archive.Compare.Tests;

// The comparison of an archive with the one a region saves after loading it. No process-wide state: every test builds its
// own archives in its own files, so the class runs in parallel with others.
public class CompareByPositionTests
{
    // Two archives from the same sample, built separately: every part and item id differs, as after a load.
    private static (string first, string second) Reloaded(Action<List<List<FixturePart>>, Dictionary<string, byte[]>> change = null)
    {
        string first = Sample();
        string second = Sample(change);
        return (first, second);
    }

    private static CompareResult Run(string a, string b) => new ArchiveComparer().CompareByPosition(a, b);

    [Fact]
    public void ThePlainComparisonSeesNewIdsAsDifferentPartsButThePositionOneDoesNot()
    {
        (string a, string b) = Reloaded();
        Assert.False(new ArchiveComparer().Compare(a, b).Pass);
        CompareResult r = Run(a, b);
        Assert.True(r.Pass, r.ToText());
        Assert.Equal(4, r.PartsOriginal);
        Assert.Equal(4, r.PartsRewritten);
        Assert.Equal(4, r.PartsMatched);
    }

    [Fact]
    public void CreatorAndOwnerTheLoaderRewritesDoNotCount()
    {
        (string a, string b) = Reloaded((builds, assets) =>
        {
            foreach (FixturePart p in builds.SelectMany(x => x))
            {
                p.Creator = "00000000-0000-4000-8000-0000000000c1";
                p.Owner = "00000000-0000-4000-8000-0000000000c2";
            }
        });
        Assert.True(Run(a, b).Pass);
    }

    [Fact]
    public void ARenamedPartIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[0][1].Name = "Tower-two");
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Kind == "removed" && d.PartName == "Tower-2");
        Assert.Contains(r.Unplanned, d => d.Kind == "added" && d.PartName == "Tower-two");
    }

    [Fact]
    public void AChangedDescriptionIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[0][1].Description = "other");
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Field == "description" && d.PartName == "Tower-2");
    }

    [Fact]
    public void AShiftedLinkNumberIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[0][2].LinkNum = 3);
        Assert.False(Run(a, b).Pass);
    }

    [Fact]
    public void ALostItemIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[0][1].Items.RemoveAt(1));
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Field == "contents" && d.Kind == "removed");
    }

    [Fact]
    public void AnItemWithAnotherAssetIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[0][1].Items[0].Asset = Guid.Parse("5c21a000-0000-4000-8000-0000000000a9"));
        Assert.False(Run(a, b).Pass);
    }

    [Fact]
    public void AChangedTextureEntryIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[1][0].Texture = "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        CompareResult r = Run(a, b);
        Assert.Contains(r.Unplanned, d => d.Field == "textureEntry");
    }

    [Fact]
    public void AnotherMeshReferenceOrFaceCountIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[0][2].Mesh = true);
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Field == "shape" && d.PartName == "Tower-3");
    }

    [Fact]
    public void AMovedBuildIsADifference()
    {
        (string a, string b) = Reloaded((builds, assets) => builds[1][0].Position = "61");
        Assert.False(Run(a, b).Pass);
    }

    [Fact]
    public void EntriesThatAreNotAssetsAreLeftOutButAssetBytesAreCompared()
    {
        (string a, string b) = Reloaded();
        Assert.True(Run(a, b).Pass);   // the sample carries a terrain entry in both; the saver writes its own
        (string c, string d) = Reloaded((builds, assets) => assets["assets/" + ScriptAsset + "_script.lsl"] = Encoding.ASCII.GetBytes("default { }"));
        CompareResult r = Run(c, d);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, x => x.Field == "entry");
    }

    [Fact]
    public void DefaultFlexiAndLightSettingsTheServerAddsDoNotCount()
    {
        // The plain comparison does see them: write the sample, then the same sample through a writer that adds the defaults.
        string a = Sample();
        string b = Sample();
        string withDefaults = TempFile(".oar");
        RewriteWithServerDefaults(b, withDefaults);
        Assert.False(new ArchiveComparer().Compare(a, withDefaults).Pass);   // they are really in the file
        Assert.True(Run(a, withDefaults).Pass, Run(a, withDefaults).ToText());
    }

    private static void RewriteWithServerDefaults(string source, string target)
    {
        using FileStream fs = File.OpenRead(source);
        using System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
        using FileStream outFs = new FileStream(target, FileMode.CreateNew);
        using System.IO.Compression.GZipStream outGz = new System.IO.Compression.GZipStream(outFs, System.IO.Compression.CompressionLevel.Fastest);
        using OpenSim.Archive.Rewrite.TarWriter tar = new OpenSim.Archive.Rewrite.TarWriter(outGz, leaveOpen: true);
        OpenSim.Archive.Inspection.TarStreamReader reader = new OpenSim.Archive.Inspection.TarStreamReader(gz);
        OpenSim.Archive.Inspection.TarEntryHeader h;
        while ((h = reader.ReadNext()) != null)
        {
            if (h.IsDirectory)
            {
                tar.WriteDirectory(h.Name);
                continue;
            }
            byte[] body = reader.ReadBody();
            if (h.Name.StartsWith("objects/", StringComparison.Ordinal))
            {
                string xml = Encoding.UTF8.GetString(body);
                xml = xml.Replace("<Shape>", "<Shape><FlexiEntry>false</FlexiEntry><FlexiDrag>0</FlexiDrag><FlexiWind>0</FlexiWind><LightEntry>false</LightEntry><LightRadius>0</LightRadius><LightIntensity>1</LightIntensity><ExtraParams>AA==</ExtraParams>");
                body = Encoding.UTF8.GetBytes(xml);
            }
            tar.WriteFile(h.Name, body);
        }
    }

    private static List<FixturePart> TwoLamps(string secondName) => new List<FixturePart> { new FixturePart { Name = secondName, LinkNum = 0, Position = "60" } };

    [Fact]
    public void IdenticalPrimsStackedOnOneSpotArePairedInTheOrderOfTheirContents()
    {
        (string a, string b) = Reloaded((builds, assets) => { });
        string withTwin1 = Sample((builds, assets) => builds.Add(TwoLamps("Lamp")));
        string withTwin2 = Sample((builds, assets) => builds.Add(TwoLamps("Lamp")));
        CompareResult r = Run(withTwin1, withTwin2);
        Assert.True(r.Pass, r.ToText());
        Assert.Equal(5, r.PartsMatched);
        Assert.True(Run(a, b).Pass);
    }

    [Fact]
    public void ADifferenceInOneOfTwoStackedPrimsIsStillFound()
    {
        string a = Sample((builds, assets) => builds.Add(TwoLamps("Lamp")));
        string b = Sample((builds, assets) => { builds.Add(TwoLamps("Lamp")); builds[2][0].Description = "changed"; });
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Field == "description");
    }
}
