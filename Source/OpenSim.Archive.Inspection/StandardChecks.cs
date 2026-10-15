/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Archive.Inspection;

/// <summary>The file-only checks: they look at nothing but the archive. Each states what the file says; none is a verdict.</summary>
public static class StandardChecks
{
    public static IEnumerable<IArchiveCheck> Create(InspectorOptions options)
    {
        yield return new IarSanityCheck(options);
        yield return new PermissionsCheck(options);
        yield return new ReferencesCheck(options);
        yield return new AuthorLinesCheck(options);
        yield return new CreatorDataCheck(options);
        yield return new ArchiveIntegrityCheck(options);
    }

    internal static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Where(ItemRecord i)
    {
        if (i.Source == ItemSource.InventoryArchive)
            return string.IsNullOrEmpty(i.FolderPath) ? "(inventory root)" : i.FolderPath;
        return "linkset " + i.LinksetId + (string.IsNullOrEmpty(i.PartName) ? "" : ", part " + i.PartName) + (i.Context == RecordContext.Boxed ? " (boxed in asset " + i.BoxedAssetId + ")" : "");
    }

    internal static string Where(PartRecord p) =>
        "linkset " + (string.IsNullOrEmpty(p.LinksetName) ? "" : p.LinksetName + " ") + p.LinksetId + (p.Context == RecordContext.Boxed ? " (boxed in asset " + p.BoxedAssetId + ")" : "");

    internal static string Who(CreatorRef c)
    {
        string name = c.Name.Length > 0 ? c.Name + (c.Home.Length > 0 ? " @ " + c.Home : "") : string.Empty;
        string id = c.Id != UUID.Zero ? c.Id.ToString() : string.Empty;
        if (name.Length > 0 && id.Length > 0)
            return name + " " + id;
        return name.Length > 0 ? name : (id.Length > 0 ? id : "(no creator)");
    }
}

/// <summary>Shared plumbing for the standard checks.</summary>
public abstract class StandardCheckBase : ArchiveCheckBase
{
    protected readonly InspectorOptions Options;
    protected ArchiveInfo Archive;

    protected StandardCheckBase(InspectorOptions options) { Options = options; }

    public override void Begin(ArchiveInfo archive) { Archive = archive; }

    protected FindingBuilder NewBuilder() => new FindingBuilder(Options.MaxFindingExamples);

    protected static Dictionary<string, string> Nums(params (string key, long value)[] pairs)
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        foreach ((string key, long value) in pairs)
            d[key] = value.ToString(CultureInfo.InvariantCulture);
        return d;
    }
}

/// <summary>
/// IAR sanity: the order the IAR loader needs (InventoryArchiveReadRequest 600-612 and 673-683), entries that do
/// not parse, item and asset types that disagree, links to nothing, system folders, size limits and boxed objects.
/// Does nothing for an OAR.
/// </summary>
public sealed class IarSanityCheck : StandardCheckBase
{
    public override string Id => "iar-sanity";

    private long m_firstAssetIndex = long.MaxValue;
    private long m_firstInventoryIndex = long.MaxValue;
    private readonly FindingBuilder m_late;
    private readonly FindingBuilder m_unparsed;
    private readonly FindingBuilder m_disagree;
    private readonly FindingBuilder m_assetType;
    private readonly FindingBuilder m_systemFolders;
    private readonly FindingBuilder m_depth;
    private readonly FindingBuilder m_boxed;
    private readonly List<(string name, UUID id, UUID target, string where, bool folder)> m_links = new List<(string, UUID, UUID, string, bool)>();
    private readonly HashSet<UUID> m_itemIds = new HashSet<UUID>();
    private readonly HashSet<UUID> m_folderIds = new HashSet<UUID>();
    private readonly Dictionary<string, long> m_perFolder = new Dictionary<string, long>();
    private readonly Dictionary<UUID, int> m_assetTypes = new Dictionary<UUID, int>();
    private readonly List<(UUID asset, int claimed, string name, UUID id, string where)> m_claims = new List<(UUID, int, string, UUID, string)>();
    private long m_items;
    private int m_maxDepth;
    private const int MaxClaims = 2_000_000;

    public IarSanityCheck(InspectorOptions options) : base(options)
    {
        m_late = NewBuilder();
        m_unparsed = NewBuilder();
        m_disagree = NewBuilder();
        m_assetType = NewBuilder();
        m_systemFolders = NewBuilder();
        m_depth = NewBuilder();
        m_boxed = NewBuilder();
    }

