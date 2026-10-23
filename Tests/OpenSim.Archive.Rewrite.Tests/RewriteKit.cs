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
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Archive.Rewrite.Tests;

/// <summary>Tests that touch the process-wide scene manager and fire-and-forget mode run one at a time.</summary>
[CollectionDefinition("scene")]
public class SceneCollection { }

/// <summary>
/// One part as the archive loader leaves it in a scene: everything the rewrite must not disturb. The loader gives every
/// object new ids, so ids are left out and a part is told apart by its group's name and position and its link number.
/// </summary>
public sealed record PartRecord(string GroupName, string Position, int LinkNum, string Name, string Description, int Faces, string Items);

internal static class RewriteKit
{
    public static readonly UUID Owner = UUID.Parse("a11ce000-0000-4000-8000-000000000001");
    public static readonly UUID DefaultTexture = UUID.Parse("89556747-24cb-43ed-920b-47caed15465f");

    public static TestScene NewScene(string name = "Test Region")
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        TestScene scene = new SceneHelpers().SetupScene(name, UUID.Random(), 1000, 1000);
        SceneHelpers.SetupSceneModules(scene, new SerialiserModule(), new ArchiverModule(), new TerrainModule());
        scene.AssetService.Store(AssetHelpers.CreateAsset(DefaultTexture, AssetType.Texture, new byte[64], UUID.Zero));
        return scene;
    }

    public static IRegionArchiverModule Api(Scene s) => s.RequestModuleInterface<IRegionArchiverModule>();

    /// <summary>A scene with a three-part linkset (one part with contents) and a single prim.</summary>
    public static byte[] SaveSampleOar(out List<PartRecord> expected, out Dictionary<(string group, int link), Guid> partIds)
    {
        TestScene scene = NewScene("Source Region");
        SceneObjectGroup linkset = SceneHelpers.CreateSceneObject(3, Owner, "Tower-", 0x300);
        linkset.Name = "Tower";
        int i = 0;
        foreach (SceneObjectPart p in linkset.Parts.OrderBy(x => x.LinkNum))
        {
            p.CreatorID = Owner;
            p.Description = "part " + (++i) + " of the tower";
        }
        linkset.AbsolutePosition = new Vector3(40, 40, 25);
        scene.AddNewSceneObject(linkset, false);

        SceneObjectPart withItems = linkset.Parts.OrderBy(x => x.LinkNum).ElementAt(1);
        UUID noteAsset = UUID.Random();
        scene.AssetService.Store(AssetHelpers.CreateAsset(noteAsset, AssetType.Notecard,
            Encoding.ASCII.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n"), Owner));
        AddItem(withItems, "Read me", noteAsset, AssetType.Notecard);
        AddItem(withItems, "Other note", noteAsset, AssetType.Notecard);

        SceneObjectGroup single = SceneHelpers.CreateSceneObject(1, Owner, "Lamp-", 0x400);
        single.Name = "Lamp";
        single.AbsolutePosition = new Vector3(60, 40, 25);
        scene.AddNewSceneObject(single, false);

        using MemoryStream ms = new MemoryStream();
        Api(scene).ArchiveRegion(ms, Guid.NewGuid(), new Dictionary<string, object>());
        expected = Snapshot(scene);
        partIds = new Dictionary<(string, int), Guid>();
        foreach (SceneObjectGroup g in scene.GetSceneObjectGroups())
            foreach (SceneObjectPart p in g.Parts)
                partIds[(g.Name, p.LinkNum)] = p.UUID.Guid;
        return ms.ToArray();
    }

    private static void AddItem(SceneObjectPart part, string name, UUID asset, AssetType type)
    {
        part.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = asset, Name = name, Type = (int)type, InvType = (int)InventoryType.Notecard,
            CreatorID = Owner, OwnerID = Owner, LastOwnerID = Owner, ParentID = part.UUID, ParentPartID = part.UUID,
            BasePermissions = 0x7fffffff, CurrentPermissions = 0x7fffffff, NextPermissions = 0x82000
        }, false);
    }

    /// <summary>Loads an OAR into a new scene and lists every part: link number, names, contents and face count.</summary>
    public static List<PartRecord> LoadAndSnapshot(Stream oar)
    {
        TestScene scene = NewScene("Load Region");
        SceneManager.Instance.Scenes.Clear();
        SceneManager.Instance.Add(scene);
        Api(scene).DearchiveRegion(Plain(oar), Guid.NewGuid(), new Dictionary<string, object>());
        return Snapshot(scene);
    }

    /// <summary>The loader reads a stream as a plain tar (only a file path is gunzipped), so a gzip stream is unwrapped first.</summary>
    public static Stream Plain(Stream archive)
    {
        if (!archive.CanSeek)
            return archive;
        bool gzip = archive.ReadByte() == 0x1f && archive.ReadByte() == 0x8b;
        archive.Seek(0, SeekOrigin.Begin);
        return gzip ? new GZipStream(archive, CompressionMode.Decompress) : archive;
    }

    public static List<PartRecord> Snapshot(Scene scene)
    {
        List<PartRecord> all = new List<PartRecord>();
        foreach (SceneObjectGroup g in scene.GetSceneObjectGroups())
            foreach (SceneObjectPart p in g.Parts)
            {
                string items = string.Join(";", p.Inventory.GetInventoryList()
                    .Select(id => p.Inventory.GetInventoryItem(id))
                    .Select(it => it.Name + ":" + it.Type + ":" + it.AssetID)
                    .OrderBy(s => s, StringComparer.Ordinal));
                all.Add(new PartRecord(g.Name, g.AbsolutePosition.ToString(), p.LinkNum, p.Name, p.Description, p.GetNumberOfSides(), items));
            }
        return all.OrderBy(r => r.GroupName, StringComparer.Ordinal).ThenBy(r => r.Position, StringComparer.Ordinal).ThenBy(r => r.LinkNum)
            .ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static List<(string name, byte[] data)> ReadEntries(byte[] archive)
    {
        using MemoryStream ms = new MemoryStream(archive);
        return ReadEntries(ms);
    }

    public static List<(string name, byte[] data)> ReadEntries(Stream archive)
    {
        List<(string, byte[])> list = new List<(string, byte[])>();
        // The archive saver's output may be a plain tar or gzip; the rewriter's output is always gzip.
        BufferedStream buffered = new BufferedStream(archive);
        bool gzip = buffered.ReadByte() == 0x1f && buffered.ReadByte() == 0x8b;
        archive.Seek(0, SeekOrigin.Begin);
        Stream body = gzip ? new GZipStream(archive, CompressionMode.Decompress) : archive;
        TarStreamReader tar = new TarStreamReader(body);
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
            list.Add((h.Name, tar.ReadBody()));
        return list;
    }

    /// <summary>A path in the test run's scratch folder (TEMP as the session sets it).</summary>
    public static string TempFile(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "archive-rewrite-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + suffix);
    }
}
