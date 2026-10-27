/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.World.NPC;

/// <summary>
/// An avatar's appearance as a notecard in an object's contents, in the format osNpcSaveAppearance writes: the
/// notecard wrapper ("Linden text version 2") around the LLSD XML of <see cref="AvatarAppearance.PackForNotecard"/>,
/// in an item named as the script asked, replacing an item of that name. That writer leaves the wearable lists
/// empty, because it saves the baked textures. A bot that is baked by the region needs the wearables, so they are
/// added here under the keys <see cref="AvatarAppearance.Pack"/> uses and <see cref="AvatarAppearance.Unpack"/>
/// reads; a notecard without them (the standard writer's) loads as before, with empty wearables.
/// </summary>
public static class AppearanceNotecard
{
    private const string Header = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length ";

    /// <summary>The appearance as the OSD map that goes into the notecard.</summary>
    public static OSDMap Pack(AvatarAppearance appearance, bool includeHuds)
    {
        OSDMap map = appearance.PackForNotecard(!includeHuds);

        AvatarWearable[] all = appearance.Wearables;
        int first = Math.Min(all.Length, AvatarAppearance.MAXWEARABLE_PV7);
        OSDArray wears = new OSDArray(first);
        for (int i = 0; i < first; i++)
            wears.Add(all[i].Pack());
        map["wearables"] = wears;

        if (all.Length > first)
        {
            OSDArray more = new OSDArray(all.Length - first);
            for (int i = first; i < all.Length; i++)
                more.Add(all[i].Pack());
            map["wrbls8"] = more;
        }
        return map;
    }

    /// <summary>
    /// Writes the appearance into <paramref name="host"/>'s contents as a notecard called <paramref name="name"/>,
    /// replacing one of that name. Returns the item, or null when there is nothing to write to or no name.
    /// </summary>
    public static TaskInventoryItem Save(SceneObjectPart host, IAssetService assets, string name, AvatarAppearance appearance, bool includeHuds)
    {
        if (host is null || assets is null || appearance is null || string.IsNullOrWhiteSpace(name)) return null;

        string body = Util.GetFormattedXml(Pack(appearance, includeHuds) as OSD);
        string text = Header + Encoding.UTF8.GetByteCount(body) + "\n" + body + "}";

        AssetBase asset = new(UUID.Random(), name, (sbyte)AssetType.Notecard, host.OwnerID.ToString())
        {
            Description = "Avatar Appearance",
            Data = Encoding.UTF8.GetBytes(text),
        };
        string stored = assets.Store(asset);
        if (string.IsNullOrEmpty(stored) || !UUID.TryParse(stored, out UUID assetId) || assetId.IsZero()) return null;

        TaskInventoryItem item = new()
        {
            ParentID = host.UUID,
            CreationDate = (uint)Util.UnixTimeSinceEpoch(),
            Name = name,
            Description = "Avatar Appearance",
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard,
            OwnerID = host.OwnerID,
            CreatorID = host.OwnerID,
            BasePermissions = (uint)OpenSim.Framework.PermissionMask.All | (uint)OpenSim.Framework.PermissionMask.Export,
            CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All | (uint)OpenSim.Framework.PermissionMask.Export,
            EveryonePermissions = 0,
            NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
            GroupID = host.GroupID,
            GroupPermissions = 0,
            Flags = 0,
            PermsGranter = UUID.Zero,
            PermsMask = 0,
            AssetID = assetId,
        };
        item.ResetIDs(host.UUID);
        host.Inventory.AddInventoryItemExclusive(item, false);
        host.ParentGroup?.InvalidateDeepEffectivePerms();
        return item;
    }

    /// <summary>
    /// Reads an appearance from a notecard in <paramref name="host"/>'s contents, named or given as an asset id.
    /// On failure the reason says which notecard and why.
    /// </summary>
    public static bool TryLoad(SceneObjectPart host, IAssetService assets, string nameOrAssetId, out AvatarAppearance appearance, out string reason)
    {
        appearance = null;
        reason = null;
        if (host is null || assets is null || string.IsNullOrWhiteSpace(nameOrAssetId))
        {
            reason = "no notecard named";
            return false;
        }

        UUID assetId;
        TaskInventoryItem item = host.Inventory.GetInventoryItem(nameOrAssetId, (int)AssetType.Notecard);
        if (item is not null) assetId = item.AssetID;
        else if (!UUID.TryParse(nameOrAssetId, out assetId))
        {
            reason = $"notecard '{nameOrAssetId}' is not in this object";
            return false;
        }

        AssetBase asset = assets.Get(assetId.ToString());
        if (asset?.Data is null)
        {
            reason = $"notecard '{nameOrAssetId}' has no data";
            return false;
        }

        try
        {
            if (OSDParser.DeserializeLLSDXml(NotecardText(asset.Data)) is OSDMap map
                && (map.ContainsKey("visualparams") || map.ContainsKey("wearables")))
            {
                AvatarAppearance read = new();
                read.Unpack(map);
                appearance = read;
                return true;
            }
        }
        catch (Exception)
        {
            // falls through to the reason below
        }
        reason = $"notecard '{nameOrAssetId}' is not an appearance";
        return false;
    }

    // The text inside the notecard wrapper: the bytes after the "Text length N" line. Text that has no wrapper is
    // taken as it is.
    private static string NotecardText(byte[] data)
    {
        string all = Encoding.UTF8.GetString(data);
        int at = all.IndexOf("Text length ", StringComparison.Ordinal);
        if (at < 0) return all;
        int eol = all.IndexOf('\n', at);
        if (eol < 0) return string.Empty;
        if (!int.TryParse(all.AsSpan(at + "Text length ".Length, eol - at - "Text length ".Length), out int length)) return all[(eol + 1)..];

        byte[] bytes = Encoding.UTF8.GetBytes(all[..(eol + 1)]);
        int start = bytes.Length;
        int count = Math.Max(0, Math.Min(length, data.Length - start));
        return Encoding.UTF8.GetString(data, start, count);
    }
}