    public override void OnRecord(ArchiveRecord record)
    {
        switch (record)
        {
            case EntryRecord e:
                if (e.Prefix == ArchiveConstants.ASSETS_PATH && m_firstAssetIndex == long.MaxValue)
                    m_firstAssetIndex = e.Index;
                else if (e.Prefix == ArchiveConstants.INVENTORY_PATH)
                {
                    if (m_firstInventoryIndex == long.MaxValue)
                        m_firstInventoryIndex = e.Index;
                    if (m_firstAssetIndex != long.MaxValue)
                        m_late.Add(e.Path, string.Empty, e.Entry, "entry " + StandardChecks.N(e.Index) + " comes after the first asset entry (entry " + StandardChecks.N(m_firstAssetIndex) + ")");
                }
                break;

            case ParseFailureRecord f:
                if (f.What == "item" || f.What == "folder")
                    m_unparsed.Add(f.Entry, string.Empty, f.Entry, f.Message);
                else if (f.What == "boxed-object")
                    m_boxed.Add(f.Entry, f.AssetId?.ToString(), f.Entry, f.Message);
                break;

            case FolderRecord fo:
                if (!fo.IdParsed)
                    m_unparsed.Add(fo.Name, string.Empty, fo.Path, "folder name has no parsable folder id; the loader skips path segments without one");
                else
                    m_folderIds.Add(fo.FolderId);
                if (TypeAgreement.SystemFolderNames.TryGetValue(fo.Name ?? string.Empty, out string type))
                    m_systemFolders.Add(fo.Name, fo.FolderId.ToString(), fo.Path, "named like the " + type + " folder");
                if (fo.Depth > m_maxDepth)
                    m_maxDepth = fo.Depth;
                if (fo.Depth > Options.Structure.MaxFolderDepth)
                    m_depth.Add(fo.Name, fo.FolderId.ToString(), fo.Path, "depth " + fo.Depth);
                break;

            case AssetRecord a:
                m_assetTypes[a.AssetId] = a.Recognised ? a.AssetType : -1;
                break;

            case ItemRecord i when i.Source == ItemSource.InventoryArchive && i.Context == RecordContext.Archive:
                m_items++;
                m_itemIds.Add(i.ItemId);
                m_perFolder.TryGetValue(i.FolderPath ?? string.Empty, out long n);
                m_perFolder[i.FolderPath ?? string.Empty] = n + 1;
                if (i.AssetType.HasValue && TypeAgreement.IsLink(i.AssetType.Value))
                    m_links.Add((i.Name, i.ItemId, i.AssetId, StandardChecks.Where(i), i.AssetType.Value == (int)AssetType.LinkFolder));
                else
                {
                    if (i.AssetType.HasValue && i.InvType.HasValue && !TypeAgreement.InventoryTypeAgrees(i.AssetType.Value, i.InvType.Value))
                        m_disagree.Add(i.Name, i.ItemId.ToString(), StandardChecks.Where(i),
                            "asset type " + ArchiveInspector.TypeName(i.AssetType.Value) + " with inventory type " + i.InvType.Value);
                    if (i.AssetType.HasValue && i.AssetId != UUID.Zero && m_claims.Count < MaxClaims)
                        m_claims.Add((i.AssetId, i.AssetType.Value, i.Name, i.ItemId, StandardChecks.Where(i)));
                }
                break;
        }
    }

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        if (Archive == null || Archive.Kind != ArchiveKind.Iar)
            return;

        // order the loader needs
        FindingBuilder order = NewBuilder();
        if (!Archive.ControlFileFound)
            order.Add(ArchiveConstants.CONTROL_FILE_PATH, string.Empty, string.Empty, "no control file; the loader stops at the first inventory or asset entry");
        else if (!Archive.ControlFileFirst)
            order.Add(ArchiveConstants.CONTROL_FILE_PATH, string.Empty, string.Empty, "the control file is not the first entry; the loader stops at any inventory or asset entry that comes before it");
        if (m_firstAssetIndex != long.MaxValue && (m_firstInventoryIndex == long.MaxValue || m_firstInventoryIndex > m_firstAssetIndex))
            order.Add(ArchiveConstants.ASSETS_PATH, string.Empty, string.Empty, "the first asset entry (entry " + StandardChecks.N(m_firstAssetIndex) + ") comes before any inventory entry; the loader stops there");
        foreach (FindingExample ex in m_late.Examples)
            order.Add(ex.Name, ex.Id, ex.Where, ex.Note);
        if (order.Any || m_late.Any)
        {
            long total = order.Count + (m_late.Count - m_late.Examples.Count);
            Finding f = order.ToFinding(Id, "IAR_ORDER", FindingSeverity.Warning, "Entries are not in the order the IAR loader needs",
                "The IAR loader needs the control file first and every inventory entry before the first asset entry, and stops with an error otherwise (InventoryArchiveReadRequest 600-612, 673-683). In this file: " +
                StandardChecks.N(total) + " entries or conditions break that order.");
            f.Count = total;
            f.ExamplesTruncated |= m_late.Truncated;
            findings.Add(f);
        }

