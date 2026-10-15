/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>Builds scenes and archives with the repository's own savers, and plain tar files by hand.</summary>
internal static class ArchiveTestKit
{
    public static readonly UUID Alice = UUID.Parse("a11ce000-0000-4000-8000-000000000001");
    public static readonly UUID Bob = UUID.Parse("b0b00000-0000-4000-8000-000000000002");
    public static readonly UUID Carol = UUID.Parse("ca701000-0000-4000-8000-000000000003");
    public static readonly UUID Dave = UUID.Parse("da7e0000-0000-4000-8000-000000000004");
    public static readonly UUID Group1 = UUID.Parse("9a009000-0000-4000-8000-000000000005");

    public sealed class OarScene
    {
        public TestScene Scene;
        public ArchiverModule Archiver;
        public IRegionArchiverModule Api => Scene.RequestModuleInterface<IRegionArchiverModule>();
    }

    public static OarScene NewScene(string name = "Test Region", UUID? estateOwner = null, uint x = 1000, uint y = 1000)
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        TestScene scene = new SceneHelpers().SetupScene(name, UUID.Random(), x, y);
        if (estateOwner.HasValue)
            scene.RegionInfo.EstateSettings.EstateOwner = estateOwner.Value;
        ArchiverModule archiver = new ArchiverModule();
        SceneHelpers.SetupSceneModules(scene, new SerialiserModule(), archiver, new TerrainModule());
        StoreDefaultTexture(scene.AssetService);
        return new OarScene { Scene = scene, Archiver = archiver };
    }

    public static SceneObjectGroup AddObject(TestScene scene, string name, UUID creator, UUID owner, int parts = 1, int uuidTail = 0x100, string creatorData = null, int? creationDate = null)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(parts, owner, name + "-", uuidTail);
        sog.Name = name;
        foreach (SceneObjectPart p in sog.Parts)
        {
            p.CreatorID = creator;
            p.LastOwnerID = owner;
            if (creatorData != null)
                p.CreatorData = creatorData;
            if (creationDate.HasValue)
                p.CreationDate = creationDate.Value;
        }
        sog.AbsolutePosition = new Vector3(20 + (uuidTail & 0x3f), 30, 25);
        scene.AddNewSceneObject(sog, false);
        return sog;
    }

    public static TaskInventoryItem AddPrimItem(SceneObjectPart part, string name, UUID assetId, AssetType type, UUID creator, UUID owner, int? date = null, string creatorData = null)
    {
        TaskInventoryItem item = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = assetId,
            Name = name,
            Type = (int)type,
            InvType = type == AssetType.LSLText ? (int)InventoryType.LSL : (int)InventoryType.Object,
            CreatorID = creator,
            OwnerID = owner,
            LastOwnerID = owner,
            ParentID = part.UUID,
            ParentPartID = part.UUID,
            BasePermissions = 0x7fffffff,
            CurrentPermissions = 0x7fffffff,
            NextPermissions = 0x82000,
            EveryonePermissions = 0,
            GroupPermissions = 0
        };
        if (creatorData != null)
            item.CreatorData = creatorData;
        if (date.HasValue)
            item.CreationDate = (uint)date.Value;
        part.Inventory.AddInventoryItem(item, false);
        return item;
    }

    /// <summary>A grid always has the default prim texture; without it every saved object would name a missing asset.</summary>
    public static void StoreDefaultTexture(IAssetService assets) =>
        assets.Store(AssetHelpers.CreateAsset(UUID.Parse("89556747-24cb-43ed-920b-47caed15465f"), AssetType.Texture, new byte[64], UUID.Zero));

    public static AssetBase StoreAsset(IAssetService assets, UUID id, AssetType type, byte[] data, UUID creator)
    {
        AssetBase a = AssetHelpers.CreateAsset(id, type, data, creator);
        assets.Store(a);
        return a;
    }

    public static byte[] SaveOar(OarScene s, Dictionary<string, object> options = null)
    {
        using MemoryStream ms = new MemoryStream();
        s.Api.ArchiveRegion(ms, Guid.NewGuid(), options ?? new Dictionary<string, object>());
        return ms.ToArray();
    }

    public static ArchiveReport Inspect(byte[] archive, InspectorOptions options = null)
    {
        using MemoryStream ms = new MemoryStream(archive);
        return new ArchiveInspector(options).Inspect(ms);
    }

    public static string Dump(ArchiveReport r) =>
        "status=" + r.Status + " stop=" + r.StopReason + " at=" + r.StoppedAt + " entries=" + r.Archive.EntryCount +
        Environment.NewLine + "problems: " + string.Join(" | ", r.Problems.Select(p => p.Entry + ": " + p.Message)) +
        Environment.NewLine + "assets: " + string.Join(" | ", r.Assets.Select(a => a.Entry + " type=" + a.TypeName + " read=" + a.BodyRead));

    // ------------------------------------------------------------------ hand-made tar archives

    public static byte[] Tar(IEnumerable<(string name, byte[] data)> entries, bool gzip = true)
    {
        using MemoryStream raw = new MemoryStream();
        TarArchiveWriter w = new TarArchiveWriter(raw);
        foreach ((string name, byte[] data) in entries)
            w.WriteFile(name, data);
        w.Close();
        byte[] tar = raw.ToArray();
        if (!gzip)
            return tar;

        using MemoryStream gz = new MemoryStream();
        using (GZipStream z = new GZipStream(gz, CompressionLevel.Fastest, true))
            z.Write(tar, 0, tar.Length);
        return gz.ToArray();
    }

    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    public static byte[] OarControl(string version = "0.8") =>
        Utf8("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<archive major_version=\"" + version.Split('.')[0] + "\" minor_version=\"" + version.Split('.')[1] + "\">" +
             "<creation_info><datetime>1700000000</datetime><id>" + UUID.Random() + "</id></creation_info><assets_included>True</assets_included>" +
             "<region_info><size_in_meters>256,256</size_in_meters></region_info></archive>");

    public static byte[] IarControl(string version = "0.3") =>
        Utf8("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<archive major_version=\"" + version.Split('.')[0] + "\" minor_version=\"" + version.Split('.')[1] + "\"><assets_included>True</assets_included></archive>");

    public static string IarItemXml(string name, UUID id, UUID creator, UUID owner, UUID asset, int assetType, int date = 1700000000, string creatorUuidText = null, string creatorData = null, string creatorIdText = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<InventoryItem>");
        sb.Append("<Name>").Append(System.Security.SecurityElement.Escape(name)).Append("</Name>");
        sb.Append("<ID>").Append(id).Append("</ID><InvType>").Append(assetType == 6 ? 6 : 7).Append("</InvType>");
        sb.Append("<CreatorUUID>").Append(System.Security.SecurityElement.Escape(creatorUuidText ?? creator.ToString())).Append("</CreatorUUID>");
        sb.Append("<CreationDate>").Append(date).Append("</CreationDate><Owner>").Append(owner).Append("</Owner><Description></Description>");
        sb.Append("<AssetType>").Append(assetType).Append("</AssetType><AssetID>").Append(asset).Append("</AssetID>");
        sb.Append("<SaleType>0</SaleType><SalePrice>0</SalePrice><BasePermissions>2147483647</BasePermissions><CurrentPermissions>2147483647</CurrentPermissions>");
        sb.Append("<EveryOnePermissions>0</EveryOnePermissions><NextPermissions>581632</NextPermissions><Flags>0</Flags>");
        sb.Append("<GroupID>").Append(UUID.Zero).Append("</GroupID><GroupOwned>False</GroupOwned>");
        if (creatorData != null)
            sb.Append("<CreatorData>").Append(System.Security.SecurityElement.Escape(creatorData)).Append("</CreatorData>");
        if (creatorIdText != null)
            sb.Append("<CreatorID>").Append(creatorIdText).Append("</CreatorID>");
        sb.Append("</InventoryItem>");
        return sb.ToString();
    }
}
