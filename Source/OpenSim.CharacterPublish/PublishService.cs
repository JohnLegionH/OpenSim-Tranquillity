/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.CharacterPublish;

/// <summary>What a publish may do. Every default is the conservative one.</summary>
public sealed class PublishOptions
{
    /// <summary>The most published outfit folders one resident may hold (default 20). A publish past it is refused and writes nothing.</summary>
    public int MaxOutfitsPerResident { get; init; } = 20;

    /// <summary>Size limits of one bundle (assets, bytes per asset, bytes in all).</summary>
    public BundleLimits Limits { get; init; } = new();

    /// <summary>The most items one outfit may hold.</summary>
    public int MaxItems { get; init; } = 32;

    /// <summary>The most links a previous-look folder is given; more Current Outfit entries than this are skipped with a warning.</summary>
    public int MaxPreviousLookLinks { get; init; } = 200;

    /// <summary>Save the resident's current look as a folder of links before the outfit is written (default on). Needs the bundle to carry the plan.</summary>
    public bool SaveCurrentLook { get; init; } = true;

    /// <summary>When set, a bundle whose creator is anyone else is refused (the operator's creator service account).</summary>
    public UUID? RequiredCreator { get; init; }
}

public sealed record PublishRefusal(string Code, string Message);

/// <summary>A write that did not happen. The publish stopped at this step; a retry of the same bundle continues from here.</summary>
public sealed record PublishFailure(string Step, string What, string Message);

/// <summary>A Current Outfit entry that could not be linked into the previous-look folder.</summary>
public sealed record BrokenLink(UUID CurrentOutfitItemId, string Name, string Reason);

/// <summary>What one <see cref="PublishService.Publish(Bundle, IReadOnlyDictionary{UUID, byte[]}, UUID, PublishOptions?)"/> call did.</summary>
public sealed class PublishResult
{
    /// <summary>Derived from the resident and the bundle's contents; the same bundle for the same resident always gives the same id.</summary>
    public UUID PublishId { get; init; }

    public List<PublishRefusal> Refusals { get; } = new();
    public List<PublishFailure> Failures { get; } = new();
    public List<string> Warnings { get; } = new();

    public UUID MyOutfitsFolderId { get; set; }
    public bool MyOutfitsCreated { get; set; }
    public UUID OutfitFolderId { get; set; }
    public bool OutfitFolderCreated { get; set; }

    public List<UUID> AssetsStored { get; } = new();
    public List<UUID> AssetsAlreadyPresent { get; } = new();
    /// <summary>Every item of the bundle, in the bundle's order, whether written now or already there.</summary>
    public List<UUID> ItemIds { get; } = new();
    public List<UUID> ItemsCreated { get; } = new();
    public List<UUID> ItemsAlreadyPresent { get; } = new();

    public UUID PreviousLookFolderId { get; set; }
    public bool PreviousLookFolderCreated { get; set; }
    /// <summary>The links the previous-look folder holds after this call (written now or already there).</summary>
    public List<UUID> PreviousLookLinks { get; } = new();
    public List<UUID> PreviousLookLinksCreated { get; } = new();
    public List<BrokenLink> BrokenLinks { get; } = new();

    public bool Refused => Refusals.Count > 0;

    /// <summary>True when nothing was refused and nothing failed: the outfit (and the previous look, when planned) is all in the inventory.</summary>
    public bool Complete => !Refused && Failures.Count == 0;

    /// <summary>True when the writes failed part way. What was written stays; a retry of the same bundle completes it.</summary>
    public bool Partial => !Refused && Failures.Count > 0;

    /// <summary>Complete, and this call found everything already in place and wrote nothing.</summary>
    public bool AlreadyPublished => Complete && !MyOutfitsCreated && !OutfitFolderCreated && !PreviousLookFolderCreated
        && AssetsStored.Count == 0 && ItemsCreated.Count == 0 && PreviousLookLinksCreated.Count == 0;

