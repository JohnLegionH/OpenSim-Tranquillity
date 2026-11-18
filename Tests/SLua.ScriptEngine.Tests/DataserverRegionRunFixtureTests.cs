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
/// Writes the region archive for a run of dataserver answers, link messages and the minimum event delay in a real region.
/// Objects: an SLua script and an //SLua:LSL script that each read a notecard's line count and a line, ask for the owner's
/// username and make one key-value call; a prim holding a Phlox script and an SLua script that ask the notecard and hear each
/// other's answers; a two-prim linkset with a Phlox script in one prim and an SLua script in the other that exchange link
/// messages both ways; and scripts with a minimum event delay (and one without) that note when their timer events start. Each
/// script writes what it saw into its prim's description or hover text. It runs only when SLUA_DATASERVER_OAR_OUT names the file
/// to write; SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class DataserverRegionRunFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_DATASERVER_OAR_OUT")))
                Skip = "SLUA_DATASERVER_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string NotecardText = "alpha\nbeta\ngamma";

    internal const string NotesSlua = @"//SLua:slua
local owner = ll.GetOwner()
local got = {}
local asked = {}
local function show()
    local text = string.format('count=%s line=%s name=%s kv=%s', got.count or '-', got.line or '-', got.name or '-', got.kv or '-')
    ll.SetObjectDesc('N slua ' .. text)
    ll.SetText('N slua ' .. text, vector(1, 1, 1), 1.0)
end
LLEvents:on('dataserver', function(query, data)
    local what = asked[tostring(query)]
    if what then
        got[what] = data
        show()
    end
end)
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    asked[tostring(ll.GetNumberOfNotecardLines('card'))] = 'count'
    asked[tostring(ll.GetNotecardLine('card', 2))] = 'line'
    asked[tostring(ll.RequestUsername(owner))] = 'name'
    asked[tostring(ll.CreateKeyValue('proof', '1'))] = 'kv'
end)
ll.SetTimerEvent(9.0)
";

    internal const string NotesLsl = @"//SLua:LSL
string count = ""-"";
string line = ""-"";
string name = ""-"";
string kv = ""-"";
key qc;
key ql;
key qn;
key qk;
show()
{
    string text = ""count="" + count + "" line="" + line + "" name="" + name + "" kv="" + kv;
    llSetObjectDesc(""N lsl "" + text);
    llSetText(""N lsl "" + text, <1.0, 1.0, 1.0>, 1.0);
}
default
{
    state_entry()
    {
        llSetTimerEvent(10.0);
    }
    timer()
    {
        llSetTimerEvent(0.0);
        qc = llGetNumberOfNotecardLines(""card"");
        ql = llGetNotecardLine(""card"", 1);
        qn = llRequestUsername(llGetOwner());
        qk = llCreateKeyValue(""proof"", ""1"");
    }
    dataserver(key q, string d)
    {
        if (q == qc) count = d;
        else if (q == ql) line = d;
        else if (q == qn) name = d;
        else if (q == qk) kv = d;
        show();
    }
}
";

    // The Phlox script asks at 8 seconds and the SLua script at 12; both hear both answers.
    internal const string SharedPhlox = @"string got = """";
default
{
    state_entry()
    {
        llSetTimerEvent(8.0);
    }
    timer()
    {
        llSetTimerEvent(0.0);
        llGetNumberOfNotecardLines(""card"");
    }
    dataserver(key q, string d)
    {
        got += d + "";"";
        llSetObjectDesc(""P shared got="" + got);
    }
}
";

    internal const string SharedSlua = @"//SLua:slua
local got = ''
LLEvents:on('dataserver', function(query, data)
    got = got .. data .. ';'
    ll.SetText('S shared got=' .. got, vector(1, 1, 1), 1.0)
end)
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    ll.GetNotecardLine('card', 3)
end)
ll.SetTimerEvent(12.0)
";

    // Link numbers: LINK_ALL_OTHERS is -2. Phlox pings at 10 seconds and the SLua script at 14; each answers the other's ping.
    internal const string LinkPhlox = @"string got = """";
integer step;
default
{
    state_entry()
    {
        llSetTimerEvent(10.0);
    }
    timer()
    {
        if (step == 0)
        {
            step = 1;
            llMessageLinked(LINK_ALL_OTHERS, 1, ""ping from phlox"", NULL_KEY);
            llSetTimerEvent(20.0);
        }
        else
        {
            llSetObjectDesc(""P link got="" + got);
            llSetText(""P link got="" + got, <1.0, 1.0, 1.0>, 1.0);
            llSetTimerEvent(0.0);
        }
    }
    link_message(integer sender, integer num, string str, key id)
    {
        got += (string)num + "":"" + str + "";"";
        if (num == 3) llMessageLinked(LINK_ALL_OTHERS, 4, ""pong from phlox"", NULL_KEY);
    }
}
";

    internal const string LinkSlua = @"//SLua:slua