        if (m_unparsed.Any)
            findings.Add(m_unparsed.ToFinding(Id, "IAR_UNPARSED", FindingSeverity.Warning, "Inventory entries that do not parse",
                StandardChecks.N(m_unparsed.Count) + " item or folder entries could not be read as an inventory item or folder name; the loader skips or fails on them."));

        if (m_disagree.Any)
            findings.Add(m_disagree.ToFinding(Id, "IAR_TYPE_DISAGREE", FindingSeverity.Warning, "Item type and asset type disagree",
                StandardChecks.N(m_disagree.Count) + " items name an asset type and an inventory type that are not a pair in the grid's type table (SLUtil.MAPPINGS)."));

        // item asset type against the asset file's own type
        foreach ((UUID asset, int claimed, string name, UUID id, string where) c in m_claims)
            if (m_assetTypes.TryGetValue(c.asset, out int actual) && actual >= 0 && !TypeAgreement.SameFamily(c.claimed, actual))
                m_assetType.Add(c.name, c.id.ToString(), c.where, "item says " + ArchiveInspector.TypeName(c.claimed) + "; asset " + c.asset + " is stored as " + ArchiveInspector.TypeName(actual));
        if (m_assetType.Any)
            findings.Add(m_assetType.ToFinding(Id, "IAR_ASSET_TYPE_MISMATCH", FindingSeverity.Warning, "Item type differs from its asset's stored type",
                StandardChecks.N(m_assetType.Count) + " items name an asset type different from the type given by their asset file's extension."));

        // links
        FindingBuilder dangling = NewBuilder();
        foreach ((string name, UUID id, UUID target, string where, bool folder) l in m_links)
            if (!(l.folder ? m_folderIds.Contains(l.target) : m_itemIds.Contains(l.target)))
                dangling.Add(l.name, l.id.ToString(), l.where, (l.folder ? "folder link" : "item link") + " points at " + l.target + ", which is not in the archive");
        if (dangling.Any)
            findings.Add(dangling.ToFinding(Id, "IAR_LINK_DANGLING", FindingSeverity.Warning, "Links that point outside the archive",
                StandardChecks.N(dangling.Count) + " of " + StandardChecks.N(m_links.Count) + " link items point at an item or folder that is not in the archive; the loader cannot restore them."));

        if (m_systemFolders.Any)
            findings.Add(m_systemFolders.ToFinding(Id, "IAR_SYSTEM_FOLDER", FindingSeverity.Notice, "Folders named like the grid's system folders",
                StandardChecks.N(m_systemFolders.Count) + " folders carry the name of a single-instance system folder. The archive records no folder type and the loader gives every folder it creates the plain type, so loading under a folder that has no folder of that name makes a second, ordinary one (InventoryArchiveReadRequest 389-428). This is a match on the name only."));

        // size limits
        FindingBuilder limits = NewBuilder();
        StructureSettings s = Options.Structure;
        if (m_depth.Any)
            limits.Add("folder depth", string.Empty, string.Empty, StandardChecks.N(m_depth.Count) + " folders deeper than " + s.MaxFolderDepth + " (deepest " + m_maxDepth + ")");
        foreach (KeyValuePair<string, long> kv in m_perFolder)
            if (kv.Value > s.MaxItemsPerFolder)
                limits.Add(kv.Key.Length == 0 ? "(inventory root)" : kv.Key, string.Empty, kv.Key, StandardChecks.N(kv.Value) + " items in one folder, over " + StandardChecks.N(s.MaxItemsPerFolder));
        if (m_items > s.MaxTotalItems)
            limits.Add("total items", string.Empty, string.Empty, StandardChecks.N(m_items) + " items, over " + StandardChecks.N(s.MaxTotalItems));
        if (limits.Any)
        {
            Finding f = limits.ToFinding(Id, "IAR_LIMITS", FindingSeverity.Notice, "Folder or item counts over the configured bounds",
                "The archive goes past a bound set in the inspector's settings (depth " + s.MaxFolderDepth + ", items per folder " + StandardChecks.N(s.MaxItemsPerFolder) + ", total items " + StandardChecks.N(s.MaxTotalItems) + "); these are settings, not limits taken from a loader.",
                Nums(("deepestFolder", m_maxDepth), ("totalItems", m_items)));
            findings.Add(f);
        }

