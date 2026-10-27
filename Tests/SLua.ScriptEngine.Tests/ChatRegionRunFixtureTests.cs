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
/// Writes the region archive for a run of chat and listens in a real region. Eight objects, each with one script, spread over
/// the region: an SLua script, an //SLua:LSL script and a plain LSL script (the region's default engine) that talk to each
/// other on one channel; two scripts far away that speak with llSay and llRegionSay on channels the SLua script listens to with
/// a name, a message and a key filter; and three scripts (one of each kind) that make 66 listens. Each script writes what it
/// heard into its prim's description. It runs only when SLUA_CHAT_OAR_OUT names the file to write; SLUA_REGION_OWNER is the
/// owner's id.
/// </summary>
public class ChatRegionRunFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_CHAT_OAR_OUT")))
                Skip = "SLUA_CHAT_OAR_OUT is not set; the archive is written only on request";
        }
    }

    // The SLua script listens on channel 30 (the three scripts talk there), 31 (heard from far away only through llRegionSay), and
    // on 40 to 45 with a name, a name nobody has, a message, another message, a key and no filter; it counts what it hears per
    // channel and writes the counts into its description after the others have spoken.
    internal const string SluaTalker = @"//SLua:slua
local counts = {}
LLEvents:on('listen', function(channel, name, id, message)
    counts[channel] = (counts[channel] or 0) + 1
end)
ll.Listen(30, '', '', '')
ll.Listen(31, '', '', '')
ll.Listen(40, 'Proof Far', '', '')
ll.Listen(41, 'Nobody Here', '', '')
ll.Listen(42, '', '', 'open')
ll.Listen(43, '', '', 'close')
ll.Listen(44, '', '{FARKEY}', '')
ll.Listen(45, '', '', '')
local step = 0
LLEvents:on('timer', function()
    if step == 0 then
        step = 1
        ll.Say(30, 'from the SLua script')
        ll.SetTimerEvent(25.0)
    else
        local function n(c) return counts[c] or 0 end
        ll.SetObjectDesc(string.format('S c30=%d c31=%d c40=%d c41=%d c42=%d c43=%d c44=%d c45=%d', n(30), n(31), n(40), n(41), n(42), n(43), n(44), n(45)))
        ll.SetTimerEvent(0.0)
    end
end)
ll.SetTimerEvent(15.0)
";

    internal const string LslTalker = @"//SLua:LSL
integer heard;
integer step;
default
{
    state_entry()
    {
        llListen(30, """", """", """");
        llSetTimerEvent(17.0);
    }
    listen(integer channel, string name, key id, string message)
    {
        heard++;
    }
    timer()
    {
        if (step == 0)
        {
            step = 1;
            llSay(30, ""from the LSL script on the SLua engine"");
            llSetTimerEvent(25.0);
        }
        else
        {
            llSetObjectDesc(""L c30="" + (string)heard);
            llSetTimerEvent(0.0);
        }
    }
}
";

    internal const string PhloxTalker = @"integer heard;
integer step;
default
{
    state_entry()
    {
        llListen(30, """", """", """");
        llSetTimerEvent(19.0);
    }
    listen(integer channel, string name, key id, string message)
    {
        heard++;
    }
    timer()
    {
        if (step == 0)
        {
            step = 1;
            llSay(30, ""from the Phlox script"");
            llSetTimerEvent(25.0);
        }
        else
        {
            llSetObjectDesc(""P c30="" + (string)heard);
            llSetTimerEvent(0.0);
        }
    }
}
";

    // The far scripts speak at 17 and 19 seconds. {MARK} is F for the first, T for the second.
    internal const string FarSpeaker = @"//SLua:LSL
default
{
    state_entry()
    {
        llSetTimerEvent({DELAY});
    }
    timer()
    {
        integer c;
        if ({SAYFIRST} == 1)
        {
            llSay(31, ""too far for llSay"");
            llRegionSay(31, ""region wide"");
        }
        for (c = 40; c <= 45; c++)
        {
            llRegionSay(c, ""open"");
            llRegionSay(c, ""close"");
        }
        llSetObjectDesc(""{MARK} spoke"");
        llSetTimerEvent(0.0);
    }
}
";

    internal const string SluaCap = @"//SLua:slua
local made, failed = 0, 0
for channel = 101, 166 do
    if ll.Listen(channel, '', '', '') < 0 then failed = failed + 1 else made = made + 1 end
end
ll.SetObjectDesc(string.format('C slua made=%d failed=%d', made, failed))
";

    internal const string LslCap = @"//SLua:LSL
default
{
    state_entry()
    {
        integer made;
        integer failed;
        integer channel;
        for (channel = 101; channel <= 166; channel++)
        {
            if (llListen(channel, """", """", """") < 0) failed++; else made++;
        }
        llSetObjectDesc(""C lsl made="" + (string)made + "" failed="" + (string)failed);
    }
}
";

    internal const string PhloxCap = @"default
{
    state_entry()
    {
        integer made;
        integer failed;
        integer channel;
        for (channel = 101; channel <= 166; channel++)
        {
            if (llListen(channel, """", """", """") < 0) failed++; else made++;
        }
        llSetObjectDesc(""C phlox made="" + (string)made + "" failed="" + (string)failed);
    }
}
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_CHAT_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));

        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7900;

        SceneObjectGroup Make(string name, Vector3 at, string scriptName, string text)
        {
            SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, name + " ", tail);
            tail += 16;
            group.Name = name;
            group.RootPart.Name = name;
            group.AbsolutePosition = at;
            groups.Add(group);
            if (text != null)
                AddScript(group.RootPart, scriptName, text);
            return group;
        }

        void AddScript(SceneObjectPart part, string name, string text)
        {
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

        // The far speaker's id is the key the SLua script's key filter names.
        SceneObjectGroup far = Make("Proof Far", new Vector3(200, 200, 30), "Far speaker", FarSpeaker.Replace("{DELAY}", "17.0").Replace("{SAYFIRST}", "1").Replace("{MARK}", "F"));
        Make("Proof Other", new Vector3(200, 203, 30), "Other speaker", FarSpeaker.Replace("{DELAY}", "19.0").Replace("{SAYFIRST}", "0").Replace("{MARK}", "T"));
        Make("Proof SLua", new Vector3(60, 60, 30), "SLua talker", SluaTalker.Replace("{FARKEY}", far.RootPart.UUID.ToString()));
        Make("Proof LSL", new Vector3(62, 60, 30), "LSL talker", LslTalker);
        Make("Proof Phlox", new Vector3(64, 60, 30), "Phlox talker", PhloxTalker);
        Make("Proof Cap SLua", new Vector3(100, 100, 30), "SLua cap", SluaCap);
        Make("Proof Cap LSL", new Vector3(110, 100, 30), "LSL cap", LslCap);
        Make("Proof Cap Phlox", new Vector3(120, 100, 30), "Phlox cap", PhloxCap);

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
