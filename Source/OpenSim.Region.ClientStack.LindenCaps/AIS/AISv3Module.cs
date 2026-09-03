using System;
using System.Collections.Generic;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// Region-side host for the AIS v3 inventory cap (Ledger A-D1). Config:
/// <code>
/// [AIS]
///     Enabled = false
/// </code>
/// When enabled it registers <c>InventoryAPIv3</c> for every agent from <c>OnRegisterCaps</c> (tree state T1: the
/// cap must be registered on the agent's Caps under that exact name; the viewer requests the name itself). When
/// disabled it registers nothing, so the viewer never sees the cap and keeps its legacy paths (risk A-R1).
/// <c>LibraryAPIv3</c> is deliberately not registered (Ledger A-D3).
/// </summary>
public class AISv3Module : ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AISv3Module));

    public const string CapName = "InventoryAPIv3";
    public const string ConfigSection = "AIS";

    public bool Enabled { get; private set; }
    private readonly List<Scene> m_scenes = new();
    private IInventoryService m_inventoryService;

    public string Name => "AISv3Module";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs[ConfigSection];
        Enabled = config is not null && config.GetBoolean("Enabled", false);
        if (Enabled)
            m_log.LogWarning("[AIS]: InventoryAPIv3 is ENABLED; the LL viewer will route inventory deletes, purges, slams and creates through it (see Docs/feature/ais-v3/AIS-V3-SPEC.md §1g)");
    }

    public void PostInitialise() { }
    public void Close() { }

    public void AddRegion(Scene scene) { }

    public void RegionLoaded(Scene scene)
    {
        if (!Enabled) return;
        m_inventoryService ??= scene.InventoryService;
        if (m_inventoryService is null)
        {
            m_log.LogError("[AIS]: region {Region} has no inventory service; InventoryAPIv3 not registered", scene.Name);
            return;
        }
        lock (m_scenes) m_scenes.Add(scene);
        scene.EventManager.OnRegisterCaps += RegisterCaps;
    }

    public void RemoveRegion(Scene scene)
    {
        if (!Enabled) return;
        scene.EventManager.OnRegisterCaps -= RegisterCaps;
        lock (m_scenes)
        {
            m_scenes.Remove(scene);
            if (m_scenes.Count == 0) m_inventoryService = null;
        }
    }

    private void RegisterCaps(UUID agentID, Caps caps)
    {
        var backend = new InventoryServiceBackend(m_inventoryService);
        var handler = new AisHandler("/" + UUID.Random(), agentID, backend);
        caps.RegisterSimpleHandler(CapName, handler);
    }

    /// <summary>
    /// Phase 1 backend: a thin pass-through over the region's <c>IInventoryService</c>. Nothing here knows about
    /// scenes. A0 wires it; the handler does not call it yet.
    /// </summary>
    public sealed class InventoryServiceBackend : IAisInventoryBackend
    {
        private readonly IInventoryService m_service;
        public InventoryServiceBackend(IInventoryService service) { m_service = service ?? throw new ArgumentNullException(nameof(service)); }

        public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type) => m_service.GetFolderForType(agentId, type);
        public InventoryFolderBase GetFolder(UUID agentId, UUID folderId) => m_service.GetFolder(agentId, folderId);
        public InventoryCollection GetFolderContent(UUID agentId, UUID folderId) => m_service.GetFolderContent(agentId, folderId);
        public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds)
        {
            var ids = new UUID[itemIds.Count];
            for (var i = 0; i < ids.Length; i++) ids[i] = itemIds[i];
            return m_service.GetMultipleItems(agentId, ids) ?? Array.Empty<InventoryItemBase>();
        }
        public InventoryItemBase GetItem(UUID agentId, UUID itemId) => m_service.GetItem(agentId, itemId);
        public bool AddFolder(InventoryFolderBase folder) => m_service.AddFolder(folder);
        public bool AddItem(InventoryItemBase item) => m_service.AddItem(item);
        public bool UpdateItem(InventoryItemBase item) => m_service.UpdateItem(item);
        public bool UpdateFolder(InventoryFolderBase folder) => m_service.UpdateFolder(folder);
        public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds) => m_service.DeleteItems(agentId, new List<UUID>(itemIds));
        public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds) => m_service.DeleteFolders(agentId, new List<UUID>(folderIds));
        public bool PurgeFolder(InventoryFolderBase folder) => m_service.PurgeFolder(folder);
    }
}
