/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Avatar.GameControl;

/// <summary>
/// Takes the region's game-controller events (<see cref="EventManager.OnGameControlData"/>) and posts the
/// game_control event to the scripts that may hear them, through <see cref="IScriptModule"/>, so any script engine
/// can take it.
/// </summary>
/// <remarks>
/// A non-shared region module. When game control is off for the region (<see cref="Scene.GameControlEnabled"/>,
/// from <see cref="GameControlConfig"/>) it subscribes to nothing.
///
/// Rules (Second Life wiki, "game_control" and "llGetGameControlMode"): game-control input is delivered only to
/// scripts the avatar has granted PERMISSION_GAME_CONTROL, and the permission is granted without a dialog to a script
/// in an attachment the avatar wears or in an object the avatar sits on. A script gets a state when its item holds
/// the bit with the avatar as granter. The candidates are
/// <list type="number">
/// <item>the scripts of the object the avatar sits on,</item>
/// <item>the scripts of the avatar's worn attachments, and</item>
/// <item>scripts anywhere in the region that hold a grant from the avatar (the remote-control case): found through
/// an index of the grants. The index is built from the items' grants when an object comes into the region (a region
/// start loads the objects the same way; so does a rez, a copy or a crossing) and when the module starts, and from
/// the grants the engines report (<see cref="EventManager.OnScriptPermissionsGranted"/>); an entry goes when its
/// object leaves, when its grant is taken (<see cref="EventManager.OnScriptPermissionsRemoved"/>) or when the grant
/// passes to another avatar.</item>
/// </list>
/// Each candidate is checked against its item before a post, so a grant that has ended since is not served, and a
/// script that appears in more than one list gets each state once.
///
/// The event is game_control(key id, integer button_levels, list axes): the avatar's key as a string, the canonical
/// button mask, and the six canonical axes as floats. The <see cref="GameControlSnapshot"/> itself rides with the
/// post as the event's data (<see cref="IScriptModule.PostScriptEvent(UUID, string, object[], object)"/>), for the
/// engine to answer llGetGameControlMode, llGetGameControlModeAxes and llGetGameControlModeButtons from.
/// </remarks>
public sealed class GameControlRouterModule : INonSharedRegionModule
{
    /// <summary>PERMISSION_GAME_CONTROL.</summary>
    public const int PermissionGameControl = 0x100000;

    public const string EventName = "game_control";

    private Scene m_scene;
    private bool m_active;

    // Grants by granting avatar: item id -> part id. The index of the scripts that are neither worn nor sat on.
    // Hints only: every use checks the item. Written under m_gate; read without it.
    private readonly object m_gate = new();
    private readonly ConcurrentDictionary<UUID, ConcurrentDictionary<UUID, UUID>> m_byGranter = new();
    private readonly Dictionary<UUID, UUID> m_granterOfItem = new();

    public string Name => "GameControlRouterModule";

    public Type ReplaceableInterface => null;

    /// <summary>True while the module is subscribed to its region's events.</summary>
    public bool Active => m_active;

    /// <summary>How many script items are indexed as holding the game-control grant of <paramref name="granter"/>.</summary>
    public int GrantIndexCount(UUID granter) => m_byGranter.TryGetValue(granter, out var items) ? items.Count : 0;

    public void Initialise(IConfigSource source)
    {
    }

    public void AddRegion(Scene scene)
    {
        if (!scene.GameControlEnabled)
            return;

        m_scene = scene;
        EventManager events = scene.EventManager;
        events.OnGameControlData += OnGameControlData;
        events.OnScriptPermissionsGranted += OnScriptPermissionsGranted;
        events.OnScriptPermissionsRemoved += OnScriptPermissionsRemoved;
        events.OnObjectAddedToScene += OnObjectAddedToScene;
        events.OnObjectBeingRemovedFromScene += OnObjectBeingRemovedFromScene;
        m_active = true;
        IndexRegion();
    }

    public void RegionLoaded(Scene scene)
    {
        // The objects a region start loads reach the scene after the modules are added; index whatever is there now.
        if (m_active)
            IndexRegion();
    }

    public void RemoveRegion(Scene scene)
    {
        if (m_active)
        {
            EventManager events = m_scene.EventManager;
            events.OnGameControlData -= OnGameControlData;
            events.OnScriptPermissionsGranted -= OnScriptPermissionsGranted;
            events.OnScriptPermissionsRemoved -= OnScriptPermissionsRemoved;
            events.OnObjectAddedToScene -= OnObjectAddedToScene;
            events.OnObjectBeingRemovedFromScene -= OnObjectBeingRemovedFromScene;
            m_active = false;
        }
        lock (m_gate)
        {
            m_byGranter.Clear();
            m_granterOfItem.Clear();
        }
        m_scene = null;
    }

