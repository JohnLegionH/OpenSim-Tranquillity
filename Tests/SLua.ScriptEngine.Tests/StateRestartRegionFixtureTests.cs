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
/// Writes the region archive for a restart of a real region: an SLua and an LSL counter, an LSL script with a long timer, an SLua
/// script that sleeps, an SLua script that stops with a fault, and an LSL listener with an SLua talker that speaks on its channel.
/// Each script writes what it saw into its prim's description. It runs only when SLUA_STATE_OAR_OUT names the file to write;
/// SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class StateRestartRegionFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_STATE_OAR_OUT")))
                Skip = "SLUA_STATE_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string CounterSlua = @"//SLua:slua
entries = (entries or 0) + 1
n = n or 0
LLEvents:on('timer', function()
    n = n + 1
    ll.SetObjectDesc('COUNT slua n=' .. n .. ' entries=' .. entries)
end)
ll.SetTimerEvent(8.0)
";

    internal const string CounterLsl = @"//SLua:LSL
integer n;
integer entries;
default
{
    state_entry()
    {
        entries++;
        llSetTimerEvent(8.0);
    }
    timer()
    {
        n++;
        llSetObjectDesc(""COUNT lsl n="" + (string)n + "" entries="" + (string)entries);
    }
}
";

    internal const string TimerLsl = @"//SLua:LSL
integer started;
integer restart;
default
{
    state_entry()
    {
        started = llGetUnixTime();
        llSetObjectDesc(""TIMER armed 70 s"");
        llSetTimerEvent(70.0);
    }
    changed(integer c)
    {
        if (c & CHANGED_REGION_START)
        {
            restart = llGetUnixTime();
            llSetObjectDesc(""TIMER restarted after="" + (string)(restart - started) + "" s"");
        }
    }
    timer()
    {
        llSetTimerEvent(0.0);
        llSetObjectDesc(""TIMER fired "" + (string)(llGetUnixTime() - restart) + "" s after the restart (armed for 70)"");
    }
}
";

    internal const string SleeperSlua = @"//SLua:slua
restart = restart or 0
LLEvents:on('changed', function(c)
    if bit32.band(c, 1024) ~= 0 then restart = ll.GetUnixTime() end
end)
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    ll.SetObjectDesc('SLEEP begins (60 s)')
    ll.Sleep(60.0)
    ll.SetObjectDesc('SLEEP over ' .. (ll.GetUnixTime() - restart) .. ' s after the restart (slept 60)')
end)
ll.SetTimerEvent(3.0)
";

    internal const string FaulterSlua = @"//SLua:slua
started = ll.GetUnixTime()
ll.SetObjectDesc('FAULT started=' .. started)
LLEvents:on('timer', function() error('boom') end)
ll.SetTimerEvent(6.0)
";

    internal const string ListenerLsl = @"//SLua:LSL
integer entries;
integer heard;
default
{
    state_entry()
    {
        entries++;
        llListen(99, """", NULL_KEY, """");
        llSetObjectDesc(""LISTEN entries="" + (string)entries + "" heard=0"");
    }
    listen(integer channel, string name, key id, string message)
    {
        heard++;
        llSetObjectDesc(""LISTEN entries="" + (string)entries + "" heard="" + (string)heard + "" last="" + message);
    }
}
";

    internal const string TalkerSlua = @"//SLua:slua
n = n or 0
LLEvents:on('timer', function()
    n = n + 1
    ll.Say(99, 'hello ' .. n)
end)
ll.SetTimerEvent(12.0)
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_STATE_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));
        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7E00;
        void Make(string name, Vector3 at, string text)
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
                ItemID = UUID.Random(), AssetID = asset, Name = name + " script", OwnerID = owner, CreatorID = owner,
                Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL, ScriptRunning = true, ParentPartID = group.RootPart.UUID,
            }, false);
        }
        Make("Restart Counter SLua", new Vector3(100, 100, 30), CounterSlua);
        Make("Restart Counter LSL", new Vector3(102, 100, 30), CounterLsl);
        Make("Restart Timer LSL", new Vector3(104, 100, 30), TimerLsl);
        Make("Restart Sleeper SLua", new Vector3(106, 100, 30), SleeperSlua);
        Make("Restart Faulter SLua", new Vector3(108, 100, 30), FaulterSlua);
        Make("Restart Listener LSL", new Vector3(110, 100, 30), ListenerLsl);
        Make("Restart Talker SLua", new Vector3(112, 100, 30), TalkerSlua);
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
