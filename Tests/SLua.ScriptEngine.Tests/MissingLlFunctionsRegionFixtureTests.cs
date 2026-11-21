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
/// Writes the region archive for a run, in a real region with both engines, of the ll functions Phlox gained as Second Life
/// defines them. One prim holds a notecard and two scripts that call the same functions, each in its own language: the Phlox
/// script (LSL, the region's default engine) writes what it got to the prim's description, the SLua script writes what it got
/// to the object's name (neither can read the other's). The owner is not in the region and owns no parcel, so every call
/// meets the case the Second Life wiki documents for it: no game-control data (-1), no such agent (NOT FOUND, TRANSFER_NO_TARGET),
/// a bad option (TRANSFER_BAD_OPTS), no land permission (PARCEL_SALE_ERROR_NO_PERMISSIONS), and the notecard search over the
/// notecard in the prim. It runs only when SLUA_REGION_OAR_OUT names the file to write; SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class MissingLlFunctionsRegionFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_REGION_OAR_OUT")))
                Skip = "SLUA_REGION_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string ObjectName = "Example Missing Functions";

    // The notecard: "b+" matches "b" at column 6 of line 0, "bb" at column 11, and "bbb" at column 5 of line 1 (three matches).
    internal const string NotecardText = "alpha beta bb\nBeta bbb\n";

    // The result is a run of codes: m mode, a number of axes, b buttons, f first key of the filtered attachments, g and o the
    // codes of llGiveAgentInventory (no such agent; a bad option), t and u those of llTransferOwnership (a bad option; no such
    // agent), p llSetParcelForSale, M llMapBeacon returned, D llRemoteDataSetRegion returned, R llRemoteLoadScript returned, s the
    // notecard search (LSL only: the SLua binding does not offer it), c the count of matches that arrives by dataserver event.
    internal const string PhloxScript = @"default
{
    state_entry()
    {
        key me = llGetOwner();
        string r = ""m"" + (string)llGetGameControlMode(me);
        r += ""a"" + (string)llGetListLength(llGetGameControlModeAxes(me));
        r += ""b"" + (string)llGetGameControlModeButtons(me);
        r += ""f"" + llList2String(llGetAttachedListFiltered(me, [FILTER_INCLUDE, ATTACH_CHEST]), 0);
        r += ""g"" + (string)llGiveAgentInventory(me, ""f"", [""x""], []);
        r += ""o"" + (string)llGiveAgentInventory(me, ""f"", [""x""], [99, 1]);
        r += ""t"" + (string)llTransferOwnership(me, 0, [1]);
        r += ""u"" + (string)llTransferOwnership(me, 0, []);
        r += ""p"" + (string)llSetParcelForSale(TRUE, [PARCEL_SALE_PRICE, 10]);
        llMapBeacon(""Example Region"", <1, 2, 3>, []);
        r += ""M"";
        llRemoteDataSetRegion();
        r += ""D"";
        llRemoteLoadScript(me, ""x"", TRUE, 0);
        r += ""R"";
        r += ""s"" + llDumpList2String(llFindNotecardTextSync(""card"", ""b+"", 0, 0, []), "","");
        llSetObjectDesc(r);
        llFindNotecardTextCount(""card"", ""b+"", []);
    }
    dataserver(key q, string data)
    {
        llSetObjectDesc(llGetObjectDesc() + ""c"" + data);
    }
}
";

    internal const string SluaScript = @"//SLua:slua
local me = ll.GetOwner()
local r = 'm' .. ll.GetGameControlMode(me)
r = r .. 'a' .. #ll.GetGameControlModeAxes(me)
r = r .. 'b' .. ll.GetGameControlModeButtons(me)
r = r .. 'f' .. ll.GetAttachedListFiltered(me, {1, 1})[1]
r = r .. 'g' .. ll.GiveAgentInventory(me, 'f', {'x'}, {})
r = r .. 'o' .. ll.GiveAgentInventory(me, 'f', {'x'}, {99, 1})
r = r .. 't' .. ll.TransferOwnership(me, 0, {1})
r = r .. 'u' .. ll.TransferOwnership(me, 0, {})
r = r .. 'p' .. ll.SetParcelForSale(true, {1, 10})
ll.MapBeacon('Example Region', vector(1, 2, 3), {})
r = r .. 'M'
ll.RemoteDataSetRegion()
r = r .. 'D'
ll.RemoteLoadScript(me, 'x', true, 0)
r = r .. 'R'
local ok = pcall(ll.FindNotecardTextSync, 'card', 'b+', 0, 0, {})
r = r .. (ok and 's' or 'x')
LLEvents:on('dataserver', function(q, data)
    ll.SetObjectName(ll.GetObjectName() .. 'c' .. data)
end)
ll.SetObjectName(r)
ll.FindNotecardTextCount('card', 'b+', {})
";

    [FixtureFact]
    public void WriteTheRegionArchive()
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
