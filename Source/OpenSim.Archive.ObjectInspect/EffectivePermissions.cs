/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ObjectInspect;

/// <summary>
/// What the region makes of an object's permissions from its root part, the contents of all its parts and the base mask. The owner,
/// group and everyone rules follow SceneObjectGroup.AggregatePerms with SceneObjectPart.AggregateInnerPerms and
/// SceneObjectPartInventory.AggregateInnerPerms (Source/OpenSim.Region.Framework/Scenes): the root part's masks are the object's masks,
/// the child parts' own masks do not count, every item in every part narrows the result, a landmark is ignored, and modify comes only
/// from the root part. The next owner rule follows SceneObjectGroup.CurrentAndFoldedNextPermissions and
/// SceneObjectPartInventory.MaskEffectivePermissions: copy and transfer pass outwards from an item whose current and next owner masks lack them.
/// </summary>
public static class EffectivePermissions
{
    private const uint Transfer = 0x2000, Modify = 0x4000, Copy = 0x8000, Export = 0x10000, Move = 0x80000;
    private const uint AllEffective = Transfer | Modify | Copy | Export | Move;
    private const uint MoveModify = Move | Modify;
    private const uint CopyTransfer = Copy | Transfer;
    private const int LandmarkInvType = 3;
    // 30 April 2017: the date AggregatePerms uses to tell old objects from new ones
    private const int NewObjectDate = 1493574994;

    private static readonly (uint bit, string word)[] Bits = { (Modify, "modify"), (Copy, "copy"), (Transfer, "transfer") };

    public static EffectivePerms Compute(ObjectEntry o)
    {
        EffectivePerms result = new EffectivePerms();
        if (o.Parts.Count == 0)
            return result;

        PartEntry root = o.Parts.FirstOrDefault(p => p.IsRoot) ?? o.Parts[0];
        uint rootBase = root.Masks.Base ?? 0, rootOwner = root.Masks.Owner ?? 0, rootGroup = root.Masks.Group ?? 0;
        uint rootEveryone = root.Masks.Everyone ?? 0, rootNext = root.Masks.NextOwner ?? 0;

        uint basePerms = (rootBase & AllEffective) | Move;
        bool noBaseTransfer = (basePerms & Transfer) == 0;
        bool newObject = !root.CreationDate.HasValue || root.CreationDate.Value == 0 || root.CreationDate.Value > NewObjectDate;

        uint owner = rootOwner, group = rootGroup, everyone = rootEveryone;
        foreach (PartEntry part in o.Parts)
        {
            uint innerOwner = AllEffective, innerGroup = AllEffective, innerEveryone = AllEffective;
            foreach (ItemEntry item in part.Items.Where(CountsForPermissions))
            {
                innerOwner &= item.Masks.Owner ?? 0;
                innerGroup &= item.Masks.Group ?? 0;
                innerEveryone &= item.Masks.Everyone ?? 0;
            }
            owner &= innerOwner;
            group &= innerGroup;
            if (newObject)
                everyone &= innerEveryone;
        }

        // the root part's modify and move come back, and an object that kept neither copy nor transfer keeps transfer
        owner |= rootOwner & MoveModify;
        if ((owner & CopyTransfer) == 0)
            owner |= Transfer;
        owner &= basePerms;
        uint ownerTransfer = owner & Transfer;

        group |= rootGroup & MoveModify;
        if (noBaseTransfer)
            group &= ~Copy;
        else
            group |= ownerTransfer;

        everyone |= rootEveryone & Move;
        everyone &= ~Modify;
        if (noBaseTransfer)
            everyone &= ~Copy;
        else
            everyone |= ownerTransfer;

        result.Owner = owner;
        result.Group = group & owner;
        result.Everyone = everyone & owner;

        // the next owner: the root part's next owner mask, within what the owner has, and within what every item passes on
        uint next = rootNext & owner & (Modify | Copy | Transfer);
        uint ownerMask = rootOwner;
        foreach (PartEntry part in o.Parts)
        {
            ownerMask &= part.Masks.Base ?? 0;
            foreach (ItemEntry item in part.Items.Where(CountsForPermissions))
            {
                uint passed = (item.Masks.Owner ?? 0) & (item.Masks.NextOwner ?? 0);
                if ((passed & Copy) == 0)
                    next &= ~Copy;
                if ((passed & Transfer) == 0)
                    next &= ~Transfer;
            }
        }
        next &= ownerMask | ~(Modify | Copy | Transfer);
        result.NextOwner = next;

        Explain(o, root, result, rootBase, rootOwner, rootNext);
        return result;
    }

    private static bool CountsForPermissions(ItemEntry item) => item.InvType != LandmarkInvType;

    private static string Part(PartEntry p) => "part \"" + p.Name + "\"";

    private static string Source(PartEntry part, ItemEntry item) => "item \"" + item.Name + "\" in " + Part(part);

    /// <summary>Names, for each of modify, copy and transfer the owner or the next owner lacks, what takes it away.</summary>
    private static void Explain(ObjectEntry o, PartEntry root, EffectivePerms eff, uint rootBase, uint rootOwner, uint rootNext)
    {
        foreach ((uint bit, string word) in Bits)
        {
            if ((eff.Owner & bit) != 0)
                continue;
            if ((rootBase & bit) == 0)
                eff.RestrictedBy.Add("root " + Part(root) + ": base mask lacks " + word);
            if ((rootOwner & bit) == 0)
                eff.RestrictedBy.Add("root " + Part(root) + ": owner mask lacks " + word);
            if (bit == Modify)
                continue;   // modify does not pass outwards from the contents
            foreach (PartEntry part in o.Parts)
                foreach (ItemEntry item in part.Items.Where(CountsForPermissions).Where(i => ((i.Masks.Owner ?? 0) & bit) == 0))
                    eff.RestrictedBy.Add(Source(part, item) + ": owner mask lacks " + word);
        }

        foreach ((uint bit, string word) in Bits)
        {
            if ((eff.Owner & bit) == 0 || (eff.NextOwner & bit) != 0)
                continue;
            if ((rootNext & bit) == 0)
                eff.RestrictedBy.Add("root " + Part(root) + ": next owner mask lacks " + word + " (next owner)");
            foreach (PartEntry part in o.Parts)
            {
                if (part != root && ((part.Masks.Base ?? 0) & bit) == 0)
                    eff.RestrictedBy.Add(Part(part) + ": base mask lacks " + word + " (next owner)");
                if (bit == Modify)
                    continue;
                foreach (ItemEntry item in part.Items.Where(CountsForPermissions).Where(i => (((i.Masks.Owner ?? 0) & (i.Masks.NextOwner ?? 0)) & bit) == 0))
                    eff.RestrictedBy.Add(Source(part, item) + ": current and next owner masks together lack " + word + " (next owner)");
            }
        }
    }
}
