/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// Takes the region's game-controller events (<see cref="EventManager.OnGameControlData"/>) and posts the
/// game_control event to the scripts that may hear them, through <see cref="IScriptModule"/>, so any script engine
/// can take it.
/// </summary>
/// <remarks>
/// Rules (Second Life wiki, "game_control" and "llGetGameControlMode"): game-control input is delivered only to
/// scripts the avatar has granted PERMISSION_GAME_CONTROL, and the permission is granted without a dialog to a script
/// in an attachment the avatar wears or in an object the avatar sits on. A script gets a state when its item holds
/// the bit with the avatar as granter. The candidates are
/// <list type="number">
/// <item>the scripts of the object the avatar sits on,</item>
/// <item>the scripts of the avatar's worn attachments, and</item>
/// <item>scripts anywhere in the region whose engine reported a grant from the avatar
/// (<see cref="EventManager.OnScriptPermissionsGranted"/>; the remote-control case).</item>
/// </list>
/// Each candidate is checked against its item before a post, so a grant that has ended since is not served, and a
/// script that appears in more than one list gets each state once.
///
/// The event is game_control(key id, integer button_levels, list axes): the avatar's key as a string, the canonical
/// button mask, and the six canonical axes as floats. The <see cref="GameControlSnapshot"/> itself rides with the
/// post as the event's data (<see cref="IScriptModule.PostScriptEvent(UUID, string, object[], object)"/>), for the
/// engine to answer llGetGameControlMode, llGetGameControlModeAxes and llGetGameControlModeButtons from.
/// </remarks>
public sealed class GameControlRouter
{
    /// <summary>PERMISSION_GAME_CONTROL.</summary>
    public const int PermissionGameControl = 0x100000;

    public const string EventName = "game_control";

    private readonly Scene m_scene;

    // Items an engine reported a grant for, by granting avatar: item id -> part id. Hints only; every use checks the item.
    private readonly ConcurrentDictionary<UUID, ConcurrentDictionary<UUID, UUID>> m_reported = new();

    public GameControlRouter(Scene scene)
    {
        m_scene = scene;
        scene.EventManager.OnGameControlData += OnGameControlData;
        scene.EventManager.OnScriptPermissionsGranted += OnScriptPermissionsGranted;
    }

    private void OnScriptPermissionsGranted(UUID partId, UUID itemId, UUID granterId, int mask)
    {
        if (granterId.IsZero() || (mask & PermissionGameControl) == 0)
            return;
        m_reported.GetOrAdd(granterId, _ => new ConcurrentDictionary<UUID, UUID>())[itemId] = partId;
    }

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

        if (m_reported.TryGetValue(agentId, out var reported))
        {
            foreach (var (itemId, partId) in reported)
            {
                SceneObjectPart part = m_scene.GetSceneObjectPart(partId);
                TaskInventoryItem item = part?.Inventory.GetInventoryItem(itemId);
                if (item is null || item.PermsGranter != agentId || (item.PermsMask & PermissionGameControl) == 0)
                {
                    reported.TryRemove(itemId, out _);      // gone, or the grant has ended
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