local got = ''
LLEvents:on('link_message', function(sender, num, str, id)
    got = got .. num .. ':' .. str .. ';'
    if num == 1 then
        ll.MessageLinked(-2, 2, 'pong from slua', '')
    end
end)
local step = 0
LLEvents:on('timer', function()
    if step == 0 then
        step = 1
        ll.MessageLinked(-2, 3, 'ping from slua', '')
        ll.SetTimerEvent(20.0)
    else
        ll.SetObjectDesc('S link got=' .. got)
        ll.SetText('S link got=' .. got, vector(1, 1, 1), 1.0)
        ll.SetTimerEvent(0.0)
    end
end)
ll.SetTimerEvent(14.0)
";

    // The timer asks for 0.1 seconds, which the engine raises to its minimum (0.5); a floor of one second then spaces the starts.
    internal const string DelaySlua = @"//SLua:slua
{FLOOR}local stamps = {}
LLEvents:on('timer', function()
    stamps[#stamps + 1] = ll.GetTime()
    if #stamps >= 4 then
        ll.SetTimerEvent(0.0)
        local d = {}
        for i = 2, #stamps do d[#d + 1] = string.format('%.2f', stamps[i] - stamps[i - 1]) end
        ll.SetObjectDesc('M {TAG} deltas=' .. table.concat(d, ','))
    end
end)
ll.SetTimerEvent(0.1)
";

    internal const string DelayLsl = @"//SLua:LSL
list stamps;
default
{
    state_entry()
    {
        llMinEventDelay(1.0);
        llSetTimerEvent(0.1);
    }
    timer()
    {
        stamps += [llGetTime()];
        if (llGetListLength(stamps) >= 4)
        {
            llSetTimerEvent(0.0);
            llSetObjectDesc(""M lsl deltas="" + llGetSubString((string)(llList2Float(stamps, 1) - llList2Float(stamps, 0)), 0, 3) + "","" +
                llGetSubString((string)(llList2Float(stamps, 2) - llList2Float(stamps, 1)), 0, 3) + "","" +
                llGetSubString((string)(llList2Float(stamps, 3) - llList2Float(stamps, 2)), 0, 3));
        }
    }
}
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_DATASERVER_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));

        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, byte[] Data, AssetType Type)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7B00;

        SceneObjectGroup Make(string name, Vector3 at, int parts = 1)
        {
            SceneObjectGroup group = SceneHelpers.CreateSceneObject(parts, owner, name + " ", tail);
            tail += 16;
            group.Name = name;
            group.RootPart.Name = name;
            group.AbsolutePosition = at;
            groups.Add(group);
            return group;
        }

        void AddScript(SceneObjectPart part, string name, string text)
        {
            UUID asset = UUID.Random();
            assets.Add((asset, new UTF8Encoding(false).GetBytes(text), AssetType.LSLText));
            part.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(), AssetID = asset, Name = name, OwnerID = owner, CreatorID = owner,
                Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL, ScriptRunning = true, ParentPartID = part.UUID,
            }, false);
        }

        void AddNotecard(SceneObjectPart part)
        {
            AssetBase card = AssetHelpers.CreateNotecardAsset(UUID.Random(), NotecardText);
            assets.Add((card.FullID, card.Data, AssetType.Notecard));
            part.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(), AssetID = card.FullID, Name = "card", OwnerID = owner, CreatorID = owner,
                Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard, ParentPartID = part.UUID,
            }, false);
        }

        SceneObjectGroup notesSlua = Make("Proof Notes SLua", new Vector3(60, 80, 30));
        AddNotecard(notesSlua.RootPart);
        AddScript(notesSlua.RootPart, "Notes SLua", NotesSlua);

        SceneObjectGroup notesLsl = Make("Proof Notes LSL", new Vector3(63, 80, 30));
        AddNotecard(notesLsl.RootPart);
        AddScript(notesLsl.RootPart, "Notes LSL", NotesLsl);

        SceneObjectGroup shared = Make("Proof Shared", new Vector3(66, 80, 30));
        AddNotecard(shared.RootPart);
        AddScript(shared.RootPart, "Shared Phlox", SharedPhlox);
        AddScript(shared.RootPart, "Shared SLua", SharedSlua);

        SceneObjectGroup link = Make("Proof Link", new Vector3(69, 80, 30), parts: 2);
        SceneObjectPart child = link.GetLinkNumPart(2);
        child.Name = "Proof Link Child";
        AddScript(link.RootPart, "Link Phlox", LinkPhlox);
        AddScript(child, "Link SLua", LinkSlua);

        AddScript(Make("Proof Delay SLua", new Vector3(72, 80, 30)).RootPart, "Delay SLua", DelaySlua.Replace("{FLOOR}", "ll.MinEventDelay(1.0)\n").Replace("{TAG}", "slua"));
        AddScript(Make("Proof Delay LSL", new Vector3(75, 80, 30)).RootPart, "Delay LSL", DelayLsl);
        AddScript(Make("Proof Delay Control", new Vector3(78, 80, 30)).RootPart, "Delay control", DelaySlua.Replace("{FLOOR}", "").Replace("{TAG}", "control"));

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
        void Add(string name, byte[] data) => tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });

        Add(ArchiveConstants.CONTROL_FILE_PATH, new UTF8Encoding(false).GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<archive major_version=\"0\" minor_version=\"8\">\n"
            + "<creation_info><datetime>0</datetime><id>" + Guid.NewGuid() + "</id></creation_info>\n"
            + "<region_info><is_megaregion>false</is_megaregion><size_in_meters>256,256</size_in_meters></region_info>\n"
            + "<assets_included>True</assets_included>\n</archive>\n"));
        foreach ((UUID id, byte[] data, AssetType type) in assets)
            Add(ArchiveConstants.ASSETS_PATH + id + ArchiveConstants.ASSET_TYPE_TO_EXTENSION[(sbyte)type], data);
        foreach (SceneObjectGroup group in groups)
            Add(ArchiveConstants.CreateOarObjectPath(group.Name, group.UUID, group.AbsolutePosition),
                new UTF8Encoding(false).GetBytes(SceneObjectSerializer.ToOriginalXmlFormat(group)));
    }
}
