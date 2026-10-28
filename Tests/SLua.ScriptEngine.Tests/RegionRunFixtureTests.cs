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
/// Writes the region archive for a run of the engine in a real region: one object of three prims: an SLua script in the root
/// prim, an LSL script (run by the SLua engine) in a child prim and, for comparison, a plain LSL script (the region's default
/// engine) in the other child prim. Each script calls a served function, an index
/// function, llSetPos (timing it with llGetTime), a service function and one function the engine does not serve, then writes
/// what it saw into its prim's description and hover text. It runs only when SLUA_REGION_OAR_OUT names the file to write;
/// SLUA_REGION_OWNER is the owner's id and SLUA_REGION_OWNER_NAME the owner's "First Last" name (an avatar of the region's estate).
/// </summary>
public class RegionRunFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_REGION_OAR_OUT")))
                Skip = "SLUA_REGION_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string SluaScript = @"//SLua:slua
local t0 = ll.GetTime()
local up = ll.ToUpper('slua')
local at = ll.SubStringIndex('hello', 'l')
local compat = llcompat.SubStringIndex('hello', 'l')
local sub = ll.GetSubString('hello', 1, 3)
ll.SetPos(ll.GetPos())
local dt = ll.GetTime() - t0
local key = ll.Name2Key('{OWNERNAME}')
local ok, err = pcall(function() ll.Say(0, 'x') end)
ll.SetObjectDesc(string.format('S p=%s i=%d/%d s=%s dt=%.3f k=%s', up, at, compat, sub, dt, tostring(key)))
ll.SetText(string.format('S caught=%s %s', tostring(not ok), tostring(err)), vector(1, 1, 1), 1.0)
";

    internal const string LslScript = @"//SLua:LSL
default
{
    state_entry()
    {
        float t0 = llGetTime();
        string up = llToUpper(""lsl"");
        integer at = llSubStringIndex(""hello"", ""l"");
        string sub = llGetSubString(""hello"", 0, 2);
        llSetPos(llGetPos());
        float dt = llGetTime() - t0;
        key found = llName2Key(""{OWNERNAME}"");
        llSetObjectDesc(""L p="" + up + "" i="" + (string)at + "" s="" + sub + "" dt="" + (string)dt + "" k="" + (string)found);
        llSetText(""L before the unserved call"", <1.0, 1.0, 1.0>, 1.0);
        llSay(0, ""x"");
        llSetText(""L after the unserved call: this must not be set"", <1.0, 1.0, 1.0>, 1.0);
    }
}
";

    // The same llSetPos timing on the engine the region runs by default, for comparison.
    internal const string ReferenceScript = @"default
{
    state_entry()
    {
        float t0 = llGetTime();
        llSetPos(llGetPos());
        llSetObjectDesc(""P dt="" + (string)(llGetTime() - t0));
    }
}
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_REGION_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));
        string ownerName = Environment.GetEnvironmentVariable("SLUA_REGION_OWNER_NAME") ?? "Test User";

        Scene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup group = SceneHelpers.CreateSceneObject(3, owner, "Example Script Object ", 0x7800);
        group.Name = "Example Script Object Two";
        group.RootPart.Name = "Example Script Object Two";
        group.AbsolutePosition = new Vector3(128, 128, 30);

        var assets = new List<(UUID Id, string Text)>();
        void AddScript(SceneObjectPart part, string name, string text)
        {
            text = text.Replace("{OWNERNAME}", ownerName);
            UUID asset = UUID.Random();
            assets.Add((asset, text));
            part.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(),
                AssetID = asset,
                Name = name,
                OwnerID = owner,
                CreatorID = owner,
                Type = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                ScriptRunning = true,
                ParentPartID = part.UUID,
            }, false);
        }

        SceneObjectPart[] parts = group.Parts;
        AddScript(group.RootPart, "Example SLua Script", SluaScript);
        SceneObjectPart[] children = parts.Where(p => p != group.RootPart).ToArray();
        AddScript(children[0], "Example LSL Script", LslScript);
        AddScript(children[1], "Example Reference Script", ReferenceScript);

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
        Add(ArchiveConstants.CreateOarObjectPath(group.Name, group.UUID, group.AbsolutePosition), SceneObjectSerializer.ToOriginalXmlFormat(group));
    }
}
