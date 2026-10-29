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
using static SLua.ScriptEngine.Tests.MissingLlFunctionsRegionFixtureTests;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Writes the region archive for a run, in a real region with both engines, of the positions the SLua engine counts from 1:
/// llFindNotecardTextSync, an os function, an iw function and a function that gives a position inside a strided answer. One prim
/// holds a notecard and two scripts that make the same calls, each in its own language: the Phlox script (LSL, the region's default
/// engine, counting from 0) writes what it got to the prim's description, the SLua script to the object's name. It runs only when
/// SLUA_REGION_OAR_OUT names the file to write; SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class PositionsRegionFixtureTests
{
    // Each result is a letter and the answer: s the notecard search (line, column, length of each match), i osStringIndexOf of
    // "l" in "hello", u osStringSubString("hello", 2), w iwSubStringIndex, c iwChar2Int("hello", second letter), m osMatchString
    // ("xxab", "ab") whose answer holds the text and its position, z (SLua only) osStringSubString("hello", 0), which an SLua
    // script is refused.
    internal const string PhloxScript = @"default
{
    state_entry()
    {
        string r = """";
        r += ""s"" + llDumpList2String(llFindNotecardTextSync(""card"", ""b+"", 0, 0, []), "","") + "";"";
        r += ""i"" + (string)osStringIndexOf(""hello"", ""l"", 0, 5, 0) + "";"";
        r += ""u"" + osStringSubString(""hello"", 2) + "";"";
        r += ""w"" + (string)iwSubStringIndex(""hello"", ""l"", 0, 1) + "";"";
        r += ""c"" + (string)iwChar2Int(""hello"", 1) + "";"";
        r += ""m"" + llDumpList2String(osMatchString(""xxab"", ""ab"", 0), "","") + "";"";
        llSetObjectDesc(r);
    }
}
";

    internal const string SluaScript = @"//SLua:slua
local r = ''
local function put(tag, f)
    local ok, v = pcall(f)
    r = r .. tag .. (ok and tostring(v) or 'x') .. ';'
end
put('s', function() return table.concat(ll.FindNotecardTextSync('card', 'b+', 1, 0, {}), ',') end)
put('i', function() return ossl.StringIndexOf('hello', 'l', 1, 5, 0) end)
put('u', function() return ossl.StringSubString('hello', 2) end)
put('w', function() return iw.SubStringIndex('hello', 'l', 1, 1) end)
put('c', function() return iw.Char2Int('hello', 2) end)
put('m', function() return table.concat(ossl.MatchString('xxab', 'ab', 1), ',') end)
put('z', function() return ossl.StringSubString('hello', 0) end)
ll.SetObjectName(r)
";

    [FixtureFact]
    public void WriteThePositionsRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_REGION_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));

        Scene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, ObjectName, 0x7b00);
        group.Name = ObjectName;
        group.RootPart.Name = ObjectName;
        group.AbsolutePosition = new Vector3(150, 128, 30);

        var assets = new List<(UUID Id, AssetType Type, byte[] Data)>();
        void AddItem(string name, AssetType type, InventoryType invType, byte[] data)
        {
            UUID asset = UUID.Random();
            assets.Add((asset, type, data));
            group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(),
                AssetID = asset,
                Name = name,
                OwnerID = owner,
                CreatorID = owner,
                Type = (int)type,
                InvType = (int)invType,
                ScriptRunning = type == AssetType.LSLText,
                ParentPartID = group.RootPart.UUID,
            }, false);
        }

        var notecard = new OpenMetaverse.Assets.AssetNotecard { BodyText = NotecardText };
        notecard.Encode();
        AddItem("card", AssetType.Notecard, InventoryType.Notecard, notecard.AssetData);
        AddItem("phlox-script", AssetType.LSLText, InventoryType.LSL, new UTF8Encoding(false).GetBytes(PhloxScript));
        AddItem("slua-script", AssetType.LSLText, InventoryType.LSL, new UTF8Encoding(false).GetBytes(SluaScript));

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
        void Add(string name, byte[] data) => tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

        Add(ArchiveConstants.CONTROL_FILE_PATH, Utf8(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<archive major_version=\"0\" minor_version=\"8\">\n"
            + "<creation_info><datetime>0</datetime><id>" + Guid.NewGuid() + "</id></creation_info>\n"
            + "<region_info><is_megaregion>false</is_megaregion><size_in_meters>256,256</size_in_meters></region_info>\n"
            + "<assets_included>True</assets_included>\n</archive>\n"));
        foreach ((UUID id, AssetType type, byte[] data) in assets)
            Add(ArchiveConstants.ASSETS_PATH + id + ArchiveConstants.ASSET_TYPE_TO_EXTENSION[(sbyte)type], data);
        Add(ArchiveConstants.CreateOarObjectPath(group.Name, group.UUID, group.AbsolutePosition), Utf8(SceneObjectSerializer.ToOriginalXmlFormat(group)));
    }
}
