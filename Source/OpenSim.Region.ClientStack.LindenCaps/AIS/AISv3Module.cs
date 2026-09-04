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
    /// <summary>The library cap. Same handler, library owner as the agent, mutations refused (John's Phase 1 ruling; supersedes A-D3).</summary>
    public const string LibraryCapName = "LibraryAPIv3";
    public const string ConfigSection = "AIS";

    public bool Enabled { get; private set; }
    private readonly List<Scene> m_scenes = new();
    private IInventoryService m_inventoryService;
    private ILibraryService m_libraryService;

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
        m_libraryService ??= scene.LibraryService;
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
            if (m_scenes.Count == 0) { m_inventoryService = null; m_libraryService = null; }
        }
    }

    /// <summary>
    /// Both caps are registered together when enabled, and neither when disabled (tree state T1: a cap reaches
    /// the viewer only if it is registered under its exact name and the viewer asked for it; the viewer asks for
    /// both, `llaisapi.cpp:72-76`). LibraryAPIv3 runs the same handler over the library service with the library
    /// owner as its agent id, and refuses every mutation with 405.
    /// </summary>
    private void RegisterCaps(UUID agentID, Caps caps)
    {
        caps.RegisterSimpleHandler(CapName, new AisHandler("/" + UUID.Random(), agentID, new InventoryServiceBackend(m_inventoryService)));

        if (m_libraryService is null)
        {
            m_log.LogWarning("[AIS]: no library service on this region; {Cap} not registered", LibraryCapName);
            return;
        }
        var libraryOwner = LibraryOwnerOf(m_libraryService);
        // COPY reads from the library and writes into the agent's inventory, so the library handler carries both
        // sides: itself as the source, the agent's inventory as the destination.
        caps.RegisterSimpleHandler(LibraryCapName,
            new AisHandler("/" + UUID.Random(), libraryOwner, new LibraryServiceBackend(m_libraryService), AisMode.Library,
                new InventoryServiceBackend(m_inventoryService), agentID));
    }

    /// <summary>
    /// The library's owner, as the tree defines it: <c>ILibraryService.LibraryRootFolder.Owner</c>, set by
    /// <c>LibraryService</c> to <c>Constants.m_MrOpenSimID</c> for the root folder and every library folder and
    /// item (<c>Source/OpenSim.Services.InventoryService/LibraryService.cs:50, 100, 115-116, 176, 199-200</c>).
    /// Read off the service rather than hardcoded, so a grid that supplies its own library owner still works.
    /// </summary>
    public static UUID LibraryOwnerOf(ILibraryService library) => library?.LibraryRootFolder?.Owner ?? UUID.Zero;

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
        public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId)
            => m_service.GetFolderContent(agentId, folderId)?.Folders ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>();
        public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId)
            => m_service.GetInventorySkeleton(agentId) ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>();
        public InventoryItemBase GetItem(UUID agentId, UUID itemId) => m_service.GetItem(agentId, itemId);
        public bool AddFolder(InventoryFolderBase folder) => m_service.AddFolder(folder);
        public bool AddItem(InventoryItemBase item) => m_service.AddItem(item);
        public bool UpdateItem(InventoryItemBase item) => m_service.UpdateItem(item);
        public bool UpdateFolder(InventoryFolderBase folder) => m_service.UpdateFolder(folder);
        public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds) => m_service.DeleteItems(agentId, new List<UUID>(itemIds));
        public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash) => m_service.DeleteFolders(agentId, new List<UUID>(folderIds), onlyIfTrash);
        public bool PurgeFolder(InventoryFolderBase folder) => m_service.PurgeFolder(folder);
    }

    /// <summary>
    /// The LibraryAPIv3 backend: the shared library over <see cref="ILibraryService"/>, which holds the whole
    /// tree in memory (<c>GetAllFolders</c>, <c>InventoryFolderImpl.RequestListOfFolders/RequestListOfItems</c>).
    /// Read-only by construction — every mutator returns false and the handler answers 405 before reaching them
    /// (John's Phase 1 ruling). The agent id is the library owner, so the same handler code needs no library
    /// special case beyond its mode.
    /// </summary>
    public sealed class LibraryServiceBackend : IAisInventoryBackend
    {
        private readonly ILibraryService m_library;
        public LibraryServiceBackend(ILibraryService library) { m_library = library ?? throw new ArgumentNullException(nameof(library)); }

        private InventoryFolderImpl Folder(UUID folderId)
        {
            var root = m_library.LibraryRootFolder;
            if (root is null) return null;
            if (root.ID.Equals(folderId)) return root;
            return m_library.GetAllFolders().TryGetValue(folderId, out var folder) ? folder : null;
        }

        /// <summary>The library has no per-agent system folders; only its root is addressable by type.</summary>
        public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type)
            => type == FolderType.Root ? m_library.LibraryRootFolder : null;

        public InventoryFolderBase GetFolder(UUID agentId, UUID folderId) => Folder(folderId);

        public InventoryCollection GetFolderContent(UUID agentId, UUID folderId)
        {
            var folder = Folder(folderId);
            if (folder is null) return null;
            return new InventoryCollection
            {
                OwnerID = folder.Owner,
                FolderID = folder.ID,
                Version = folder.Version,
                Folders = folder.RequestListOfFolders(),
                Items = folder.RequestListOfItems(),
            };
        }

        public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId)
            => Folder(folderId)?.RequestListOfFolders() ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>();

        public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId)
        {
            var all = m_library.GetAllFolders();
            var list = new List<InventoryFolderBase>(all.Count);
            foreach (var folder in all.Values) list.Add(folder);
            return list;
        }

        public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds)
        {
            var ids = new UUID[itemIds.Count];
            for (var i = 0; i < ids.Length; i++) ids[i] = itemIds[i];
            return m_library.GetMultipleItems(ids) ?? Array.Empty<InventoryItemBase>();
        }

        public InventoryItemBase GetItem(UUID agentId, UUID itemId) => m_library.GetItem(itemId);

        // read-only: the handler answers 405 for every mutation before it reaches these (AisMode.Library)
        public bool AddFolder(InventoryFolderBase folder) => false;
        public bool AddItem(InventoryItemBase item) => false;
        public bool UpdateItem(InventoryItemBase item) => false;
        public bool UpdateFolder(InventoryFolderBase folder) => false;
        public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds) => false;
        public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash) => false;
        public bool PurgeFolder(InventoryFolderBase folder) => false;
    }
}