        if (m_boxed.Any)
            findings.Add(m_boxed.ToFinding(Id, "IAR_BOXED_UNPARSED", FindingSeverity.Warning, "Boxed objects that do not parse",
                StandardChecks.N(m_boxed.Count) + " object assets held by items could not be read as object XML."));
    }
}

/// <summary>Permissions and export: masks wider than their base, fully open records and records held by someone whose creator did not allow export.</summary>
public sealed class PermissionsCheck : StandardCheckBase
{
    public override string Id => "permissions";

    // Transfer | Modify | Copy | Export. Move is left out: the grid forces it on when an item is given or rezzed
    // (Scene.Inventory GiveInventoryItem "permalocked get unlocked"), so owner masks routinely carry it past base.
    private const uint Content = (uint)(PermissionMask.Transfer | PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Export);
    private const uint Open = (uint)(PermissionMask.Transfer | PermissionMask.Modify | PermissionMask.Copy);
    private const uint ExportBit = (uint)PermissionMask.Export;

    private readonly FindingBuilder m_wider;
    private readonly FindingBuilder m_noExport;
    private readonly Dictionary<string, long> m_noExportByCreator = new Dictionary<string, long>();
    private long m_records, m_withBase, m_fullyOpen, m_held, m_heldUnknownCreator, m_heldNoExportInfo, m_anyExportBit;
    private long m_widerOwner, m_widerGroup, m_widerEveryone, m_widerNext;

    public PermissionsCheck(InspectorOptions options) : base(options)
    {
        m_wider = NewBuilder();
        m_noExport = NewBuilder();
    }

    public override void OnRecord(ArchiveRecord record)
    {
        if (record is PartRecord p)
            Look(p.Name, p.PartId.ToString(), StandardChecks.Where(p), p.Masks, p.Creator, p.OwnerId);
        else if (record is ItemRecord i)
            Look(i.Name, i.ItemId.ToString(), StandardChecks.Where(i), i.Masks, i.Creator, i.OwnerId);
    }

    private void Look(string name, string id, string where, PermissionMasks m, CreatorRef creator, UUID owner)
    {
        m_records++;
        if (m.Base.HasValue)
        {
            m_withBase++;
            uint b = m.Base.Value;
            List<string> wide = new List<string>();
            Wider("owner", m.Owner, b, wide, ref m_widerOwner);
            Wider("group", m.Group, b, wide, ref m_widerGroup);
            Wider("everyone", m.Everyone, b, wide, ref m_widerEveryone);
            Wider("next owner", m.NextOwner, b, wide, ref m_widerNext);
            if (wide.Count > 0)
                m_wider.Add(name, id, where, string.Join("; ", wide));
        }

        uint anyBits = (m.Base ?? 0) | (m.Owner ?? 0) | (m.Group ?? 0) | (m.Everyone ?? 0) | (m.NextOwner ?? 0);
        if ((anyBits & ExportBit) != 0)
            m_anyExportBit++;

        if (m.Base.HasValue && m.Owner.HasValue && m.Group.HasValue && m.Everyone.HasValue && m.NextOwner.HasValue
            && (m.Base.Value & Open) == Open && (m.Owner.Value & Open) == Open && (m.Group.Value & Open) == Open
            && (m.Everyone.Value & Open) == Open && (m.NextOwner.Value & Open) == Open)
            m_fullyOpen++;

        // held by someone other than the creator
        if (creator.IsLibrary || owner == UUID.Zero)
            return;
        if (creator.Id == UUID.Zero)
        {
            if (creator.Name.Length > 0)
                m_heldUnknownCreator++;
            return;
        }
        if (creator.Id == owner)
            return;

        m_held++;
        if (!m.Base.HasValue || (m.Base.Value & ExportBit) == 0)
        {
            m_heldNoExportInfo++;
            return;
        }
        bool allowed = ((m.Owner ?? 0) & ExportBit) != 0 || ((m.Everyone ?? 0) & ExportBit) != 0;
        if (!allowed)
        {
            string key = StandardChecks.Who(creator);
            m_noExportByCreator.TryGetValue(key, out long n);
            m_noExportByCreator[key] = n + 1;
            m_noExport.Add(name, id, where, "creator " + key);
        }
    }