    /// <summary>One line a resident or an operator can read.</summary>
    public string Summary
    {
        get
        {
            if (Refused) return "Refused: " + string.Join(" ", Refusals.Select(r => r.Message));
            if (Partial) return $"Partly written, then stopped at {Failures[0].Step} ({Failures[0].Message}). {AssetsStored.Count} assets, {ItemsCreated.Count} items and {PreviousLookLinksCreated.Count} links were written; publishing the same outfit again completes it.";
            if (AlreadyPublished) return "Already published: the outfit is in My Outfits and nothing was written.";
            return $"Published: {AssetsStored.Count} assets stored ({AssetsAlreadyPresent.Count} already on the grid), {ItemsCreated.Count} items written, {PreviousLookLinksCreated.Count} previous-look links written.";
        }
    }
}

/// <summary>
/// Writes a validated publish bundle into a resident's inventory through the grid's asset and inventory services.
///
/// <para>It only ever adds. It calls no delete, update, move or purge method of either service, and it never writes to the
/// resident's Current Outfit folder or to any folder or item that was there before. Nothing is written until the bundle has
/// passed <see cref="BundleValidator"/> and every conflict check, so a refusal leaves no trace.</para>
///
/// <para>Order of writes: assets (harmless if orphaned: an asset id is a hash of its bytes and the asset service keeps the
/// first copy), then My Outfits when it is missing, the outfit folder and its items, then the previous-look folder and its
/// links. Every inventory id comes from the bundle (derived from the resident, the character and the folder number), so a
/// retry writes only what is missing and the same publish twice makes one folder.</para>
///
/// <para>The service interfaces cannot make a write conditional, and both data layers store with "replace into", so an
/// AddItem on an id that exists would overwrite it. The service therefore reads before every write and treats an existing
/// row that differs from the expected one as a conflict, never as something to overwrite.</para>
/// </summary>
public sealed class PublishService
{
    const int AssetLink = BundleTypes.AssetLink;
    const int AssetLinkFolder = 25;
    const int MaxLinkHops = 8;

    // Names the publisher gives outfit folders: "<character> #<number>".
    static readonly Regex PublishedFolderName = new(@"^.+ #[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Publishes for the same resident run one at a time inside this process. A fixed array of locks cannot leak.
    const int LockStripes = 64;
    static readonly object[] s_locks = Enumerable.Range(0, LockStripes).Select(_ => new object()).ToArray();
    static object LockFor(UUID resident) => s_locks[(resident.GetHashCode() & int.MaxValue) % LockStripes];

    readonly IAssetService _assets;
    readonly IInventoryService _inventory;

    public PublishService(IAssetService assets, IInventoryService inventory)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    }

    /// <summary>Publishes a bundle read from a folder; a file in the folder that bundle.json does not list is refused.</summary>
    public PublishResult Publish(BundleFiles.Loaded loaded, UUID residentId, PublishOptions? options = null)
    {
        var result = Publish(loaded.Bundle, loaded.Content, residentId, options, loaded.UnlistedFiles);
        return result;
    }

    /// <summary>The id a publish of this bundle for this resident gets: the same for the same bundle contents, different when any item or its asset differs.</summary>
    public static UUID DerivePublishId(Bundle bundle, UUID residentId)
    {
        var parts = new List<string> { "publish", residentId.ToString(), bundle.OutfitFolder.Id.ToString(), bundle.CurrentLook?.FolderId.ToString() ?? "-" };
        parts.AddRange(bundle.Items.OrderBy(i => i.Id.ToString(), StringComparer.Ordinal).Select(i => i.Id + ":" + i.AssetId));
        return ContentId.Derive(parts.ToArray());
    }

    /// <summary>The id of a resident's My Outfits folder when this service has to make it. Fixed per resident, so two attempts make one folder.</summary>
    public static UUID DeriveMyOutfitsId(UUID residentId) => ContentId.Derive("my-outfits", residentId.ToString());

