/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// Reads an OAR or IAR in one forward pass and builds an <see cref="ArchiveReport"/>. Nothing is extracted or written.
/// Memory is bounded by the largest body that is parsed (object XML, text assets) and by tables keyed by distinct
/// ids (assets, creators, owners), not by the size of the file.
/// </summary>
public sealed class ArchiveInspector
{
    private readonly InspectorOptions m_options;

    public ArchiveInspector(InspectorOptions options = null)
    {
        m_options = options ?? new InspectorOptions();
    }

    public ArchiveReport Inspect(string path)
    {
        try
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Inspect(fs);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            ArchiveReport r = new ArchiveReport { Status = InspectionStatus.NotAnArchive, StopReason = "cannot open file: " + Sanitize.Text(e.Message) };
            r.EditNote = LoaderBehaviour.EditNote;
            return r;
        }
    }

    public ArchiveReport Inspect(Stream input)
    {
        Pass pass = new Pass(m_options, input);
        return pass.Run();
    }

    /// <summary>One run over one archive.</summary>
    private sealed class Pass
    {
        private readonly InspectorOptions m_opt;
        private readonly Stream m_input;
        private readonly ArchiveReport m_report = new ArchiveReport();
        private readonly List<IArchiveCheck> m_checks = new List<IArchiveCheck>();
        private readonly CreatorAnalysisCheck m_analysis;

        private ControlFile m_control;
        private ArchiveKind m_kindFromPaths = ArchiveKind.Unknown;
        private bool m_partial;
        private bool m_beginCalled;
        private string m_currentEntry;

        private readonly Dictionary<UUID, AssetEntry> m_assets = new Dictionary<UUID, AssetEntry>();
        private readonly Dictionary<UUID, RefAgg> m_refs = new Dictionary<UUID, RefAgg>();
        private readonly Dictionary<UUID, List<HolderInfo>> m_holders = new Dictionary<UUID, List<HolderInfo>>();
        private readonly Dictionary<string, PartyTotals> m_creators = new Dictionary<string, PartyTotals>();
        private readonly Dictionary<UUID, PartyTotals> m_owners = new Dictionary<UUID, PartyTotals>();
        private readonly Dictionary<UUID, List<UUID>> m_objectEdges = new Dictionary<UUID, List<UUID>>();
        private readonly List<UUID> m_topLevelObjectAssets = new List<UUID>();
        private readonly Dictionary<UUID, BoxedObjectRecord> m_boxed = new Dictionary<UUID, BoxedObjectRecord>();
        private readonly Dictionary<string, LegacySummary> m_legacy = new Dictionary<string, LegacySummary>();
        private readonly Dictionary<string, FolderRecord> m_folders = new Dictionary<string, FolderRecord>();
        private readonly HashSet<string> m_unknownEntryKinds = new HashSet<string>();
        private long m_holderEntries;

        private sealed class AssetEntry
        {
            public int Type;
            public long Size;
            public bool Recognised;
        }

        private sealed class HolderInfo
        {
            public string Kind;
            public string Name;
            public UUID Id;
            public CreatorRef Creator;
            public UUID OwnerId;
        }

        private sealed class RefAgg
        {
            public int KindMask;
            public bool AnyCertain;
            public int? AssetType;
            public long Uses;
            public Dictionary<string, AssetUser> Users = new Dictionary<string, AssetUser>();
            public long OtherUsers;
        }

        public Pass(InspectorOptions options, Stream input)
        {
            m_opt = options;
            m_input = input;
            m_analysis = new CreatorAnalysisCheck(options.Analysis);
            m_checks.Add(m_analysis);
            m_checks.AddRange(options.Checks);
            m_report.Retention.MaxRetainedRecords = options.MaxRetainedRecords;
        }

        // ---------------------------------------------------------------- the pass

        public ArchiveReport Run()
        {
            DateTime started = DateTime.UtcNow;
            m_report.EditNote = LoaderBehaviour.EditNote;
            ArchiveInfo info = m_report.Archive;

            Stream tarStream = null;
            CountingStream counting = null;
            GuardStream guard = null;
            bool opened = false;

            try
            {
                info.FileBytes = m_input.CanSeek ? m_input.Length : 0;

                byte[] magic = new byte[2];
                int got = 0;
                while (got < 2)
                {
                    int n = m_input.Read(magic, got, 2 - got);
                    if (n <= 0)
                        break;
                    got += n;
                }

                if (got < 2)
                    return NotArchive("file is empty or too short to be an archive");
                if (!m_input.CanSeek)
                    return NotArchive("input stream cannot seek");
                m_input.Seek(0, SeekOrigin.Begin);

                counting = new CountingStream(m_input);
                info.Compressed = magic[0] == 0x1f && magic[1] == 0x8b;
                if (info.Compressed)
                {
                    guard = new GuardStream(new GZipStream(counting, CompressionMode.Decompress), counting, m_opt, () => m_currentEntry);
                    tarStream = guard;
                }
                else
                {
                    guard = new GuardStream(counting, counting, m_opt, () => m_currentEntry);
                    tarStream = guard;
                }

                TarStreamReader tar = new TarStreamReader(tarStream);
                ReadEntries(tar, guard, counting);
                opened = true;
            }
            catch (TarFormatException e)
            {
                Stop(e.Message, e.EntryName ?? m_currentEntry);
            }
            catch (ArchiveLimitException e)
            {
                Stop(e.Message, e.EntryName ?? m_currentEntry);
            }
            catch (InvalidDataException e)
            {
                Stop("compressed data is damaged: " + e.Message, m_currentEntry);
            }
            catch (EndOfStreamException e)
            {
                Stop("archive ends early: " + e.Message, m_currentEntry);
            }
            catch (IOException e)
            {
                Stop("read error: " + e.Message, m_currentEntry);
            }

            if (guard != null)
            {
                info.UncompressedBytes = guard.Total;
                info.CompressedBytes = counting.BytesRead;
            }
            _ = opened;
            _ = started;

            return Finish();
        }

        private ArchiveReport NotArchive(string reason)
        {
            m_report.Status = InspectionStatus.NotAnArchive;
            m_report.StopReason = reason;
            return m_report;
        }

        private void Stop(string reason, string entry)
        {
            m_partial = true;
            m_report.StopReason = Sanitize.Text(reason);
            m_report.StoppedAt = entry == null ? null : Sanitize.Text(entry);
            if (m_report.Archive.EntryCount == 0)
                m_report.StopReason = "not a readable archive: " + m_report.StopReason;
        }

        private void ReadEntries(TarStreamReader tar, GuardStream guard, CountingStream counting)
        {
            while (true)
            {
                TarEntryHeader h;
                try
                {
                    m_currentEntry = null;
                    h = tar.ReadNext();
                }
                catch (Exception e) when (tar.EntriesRead == 0 && (e is TarFormatException || e is InvalidDataException || e is EndOfStreamException || e is IOException))
                {
                    m_report.Status = InspectionStatus.NotAnArchive;
                    throw;
                }

                if (h == null)
                    break;

                m_currentEntry = h.Name;
                m_report.Archive.EntryCount = tar.EntriesRead;
                if (tar.EntriesRead > m_opt.MaxEntries)
                    throw new ArchiveLimitException("archive has more than " + m_opt.MaxEntries + " entries; stopped at '" + Sanitize.Text(h.Name) + "'", h.Name);

                if (h.IsDirectory)
                {
                    HandleDirectory(h);
                    continue;
                }

                try
                {
                    HandleFile(tar, h);
                }
                catch (Exception e) when (!(e is TarFormatException) && !(e is ArchiveLimitException) && !(e is InvalidDataException) && !(e is IOException))
                {
                    Problem(h.Name, "entry could not be read: " + e.Message, true);
                    tar.SkipBody();
                }
            }

            m_report.Archive.EntryCount = tar.EntriesRead;
        }

        // ---------------------------------------------------------------- entries

        private static string Normalise(string name, out string regionDir)
        {
            regionDir = string.Empty;
            string n = name.Replace('\\', '/');
            while (n.StartsWith("./", StringComparison.Ordinal))
                n = n.Substring(2);

            if (n.StartsWith(ArchiveConstants.REGIONS_PATH, StringComparison.Ordinal))
            {
                int slash = n.IndexOf('/', ArchiveConstants.REGIONS_PATH.Length);
                if (slash > 0)
                {
                    regionDir = n.Substring(ArchiveConstants.REGIONS_PATH.Length, slash - ArchiveConstants.REGIONS_PATH.Length);
                    n = n.Substring(slash + 1);
                }
            }
            return n;
        }

        private void NoteKindFromPath(string path)
        {
            if (m_kindFromPaths != ArchiveKind.Unknown)
                return;
            if (path.StartsWith(ArchiveConstants.OBJECTS_PATH, StringComparison.Ordinal) || path.StartsWith(ArchiveConstants.SETTINGS_PATH, StringComparison.Ordinal)
                || path.StartsWith(ArchiveConstants.LANDDATA_PATH, StringComparison.Ordinal) || path.StartsWith(ArchiveConstants.TERRAINS_PATH, StringComparison.Ordinal))
                m_kindFromPaths = ArchiveKind.Oar;
            else if (path.StartsWith(ArchiveConstants.INVENTORY_PATH, StringComparison.Ordinal))
                m_kindFromPaths = ArchiveKind.Iar;
        }

        private void HandleDirectory(TarEntryHeader h)
        {
            string path = Normalise(h.Name, out _);
            NoteKindFromPath(path);
            if (!path.StartsWith(ArchiveConstants.INVENTORY_PATH, StringComparison.Ordinal))
                return;

            string rel = path.Substring(ArchiveConstants.INVENTORY_PATH.Length).TrimEnd('/');
            if (rel.Length == 0)
                return;
            EnsureFolder(rel, h.Name);
        }

        private static void SplitFolderSegment(string segment, out string name, out UUID id)
        {
            id = UUID.Zero;
            int sep = segment.LastIndexOf(ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR, StringComparison.Ordinal);
            if (sep >= 0 && UUID.TryParse(segment.Substring(sep + ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR.Length), out UUID parsed))
            {
                id = parsed;
                segment = segment.Substring(0, sep);
            }
            name = segment.Replace("&#47;", "/").Replace("&amp;", "&");
        }

        private FolderRecord EnsureFolder(string rel, string entry)
        {
            if (m_folders.TryGetValue(rel, out FolderRecord existing))
                return existing;

            string[] segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string plain = string.Join("/", segments.Select(s => { SplitFolderSegment(s, out string n, out _); return n; }));
            SplitFolderSegment(segments[segments.Length - 1], out string name, out UUID id);

            FolderRecord f = new FolderRecord { Entry = entry, Path = plain, Name = name, FolderId = id };
            Clean(f);
            m_folders[rel] = f;
            m_report.FolderCount++;
            if (m_report.Folders.Count < m_opt.MaxRetainedRecords)
                m_report.Folders.Add(f);
            else
                m_report.Retention.FoldersTruncated = true;

            // parents implied by the path
            if (segments.Length > 1)
                EnsureFolder(string.Join("/", segments.Take(segments.Length - 1)), entry);
            Emit(f);
            return f;
        }

        private void HandleFile(TarStreamReader tar, TarEntryHeader h)
        {
            string path = Normalise(h.Name, out string regionDir);
            NoteKindFromPath(path);

            if (path == ArchiveConstants.CONTROL_FILE_PATH)
            {
                byte[] body = ReadCapped(tar, h, m_opt.MaxMetadataBodyBytes, "control file");
                if (body == null)
                    return;
                bool first = tar.EntriesRead == 1;
                try
                {
                    m_control = MetadataParsers.ParseControl(body);
                    ApplyControl(first);
                }
                catch (System.Xml.XmlException e)
                {
                    Problem(h.Name, "control file is not valid XML: " + e.Message, true);
                }
                return;
            }

            if (path.StartsWith(ArchiveConstants.OBJECTS_PATH, StringComparison.Ordinal))
            {
                byte[] body = ReadCapped(tar, h, m_opt.MaxObjectBodyBytes, "object file");
                if (body == null)
                    return;
                ProcessObjectBody(body, h.Name, regionDir, null, RecordContext.Archive);
            }
            else if (path.StartsWith(ArchiveConstants.ASSETS_PATH, StringComparison.Ordinal))
                HandleAsset(tar, h, path);
            else if (path.StartsWith(ArchiveConstants.INVENTORY_PATH, StringComparison.Ordinal))
                HandleIarItem(tar, h, path);
            else if (path.StartsWith(ArchiveConstants.LANDDATA_PATH, StringComparison.Ordinal))
            {
                byte[] body = ReadCapped(tar, h, m_opt.MaxMetadataBodyBytes, "parcel file");
                if (body == null)
                    return;
                try
                {
                    ParcelRecord p = MetadataParsers.ParseParcel(body);
                    p.Entry = h.Name;
                    p.RegionDir = regionDir;
                    Clean(p);
                    m_report.ParcelCount++;
                    if (m_report.Parcels.Count < m_opt.MaxRetainedRecords)
                        m_report.Parcels.Add(p);
                    if (p.MediaId != UUID.Zero)
                        AddReference(p.MediaId, ReferenceKind.ParcelMedia, Certainty.Certain, (int)AssetType.Texture,
                            new ReferrerInfo { RecordKind = "parcel", Name = p.Name, Id = p.GroupId, Creator = null, OwnerId = p.OwnerId }, h.Name, RecordContext.Archive, null);
                    Emit(p);
                }
                catch (System.Xml.XmlException e)
                {
                    Problem(h.Name, "parcel file is not valid XML: " + e.Message, true);
                }
            }
            else if (path.StartsWith(ArchiveConstants.SETTINGS_PATH, StringComparison.Ordinal))
            {
                byte[] body = ReadCapped(tar, h, m_opt.MaxMetadataBodyBytes, "region settings file");
                if (body == null)
                    return;
                try
                {
                    RegionSettingsRecord s = MetadataParsers.ParseRegionSettings(body);
                    s.Entry = h.Name;
                    s.RegionDir = regionDir;
                    string file = path.Substring(ArchiveConstants.SETTINGS_PATH.Length);
                    s.RegionName = file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
                    Clean(s);
                    m_report.RegionSettings.Add(s);
                    foreach (UUID t in s.TerrainTextures)
                        if (!IsDefaultTerrain(t))
                            AddReference(t, ReferenceKind.TerrainTexture, Certainty.Certain, (int)AssetType.Texture,
                                new ReferrerInfo { RecordKind = "region settings", Name = s.RegionName }, h.Name, RecordContext.Archive, null);
                    foreach (UUID t in s.TerrainPbr)
                        if (!IsDefaultTerrain(t))
                            AddReference(t, ReferenceKind.TerrainPbr, Certainty.Certain, (int)AssetType.Material,
                                new ReferrerInfo { RecordKind = "region settings", Name = s.RegionName }, h.Name, RecordContext.Archive, null);
                    Emit(s);
                }
                catch (System.Xml.XmlException e)
                {
                    Problem(h.Name, "region settings file is not valid XML: " + e.Message, true);
                }
            }
            else
            {
                if (m_unknownEntryKinds.Add(path.Length > 0 ? path.Split('/')[0] : path))
                    Note(h.Name, "entry is not part of the OAR or IAR layout and was skipped");
                tar.SkipBody();
            }
        }

        private static bool IsDefaultTerrain(UUID id)
        {
            return id == RegionSettings.DEFAULT_TERRAIN_TEXTURE_1 || id == RegionSettings.DEFAULT_TERRAIN_TEXTURE_2
                || id == RegionSettings.DEFAULT_TERRAIN_TEXTURE_3 || id == RegionSettings.DEFAULT_TERRAIN_TEXTURE_4
                || id == RegionSettings.DEFAULT_TERRAIN_PBR_1 || id == RegionSettings.DEFAULT_TERRAIN_PBR_2
                || id == RegionSettings.DEFAULT_TERRAIN_PBR_3 || id == RegionSettings.DEFAULT_TERRAIN_PBR_4 || id == UUID.Zero;
        }

        /// <summary>Reads the body when it is within the cap; otherwise records the problem and skips it.</summary>
        private byte[] ReadCapped(TarStreamReader tar, TarEntryHeader h, long cap, string what)
        {
            if (h.Size > cap)
            {
                Problem(h.Name, what + " is " + h.Size + " bytes, over the " + cap + " byte limit; skipped", true);
                tar.SkipBody();
                return null;
            }
            return tar.ReadBody();
        }

        private void ApplyControl(bool first)
        {
            ArchiveInfo info = m_report.Archive;
            ControlFile c = m_control;
            info.ControlFileFound = true;
            info.ControlFileFirst = first;
            info.MajorVersion = c.Major;
            info.MinorVersion = c.Minor;
            info.CreationTime = c.CreationTime;
            info.AssetsIncluded = c.AssetsIncluded;
            info.MultiRegion = c.HasRegions;
            info.Regions = c.Regions;

            if (c.HasCreationInfo || c.HasRegionInfo || c.HasRegions)
                info.Kind = ArchiveKind.Oar;
            else if (m_kindFromPaths != ArchiveKind.Unknown)
                info.Kind = m_kindFromPaths;
            else if (c.AssetsIncluded.HasValue)
                info.Kind = ArchiveKind.Iar;

            CallBegin();
        }

        private void CallBegin()
        {
            if (m_beginCalled)
                return;
            m_beginCalled = true;
            foreach (IArchiveCheck check in m_checks)
                Guarded(check, () => check.Begin(m_report.Archive));
        }

        // ---------------------------------------------------------------- objects

        private void ProcessObjectBody(byte[] body, string entry, string regionDir, UUID? boxedAsset, RecordContext context)
        {
            ParsedObject obj;
            try
            {
                obj = ObjectXmlParser.Parse(body);
            }
            catch (System.Xml.XmlException e)
            {
                Problem(entry, "object XML is not valid: " + e.Message, true);
                return;
            }

            if (obj.Groups.Count == 0)
            {
                Problem(entry, "no parts found in object file", true);
                return;
            }

            if (obj.PhloxScriptStates > 0)
                AddLegacy("script-state-phlox", boxedAsset, obj.PhloxScriptStates, 0, entry);
            m_report.ScriptStateBlobs += obj.ScriptStates;

            BoxedObjectRecord boxed = null;
            if (boxedAsset.HasValue)
            {
                boxed = new BoxedObjectRecord
                {
                    Entry = entry,
                    Context = RecordContext.Boxed,
                    AssetId = boxedAsset.Value,
                    BoxedAssetId = boxedAsset.Value,
                    Format = obj.Format,
                    LinksetCount = obj.Groups.Count,
                    PartCount = obj.Groups.Sum(g => g.Parts.Count),
                    ItemCount = obj.Groups.Sum(g => g.Parts.Sum(p => p.Items.Count)),
                    RootCreator = obj.Groups[0].Parts.First(p => p.Part.IsRoot).Part.Creator
                };
                Clean(boxed.RootCreator);
                m_boxed[boxedAsset.Value] = boxed;
                m_report.BoxedObjectCount++;
                if (m_report.BoxedObjects.Count < m_opt.MaxRetainedRecords)
                    m_report.BoxedObjects.Add(boxed);
                else
                    m_report.Retention.BoxedTruncated = true;
                Credit(boxed.RootCreator, 0, 0, 0, 1, null);
                Emit(boxed);
            }

            foreach (ParsedGroup g in obj.Groups)
            {
                LinksetRecord ls = new LinksetRecord
                {
                    Entry = entry,
                    Context = context,
                    BoxedAssetId = boxedAsset,
                    RegionDir = regionDir,
                    LinksetId = g.RootId,
                    Name = g.Name,
                    X = g.X,
                    Y = g.Y,
                    Z = g.Z,
                    PrimCount = g.Parts.Count
                };
                Clean(ls);
                if (boxedAsset.HasValue)
                    m_report.BoxedLinksetCount++;
                else
                    m_report.LinksetCount++;
                if (!boxedAsset.HasValue && m_report.Linksets.Count < m_opt.MaxRetainedRecords)
                    m_report.Linksets.Add(ls);
                else if (!boxedAsset.HasValue)
                    m_report.Retention.LinksetsTruncated = true;
                ParsedPart root = g.Parts.First(p => p.Part.IsRoot);
                Credit(root.Part.Creator, 0, 1, 0, 0, root.Part.OwnerId);
                Emit(ls);

                foreach (ParsedPart pp in g.Parts)
                {
                    PartRecord part = pp.Part;
                    part.Entry = entry;
                    part.Context = context;
                    part.BoxedAssetId = boxedAsset;
                    part.RegionDir = regionDir;
                    Clean(part);
                    if (boxedAsset.HasValue)
                        m_report.BoxedPartCount++;
                    else
                        m_report.PartCount++;
                    if (m_report.Parts.Count < m_opt.MaxRetainedRecords)
                        m_report.Parts.Add(part);
                    else
                        m_report.Retention.PartsTruncated = true;
                    Credit(part.Creator, 1, 0, 0, 0, part.OwnerId, part.CreationDate);
                    Emit(part);

                    ReferrerInfo partRef = new ReferrerInfo { RecordKind = "part", Name = part.Name, Id = part.PartId, Creator = part.Creator, OwnerId = part.OwnerId };
                    foreach (RefInfo r in pp.Refs)
                        AddReference(r.Id, r.Kind, r.Certainty, r.AssetType, partRef, entry, context, boxedAsset);
                    if (pp.HasDynAttrs)
                        m_unknownEntryKinds.Add("dynattrs");

                    foreach (ItemRecord item in pp.Items)
                    {
                        item.Entry = entry;
                        item.Context = context;
                        item.BoxedAssetId = boxedAsset;
                        item.RegionDir = regionDir;
                        Clean(item);
                        if (boxedAsset.HasValue)
                            m_report.BoxedItemCount++;
                        else
                            m_report.PrimItemCount++;
                        if (m_report.Items.Count < m_opt.MaxRetainedRecords)
                            m_report.Items.Add(item);
                        else
                            m_report.Retention.ItemsTruncated = true;
                        Credit(item.Creator, 0, 0, 1, 0, item.OwnerId, item.CreationDate);
                        Emit(item);
                        RegisterItemAsset(item, boxedAsset);
                    }
                }
            }
        }

        private void RegisterItemAsset(ItemRecord item, UUID? holderObject)
        {
            if (item.AssetId == UUID.Zero)
                return;
            int type = item.AssetType ?? -1;
            if (type == (int)AssetType.Link || type == (int)AssetType.LinkFolder)
                return;

            ReferrerInfo who = new ReferrerInfo { RecordKind = "item", Name = item.Name, Id = item.ItemId, Creator = item.Creator, OwnerId = item.OwnerId };
            AddReference(item.AssetId, ReferenceKind.InventoryItem, Certainty.Certain, item.AssetType, who, item.Entry, item.Context, holderObject);

            if (type == (int)AssetType.Object || type < 0)
            {
                UUID parent = holderObject ?? UUID.Zero;
                if (!m_objectEdges.TryGetValue(parent, out List<UUID> list))
                    m_objectEdges[parent] = list = new List<UUID>();
                list.Add(item.AssetId);
            }

            if ((type < 0 || type == (int)AssetType.Object || TextScan.IsScannedType(type)) && m_holderEntries < 2_000_000)
            {
                if (!m_holders.TryGetValue(item.AssetId, out List<HolderInfo> holders))
                    m_holders[item.AssetId] = holders = new List<HolderInfo>();
                if (holders.Count < m_opt.MaxReferrersPerAsset)
                {
                    holders.Add(new HolderInfo { Kind = "item", Name = item.Name, Id = item.ItemId, Creator = item.Creator, OwnerId = item.OwnerId });
                    m_holderEntries++;
                }
            }
        }

        // ---------------------------------------------------------------- IAR items

        private void HandleIarItem(TarStreamReader tar, TarEntryHeader h, string path)
        {
            string rel = path.Substring(ArchiveConstants.INVENTORY_PATH.Length);
            if (!rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                tar.SkipBody();
                return;
            }

            byte[] body = ReadCapped(tar, h, m_opt.MaxMetadataBodyBytes, "inventory item file");
            if (body == null)
                return;

            ItemRecord item;
            try
            {
                item = MetadataParsers.ParseIarItem(body);
            }
            catch (System.Xml.XmlException e)
            {
                Problem(h.Name, "inventory item is not valid XML: " + e.Message, true);
                return;
            }

            int slash = rel.LastIndexOf('/');
            string folderRel = slash > 0 ? rel.Substring(0, slash) : string.Empty;
            item.Entry = h.Name;
            item.Context = RecordContext.Archive;
            if (folderRel.Length > 0)
            {
                FolderRecord f = EnsureFolder(folderRel, h.Name);
                item.FolderId = f.FolderId;
                item.FolderPath = f.Path;
                f.ItemCount++;
            }
            Clean(item);
            m_report.IarItemCount++;
            if (m_report.Items.Count < m_opt.MaxRetainedRecords)
                m_report.Items.Add(item);
            else
                m_report.Retention.ItemsTruncated = true;
            Credit(item.Creator, 0, 0, 1, 0, item.OwnerId, item.CreationDate);
            Emit(item);
            RegisterItemAsset(item, null);
        }

        // ---------------------------------------------------------------- assets

        private static readonly Dictionary<string, string> HalcyonExtensions = new Dictionary<string, string>
        {
            ["_lostandfoundfolder.txt"] = "folder (lost and found)",
            ["_rootfolder.txt"] = "folder (root)",
            ["_snapshotfolder.txt"] = "folder (snapshot)",
            ["_trashfolder.txt"] = "folder (trash)"
        };

        public static string TypeName(int type)
        {
            if (type < 0)
                return "unknown";
            if (Enum.IsDefined(typeof(AssetType), (sbyte)type))
                return ((AssetType)(sbyte)type).ToString();
            return "type " + type;
        }

        private void HandleAsset(TarStreamReader tar, TarEntryHeader h, string path)
        {
            string filename = path.Substring(ArchiveConstants.ASSETS_PATH.Length);
            int indx = filename.LastIndexOf(ArchiveConstants.ASSET_EXTENSION_SEPARATOR, StringComparison.Ordinal);
            string idText = indx >= 32 ? filename.Substring(0, indx) : filename;
            string ext = indx >= 32 ? filename.Substring(indx) : string.Empty;

            if (!UUID.TryParse(idText, out UUID id))
            {
                Problem(h.Name, "asset file name does not start with an asset id", false);
                tar.SkipBody();
                return;
            }

            int type = -1;
            bool recognised = false;
            string note = null;
            if (ext.Length > 0 && ArchiveConstants.EXTENSION_TO_ASSET_TYPE.TryGetValue(ext, out sbyte t))
            {
                type = t;
                recognised = true;
            }
            else if (ext.Length > 0 && HalcyonExtensions.TryGetValue(ext, out string halcyonName))
            {
                type = (int)AssetType.Folder;
                recognised = true;
                note = halcyonName;
            }
            else
                note = ext.Length == 0 ? "no type extension in the file name" : "unknown type extension " + Sanitize.Text(ext);

            m_assets[id] = new AssetEntry { Type = type, Size = h.Size, Recognised = recognised };
            m_report.AssetCount++;
            AssetRecord rec = new AssetRecord
            {
                Entry = h.Name,
                AssetId = id,
                AssetType = type,
                TypeName = TypeName(type),
                Size = h.Size,
                Recognised = recognised,
                Note = note
            };
            Clean(rec);

            bool isObject = type == (int)AssetType.Object;
            bool isText = TextScan.IsScannedType(type);
            byte[] body = null;
            if (isObject)
                body = ReadCapped(tar, h, m_opt.MaxObjectBodyBytes, "object asset");
            else if (isText)
                body = ReadCapped(tar, h, m_opt.MaxTextBodyBytes, "text asset");
            else
                tar.SkipBody();

            rec.BodyRead = body != null;
            if (m_report.Assets.Count < m_opt.MaxRetainedRecords)
                m_report.Assets.Add(rec);
            else
                m_report.Retention.AssetsTruncated = true;
            Emit(rec);

            if (body == null)
                return;

            if (isObject)
                ProcessObjectAsset(body, h.Name, id);
            else
                ProcessTextAsset(body, h.Name, id, type);
        }

        private void ProcessObjectAsset(byte[] body, string entry, UUID id)
        {
            int start = 0;
            if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
                start = 3;
            while (start < body.Length && (body[start] == ' ' || body[start] == '\t' || body[start] == '\r' || body[start] == '\n'))
                start++;

            if (start < body.Length && body[start] == (byte)'<')
            {
                ProcessObjectBody(body, entry, string.Empty, id, RecordContext.Boxed);
                return;
            }

            // Not XML: a binary object payload from another fork. Detected and counted, never decoded.
            string kind;
            if (body.Length >= 4 && body[0] == 0x49 && body[1] == 0x54 && body[2] == 0x47 && body[3] == 0x01)
                kind = "binary-object-thoosa-group";
            else if (body.Length >= 4 && body[0] == 0x49 && body[1] == 0x54 && body[2] == 0x43 && body[3] == 0x01)
                kind = "binary-object-thoosa-coalesced";
            else
                kind = "binary-object-unknown";
            AddLegacy(kind, id, 1, body.Length, entry);
        }

        private void ProcessTextAsset(byte[] body, string entry, UUID id, int type)
        {
            TextAssetBody tb = new TextAssetBody { Entry = Sanitize.Text(entry), AssetId = id, AssetType = type, Bytes = body };
            foreach (IArchiveCheck check in m_checks)
                Guarded(check, () => check.OnTextAsset(tb));

            List<RefInfo> refs = TextScan.Scan(type, body);
            if (refs.Count == 0)
                return;

            m_holders.TryGetValue(id, out List<HolderInfo> holders);
            foreach (RefInfo r in refs)
            {
                if (holders == null || holders.Count == 0)
                    AddReference(r.Id, r.Kind, r.Certainty, r.AssetType,
                        new ReferrerInfo { RecordKind = "text asset", Name = TypeName(type), Id = id }, entry, RecordContext.Archive, null);
                else
                    foreach (HolderInfo hi in holders)
                        AddReference(r.Id, r.Kind, r.Certainty, r.AssetType,
                            new ReferrerInfo { RecordKind = hi.Kind, Name = hi.Name, Id = hi.Id, Creator = hi.Creator, OwnerId = hi.OwnerId }, entry, RecordContext.Archive, null);
            }
        }

        private void AddLegacy(string kind, UUID? asset, long count, long bytes, string entry)
        {
            if (!m_legacy.TryGetValue(kind, out LegacySummary s))
                m_legacy[kind] = s = new LegacySummary { Kind = kind };
            s.Count += count;
            s.Bytes += bytes;
            Emit(new LegacyPayloadRecord { Entry = Sanitize.Text(entry), Kind = kind, AssetId = asset, Bytes = bytes });
        }

        // ---------------------------------------------------------------- references and totals

        private void AddReference(UUID id, ReferenceKind kind, Certainty certainty, int? assetType, ReferrerInfo who, string entry, RecordContext context, UUID? boxed)
        {
            if (id == UUID.Zero)
                return;

            if (!m_refs.TryGetValue(id, out RefAgg agg))
                m_refs[id] = agg = new RefAgg();
            agg.KindMask |= 1 << (int)kind;
            if (certainty == Certainty.Certain)
                agg.AnyCertain = true;
            if (assetType.HasValue && !agg.AssetType.HasValue)
                agg.AssetType = assetType;
            agg.Uses++;

            string key = who?.Creator == null ? "(none)" : (who.Creator.Id != UUID.Zero ? who.Creator.Id.ToString() : "name:" + who.Creator.Name);
            if (!agg.Users.TryGetValue(key, out AssetUser u))
            {
                if (agg.Users.Count >= m_opt.MaxReferrersPerAsset)
                    agg.OtherUsers++;
                else
                    agg.Users[key] = u = new AssetUser { CreatorId = who?.Creator?.Id ?? UUID.Zero, CreatorName = who?.Creator?.Name ?? string.Empty };
            }
            if (u != null)
                u.Uses++;

            m_report.ReferencedAssetIds.Add(id);
            if (certainty == Certainty.Certain)
                m_report.CertainReferencedAssetIds.Add(id);

            Emit(new ReferenceRecord
            {
                Entry = Sanitize.Text(entry),
                Context = context,
                BoxedAssetId = boxed,
                AssetId = id,
                AssetType = assetType,
                Kind = kind,
                Certainty = certainty,
                Referrer = who
            });
        }

        private string CreatorKey(CreatorRef c) => c.Id != UUID.Zero ? c.Id.ToString() : (c.Name.Length > 0 ? "name:" + c.Name : "blank");

        private void Credit(CreatorRef creator, int parts, int linksets, int items, int boxedObjects, UUID? owner, int? date = null)
        {
            string key = CreatorKey(creator);
            if (key == "blank")
                m_report.BlankCreatorRecords += parts + items;
            if (!m_creators.TryGetValue(key, out PartyTotals t))
            {
                t = new PartyTotals { Id = creator.Id, Name = creator.Name, Home = creator.Home, IsLibrary = creator.IsLibrary };
                m_creators[key] = t;
            }
            if (t.Name.Length == 0 && creator.Name.Length > 0)
            {
                t.Name = creator.Name;
                t.Home = creator.Home;
            }
            t.Parts += parts;
            t.Linksets += linksets;
            t.Items += items;
            t.BoxedObjects += boxedObjects;
            if (creator.HasData)
                t.WithCreatorData += parts + items;
            Widen(t, date);

            if (owner.HasValue && (parts + items) > 0)
            {
                if (!m_owners.TryGetValue(owner.Value, out PartyTotals o))
                    m_owners[owner.Value] = o = new PartyTotals { Id = owner.Value };
                o.Parts += parts;
                o.Items += items;
                Widen(o, date);
            }
        }

        private static void Widen(PartyTotals t, int? date)
        {
            if (!date.HasValue || date.Value <= 0)
                return;
            if (!t.EarliestDate.HasValue || date.Value < t.EarliestDate.Value)
                t.EarliestDate = date;
            if (!t.LatestDate.HasValue || date.Value > t.LatestDate.Value)
                t.LatestDate = date;
        }

        // ---------------------------------------------------------------- bookkeeping

        private void Emit(ArchiveRecord record)
        {
            foreach (IArchiveCheck check in m_checks)
                Guarded(check, () => check.OnRecord(record));
        }

        private void Guarded(IArchiveCheck check, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Problem(null, "check '" + check.Id + "' failed: " + e.Message, false);
            }
        }

        private void Problem(string entry, string message, bool partial)
        {
            if (partial)
                m_partial = true;
            m_report.ProblemCount++;
            if (m_report.Problems.Count < m_opt.MaxProblems)
                m_report.Problems.Add(new ProblemRecord { Entry = entry == null ? null : Sanitize.Text(entry), Message = Sanitize.Text(message) });
        }

        private void Note(string entry, string message) => Problem(entry, message, false);

        private static void Clean(CreatorRef c)
        {
            c.Data = Sanitize.Text(c.Data);
            c.Home = Sanitize.Text(c.Home);
            c.Name = Sanitize.Text(c.Name);
            c.Raw = Sanitize.Text(c.Raw);
        }

        private static void Clean(LinksetRecord r) { r.Entry = Sanitize.Text(r.Entry); r.Name = Sanitize.Text(r.Name); r.RegionDir = Sanitize.Text(r.RegionDir); }
        private static void Clean(PartRecord r) { r.Entry = Sanitize.Text(r.Entry); r.Name = Sanitize.Text(r.Name); r.LinksetName = Sanitize.Text(r.LinksetName); r.RegionDir = Sanitize.Text(r.RegionDir); Clean(r.Creator); }
        private static void Clean(ItemRecord r) { r.Entry = Sanitize.Text(r.Entry); r.Name = Sanitize.Text(r.Name); r.PartName = Sanitize.Text(r.PartName); r.FolderPath = Sanitize.Text(r.FolderPath); r.RegionDir = Sanitize.Text(r.RegionDir); Clean(r.Creator); }
        private static void Clean(FolderRecord r) { r.Entry = Sanitize.Text(r.Entry); r.Name = Sanitize.Text(r.Name); r.Path = Sanitize.Text(r.Path); }
        private static void Clean(ParcelRecord r) { r.Entry = Sanitize.Text(r.Entry); r.Name = Sanitize.Text(r.Name); r.RegionDir = Sanitize.Text(r.RegionDir); }
        private static void Clean(RegionSettingsRecord r) { r.Entry = Sanitize.Text(r.Entry); r.RegionName = Sanitize.Text(r.RegionName); r.RegionDir = Sanitize.Text(r.RegionDir); }
        private static void Clean(AssetRecord r) { r.Entry = Sanitize.Text(r.Entry); r.Note = Sanitize.Text(r.Note); }

        // ---------------------------------------------------------------- the end

        private ArchiveReport Finish()
        {
            ArchiveInfo info = m_report.Archive;
            if (info.Kind == ArchiveKind.Unknown && m_kindFromPaths != ArchiveKind.Unknown)
                info.Kind = m_kindFromPaths;

            if (info.EntryCount == 0 && m_report.Status != InspectionStatus.NotAnArchive)
            {
                m_report.Status = InspectionStatus.NotAnArchive;
                m_report.StopReason ??= "the file holds no archive entries";
                return m_report;
            }

            if (m_report.Status == InspectionStatus.NotAnArchive || (!info.ControlFileFound && m_kindFromPaths == ArchiveKind.Unknown && info.EntryCount > 0 && m_report.PartCount + m_report.BoxedPartCount + m_report.IarItemCount == 0 && m_assets.Count == 0))
            {
                m_report.Status = InspectionStatus.NotAnArchive;
                if (m_report.StopReason == null)
                    m_report.StopReason = "no archive.xml control file and no OAR or IAR paths found";
                return m_report;
            }

            if (!info.ControlFileFound)
                Problem(null, "control file archive.xml was not found", true);
            CallBegin();

            // region ids for retained records
            Dictionary<string, RegionInfo> regions = info.Regions.Where(r => r.Dir != null).GroupBy(r => r.Dir).ToDictionary(g => g.Key, g => g.First());
            foreach (PartRecord p in m_report.Parts)
                if (regions.TryGetValue(p.RegionDir, out RegionInfo ri))
                    p.RegionId = ri.Id;
            foreach (LinksetRecord l in m_report.Linksets)
                if (regions.TryGetValue(l.RegionDir, out RegionInfo ri))
                    l.RegionId = ri.Id;

            ComputeAssets();
            ComputeBoxedDepth();

            m_report.Creators = m_creators.Values.OrderByDescending(c => c.Records).ThenBy(c => c.Name).ToList();
            m_report.Owners = m_owners.Values.OrderByDescending(c => c.Records).ToList();
            m_report.LegacyPayloads = m_legacy.Values.OrderBy(l => l.Kind).ToList();

            foreach (IArchiveCheck check in m_checks)
                Guarded(check, () => check.Complete(m_report, m_report.Findings));

            m_report.LoaderWarnings = BuildLoaderWarnings();
            m_report.Status = m_partial ? InspectionStatus.Partial : InspectionStatus.Complete;
            return m_report;
        }

        private List<string> BuildLoaderWarnings()
        {
            List<string> lines = new List<string>();
            ArchiveKind kind = m_report.Archive.Kind;
            if (kind != ArchiveKind.Iar)
            {
                lines.Add(LoaderBehaviour.OarRewrite);
                lines.Add(LoaderBehaviour.OarOwnerRewrite);
            }
            if (kind != ArchiveKind.Oar)
                lines.Add(LoaderBehaviour.IarRewrite);
            return lines;
        }

        private static readonly HashSet<int> CreatorlessTypes = new HashSet<int>
        {
            (int)AssetType.Texture, (int)AssetType.TextureTGA, (int)AssetType.ImageJPEG, (int)AssetType.ImageTGA,
            (int)AssetType.Sound, (int)AssetType.SoundWAV, (int)AssetType.Animation, (int)AssetType.Mesh,
            (int)AssetType.Material, (int)AssetType.OSMaterial
        };

        private void ComputeAssets()
        {
            bool hasAssets = m_report.Archive.AssetsIncluded != false;
            Dictionary<int, AssetTypeSummary> byType = new Dictionary<int, AssetTypeSummary>();
            foreach (KeyValuePair<UUID, AssetEntry> kv in m_assets)
            {
                m_report.PresentAssetIds.Add(kv.Key);
                if (!byType.TryGetValue(kv.Value.Type, out AssetTypeSummary s))
                    byType[kv.Value.Type] = s = new AssetTypeSummary { AssetType = kv.Value.Type, TypeName = TypeName(kv.Value.Type) };
                s.Count++;
                s.Bytes += kv.Value.Size;
            }
            m_report.AssetsByType = byType.Values.OrderByDescending(a => a.Bytes).ToList();

            m_report.ReferencedAssetCount = m_refs.Count;
            List<AssetUse> uses = new List<AssetUse>();
            foreach (KeyValuePair<UUID, RefAgg> kv in m_refs)
            {
                UUID id = kv.Key;
                RefAgg agg = kv.Value;
                bool present = m_assets.TryGetValue(id, out AssetEntry entry);
                int? type = present && entry.Recognised ? entry.Type : agg.AssetType;
                bool wellKnown = WellKnownAssets.IsWellKnown(id);

                if (!present && agg.AnyCertain && !wellKnown && hasAssets)
                {
                    m_report.MissingAssetCount++;
                    if (m_report.MissingAssets.Count < m_opt.MaxRetainedRecords)
                        m_report.MissingAssets.Add(id);
                }

                bool creatorless = !type.HasValue || CreatorlessTypes.Contains(type.Value);
                if (creatorless && !wellKnown)
                {
                    List<string> kinds = new List<string>();
                    foreach (ReferenceKind k in Enum.GetValues(typeof(ReferenceKind)))
                        if ((agg.KindMask & (1 << (int)k)) != 0)
                            kinds.Add(k.ToString());
                    uses.Add(new AssetUse
                    {
                        AssetId = id,
                        AssetType = type,
                        TypeName = type.HasValue ? TypeName(type.Value) : "unknown",
                        Present = present,
                        Uncertain = !agg.AnyCertain,
                        Uses = agg.Uses,
                        Kinds = kinds,
                        Users = agg.Users.Values.OrderByDescending(u => u.Uses).ToList()
                    });
                }
            }

            m_report.AssetsWithoutCreator = uses.OrderByDescending(u => u.Uses).Take(Math.Max(1, Math.Min(m_opt.MaxRetainedRecords, 100_000))).ToList();

            foreach (UUID id in m_assets.Keys)
            {
                if (!m_refs.ContainsKey(id))
                {
                    m_report.UnreferencedAssetCount++;
                    if (m_report.UnreferencedAssets.Count < m_opt.MaxRetainedRecords)
                        m_report.UnreferencedAssets.Add(id);
                }
            }
        }

        private void ComputeBoxedDepth()
        {
            // Breadth-first from the assets held by archive items and parts. A visited set stops cycles;
            // objects held deeper than the limit are reported and not followed further.
            Dictionary<UUID, int> depth = new Dictionary<UUID, int>();
            Queue<UUID> queue = new Queue<UUID>();
            Dictionary<UUID, int> holderCount = new Dictionary<UUID, int>();

            if (m_objectEdges.TryGetValue(UUID.Zero, out List<UUID> top))
                foreach (UUID id in top)
                {
                    holderCount[id] = holderCount.TryGetValue(id, out int c) ? c + 1 : 1;
                    if (!depth.ContainsKey(id))
                    {
                        depth[id] = 1;
                        queue.Enqueue(id);
                    }
                }

            while (queue.Count > 0)
            {
                UUID id = queue.Dequeue();
                int d = depth[id];
                if (!m_objectEdges.TryGetValue(id, out List<UUID> children))
                    continue;
                foreach (UUID child in children)
                {
                    holderCount[child] = holderCount.TryGetValue(child, out int c) ? c + 1 : 1;
                    if (depth.ContainsKey(child))
                    {
                        if (child == id || IsAncestorLoop(depth, child, d))
                            Problem(null, "boxed object " + child + " holds itself through a cycle; not followed again", false);
                        continue;
                    }
                    if (d + 1 > m_opt.MaxBoxedDepth)
                    {
                        Problem(null, "boxed object " + child + " is nested deeper than " + m_opt.MaxBoxedDepth + " levels; not followed", false);
                        depth[child] = d + 1;
                        continue;
                    }
                    depth[child] = d + 1;
                    queue.Enqueue(child);
                }
            }

            foreach (KeyValuePair<UUID, BoxedObjectRecord> kv in m_boxed)
            {
                kv.Value.Depth = depth.TryGetValue(kv.Key, out int dd) ? dd : 0;
                kv.Value.HolderCount = holderCount.TryGetValue(kv.Key, out int hc) ? hc : 0;
            }
        }

        private static bool IsAncestorLoop(Dictionary<UUID, int> depth, UUID child, int currentDepth)
        {
            return depth[child] <= currentDepth;
        }
    }

    /// <summary>
    /// Sits between the decompressor and the tar reader: counts the bytes produced and stops the pass when the
    /// total size or the compression ratio goes past the limits, naming the entry being read.
    /// </summary>
    private sealed class GuardStream : Stream
    {
        private readonly Stream m_inner;
        private readonly CountingStream m_compressed;
        private readonly InspectorOptions m_options;
        private readonly Func<string> m_entry;
        public long Total { get; private set; }

        public GuardStream(Stream inner, CountingStream compressed, InspectorOptions options, Func<string> entry)
        {
            m_inner = inner;
            m_compressed = compressed;
            m_options = options;
            m_entry = entry;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = m_inner.Read(buffer, offset, count);
            if (n > 0)
            {
                Total += n;
                if (Total > m_options.MaxTotalUncompressedBytes)
                    throw new ArchiveLimitException("uncompressed size passed " + m_options.MaxTotalUncompressedBytes + " bytes while reading '" + Sanitize.Text(m_entry() ?? "") + "'", m_entry());

                if (!ReferenceEquals(m_inner, m_compressed) && Total >= m_options.RatioCheckMinBytes && m_compressed.BytesRead > 0)
                {
                    double ratio = (double)Total / m_compressed.BytesRead;
                    if (ratio > m_options.MaxCompressionRatio)
                        throw new ArchiveLimitException(
                            "compression ratio " + ratio.ToString("0") + ":1 is over the limit of " + m_options.MaxCompressionRatio.ToString("0") +
                            ":1 while reading '" + Sanitize.Text(m_entry() ?? "") + "'", m_entry());
                }
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Removes characters that could disturb a terminal or a log when archive text is printed.</summary>
public static class Sanitize
{
    public static string Text(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        bool dirty = false;
        foreach (char c in s)
            if (IsBad(c)) { dirty = true; break; }
        if (!dirty)
            return s;

        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!IsBad(c))
                sb.Append(c);
        return sb.ToString();
    }

    private static bool IsBad(char c)
    {
        // controls, line and paragraph separators, and the bidirectional override and isolate characters
        return char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029 || (c >= (char)0x202A && c <= (char)0x202E) || (c >= (char)0x2066 && c <= (char)0x2069);
    }
}