    public void Close()
    {
    }

    // ── the index ─────────────────────────────────────────────────────────────────────────────────────────────

    private void IndexRegion()
    {
        foreach (SceneObjectGroup group in m_scene.GetSceneObjectGroups())
            IndexGroup(group);
    }

    private void IndexGroup(SceneObjectGroup group)
    {
        if (group is null || group.IsDeleted)
            return;
        foreach (SceneObjectPart part in group.Parts)
        {
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
            {
                if (item.PermsGranter.IsNotZero() && (item.PermsMask & PermissionGameControl) != 0)
                    Index(part.UUID, item.ItemID, item.PermsGranter);
            }
        }
    }

    private void Index(UUID partId, UUID itemId, UUID granterId)
    {
        lock (m_gate)
        {
            if (m_granterOfItem.TryGetValue(itemId, out UUID previous) && previous != granterId
                && m_byGranter.TryGetValue(previous, out var old))
                old.TryRemove(itemId, out _);
            m_granterOfItem[itemId] = granterId;
            m_byGranter.GetOrAdd(granterId, _ => new ConcurrentDictionary<UUID, UUID>())[itemId] = partId;
        }
    }

    private void Unindex(UUID itemId)
    {
        lock (m_gate)
        {
            if (m_granterOfItem.Remove(itemId, out UUID granterId) && m_byGranter.TryGetValue(granterId, out var items))
                items.TryRemove(itemId, out _);
        }
    }

    private void OnScriptPermissionsGranted(UUID partId, UUID itemId, UUID granterId, int mask)
    {
        if (granterId.IsZero() || (mask & PermissionGameControl) == 0)
        {
            // A new grant to this item that does not hold the bit replaces the one the index knew.
            Unindex(itemId);
            return;
        }
        Index(partId, itemId, granterId);
    }

    private void OnScriptPermissionsRemoved(UUID partId, UUID itemId, UUID granterId, int removedMask)
    {
        if ((removedMask & PermissionGameControl) != 0)
            Unindex(itemId);
    }

    private void OnObjectAddedToScene(SceneObjectGroup group) => IndexGroup(group);

    private void OnObjectBeingRemovedFromScene(SceneObjectGroup group)
    {
        foreach (SceneObjectPart part in group.Parts)
        {
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                Unindex(item.ItemID);
        }
    }

    // ── delivery ──────────────────────────────────────────────────────────────────────────────────────────────

    private void OnGameControlData(UUID agentId, GameControlSnapshot snapshot)
    {
        ScenePresence sp = m_scene.GetScenePresence(agentId);
        if (sp is null || sp.IsChildAgent)
            return;

        HashSet<UUID> done = new();
        List<TaskInventoryItem> targets = null;

        void Consider(SceneObjectPart part, TaskInventoryItem item)
        {
            if (item.PermsGranter != agentId || (item.PermsMask & PermissionGameControl) == 0 || !done.Add(item.ItemID))
                return;
            (targets ??= new()).Add(item);
        }

        SceneObjectPart seat = sp.ParentPart;
        if (seat is not null)
            ForEachScript(seat.ParentGroup, Consider);

        foreach (SceneObjectGroup attachment in sp.GetAttachments())
            ForEachScript(attachment, Consider);

        if (m_byGranter.TryGetValue(agentId, out var indexed))
        {
            foreach (var (itemId, partId) in indexed)
            {
                SceneObjectPart part = m_scene.GetSceneObjectPart(partId);
                TaskInventoryItem item = part?.Inventory.GetInventoryItem(itemId);
                if (item is null || item.PermsGranter != agentId || (item.PermsMask & PermissionGameControl) == 0)
                {
                    Unindex(itemId);      // gone, or the grant has ended
                    continue;
                }
                Consider(part, item);
            }
        }

        if (targets is null)
            return;

        IScriptModule[] engines = m_scene.RequestModuleInterfaces<IScriptModule>();
        if (engines.Length == 0)
            return;

        foreach (TaskInventoryItem item in targets)
        {
            // The arguments are built for each post: an engine may keep or change what it is given.
            object[] args =
            {
                agentId.ToString(),
                (int)snapshot.RawButtons,
                Array.ConvertAll(snapshot.RawAxes, a => (object)a),
            };
            foreach (IScriptModule engine in engines)
            {
                if (engine.HasScript(item.ItemID, out _))
                {
                    engine.PostScriptEvent(item.ItemID, EventName, args, snapshot);
                    break;
                }
            }
        }
    }

    private static void ForEachScript(SceneObjectGroup group, Action<SceneObjectPart, TaskInventoryItem> action)
    {
        if (group is null || group.IsDeleted)
            return;
        foreach (SceneObjectPart part in group.Parts)
        {
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                action(part, item);
        }
    }
}
