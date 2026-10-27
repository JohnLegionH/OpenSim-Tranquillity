/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Restore.Import;
using OpenSim.Framework;
using SkiaSharp;
using Quaternion = System.Numerics.Quaternion;
using SysVector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Restore.Import;

/// <summary>The round trip of a linked object (see <see cref="ImportCheck.RunLinkset"/>).</summary>
public static partial class ImportCheck
{
    private const float RotationTolerance = 1e-4f;
    private static readonly Dictionary<string, string> ContentTypeNames = new Dictionary<string, string> { ["script"] = "LSLText", ["notecard"] = "Notecard", ["texture"] = "Texture" };

    /// <summary>
    /// The round trip of a linked object. The archive is written out to glTF by the exporter and each part of the build it makes is compared with the manifest
    /// the archive was made from: the parts in link order, each part's name and description equal, its place and turn relative to the root part within half a
    /// millimetre (and a ten-thousandth of a turn), its triangle count, and its contents: the names and types the exporter lists, and the bytes of every script,
    /// the text of every notecard and the pixels of every texture, read from the archive and compared with the files they were made from.
    /// </summary>
    public static ImportCheckResult RunLinkset(string archivePath, LinksetManifest manifest, ImportOptions options, string workFolder)
    {
        ImportCheckResult result = new ImportCheckResult();
        try
        {
            RunLinksetCore(result, archivePath, manifest, workFolder);
        }
        catch (Exception e) when (e is ImportException || e is IOException || e is InvalidDataException || e is OpenSim.MeshAsset.MeshFormatException || e is InvalidOperationException || e is ArgumentException
            || e is UnauthorizedAccessException || e is System.Text.Json.JsonException || e is KeyNotFoundException)
        {
            result.Lines.Add(new CheckLine { What = "check", Ok = false, Detail = "the check could not run: " + e.Message });
        }
        result.Passed = result.Lines.Count > 0 && result.Lines.All(l => l.Ok);
        return result;
    }

    private static string Cut(string text, int chars) => text.Length > chars ? text.Substring(0, chars) : text;

