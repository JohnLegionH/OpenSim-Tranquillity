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
/// Writes the region archive for the proof of the status command: a running script, a script a fault stops, a script with a minimum event
/// delay, a script that reports the same error in a loop, and a listener on the debug channel that writes what it heard into its prim's
/// description. It runs only when SLUA_STATUS_OAR_OUT names the file to write; SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class StatusRegionFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_STATUS_OAR_OUT")))
                Skip = "SLUA_STATUS_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string Running = "//SLua:slua\nn = 0\nLLEvents:on('timer', function() n = n + 1 end)\nll.SetTimerEvent(5.0)\n";

    internal const string Faulter = "//SLua:slua\nLLEvents:on('timer', function() error('boom') end)\nll.SetTimerEvent(4.0)\n";

    internal const string MinDelay = "//SLua:slua\nll.MinEventDelay(0.5)\nn = 0\nLLEvents:on('timer', function() n = n + 1 end)\nll.SetTimerEvent(5.0)\n";

    internal const string ErrorLoop = "//SLua:slua\nn = 0\nLLEvents:on('timer', function()\n  n = n + 1\n  if n <= 15 then ll.GetScriptState('no such script') end\nend)\nll.SetTimerEvent(0.5)\n";

    internal const string Listener = "//SLua:slua\nheard = 0\nLLEvents:on('listen', function(channel, name, id, msg)\n  heard = heard + 1\n  ll.SetObjectDesc('ERR heard=' .. heard .. ' last=' .. string.sub(msg, 1, 70))\nend)\nll.Listen(2147483647, '', NULL_KEY, '')\n";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_STATUS_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));
        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7D00;
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
        Make("Status Listener", new Vector3(100, 140, 30), Listener);
        Make("Status Running", new Vector3(102, 140, 30), Running);
        Make("Status Faulter", new Vector3(104, 140, 30), Faulter);
        Make("Status MinDelay", new Vector3(106, 140, 30), MinDelay);
        Make("Status ErrorLoop", new Vector3(108, 140, 30), ErrorLoop);
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
