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
/// Writes the region archive for a run, in a real region with both engines, of osSetScriptEngine called from an SLua script:
/// one prim holds an //SLua:LSL script and an SLua script. After a delay the SLua script
/// asks for the other one (an //SLua:LSL script) to run on Phlox, by engine name, and writes the call's outcome (the answer, or the error when the
/// function is unknown) into the object's name; the other script appends to the prim's description each time it starts, so a
/// restart in the new engine shows as a second mark. It runs only when SLUA_SETENGINE_OAR_OUT names the file to write;
/// SLUA_REGION_OWNER is the owner's id.
/// </summary>
public class SetScriptEngineRegionFixtureTests
{
    public sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_SETENGINE_OAR_OUT")))
                Skip = "SLUA_SETENGINE_OAR_OUT is not set; the archive is written only on request";
        }
    }

    internal const string ObjectName = "Example Engine Switch";

    internal const string TargetScript = @"//SLua:LSL
default
{
    state_entry()
    {
        // the free memory a script is told differs by engine, so each start leaves the number of the engine it ran in
        llSetObjectDesc(llGetObjectDesc() + ""T"" + (string)llGetFreeMemory() + "";"");
    }
}
";

    internal const string CallerScript = @"//SLua:slua
LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    local ok, r = pcall(ossl.SetScriptEngine, 'target-script', 'Phlox')
    ll.SetObjectName(ll.GetObjectName() .. ' call ok=' .. tostring(ok) .. ' r=' .. tostring(r))
end)
ll.SetTimerEvent(12.0)
";

    [FixtureFact]
    public void WriteTheRegionArchive()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_SETENGINE_OAR_OUT");
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

        AddScript("target-script", TargetScript);
        AddScript("caller-script", CallerScript);

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