    public PublishResult Publish(Bundle bundle, IReadOnlyDictionary<UUID, byte[]> content, UUID residentId, PublishOptions? options = null)
        => Publish(bundle, content, residentId, options, Array.Empty<string>());

    PublishResult Publish(Bundle bundle, IReadOnlyDictionary<UUID, byte[]> content, UUID residentId, PublishOptions? options, IReadOnlyList<string> unlistedFiles)
    {
        options ??= new PublishOptions();
        var result = new PublishResult { PublishId = DerivePublishId(bundle, residentId) };
        void Refuse(string code, string message) => result.Refusals.Add(new PublishRefusal(code, message));

        // ---- refuse before reading the inventory
        if (options.MaxOutfitsPerResident < 1) { Refuse("options", "The outfit limit must be at least 1."); return result; }
        if (residentId.IsZero()) { Refuse("wrong-owner", "No resident was named. Nothing was written."); return result; }
        if (bundle.OwnerId != residentId)
            Refuse("wrong-owner", "This outfit was built for a different resident than the one it was sent to. Nothing was written.");
        if (options.RequiredCreator is { } required && bundle.CreatorId != required)
            Refuse("wrong-creator", "This outfit was not made by this grid's character publishing account. Nothing was written.");
        if (bundle.Items.Count > options.MaxItems)
            Refuse("bundle-too-large", $"The outfit holds {bundle.Items.Count} items; this grid allows {options.MaxItems}. Nothing was written.");
        if (result.Refused) return result;

        var validation = BundleValidator.Validate(bundle, content, options.Limits);
        foreach (var n in unlistedFiles) validation = With(validation, new BundleProblem("unlisted-file", n, "is in the bundle folder but bundle.json does not list it"));
        if (!validation.Ok)
        {
            var limits = validation.Problems.Where(p => p.Code == "limit").ToList();
            if (limits.Count > 0)
                Refuse("bundle-too-large", "The outfit is larger than this grid allows (" + string.Join("; ", limits.Select(p => p.Message)) + "). Nothing was written.");
            var others = validation.Problems.Where(p => p.Code != "limit").ToList();
            if (others.Count > 0)
                Refuse("invalid-bundle", "The outfit failed its checks: " + string.Join("; ", others.Take(5).Select(p => p.ToString())) + (others.Count > 5 ? $"; and {others.Count - 5} more" : "") + ". Nothing was written.");
            return result;
        }

        lock (LockFor(residentId))
        {
            try { PublishLocked(bundle, content, residentId, options, result); }
            catch (Exception e) { result.Failures.Add(new PublishFailure("unexpected", "publish", e.GetType().Name + ": " + e.Message)); }
        }
        return result;
    }

    static ValidationResult With(ValidationResult r, BundleProblem p) => new(r.Problems.Append(p).ToList());

