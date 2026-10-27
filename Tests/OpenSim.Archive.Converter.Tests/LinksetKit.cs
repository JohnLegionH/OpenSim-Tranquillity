/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml.Linq;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A small house for the linked-object import tests, written into its own folder: a shell from a mesh tool's output folder (the root part), a door leaf and a window
/// that are two nodes of one multi-node .glb, a script, a notecard and a picture, and the manifest that puts them together. Positions are in the viewer's axes.
/// Door: model box centre (0, 0, 0.1), placed at (0.3, 0.2, 0), no turn, so its centre is 0.3, 0.2, 0 from the shell's centre (0, 0, 0.1).
/// Window: model box centre (0.1, 0, 0.1), placed by its node at (1, 2, 0) and turned 90 degrees about z, so its centre is (1, 2.1, 0) from the shell's.
/// </summary>
internal static class LinksetKit
{
    public const string DoorScriptText = "default\n{\n    touch_start(integer total)\n    {\n        llSay(0, \"Open\");\n    }\n}\n";
    public const string NotecardText = "Welcome to the test house.\nThe door opens when touched.\n";
    public const float Sqrt12 = 0.70710678f;

    public sealed class Built
    {
        public string Folder;
        public string ManifestPath;
        public string ManifestJson;
        public string DoorScript, Notecard, Badge;
        public byte[] BadgePng;
    }

    public static Built Write(string name = "Test House", string manifestOverride = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ImportKit.Write(normalMap: false, into: Path.Combine(root, "shell"), name: "shell");

        ImportMaterial wood = new ImportMaterial { Name = "wood", Color = new[] { 0.5f, 0.3f, 0.1f, 1f }, Picture = ImportKit.Png(120, 80, 40) };
        ImportKit.Geo door = new ImportKit.Geo(), window = new ImportKit.Geo();
        ImportKit.AddBox(door, 1, _ => true, 0f, 0.1f, 0f, 0.05f, 0.1f, 0.01f);
        ImportKit.AddBox(window, 1, _ => true, 0.1f, 0.1f, 0f, 0.05f, 0.05f, 0.01f);
        var nodes = new (string, float[], float[], IReadOnlyList<(ImportMaterial, ImportKit.Geo)>)[]
        {
            ("Door", null, null, new[] { (wood, door) }),
            ("Window", new[] { 1f, 0f, -2f }, new[] { 0f, Sqrt12, 0f, Sqrt12 }, new[] { (wood, window) })
        };
        File.WriteAllBytes(Path.Combine(root, "building.glb"), ImportKit.GlbNodes(nodes));

        Built b = new Built
        {
            Folder = root,
            DoorScript = Path.Combine(root, "door.lsl"),
            Notecard = Path.Combine(root, "readme.txt"),
            Badge = Path.Combine(root, "badge.png"),
            BadgePng = ImportKit.Png(10, 200, 90),
            ManifestPath = Path.Combine(root, "linkset.json")
        };
        File.WriteAllBytes(b.DoorScript, new UTF8Encoding(false).GetBytes(DoorScriptText));
        File.WriteAllBytes(b.Notecard, new UTF8Encoding(false).GetBytes(NotecardText));
        File.WriteAllBytes(b.Badge, b.BadgePng);
        b.ManifestJson = manifestOverride ?? @"{
  ""name"": """ + name + @""",
  ""description"": ""A house for the import tests"",
  ""parts"": [
    { ""name"": ""Shell"", ""description"": ""the walls"", ""folder"": ""shell"",
      ""contents"": [ { ""type"": ""notecard"", ""file"": ""readme.txt"", ""name"": ""Read me"" } ] },
    { ""name"": ""Door"", ""description"": ""front door leaf"", ""glb"": ""building.glb"", ""node"": ""Door"", ""position"": [0.3, 0.2, 0], ""rotation"": [0, 0, 0, 1],
      ""contents"": [ { ""type"": ""script"", ""file"": ""door.lsl"", ""name"": ""Door script"" }, { ""type"": ""texture"", ""file"": ""badge.png"", ""name"": ""Badge"" } ] },
    { ""name"": ""Window"", ""description"": ""side window"", ""glb"": ""building.glb"", ""node"": ""Window"" }
  ]
}";
        File.WriteAllText(b.ManifestPath, b.ManifestJson);
        return b;
    }

    /// <summary>The parts of the one object in an archive: the root part first, then the others in file order.</summary>
    public static List<XElement> Parts(Dictionary<string, byte[]> oar)
    {
        string entry = oar.Keys.Single(k => k.StartsWith("objects/", StringComparison.Ordinal) && k.EndsWith(".xml", StringComparison.Ordinal));
        XElement group = XDocument.Parse(Encoding.UTF8.GetString(oar[entry])).Root;
        List<XElement> parts = new List<XElement> { group.Element("SceneObjectPart") ?? group.Element("RootPart").Element("SceneObjectPart") };
        parts.AddRange(group.Element("OtherParts")?.Elements("SceneObjectPart") ?? Enumerable.Empty<XElement>());
        return parts;
    }

    public static List<XElement> Items(XElement part) => part.Element("TaskInventory")?.Elements("TaskInventoryItem").ToList() ?? new List<XElement>();

    public static XElement Item(XElement part, string name) => Items(part).Single(i => i.Element("Name").Value == name);
}
