/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Writes a region archive for a run of the engine selection in a real region: one object per first-line form, each with a
/// script that writes "ran" into its prim's description, so a run shows which scripts started. SLUA_SELECTION_OAR_OUT names the
/// file; SLUA_SELECTION_SET is "forms" (one object for each header form) or "old" (a single marked SLua script, to be run before
/// the SLua engine is enabled and left with its saved state). "reset-old" and "reset-new" make objects for proving that a reset
/// does not move a script: each holds the script "probe" (every start appends an "x" to the object's description, so the number
/// of starts can be read) and a script "resetter" that resets "probe" when the object is touched; "reset-old" is a single
/// "--!slua" object, to be run before the SLua engine is enabled, "reset-new" is run after; "reset-slua" is one SLua object whose
/// script resets itself when touched. SLUA_REGION_OWNER is the owner's id.
/// It runs only when the file is named.
/// </summary>
public class SelectionRegionRunFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_SELECTION_OAR_OUT")))
                Skip = "SLUA_SELECTION_OAR_OUT is not set; the archive is written only on request";
        }
    }

    private static string Lsl(string tag) => $"default {{ state_entry() {{ llSetObjectDesc(\"{tag} ran\"); }} }}\n";
    private const string LslCount = "default { state_entry() { llSetObjectDesc(llGetObjectDesc() + \"x\"); } }\n";
    private const string LuaCount = "ll.SetObjectDesc(ll.GetObjectDesc() .. 'x')\n";
    private const string Resetter = "default { touch_start(integer n) { llResetOtherScript(\"probe\"); } }\n";
    private static string Lua(string tag) => $"ll.SetObjectDesc('{tag} ran')\n";

    /// <summary>The objects of each set: the object's name, its script's text.</summary>
    internal static IReadOnlyList<(string Name, string Script)> Objects(string set)
    {
        if (set == "old")
            return new[] { ("E17 old-bang", "--!slua\n" + Lua("E17 old-bang")) };
        if (set == "reset-old")
            return new[] { ("E18 held-by-phlox", "--!slua\n" + LuaCount) };
        if (set == "reset-slua")
            return new[] { ("E18 on-slua-self", "--!slua\n" + LuaCount + "LLEvents:on('touch_start', function() ll.ResetScript() end)\n") };
        if (set == "reset-new")
            return new[]
            {
                ("E18 on-slua", "--!slua\n" + LuaCount),
                ("E18 phlox-header", "//Phlox:\n" + LslCount),
            };
        return new[]
        {
            ("E17 header-SLua",           "//SLua:\n" + Lua("E17 header-SLua")),
            ("E17 header-SLua-LSL",       "//SLua:LSL\n" + Lsl("E17 header-SLua-LSL")),
            ("E17 marker-bang",           "--!slua\n" + Lua("E17 marker-bang")),
            ("E17 header-Phlox",          "//Phlox:\n" + Lsl("E17 header-Phlox")),
            ("E17 header-InWorldz-Phlox", "//InWorldz.Phlox:\n" + Lsl("E17 header-InWorldz-Phlox")),
            ("E17 header-Phlox-slua",     "//Phlox:slua\n" + Lua("E17 header-Phlox-slua")),
            ("E17 header-IW-Phlox-slua",  "//InWorldz.Phlox:slua\n" + Lua("E17 header-IW-Phlox-slua")),
            ("E17 header-YEngine",        "//YEngine:\n" + Lsl("E17 header-YEngine")),
            ("E17 no-header-LSL",         Lsl("E17 no-header-LSL")),
            ("E17 no-header-Lua",         "-- plain Lua, no marker\n" + Lua("E17 no-header-Lua")),
        };
    }

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_SELECTION_OAR_OUT");
        string set = Environment.GetEnvironmentVariable("SLUA_SELECTION_SET") ?? "forms";
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));

        new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = set == "old" ? 0x7B00 : set.StartsWith("reset") ? (set == "reset-old" ? 0x7D00 : set == "reset-new" ? 0x7E00 : 0x7F00) : 0x7C00;
        float x = 40;

        foreach ((string name, string script) in Objects(set))
        {
            SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, name + " ", tail);
            tail += 16;
            group.Name = name;
            group.RootPart.Name = name;
            group.AbsolutePosition = new Vector3(x, 60, 30);
            x += 4;
            groups.Add(group);

            UUID asset = UUID.Random();
            assets.Add((asset, script));
            group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(),
                AssetID = asset,
                Name = "probe",
                OwnerID = owner,
                CreatorID = owner,
                Type = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                ScriptRunning = true,
                ParentPartID = group.RootPart.UUID,
            }, false);

            if (set.StartsWith("reset"))
            {
                UUID resetterAsset = UUID.Random();
                assets.Add((resetterAsset, Resetter));
                group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
                {
                    ItemID = UUID.Random(),
                    AssetID = resetterAsset,
                    Name = "resetter",
                    OwnerID = owner,
                    CreatorID = owner,
                    Type = (int)AssetType.LSLText,
                    InvType = (int)InventoryType.LSL,
                    ScriptRunning = true,
                    ParentPartID = group.RootPart.UUID,
                }, false);
            }
        }

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
        void Add(string name, string text) => tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(new UTF8Encoding(false).GetBytes(text)) });

        Add(ArchiveConstants.CONTROL_FILE_PATH,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<archive major_version=\"0\" minor_version=\"8\">\n"
            + "<creation_info><datetime>0</datetime><id>" + Guid.NewGuid() + "</id></creation_info>\n"
            + "<region_info><is_megaregion>false</is_megaregion><size_in_meters>256,256</size_in_meters></region_info>\n"
            + "<assets_included>True</assets_included>\n</archive>\n");
        foreach ((UUID id, string text) in assets)
            Add(ArchiveConstants.ASSETS_PATH + id + ArchiveConstants.ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.LSLText], text);
        foreach (SceneObjectGroup group in groups)
            Add(ArchiveConstants.CreateOarObjectPath(group.Name, group.UUID, group.AbsolutePosition), SceneObjectSerializer.ToOriginalXmlFormat(group));
    }
}