    private static string Plain(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s ?? string.Empty)
            if (System.Xml.XmlConvert.IsXmlChar(c))
                sb.Append(c);
        return sb.ToString();
    }

    private static void RunLinksetCore(ImportCheckResult result, string archivePath, LinksetManifest manifest, string workFolder)
    {
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);

        // what the manifest asks for: each part's model (for its box and triangle count) and the place that follows
        Dictionary<string, GlbFile> cache = new Dictionary<string, GlbFile>(StringComparer.OrdinalIgnoreCase);
        List<int> inputTriangles = new List<int>();
        List<SysVector3> centres = new List<SysVector3>();
        List<ObjectImporter.NodePlace> places = new List<ObjectImporter.NodePlace>();
        foreach (LinksetPart part in manifest.Parts)
        {
            string label = "part '" + part.Name + "'";
            List<string> notes = new List<string>();
            ObjectImporter.NodePlace place = null;
            ObjectImporter.ModelLevels model = part.Model.IsFolder
                ? ObjectImporter.ReadFolderModel(part.Model.Folder, ObjectImporter.FolderBase(part.Model), notes)
                : ObjectImporter.ReadNodeModel(part, label, cache, notes, out place);
            PartGeometry high = PartGeometry.FromNode(model.Files[0], model.Nodes[0], model.Nodes[0].Scale, true);
            (Vector3 size, Vector3 centre) = ObjectImporter.Box(high);
            inputTriangles.Add(high.TriangleCount);
            centres.Add(new SysVector3(centre.X, centre.Y, centre.Z));
            places.Add(place);
        }
        (SysVector3 Offset, Quaternion Rotation)[] expected = ObjectImporter.PlaceParts(manifest.Parts, centres, places);

        // the archive through the exporter
        ExportOptions export = new ExportOptions { OutputDirectory = workFolder, SelfContained = true };
        export.MaxMemoryMegabytes = ArchiveExporter.MaxMemoryMegabytesFor(export);
        ArchiveExporter.Export(archivePath, export);
        string manifestPath = Directory.Exists(workFolder) ? Directory.EnumerateFiles(workFolder, "manifest.json", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (manifestPath == null)
            throw new ImportException("the exporter wrote no build for the archive");
        JsonNode built = JsonNode.Parse(File.ReadAllText(manifestPath));
        JsonArray exportedParts = built["parts"]?.AsArray() ?? throw new ImportException("the exporter's manifest lists no parts");
        string glbName = built["file"]?.GetValue<string>() ?? throw new ImportException("the exporter wrote no glTF file");
        GlbFile exported = GlbFile.Read(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(manifestPath), glbName)));
        // the build node holds the parts as children in link order
        GlbNode buildNode = exported.Nodes.FirstOrDefault(n => n.Extras != null && n.Extras["kind"]?.GetValue<string>() == "object") ?? throw new ImportException("the exported glTF has no build node");
        List<GlbNode> partNodes = buildNode.Children.Select(i => exported.Nodes[i]).ToList();

        // the link order
        List<string> wantedNames = manifest.Parts.Select(p => Cut(Plain(p.Name), 63)).ToList();
        List<string> gotNames = exportedParts.Select(p => p["name"]?.GetValue<string>() ?? string.Empty).ToList();
        bool orderOk = wantedNames.SequenceEqual(gotNames) && partNodes.Count == wantedNames.Count;
        result.Lines.Add(new CheckLine
        {
            What = "link order", Ok = orderOk,
            Detail = orderOk ? "the exported parts are " + string.Join(", ", gotNames) : "the manifest lists " + string.Join(", ", wantedNames) + " and the exporter found " + string.Join(", ", gotNames)
        });

        XElement objectRoot = XDocument.Parse(Encoding.UTF8.GetString(entries[entries.Keys.First(k => k.StartsWith("objects/", StringComparison.Ordinal))])).Root;
        List<XElement> xmlParts = new List<XElement> { objectRoot.Element("SceneObjectPart") ?? objectRoot.Element("RootPart")?.Element("SceneObjectPart") };
        xmlParts.AddRange(objectRoot.Element("OtherParts")?.Elements("SceneObjectPart") ?? Enumerable.Empty<XElement>());

        for (int i = 0; i < manifest.Parts.Count; i++)
        {
            LinksetPart part = manifest.Parts[i];
            string name = part.Name;
            JsonNode got = i < exportedParts.Count ? exportedParts[i] : null;
            GlbNode node = i < partNodes.Count ? partNodes[i] : null;

            string wantedName = Cut(Plain(part.Name), 63), gotName = got?["name"]?.GetValue<string>();
            result.Lines.Add(new CheckLine { What = "name of " + name, Ok = got != null && wantedName == gotName, Detail = "'" + wantedName + "' is written as '" + gotName + "'" });
            string wantedDescription = Cut(Plain(part.Description ?? string.Empty), 127), gotDescription = got?["description"]?.GetValue<string>() ?? string.Empty;
            result.Lines.Add(new CheckLine { What = "description of " + name, Ok = got != null && wantedDescription == gotDescription, Detail = "'" + wantedDescription + "' is written as '" + gotDescription + "'" });

            // the place and the turn relative to the root part: the exporter writes the root's own node at the origin and each child at its offset, in glTF axes
            SysVector3 offset = i == 0 || node == null || node.Translation == null ? SysVector3.Zero : new SysVector3(node.Translation[0], -node.Translation[2], node.Translation[1]);
            Quaternion turn = i == 0 || node == null || node.Rotation == null || node.Rotation.Length < 4 ? Quaternion.Identity : new Quaternion(node.Rotation[0], -node.Rotation[2], node.Rotation[1], node.Rotation[3]);
            float distance = SysVector3.Distance(offset, expected[i].Offset);
            result.Lines.Add(new CheckLine
            {
                What = "position of " + name, MaxPosition = distance, Ok = node != null && distance <= PositionTolerance,
                Detail = "<" + F(expected[i].Offset.X) + ", " + F(expected[i].Offset.Y) + ", " + F(expected[i].Offset.Z) + "> from the root part, written as <" + F(offset.X) + ", " + F(offset.Y) + ", " + F(offset.Z) + ">"
                    + (distance <= PositionTolerance ? string.Empty : " (limit " + (PositionTolerance * 1000f).ToString("0.###", CultureInfo.InvariantCulture) + " mm)")
            });
            float dot = Math.Abs(Quaternion.Dot(Quaternion.Normalize(turn), Quaternion.Normalize(expected[i].Rotation)));
            float angle = 2f * MathF.Acos(Math.Min(1f, dot));
            result.Lines.Add(new CheckLine { What = "rotation of " + name, Ok = node != null && angle <= RotationTolerance, Detail = "turned " + (angle * 180f / MathF.PI).ToString("0.####", CultureInfo.InvariantCulture) + " degrees from the manifest" });

            // the geometry: the exporter's triangles for the part against the input's
            int outputTriangles = node == null ? 0 : PartGeometry.FromNode(exported, node).TriangleCount;
            result.Lines.Add(new CheckLine
            {
                What = "triangles of " + name, Measured = false, InputTriangles = inputTriangles[i], OutputTriangles = outputTriangles, Ok = outputTriangles == inputTriangles[i],
                Detail = outputTriangles == inputTriangles[i] ? "the full mesh has the triangles of the input" : "the exported part has " + outputTriangles + " triangles, the input " + inputTriangles[i]
            });

            result.Lines.Add(CompareContents(part, got, i < xmlParts.Count ? xmlParts[i] : null, entries));
        }
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static CheckLine CompareContents(LinksetPart part, JsonNode got, XElement xml, Dictionary<string, byte[]> entries)
    {
        CheckLine line = new CheckLine { What = "contents of " + part.Name };
        List<string> problems = new List<string>();
        List<string> listed = (got?["contents"]?.AsArray() ?? new JsonArray()).Select(c => (c["name"]?.GetValue<string>() ?? string.Empty) + "|" + (c["type"]?.GetValue<string>() ?? string.Empty)).ToList();
        List<string> wanted = part.Contents.Select(c => Cut(Plain(c.Name), 63) + "|" + ContentTypeNames[c.Type]).ToList();
        if (!wanted.SequenceEqual(listed))
            problems.Add("the exporter lists [" + string.Join(", ", listed.Select(s => s.Replace('|', ' '))) + "] and the manifest has [" + string.Join(", ", wanted.Select(s => s.Replace('|', ' '))) + "]");
        List<XElement> items = xml?.Element("TaskInventory")?.Elements("TaskInventoryItem").ToList() ?? new List<XElement>();
        foreach (LinksetContent c in part.Contents)
        {
            string itemName = Cut(Plain(c.Name), 63);
            XElement item = items.FirstOrDefault(i => i.Element("Name")?.Value == itemName);
            if (item == null)
            {
                problems.Add("'" + itemName + "' is not in the part's inventory");
                continue;
            }
            string assetId = item.Element("AssetID")?.Element("UUID")?.Value;
            string suffix = c.Type == "script" ? "_script.lsl" : c.Type == "notecard" ? "_notecard.txt" : "_texture.jp2";
            if (!entries.TryGetValue("assets/" + assetId + suffix, out byte[] stored))
            {
                problems.Add("'" + itemName + "': the asset " + assetId + " is not in the archive");
                continue;
            }
            byte[] input = File.ReadAllBytes(c.File);
            bool equal;
            switch (c.Type)
            {
                case "script":
                    equal = stored.AsSpan().SequenceEqual(input);
                    break;
                case "notecard":
                    string text = Encoding.UTF8.GetString(input).Replace("\r\n", "\n");
                    string card = Encoding.UTF8.GetString(stored);
                    equal = card.StartsWith("Linden text version 2\n", StringComparison.Ordinal) && card.EndsWith("\n" + text + "}\n", StringComparison.Ordinal)
                        && card.Contains("Text length " + Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture) + "\n", StringComparison.Ordinal);
                    break;
                default:
                    byte[] a = DecodedPixels(input, false, out int aw, out int ah), b = DecodedPixels(stored, true, out int bw, out int bh);
                    equal = a != null && b != null && aw == bw && ah == bh && a.AsSpan().SequenceEqual(b);
                    break;
            }
            if (!equal)
                problems.Add("'" + itemName + "' (" + c.Type + ") differs from " + Path.GetFileName(c.File));
        }
        line.Ok = problems.Count == 0;
        line.Detail = line.Ok
            ? part.Contents.Count + (part.Contents.Count == 1 ? " item" : " items") + (part.Contents.Count == 0 ? string.Empty : ": " + string.Join(", ", part.Contents.Select(c => Cut(Plain(c.Name), 63) + " (" + c.Type + ")")) + ", all equal to their files")
            : string.Join("; ", problems);
        return line;
    }

    private static byte[] DecodedPixels(byte[] encoded, bool jpeg2000, out int width, out int height)
    {
        width = height = 0;
        SKBitmap bitmap;
        if (!(jpeg2000 ? SkiaImageUtils.TryDecodeFromJ2K(encoded, out bitmap) : SkiaImageUtils.TryDecodeFromBytes(encoded, out bitmap)) || bitmap == null)
            return null;
        using (bitmap)
        using (SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888))
        {
            width = rgba.Width;
            height = rgba.Height;
            return rgba.Bytes;
        }
    }
}
