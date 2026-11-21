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
/// Writes the region archive for the proof of what a script saves besides its variables: an SLua script with a repeating sensor that
/// sets it once (so after a restart only the saved record can bring it back), a prim for it to sense, and one object with three scripts
/// that the proof deletes to see that their rows go. It runs only when SLUA_EXTRAS_OAR_OUT names the file to write; SLUA_REGION_OWNER is
/// the owner's id.
/// </summary>
public class SavedExtrasRegionFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_EXTRAS_OAR_OUT")))
                Skip = "SLUA_EXTRAS_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string SensorSlua = @"//SLua:slua
entries = (entries or 0) + 1
n = n or 0
LLEvents:on('sensor', function(found)
    n = n + 1
    ll.SetObjectDesc('SENSOR n=' .. n .. ' entries=' .. entries)
end)
ll.SensorRepeat('Extras Target', NULL_KEY, PASSIVE, 30.0, PI, 3.0)
ll.SetObjectDesc('SENSOR n=0 entries=' .. entries)
";

    internal const string Idle = @"//SLua:slua
entries = (entries or 0) + 1
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_EXTRAS_OAR_OUT");
        var owner = new UUID(Environment.GetEnvironmentVariable("SLUA_REGION_OWNER"));
        Scene scene = new SceneHelpers().SetupScene();
        var assets = new List<(UUID Id, string Text)>();
        var groups = new List<SceneObjectGroup>();
        int tail = 0x7F00;
        SceneObjectGroup Make(string name, Vector3 at, params string[] scripts)
        {
            SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, owner, name + " ", tail);
            tail += 16;
            group.Name = name;
            group.RootPart.Name = name;
            group.AbsolutePosition = at;
            groups.Add(group);
            int i = 0;
            foreach (string text in scripts)
            {
                UUID asset = UUID.Random();
                assets.Add((asset, text));
                group.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
                {
                    ItemID = UUID.Random(), AssetID = asset, Name = name + " script " + (++i), OwnerID = owner, CreatorID = owner,
                    Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL, ScriptRunning = true, ParentPartID = group.RootPart.UUID,
                }, false);
            }
            return group;
        }
        Make("Extras Sensor SLua", new Vector3(100, 120, 30), SensorSlua);
        Make("Extras Target", new Vector3(104, 120, 30));
        Make("Extras Delete Me", new Vector3(100, 124, 30), Idle, Idle, Idle);
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
