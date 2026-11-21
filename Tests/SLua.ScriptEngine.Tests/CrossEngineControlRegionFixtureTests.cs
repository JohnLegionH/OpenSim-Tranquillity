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
/// Writes the region archive for a run, in a real region with both engines, of the functions that act on another script of the
/// prim: one prim holds a Phlox script (no header, the region's default engine) and an SLua script. Each one, once, asks the
/// other for llGetScriptState, resets it with llResetOtherScript, and later calls osResetAllScripts, which resets the other one
/// and then itself. Every start of a script appends to a record the other cannot lose (the Phlox script to the prim's
/// description, the SLua script to the object's name; neither can read the other's), so what each call did can be read from
/// the stored prim afterwards: with the calls timed apart, each script starts four times (its own start, the other's
/// llResetOtherScript, the first osResetAllScripts, the second). It runs only when SLUA_REGION_OAR_OUT names the file to write;
/// SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class CrossEngineControlRegionFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_REGION_OAR_OUT")))
                Skip = "SLUA_REGION_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string ObjectName = "Example Engines Pair";

    // Timers are 4 s here and 10 s in the other script, so the two never fire together. Ag<n> is the answer of llGetScriptState
    // (1 running), Ar the reset, Ao and Bx the osResetAllScripts of each script.
    internal const string PhloxScript = @"default
{
    state_entry()
    {
        llSetObjectDesc(llGetObjectDesc() + ""As"");
        llSetTimerEvent(4.0);
    }
    timer()
    {
        llSetTimerEvent(0.0);
        string d = llGetObjectDesc();
        if (llSubStringIndex(d, ""Ag"") < 0)
        {
            integer st = llGetScriptState(""slua-script"");
            llSetObjectDesc(d + ""Ag"" + (string)st + ""Ar"");
            llResetOtherScript(""slua-script"");
        }
        else if (llSubStringIndex(d, ""Ao"") < 0)
        {
            llSetObjectDesc(d + ""Ao"");
            osResetAllScripts(1);
        }
    }
}
";

    internal const string SluaScript = @"//SLua:slua
local function mark(t) ll.SetObjectName(ll.GetObjectName() .. t) end
mark('Bs')
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    local n = ll.GetObjectName()
    if not string.find(n, 'Bg', 1, true) then
        local st = ll.GetScriptState('phlox-script')
        mark('Bg' .. (st and '1' or '0') .. 'Br')
        ll.ResetOtherScript('phlox-script')
    elseif not string.find(n, 'Bx', 1, true) then
        mark('Bx')
        ossl.ResetAllScripts(1)
    end
end)
ll.SetTimerEvent(10.0)
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_REGION_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));

        Scene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, ObjectName, 0x7a00);
        group.Name = ObjectName;
        group.RootPart.Name = ObjectName;
        group.AbsolutePosition = new Vector3(140, 128, 30);

        var assets = new List<(UUID Id, string Text)>();
        void AddScript(string name, string text)
        {
            UUID asset = UUID.Random();
            assets.Add((asset, text));
            group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = UUID.Random(),
                AssetID = asset,
                Name = name,
                OwnerID = owner,
                CreatorID = owner,
                Type = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                ScriptRunning = true,
                ParentPartID = group.RootPart.UUID,
            }, false);
        }

        AddScript("phlox-script", PhloxScript);
        AddScript("slua-script", SluaScript);

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