    // ------------------------------------------------------------------------------------------------------------------
    void PublishLocked(Bundle bundle, IReadOnlyDictionary<UUID, byte[]> content, UUID resident, PublishOptions options, PublishResult result)
    {
        void Refuse(string code, string message) => result.Refusals.Add(new PublishRefusal(code, message));
        var folder = bundle.OutfitFolder;
        result.OutfitFolderId = folder.Id;

        // ---- 1. read only: nothing is written until every check has passed
        var root = _inventory.GetRootFolder(resident);
        if (root == null) { Refuse("no-inventory", "This resident has no inventory on this grid, so there is nowhere to put the outfit. Nothing was written."); return; }

        var rootChildren = _inventory.GetFolderContent(resident, root.ID)?.Folders ?? new List<InventoryFolderBase>();
        var myOutfitsCandidates = rootChildren.Where(f => f.Type == BundleTypes.FolderMyOutfits && f.Owner == resident).OrderBy(f => f.ID.ToString(), StringComparer.Ordinal).ToList();

        var existingFolder = _inventory.GetFolder(resident, folder.Id);
        if (existingFolder != null && (existingFolder.Owner != resident || existingFolder.Type != BundleTypes.FolderOutfit || existingFolder.Name != folder.Name
            || !myOutfitsCandidates.Any(c => c.ID == existingFolder.ParentID)))
        {
            Refuse("folder-conflict", $"A folder with the id of this outfit already exists and is not this outfit, so the outfit was not written. Nothing was written.");
            return;
        }

        // Which My Outfits: the one that already holds this outfit; else the first by id. More than one is a data fault the
        // inventory service does not repair or report on a lookup, so say so.
        InventoryFolderBase? myOutfits = null;
        if (existingFolder != null) myOutfits = myOutfitsCandidates.First(c => c.ID == existingFolder.ParentID);
        else if (myOutfitsCandidates.Count > 0) myOutfits = myOutfitsCandidates[0];
        if (myOutfitsCandidates.Count > 1)
            result.Warnings.Add($"The resident has {myOutfitsCandidates.Count} My Outfits folders; the outfit goes in {myOutfits!.ID}. Merging them is left to the operator.");

        // items: an existing row must be exactly the expected one; "replace into" would overwrite anything else
        var expected = bundle.Items.Select(it => (Bundle: it, Row: ToRow(it, resident, folder.Id))).ToList();
        var itemPresent = new Dictionary<UUID, bool>();
        foreach (var (it, row) in expected)
        {
            var have = _inventory.GetItem(resident, it.Id);
            if (have == null) { itemPresent[it.Id] = false; continue; }
            if (!SameItem(have, row))
            {
                Refuse("item-conflict", $"An item with the id of '{it.Name}' already exists and differs from the one in this outfit, so nothing was written. If the outfit was changed, publish it with a new folder number.");
                return;
            }
            itemPresent[it.Id] = true;
        }

        // the cap counts this publisher's outfits; an outfit already here (a retry) is not a new one
        if (existingFolder == null)
        {
            var count = CountPublishedOutfits(resident, myOutfitsCandidates, bundle.CreatorId);
            if (count >= options.MaxOutfitsPerResident)
            {
                Refuse("cap-reached", $"You already have {count} published outfits in My Outfits and the limit on this grid is {options.MaxOutfitsPerResident}. Remove one you no longer want, then publish again. Nothing was written.");
                return;
            }
        }

        // assets: one that exists must hold exactly our bytes (ids are hashes, so a mismatch means someone parked other data under it)
        var assetIds = bundle.Assets.Select(a => a.Id.ToString()).ToArray();
        var exist = _assets.AssetsExist(assetIds);
        var assetPresent = new Dictionary<UUID, bool>();
        for (var i = 0; i < bundle.Assets.Count; i++)
        {
            var a = bundle.Assets[i];
            if (!exist[i]) { assetPresent[a.Id] = false; continue; }
            var data = _assets.GetData(a.Id.ToString());
            if (data == null || !data.AsSpan().SequenceEqual(content[a.Id]))
            {
                Refuse("asset-conflict", $"An asset with the id of '{a.Name}' already exists on this grid with different content, so nothing was written.");
                return;
            }
            assetPresent[a.Id] = true;
        }
        if (bundle.ExternalAssets.Count > 0)
        {
            var ext = _assets.AssetsExist(bundle.ExternalAssets.Select(e => e.ToString()).ToArray());
            for (var i = 0; i < ext.Length; i++)
                if (!ext[i]) result.Warnings.Add($"A texture the outfit uses ({bundle.ExternalAssets[i]}) is not on this grid; it will show as the default texture.");
        }

        // the previous look: read the Current Outfit now, write it last
        var look = options.SaveCurrentLook ? bundle.CurrentLook : null;
        InventoryFolderBase? existingLookFolder = null;
        List<InventoryItemBase> lookLinks = new();
        if (look != null)
        {
            result.PreviousLookFolderId = look.FolderId;
            existingLookFolder = _inventory.GetFolder(resident, look.FolderId);
            if (existingLookFolder != null && (existingLookFolder.Owner != resident || existingLookFolder.Type != BundleTypes.FolderOutfit))
            {
                Refuse("folder-conflict", "A folder with the id of the previous-look folder already exists and is not one, so nothing was written.");
                return;
            }
            lookLinks = PlanPreviousLook(resident, rootChildren, look, options.MaxPreviousLookLinks, result);
        }

        // ---- 2. writes
        // assets first: harmless if orphaned
        foreach (var a in bundle.Assets)
        {
            if (assetPresent[a.Id]) { result.AssetsAlreadyPresent.Add(a.Id); continue; }
            var asset = new AssetBase(a.Id, a.Name, (sbyte)a.AssetType, bundle.CreatorId.ToString()) { Data = content[a.Id], Local = false, Temporary = false };
            string stored;
            try { stored = _assets.Store(asset); }
            catch (Exception e) { Fail("asset", a.Name, e.Message); return; }
            if (string.IsNullOrEmpty(stored) || stored == UUID.Zero.ToString()) { Fail("asset", a.Name, "the asset service did not store it"); return; }
            result.AssetsStored.Add(a.Id);
        }

        // My Outfits, when the resident has none
        if (myOutfits == null)
        {
            var mine = new InventoryFolderBase(DeriveMyOutfitsId(resident), "My Outfits", resident, root.ID) { Type = BundleTypes.FolderMyOutfits, Version = 1 };
            var made = Try(() => _inventory.AddFolder(mine), out _);
            // AddFolder says false when the folder (or a system folder of the type) is already there: look again before failing
            myOutfits = _inventory.GetFolderContent(resident, root.ID)?.Folders.Where(f => f.Type == BundleTypes.FolderMyOutfits && f.Owner == resident).OrderBy(f => f.ID.ToString(), StringComparer.Ordinal).FirstOrDefault();
            if (myOutfits == null) { Fail("folder", "My Outfits", "the inventory service did not create it"); return; }
            result.MyOutfitsCreated = made && myOutfits.ID == mine.ID;
        }
        result.MyOutfitsFolderId = myOutfits.ID;

        // the outfit folder, then its items
        if (existingFolder == null)
        {
            var f = new InventoryFolderBase(folder.Id, folder.Name, resident, myOutfits.ID) { Type = BundleTypes.FolderOutfit, Version = 1 };
            if (!Try(() => _inventory.AddFolder(f), out var why)) { Fail("folder", folder.Name, Why(why, "create it")); return; }
            result.OutfitFolderCreated = true;
        }
        foreach (var (it, row) in expected)
        {
            result.ItemIds.Add(it.Id);
            if (itemPresent[it.Id]) { result.ItemsAlreadyPresent.Add(it.Id); continue; }
            if (!Try(() => _inventory.AddItem(row), out var why)) { Fail("item", it.Name, Why(why, "add it")); return; }
            result.ItemsCreated.Add(it.Id);
        }

        // the previous look last
        if (look != null && lookLinks.Count > 0)
        {
            if (existingLookFolder == null)
            {
                var lf = new InventoryFolderBase(look.FolderId, look.FolderName, resident, myOutfits.ID) { Type = BundleTypes.FolderOutfit, Version = 1 };
                if (!Try(() => _inventory.AddFolder(lf), out var why)) { Fail("folder", look.FolderName, Why(why, "create it")); return; }
                result.PreviousLookFolderCreated = true;
            }
            foreach (var link in lookLinks)
            {
                var have = _inventory.GetItem(resident, link.ID);
                if (have != null)
                {
                    // the description comes from the Current Outfit at the time, so it is not compared
                    if (have.Owner != link.Owner || have.Folder != link.Folder || have.AssetID != link.AssetID || have.AssetType != link.AssetType)
                    { Fail("link", link.Name, "an item with the id of this link exists and differs; left as it is"); return; }
                    result.PreviousLookLinks.Add(link.ID);
                    continue;
                }
                if (!Try(() => _inventory.AddItem(link), out var why)) { Fail("link", link.Name, Why(why, "add it")); return; }
                result.PreviousLookLinks.Add(link.ID);
                result.PreviousLookLinksCreated.Add(link.ID);
            }
        }

        void Fail(string step, string what, string message) => result.Failures.Add(new PublishFailure(step, what, message));
        static bool Try(Func<bool> write, out string? error) { try { error = null; return write(); } catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; } }
        static string Why(string? error, string what) => error == null ? "the inventory service did not " + what : "the inventory service failed to " + what + " (" + error + ")";
    }

    // ------------------------------------------------------------------------------------------------------------------
    /// <summary>Published outfits the resident holds: Outfit-type folders under My Outfits named "&lt;character&gt; #&lt;n&gt;" that hold an item made by the publisher's account.</summary>
    int CountPublishedOutfits(UUID resident, List<InventoryFolderBase> myOutfits, UUID creator)
    {
        var mark = creator.ToString();
        var count = 0;
        foreach (var mo in myOutfits)
        {
            var content = _inventory.GetFolderContent(resident, mo.ID);
            if (content?.Folders == null) continue;
            foreach (var f in content.Folders.Where(f => f.Type == BundleTypes.FolderOutfit && PublishedFolderName.IsMatch(f.Name ?? "")))
            {
                var items = _inventory.GetFolderContent(resident, f.ID)?.Items;
                if (items != null && items.Any(i => i.AssetType != AssetLink && string.Equals(i.CreatorId, mark, StringComparison.OrdinalIgnoreCase))) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// The links the previous-look folder should hold: one per item the Current Outfit folder points at, never a link to a link
    /// (a chain is followed to its item), skipping and reporting links whose target is gone or is not this resident's. Reads only.
    /// A folder that already exists keeps the links it has; only missing ones are added, and only when it holds nothing the Current
    /// Outfit no longer holds (otherwise the look has changed since the first attempt and the folder is left as it was).
    /// </summary>
    List<InventoryItemBase> PlanPreviousLook(UUID resident, List<InventoryFolderBase> rootChildren, CurrentLookPlan look, int maxLinks, PublishResult result)
    {
        var cofs = rootChildren.Where(f => f.Type == BundleTypes.FolderCurrentOutfit && f.Owner == resident).OrderBy(f => f.ID.ToString(), StringComparer.Ordinal).ToList();
        if (cofs.Count == 0) { result.Warnings.Add("The resident has no Current Outfit folder; no previous-look folder was made."); return new(); }
        if (cofs.Count > 1) result.Warnings.Add($"The resident has {cofs.Count} Current Outfit folders; the first was used for the previous look.");

        var entries = _inventory.GetFolderContent(resident, cofs[0].ID)?.Items ?? new List<InventoryItemBase>();
        var targets = new List<(InventoryItemBase Target, InventoryItemBase Entry)>();
        var seen = new HashSet<UUID>();
        foreach (var entry in entries)
        {
            if (entry.AssetType == AssetLinkFolder) continue; // the link to the base outfit folder: not an item
            InventoryItemBase? target = entry;
            if (entry.AssetType == AssetLink)
            {
                var id = entry.AssetID;
                target = null;
                for (var hop = 0; hop < MaxLinkHops; hop++)
                {
                    var t = _inventory.GetItem(resident, id);
                    if (t == null) { result.BrokenLinks.Add(new BrokenLink(entry.ID, entry.Name, "the item it points at is gone")); break; }
                    if (t.Owner != resident) { result.BrokenLinks.Add(new BrokenLink(entry.ID, entry.Name, "the item it points at belongs to someone else")); break; }
                    if (t.AssetType == AssetLinkFolder) { result.BrokenLinks.Add(new BrokenLink(entry.ID, entry.Name, "points at a folder link")); break; }
                    if (t.AssetType != AssetLink) { target = t; break; }
                    id = t.AssetID; // a link to a link: follow it to the item
                }
                if (target == null)
                {
                    if (!result.BrokenLinks.Any(b => b.CurrentOutfitItemId == entry.ID)) result.BrokenLinks.Add(new BrokenLink(entry.ID, entry.Name, "a chain of links with no item at the end"));
                    continue;
                }
            }
            if (seen.Add(target.ID)) targets.Add((target, entry));
        }
        foreach (var b in result.BrokenLinks) result.Warnings.Add($"Current Outfit entry '{b.Name}' was not saved in the previous look: {b.Reason}.");
        if (targets.Count == 0) { result.Warnings.Add("The Current Outfit holds nothing to link; no previous-look folder was made."); return new(); }
        if (targets.Count > maxLinks)
        {
            result.Warnings.Add($"The Current Outfit holds {targets.Count} items; only the first {maxLinks} were saved in the previous look.");
            targets = targets.Take(maxLinks).ToList();
        }

        var links = targets.Select(p => new InventoryItemBase(ContentId.Derive("looklink", resident.ToString(), look.FolderId.ToString(), p.Target.ID.ToString()), resident)
        {
            Folder = look.FolderId,
            Name = p.Target.Name,
            Description = string.IsNullOrEmpty(p.Entry.Description) ? p.Target.Description : p.Entry.Description,
            AssetID = p.Target.ID,
            AssetType = AssetLink,
            InvType = p.Target.InvType,
            Flags = p.Target.Flags,
            CreatorId = resident.ToString(),
            CreationDate = look.CreationDate,
            BasePermissions = look.LinkPermissions,
            CurrentPermissions = look.LinkPermissions,
            NextPermissions = look.LinkPermissions,
            EveryOnePermissions = 0,
            GroupPermissions = 0,
            SalePrice = 0,
            SaleType = 0,
        }).ToList();

        // a folder from an earlier attempt: keep what it has; add only when it is a subset of the Current Outfit as it is now
        var existing = _inventory.GetFolderContent(resident, look.FolderId)?.Items ?? new List<InventoryItemBase>();
        if (existing.Count > 0)
        {
            var wanted = targets.Select(p => p.Target.ID).ToHashSet();
            if (existing.Any(e => !wanted.Contains(e.AssetID)))
            {
                result.Warnings.Add("The previous-look folder from an earlier attempt holds items the Current Outfit no longer holds; it was left as it was.");
                foreach (var e in existing) result.PreviousLookLinks.Add(e.ID);
                return new();
            }
        }
        return links;
    }

    static InventoryItemBase ToRow(BundleItem it, UUID resident, UUID folderId) => new(it.Id, resident)
    {
        Folder = folderId,
        Name = it.Name,
        Description = it.Description,
        AssetID = it.AssetId,
        AssetType = it.AssetType,
        InvType = it.InvType,
        Flags = it.Flags,
        BasePermissions = it.BasePermissions,
        CurrentPermissions = it.CurrentPermissions,
        NextPermissions = it.NextPermissions,
        GroupPermissions = it.GroupPermissions,
        EveryOnePermissions = it.EveryonePermissions,
        CreatorId = it.CreatorId.ToString(),
        CreationDate = it.CreationDate,
        SalePrice = it.SalePrice,
        SaleType = it.SaleType,
    };

    /// <summary>Every field a publish sets. The creator is compared as the service stores it.</summary>
    static bool SameItem(InventoryItemBase have, InventoryItemBase want) =>
        have.Owner == want.Owner && have.Folder == want.Folder && have.AssetID == want.AssetID && have.AssetType == want.AssetType
        && have.InvType == want.InvType && have.Flags == want.Flags && have.Name == want.Name && have.Description == want.Description
        && have.BasePermissions == want.BasePermissions && have.CurrentPermissions == want.CurrentPermissions
        && have.NextPermissions == want.NextPermissions && have.GroupPermissions == want.GroupPermissions
        && have.EveryOnePermissions == want.EveryOnePermissions && have.SalePrice == want.SalePrice && have.SaleType == want.SaleType
        && have.CreationDate == want.CreationDate
        && string.Equals(have.CreatorIdentification, want.CreatorIdentification, StringComparison.OrdinalIgnoreCase);
}