    private static void Wider(string label, uint? mask, uint baseMask, List<string> into, ref long counter)
    {
        if (!mask.HasValue)
            return;
        uint extra = mask.Value & ~baseMask & Content;
        if (extra == 0)
            return;
        counter++;
        into.Add(label + " mask has " + Bits(extra) + " that base lacks");
    }

    private static string Bits(uint bits)
    {
        List<string> parts = new List<string>();
        if ((bits & (uint)PermissionMask.Modify) != 0) parts.Add("modify");
        if ((bits & (uint)PermissionMask.Copy) != 0) parts.Add("copy");
        if ((bits & (uint)PermissionMask.Transfer) != 0) parts.Add("transfer");
        if ((bits & ExportBit) != 0) parts.Add("export");
        return string.Join("+", parts);
    }

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        if (m_wider.Any)
            findings.Add(m_wider.ToFinding(Id, "PERM_WIDER_THAN_BASE", FindingSeverity.Notice, "Masks wider than the base mask",
                StandardChecks.N(m_wider.Count) + " of " + StandardChecks.N(m_withBase) + " records with a base mask have an owner, group, everyone or next-owner mask that grants copy, modify, transfer or export that the base mask does not (move is not compared).",
                Nums(("owner", m_widerOwner), ("group", m_widerGroup), ("everyone", m_widerEveryone), ("nextOwner", m_widerNext), ("records", m_records), ("recordsWithBase", m_withBase))));

        if (m_fullyOpen > 0)
            findings.Add(new Finding
            {
                Source = Id,
                Code = "PERM_ALL_OPEN",
                Severity = FindingSeverity.Info,
                Title = "Records with every mask fully open",
                Detail = StandardChecks.N(m_fullyOpen) + " of " + StandardChecks.N(m_records) + " records have copy, modify and transfer set in the base, owner, group, everyone and next-owner masks.",
                Count = m_fullyOpen,
                Numbers = Nums(("fullyOpenRecords", m_fullyOpen), ("records", m_records))
            });

        if (m_noExport.Any)
        {
            Dictionary<string, string> nums = Nums(("heldRecords", m_held), ("notAllowedRecords", m_noExport.Count));
            foreach (KeyValuePair<string, long> kv in m_noExportByCreator.OrderByDescending(k => k.Value).Take(50))
                nums["creator: " + Sanitize.Text(kv.Key)] = kv.Value.ToString(CultureInfo.InvariantCulture);
            findings.Add(m_noExport.ToFinding(Id, "PERM_NO_EXPORT_HELD", FindingSeverity.Warning, "Held items whose creators did not allow export",
                "The creators of these items did not allow them to leave their grid: " + StandardChecks.N(m_noExport.Count) + " of " + StandardChecks.N(m_held) +
                " records held by someone other than their creator carry the export right in the base mask but in neither the owner nor the everyone mask, across " + m_noExportByCreator.Count + (m_noExportByCreator.Count == 1 ? " creator." : " creators."), nums));
        }

        if (m_heldNoExportInfo > 0)
            findings.Add(new Finding
            {
                Source = Id,
                Code = "PERM_NO_EXPORT_INFO",
                Severity = FindingSeverity.Info,
                Title = "Held records with no export information",
                Detail = StandardChecks.N(m_heldNoExportInfo) + " of " + StandardChecks.N(m_held) + " records held by someone other than their creator have no export bit in the base mask, " +
                    (m_anyExportBit == 0 ? "and no mask in the archive carries one, which is what an archive saved before the export bit existed looks like. They are not counted as refused." : "so the creator's choice cannot be read from them. They are not counted as refused."),
                Count = m_heldNoExportInfo,
                Numbers = Nums(("heldRecords", m_held), ("recordsWithNoExportBit", m_heldNoExportInfo), ("recordsWithAnyExportBit", m_anyExportBit), ("heldWithUnknownCreatorId", m_heldUnknownCreator))
            });
    }
}
