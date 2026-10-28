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
/// Writes the region archive for a run of sensors, an HTTP request and a sit with permissions and an animation in a real
/// region: a named passive object and two scripts (SLua and //SLua:LSL) that sense it and sense a name nobody has; an SLua and an
/// LSL script that each make one HTTP request; and a prim with an SLua script that, when an avatar sits on it, requests the
/// permission to trigger animations, starts an animation and says what happened. The sensor and HTTP scripts write their results
/// into their prim's description. It runs only when SLUA_ASYNC_OAR_OUT names the file to write; SLUA_REGION_OWNER is the
/// owner's id and SLUA_HTTP_URL the address the request goes to.
/// </summary>
public class AsyncRegionRunFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_ASYNC_OAR_OUT")))
                Skip = "SLUA_ASYNC_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string SensorSlua = @"//SLua:slua
local found, none = '-', '-'
local function show()
    local text = 'SENS slua found=' .. found .. ' none=' .. none
    ll.SetObjectDesc(text)
    ll.SetText(text, vector(1, 1, 1), 1.0)
end
LLEvents:on('sensor', function(detected)
    found = #detected .. '|' .. detected[1]:getName()
    show()
    ll.Sensor('Nobody Here', NULL_KEY, 4, 20.0, PI)
end)
LLEvents:on('no_sensor', function()
    none = 'yes'
    show()
end)
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    ll.Sensor('Proof Sensor Target', NULL_KEY, 4, 20.0, PI)
end)
ll.SetTimerEvent(6.0)
";

    internal const string SensorLsl = @"//SLua:LSL
string found = ""-"";
string none = ""-"";
show()
{
    string text = ""SENS lsl found="" + found + "" none="" + none;
    llSetObjectDesc(text);
    llSetText(text, <1.0, 1.0, 1.0>, 1.0);
}
default
{
    state_entry()
    {
        llSetTimerEvent(7.0);
    }
    timer()
    {
        llSetTimerEvent(0.0);
        llSensor(""Proof Sensor Target"", NULL_KEY, PASSIVE, 20.0, PI);
    }
    sensor(integer n)
    {
        found = (string)n + ""|"" + llDetectedName(0);
        show();
        llSensor(""Nobody Here"", NULL_KEY, PASSIVE, 20.0, PI);
    }
    no_sensor()
    {
        none = ""yes"";
        show();
    }
}
";

    internal const string HttpSlua = @"//SLua:slua
local q
LLEvents:on('http_response', function(id, status, meta, body)
    if tostring(id) == tostring(q) then
        ll.SetObjectDesc('HTTP slua status=' .. status .. ' bytes=' .. #body)
    end
end)
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    q = ll.HTTPRequest('{URL}', {}, '')
    ll.SetObjectDesc('HTTP slua asked key=' .. tostring(q))
end)
ll.SetTimerEvent(8.0)
";

    internal const string HttpLsl = @"//SLua:LSL
key q;
default
{
    state_entry()
    {
        llSetTimerEvent(9.0);
    }
    timer()
    {
        llSetTimerEvent(0.0);
        q = llHTTPRequest(""{URL}"", [], """");
        llSetObjectDesc(""HTTP lsl asked key="" + (string)q);
    }
    http_response(key id, integer status, list meta, string body)
    {
        if (id == q) llSetObjectDesc(""HTTP lsl status="" + (string)status + "" bytes="" + (string)llStringLength(body));
    }
}
";

    // An avatar sits: ask for the permission to trigger animations (the sitter gets it with no dialog), start an animation, and
    // say what the avatar's animation list was before and after.
    internal const string SitSlua = @"//SLua:slua
ll.SitTarget(vector(0, 0, 0.6), quaternion(0, 0, 0, 1))
local sitter = nil
local before = 0
LLEvents:on('changed', function(change)
    if bit32.band(change, 32) ~= 0 then
        local who = ll.AvatarOnSitTarget()
        if tostring(who) ~= tostring(NULL_KEY) then
            if not sitter then
                sitter = who
                before = #ll.GetAnimationList(sitter)
                ll.RequestPermissions(sitter, 16)
            end
        else
            sitter = nil
        end
    end
end)
LLEvents:on('run_time_permissions', function(perms)
    ll.Say(0, 'SIT perm=' .. perms .. ' key=' .. tostring(ll.GetPermissionsKey()))
    if bit32.band(perms, 16) ~= 0 then
        ll.StartAnimation('dance1')
        ll.SetTimerEvent(2.0)
    end
end)
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    if sitter then
        local now = ll.GetAnimationList(sitter)
        local text = 'SIT anims before=' .. before .. ' after=' .. #now
        ll.SetObjectDesc(text)
        ll.Say(0, text)
    end
end)
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_ASYNC_OAR_OUT");
        string url = Environment.GetEnvironmentVariable("SLUA_HTTP_URL") ?? "https://example.org/";
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));

        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7C00;

        SceneObjectGroup Make(string name, Vector3 at, string scriptName, string text)
        {
            SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, name + " ", tail);
            tail += 16;
            group.Name = name;
            group.RootPart.Name = name;
            group.AbsolutePosition = at;
            groups.Add(group);
            if (text != null)
            {
                UUID asset = UUID.Random();
                assets.Add((asset, text));
                group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
                {
                    ItemID = UUID.Random(), AssetID = asset, Name = scriptName, OwnerID = owner, CreatorID = owner,
                    Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL, ScriptRunning = true, ParentPartID = group.RootPart.UUID,
                }, false);
            }
            return group;
        }

        Make("Proof Sensor Target", new Vector3(60, 100, 30), null, null);
        Make("Proof Sensor SLua", new Vector3(62, 100, 30), "Sensor SLua", SensorSlua);
        Make("Proof Sensor LSL", new Vector3(64, 100, 30), "Sensor LSL", SensorLsl);
        Make("Proof Http SLua", new Vector3(66, 100, 30), "Http SLua", HttpSlua.Replace("{URL}", url));
        Make("Proof Http LSL", new Vector3(68, 100, 30), "Http LSL", HttpLsl.Replace("{URL}", url));
        Make("Proof Sit", new Vector3(74, 100, 30), "Sit SLua", SitSlua);

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
