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
/// Writes the region archive for a run of os, iw and bot functions in a real region: an SLua script and an //SLua:LSL script
/// that each call osSHA256, osGetGridName, an iw list function, create a bot and remove it, and (SLua) call one function the
/// region's settings deny. Each script writes what it saw into its prim's description and hover text. It runs only when
/// SLUA_OS_OAR_OUT names the file to write; SLUA_REGION_OWNER is the owner's id and SLUA_DENIED_FUNCTION the function the
/// region's settings deny.
/// </summary>
public class OsRegionRunFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_OS_OAR_OUT")))
                Skip = "SLUA_OS_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string OsSlua = @"//SLua:slua
local sha, grid, iwn, made, gone, denied = '-', '-', '-', '-', '-', '-'
local function show()
    ll.SetObjectDesc('OS slua sha=' .. sha .. ' grid=' .. grid .. ' iw=' .. iwn .. ' bot=' .. made .. ' gone=' .. gone)
    ll.SetText('DENIED slua ' .. denied, vector(1, 1, 1), 1.0)
end
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    sha = string.sub(ossl.SHA256('abc'), 1, 8)
    grid = ossl.GetGridName()
    iwn = tostring(#iw.ListRemoveDuplicates({1, 1, 2, 2, 3}))
    show()
    local ok, e = pcall(ossl.{DENIED})
    denied = tostring(ok) .. ' ' .. tostring(e)
    show()
    local b = bot.CreateBot('Proof', 'Slua', '', vector(130, 130, 30), 0)
    made = string.sub(tostring(b), 1, 8) .. '/isbot=' .. tostring(bot.IsBot(b))
    show()
    bot.RemoveBot(b)
    gone = tostring(bot.IsBot(b))
    show()
end)
ll.SetTimerEvent(6.0)
";

    internal const string OsLsl = @"//SLua:LSL
default
{
    state_entry()
    {
        llSetTimerEvent(14.0);
    }
    timer()
    {
        llSetTimerEvent(0.0);
        string sha = llGetSubString(osSHA256(""abc""), 0, 7);
        string grid = osGetGridName();
        integer n = llGetListLength(iwListRemoveDuplicates([1, 1, 2, 2, 3]));
        llSetObjectDesc(""OS lsl sha="" + sha + "" grid="" + grid + "" iw="" + (string)n);
        key b = botCreateBot(""Proof"", ""Lsl"", """", <132, 130, 30>, 0);
        string made = llGetSubString((string)b, 0, 7) + ""/isbot="" + (string)botIsBot(b);
        botRemoveBot(b);
        llSetObjectDesc(""OS lsl sha="" + sha + "" grid="" + grid + "" iw="" + (string)n + "" bot="" + made + "" gone="" + (string)botIsBot(b));
    }
}
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_OS_OAR_OUT");
        string denied = Environment.GetEnvironmentVariable("SLUA_DENIED_FUNCTION") ?? "GetSimulatorVersion";
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));
        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7D00;
        void Make(string name, Vector3 at, string scriptName, string text)
        {
            SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, name + " ", tail);
            tail += 16;
            group.Name = name;
            group.RootPart.Name = name;
            group.AbsolutePosition = at;
            groups.Add(group);
            UUID asset = UUID.Random();
            assets.Add((asset, text));
            group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(), AssetID = asset, Name = scriptName, OwnerID = owner, CreatorID = owner,
                Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL, ScriptRunning = true, ParentPartID = group.RootPart.UUID,
            }, false);
        }
        Make("Proof Os SLua", new Vector3(80, 100, 30), "Os SLua", OsSlua.Replace("{DENIED}", denied));
        Make("Proof Os LSL", new Vector3(82, 100, 30), "Os LSL", OsLsl);
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
