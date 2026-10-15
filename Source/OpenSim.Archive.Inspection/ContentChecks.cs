/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// Assets and what refers to them: extensions nobody knows, files whose stored type differs from what their users say,
/// duplicate ids, object assets that do not parse, references with no asset and assets nothing refers to.
/// </summary>
public sealed class ReferencesCheck : StandardCheckBase
{
    public override string Id => "references";

    private sealed class Claim
    {
        public int Type;
        public ReferenceKind Kind;
        public string Name;
        public string Id;
        public string Where;
    }

    private readonly FindingBuilder m_unknown;
    private readonly FindingBuilder m_duplicate;
    private readonly FindingBuilder m_objects;
    private readonly HashSet<ulong> m_seen = new HashSet<ulong>();
    private readonly Dictionary<UUID, int> m_types = new Dictionary<UUID, int>();
    private readonly Dictionary<UUID, List<Claim>> m_claims = new Dictionary<UUID, List<Claim>>();
    private const int MaxClaimsPerAsset = 3;

    public ReferencesCheck(InspectorOptions options) : base(options)
    {
        m_unknown = NewBuilder();
        m_duplicate = NewBuilder();
        m_objects = NewBuilder();
    }

    private static ulong Hash(UUID id)
    {
        byte[] b = id.GetBytes();
        ulong h = 1469598103934665603UL;
        foreach (byte x in b)
        {
            h ^= x;
            h *= 1099511628211UL;
        }
        return h;
    }

    public override void OnRecord(ArchiveRecord record)
    {
        switch (record)
        {
            case AssetRecord a:
                if (!a.Recognised)
                    m_unknown.Add(a.Entry, a.AssetId.ToString(), a.Entry, a.Note);
                else
                    m_types[a.AssetId] = a.AssetType;
                if (!m_seen.Add(Hash(a.AssetId)))
                    m_duplicate.Add(a.Entry, a.AssetId.ToString(), a.Entry, "asset id appears in more than one entry");
                break;

            case ParseFailureRecord f when f.What == "boxed-object":
                m_objects.Add(f.Entry, f.AssetId?.ToString(), f.Entry, f.Message);
                break;

            case ReferenceRecord r when r.Certainty == Certainty.Certain && r.AssetType.HasValue && r.Kind != ReferenceKind.InventoryItem:
                AddClaim(r.AssetId, new Claim
                {
                    Type = r.AssetType.Value,
                    Kind = r.Kind,
                    Name = r.Referrer?.Name,
                    Id = r.Referrer?.Id.ToString(),
                    Where = r.Entry
                });
                break;

            case ItemRecord i when i.Source == ItemSource.PrimInventory && i.AssetType.HasValue && i.AssetId != UUID.Zero && !TypeAgreement.IsLink(i.AssetType.Value):
                AddClaim(i.AssetId, new Claim { Type = i.AssetType.Value, Kind = ReferenceKind.InventoryItem, Name = i.Name, Id = i.ItemId.ToString(), Where = StandardChecks.Where(i) });
                break;
        }
    }

    private void AddClaim(UUID asset, Claim c)
    {
        if (!m_claims.TryGetValue(asset, out List<Claim> list))
            m_claims[asset] = list = new List<Claim>();
        if (list.Count < MaxClaimsPerAsset && !list.Any(x => x.Type == c.Type && x.Kind == c.Kind))
            list.Add(c);
    }

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        if (m_unknown.Any)
            findings.Add(m_unknown.ToFinding(Id, "REF_UNKNOWN_EXTENSION", FindingSeverity.Notice, "Asset files with an unknown extension",
                StandardChecks.N(m_unknown.Count) + " asset entries have a file extension that is not in the archive extension table (ArchiveConstants), so the loader cannot tell what type they are."));

        FindingBuilder mismatch = NewBuilder();
        foreach (KeyValuePair<UUID, List<Claim>> kv in m_claims)
        {
            if (!m_types.TryGetValue(kv.Key, out int actual))
                continue;
            foreach (Claim c in kv.Value)
                if (!TypeAgreement.ReferenceAgrees(c.Kind, c.Type, actual))
                    mismatch.Add(c.Name, c.Id, c.Where, c.Kind + " says " + ArchiveInspector.TypeName(c.Type) + "; asset " + kv.Key + " is stored as " + ArchiveInspector.TypeName(actual));
        }
        if (mismatch.Any)
            findings.Add(mismatch.ToFinding(Id, "REF_TYPE_MISMATCH", FindingSeverity.Warning, "Asset type differs from what its users say",
                StandardChecks.N(mismatch.Count) + " references from parts and prim inventory items name an asset type different from the type given by the asset file's extension."));

        if (m_duplicate.Any)
            findings.Add(m_duplicate.ToFinding(Id, "REF_DUPLICATE_ASSET", FindingSeverity.Notice, "Duplicate asset ids",
                StandardChecks.N(m_duplicate.Count) + " asset entries repeat an asset id that an earlier entry already holds."));

        if (m_objects.Any)
            findings.Add(m_objects.ToFinding(Id, "REF_OBJECT_UNPARSED", FindingSeverity.Warning, "Object assets that do not parse",
                StandardChecks.N(m_objects.Count) + " object assets could not be read as object XML, so what they hold is not counted anywhere."));

        if (Archive != null && Archive.AssetsIncluded == false)
            findings.Add(new Finding
            {
                Source = Id,
                Code = "REF_NO_ASSETS",
                Severity = FindingSeverity.Info,
                Title = "Archive saved without assets",
                Detail = "The control file says assets were not included, so references cannot be checked against asset files. " + StandardChecks.N(report.ReferencedAssetCount) + " asset ids are named.",
                Count = report.ReferencedAssetCount,
                Numbers = Nums(("referencedAssets", report.ReferencedAssetCount))
            });
        else if (report.MissingAssetCount > 0)
        {
            FindingBuilder missing = NewBuilder();
            foreach (UUID id in report.MissingAssets)
                missing.Add(string.Empty, id.ToString(), string.Empty, "named by a part, item or setting; no asset file in the archive");
            Finding f = missing.ToFinding(Id, "REF_MISSING", FindingSeverity.Notice, "Referenced assets that are missing",
                StandardChecks.N(report.MissingAssetCount) + " of " + StandardChecks.N(report.ReferencedAssetCount) + " asset ids named by the content have no asset file in the archive (well-known default assets are not counted).",
                Nums(("missing", report.MissingAssetCount), ("referenced", report.ReferencedAssetCount)));
            f.Count = report.MissingAssetCount;
            f.ExamplesTruncated |= report.MissingAssetCount > report.MissingAssets.Count;
            findings.Add(f);
        }

        if (report.UnreferencedAssetCount > 0)
        {
            FindingBuilder unref = NewBuilder();
            foreach (UUID id in report.UnreferencedAssets)
                unref.Add(string.Empty, id.ToString(), string.Empty, "an asset file that nothing in the archive names");
            Finding f = unref.ToFinding(Id, "REF_UNREFERENCED", FindingSeverity.Info, "Assets nothing refers to",
                StandardChecks.N(report.UnreferencedAssetCount) + " of " + StandardChecks.N(report.AssetCount) + " asset files are not named by any part, item, setting or text the inspector read.",
                Nums(("unreferenced", report.UnreferencedAssetCount), ("assets", report.AssetCount)));
            f.Count = report.UnreferencedAssetCount;
            f.ExamplesTruncated |= report.UnreferencedAssetCount > report.UnreferencedAssets.Count;
            findings.Add(f);
        }
    }
}

/// <summary>
/// Author lines in scripts and notecards: lines that name an author or rights holder. Listed for a person to compare
/// with the credited creator; nothing is matched.
/// </summary>
public sealed class AuthorLinesCheck : StandardCheckBase
{
    public override string Id => "author-lines";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    private const int MaxLineChars = 400;
    private const int MaxDistinct = 5000;
    private const int MaxLinesPerAsset = 200;

    // Each pattern captures the name (or nothing, for phrases that name nobody).
    private static readonly (string label, Regex rx)[] Patterns =
    {
        ("copyright", new Regex(@"(?:copyright\b|©|\(c\)(?=\s*(?:\d{4}|(?-i:[A-Z]))))\s*(?:\(c\)|©)?\s*(?<name>.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)),
        ("created by", new Regex(@"(?:created|made|written|designed|scripted|authored|built|modified|maintained)\s+by\s*[:\-]?\s*(?<name>.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)),
        ("script by", new Regex(@"(?:script|original|orig\.?|code)\s+by\s*[:\-]?\s*(?<name>.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)),
        ("author", new Regex(@"\bauthors?\b\s*(?:[:=]|\s-\s)\s*(?<name>.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)),
        ("licensed to", new Regex(@"licen[sc]ed\s+(?:to|for)\s*[:\-]?\s*(?<name>.*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)),
        ("all rights reserved", new Regex(@"all\s+rights\s+reserved", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)),
        ("do not resell", new Regex(@"do\s+not\s+(?:re-?sell|redistribute|resale)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout))
    };

    // licence boilerplate that mentions copyright without naming anyone ("the above copyright notice", "THE COPYRIGHT HOLDERS")
    private static readonly Regex Boilerplate = new Regex(@"(?:\b(?:the|above|or|and|of)\s+copyright\b|copyright\s+(?:notice|holders?|owners?|law|statement|laws)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex LicenceWord = new Regex(@"^(?:mit|bsd|gpl|lgpl|agpl|apache|cc[- ]?by.*|creative commons.*|public domain)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Years = new Regex(@"\b(?:19|20)\d\d(?:\s*[-–,]\s*(?:19|20)?\d\d)*\b", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Trailer = new Regex(@"(?:\.?\s*all\s+rights\s+reserved.*|\*/.*|-->.*|[:,;\-]?\s*(?:https?://|www\.).*|\bwho\b.*|\bbased\s+on\b.*|\bunder\b.*|\blicen[sc]e[ds]?\b.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private sealed class NameAgg
    {
        public string Display;
        public string Label;
        public long Assets;
        public Dictionary<string, long> Creators = new Dictionary<string, long>();
        public string FirstAsset;
        public string FirstLine;
    }

    private readonly Dictionary<string, NameAgg> m_names = new Dictionary<string, NameAgg>();
    private long m_scanned, m_withLines;
    private bool m_capped;

    public AuthorLinesCheck(InspectorOptions options) : base(options) { }

    public override void OnTextAsset(TextAssetBody body)
    {
        if (body.AssetType != (int)AssetType.LSLText && body.AssetType != (int)AssetType.Notecard)
            return;
        m_scanned++;

        string text;
        try { text = Encoding.UTF8.GetString(body.Bytes.Span); }
        catch (Exception) { return; }

        HashSet<string> seen = new HashSet<string>();
        List<(string key, string display, string label, string line)> found = new List<(string, string, string, string)>();
        int lines = 0;
        foreach (string raw in text.Split('\n'))
        {
            if (++lines > 20_000 || found.Count >= MaxLinesPerAsset)
                break;
            string line = raw.Length > MaxLineChars ? raw.Substring(0, MaxLineChars) : raw;
            try
            {
                if (Boilerplate.IsMatch(line))
                    continue;
            }
            catch (RegexMatchTimeoutException) { continue; }
            foreach ((string label, Regex rx) in Patterns)
            {
                Match m;
                try { m = rx.Match(line); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success)
                    continue;

                string name = m.Groups["name"].Success ? Clean(m.Groups["name"].Value) : string.Empty;
                string key = name.Length > 0 ? name.ToLowerInvariant() : "(" + label + ")";
                if (seen.Add(key))
                    found.Add((key, name.Length > 0 ? name : "(" + label + ", no name on the line)", label, line.Trim()));
                break;
            }
        }
        if (found.Count == 0)
            return;

        m_withLines++;
        List<string> creators = body.Holders.Select(h => h.Creator == null ? "(creator not known)" : StandardChecks.Who(h.Creator)).Distinct().Take(4).ToList();
        if (creators.Count == 0)
            creators.Add("(no item or part holding this asset was read)");

        foreach ((string key, string display, string label, string line) f in found)
        {
            if (!m_names.TryGetValue(f.key, out NameAgg agg))
            {
                if (m_names.Count >= MaxDistinct)
                {
                    m_capped = true;
                    continue;
                }
                m_names[f.key] = agg = new NameAgg { Display = f.display, Label = f.label, FirstAsset = body.AssetId.ToString(), FirstLine = f.line };
            }
            agg.Assets++;
            foreach (string c in creators)
            {
                agg.Creators.TryGetValue(c, out long n);
                agg.Creators[c] = n + 1;
            }
        }
    }

    private static string Clean(string s)
    {
        try
        {
            s = Trailer.Replace(s, string.Empty);
            s = Years.Replace(s, string.Empty);
        }
        catch (RegexMatchTimeoutException) { }
        s = s.Trim(' ', '\t', '\r', '*', '/', '#', '-', '=', ':', ';', '.', ',', '"', '\'', '<', '>', '[', ']', '(', ')', '©');
        s = Regex.Replace(s, @"\s+", " ", RegexOptions.CultureInvariant, Timeout);
        s = Regex.Replace(s, @"^by\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        // a name is the first clause; prose that runs on is not a name
        int cut = Regex.Match(s, @"[,;!?]|\.\s|\s[-–]\s", RegexOptions.CultureInvariant, Timeout).Index;
        if (cut > 0)
            s = s.Substring(0, cut);
        s = s.Trim(' ', '.', ',', ':', ';', '-');
        if (s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5 || s.Length > 60 || LicenceWord.IsMatch(s))
            return string.Empty;
        return s;
    }

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        if (m_names.Count == 0)
            return;

        FindingBuilder b = NewBuilder();
        foreach (NameAgg a in m_names.Values.OrderByDescending(x => x.Assets).ThenBy(x => x.Display, StringComparer.OrdinalIgnoreCase))
            b.Add(a.Display, a.FirstAsset, string.Empty,
                "in " + StandardChecks.N(a.Assets) + (a.Assets == 1 ? " asset" : " assets") + "; credited creator of the items that hold them: " +
                string.Join("; ", a.Creators.OrderByDescending(c => c.Value).Take(4).Select(c => c.Key + " x" + c.Value)) + "; first line: " + a.FirstLine);

        Finding f = b.ToFinding(Id, "AUTHOR_LINES", FindingSeverity.Info, "Author and rights-holder lines in scripts and notecards",
            "Lines in " + StandardChecks.N(m_withLines) + " of " + StandardChecks.N(m_scanned) + " scripts and notecards read name an author or rights holder; " + StandardChecks.N(m_names.Count) +
            " distinct names or phrases are listed with the creator of the items that hold them. This is text typed into the content, listed for a person to compare with the credited creator; it is not matched." +
            (m_capped ? " More than " + MaxDistinct + " distinct names were found; the rest are not listed." : ""),
            Nums(("distinctNames", m_names.Count), ("assetsWithLines", m_withLines), ("assetsScanned", m_scanned)));
        findings.Add(f);
    }
}

/// <summary>Creator data that disagrees with itself: one id with several names, one name with several ids, and creator text that does not parse.</summary>
public sealed class CreatorDataCheck : StandardCheckBase
{
    public override string Id => "creator-data";

    private readonly Dictionary<UUID, Dictionary<string, string>> m_namesOfId = new Dictionary<UUID, Dictionary<string, string>>();
    private readonly Dictionary<string, Dictionary<UUID, bool>> m_idsOfName = new Dictionary<string, Dictionary<UUID, bool>>();
    private readonly Dictionary<string, string> m_nameDisplay = new Dictionary<string, string>();
    private readonly FindingBuilder m_unparsed;
    private long m_records;
    private const int MaxPerKey = 8;
    private const int MaxKeys = 500_000;

    public CreatorDataCheck(InspectorOptions options) : base(options)
    {
        m_unparsed = NewBuilder();
    }

    public override void OnRecord(ArchiveRecord record)
    {
        if (record is PartRecord p)
            Look(p.Creator, p.Name, p.PartId.ToString(), StandardChecks.Where(p));
        else if (record is ItemRecord i)
            Look(i.Creator, i.Name, i.ItemId.ToString(), StandardChecks.Where(i));
    }

    private void Look(CreatorRef c, string name, string id, string where)
    {
        m_records++;
        if (c.IsLibrary)
            return;

        if (c.HasData)
        {
            bool badHome = c.Home.Length > 0 && !Uri.TryCreate(c.Home, UriKind.Absolute, out _);
            if (c.Name.Length == 0)
                m_unparsed.Add(name, id, where, "creator data \"" + Trim(c.Data) + "\" holds no name");
            else if (badHome)
                m_unparsed.Add(name, id, where, "creator data \"" + Trim(c.Data) + "\" has a home that is not a URL");
        }
        else if (c.Raw.Length > 0 && c.Id == UUID.Zero && c.Name.Length == 0)
            m_unparsed.Add(name, id, where, "creator field \"" + Trim(c.Raw) + "\" is neither a UUID nor an OSPA name");

        if (c.Name.Length == 0)
            return;

        string nameKey = c.Name.ToLowerInvariant() + "@" + c.Home.ToLowerInvariant();
        string display = c.Home.Length > 0 ? c.Name + " @ " + c.Home : c.Name;

        if (c.Id != UUID.Zero)
        {
            if (!m_namesOfId.TryGetValue(c.Id, out Dictionary<string, string> names))
            {
                if (m_namesOfId.Count >= MaxKeys)
                    return;
                m_namesOfId[c.Id] = names = new Dictionary<string, string>();
            }
            if (names.Count < MaxPerKey)
                names.TryAdd(nameKey, display);

            if (!m_idsOfName.TryGetValue(nameKey, out Dictionary<UUID, bool> ids))
            {
                if (m_idsOfName.Count >= MaxKeys)
                    return;
                m_idsOfName[nameKey] = ids = new Dictionary<UUID, bool>();
                m_nameDisplay[nameKey] = display;
            }
            if (ids.Count < MaxPerKey)
                ids.TryAdd(c.Id, true);
        }
    }

    private static string Trim(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s;

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        FindingBuilder manyNames = NewBuilder();
        foreach (KeyValuePair<UUID, Dictionary<string, string>> kv in m_namesOfId)
            if (kv.Value.Count > 1)
                manyNames.Add(string.Join(" | ", kv.Value.Values), kv.Key.ToString(), string.Empty, "one creator id, " + kv.Value.Count + " different names or homes");
        if (manyNames.Any)
            findings.Add(manyNames.ToFinding(Id, "CREATOR_ID_MANY_NAMES", FindingSeverity.Notice, "One creator id with different names or homes",
                StandardChecks.N(manyNames.Count) + " creator ids appear with more than one name or home grid in the archive's creator data."));

        FindingBuilder manyIds = NewBuilder();
        foreach (KeyValuePair<string, Dictionary<UUID, bool>> kv in m_idsOfName)
            if (kv.Value.Count > 1)
                manyIds.Add(m_nameDisplay[kv.Key], string.Join(" | ", kv.Value.Keys), string.Empty, "one name and home, " + kv.Value.Count + " different creator ids");
        if (manyIds.Any)
            findings.Add(manyIds.ToFinding(Id, "CREATOR_NAME_MANY_IDS", FindingSeverity.Notice, "One name and home with different creator ids",
                StandardChecks.N(manyIds.Count) + " names with the same home grid appear with more than one creator id in the archive's creator data."));

        if (m_unparsed.Any)
            findings.Add(m_unparsed.ToFinding(Id, "CREATOR_DATA_UNPARSED", FindingSeverity.Notice, "Creator data that does not parse",
                StandardChecks.N(m_unparsed.Count) + " of " + StandardChecks.N(m_records) + " records carry creator text the inspector could not split into a home and a name."));
    }
}

/// <summary>The tar file itself: entries no loader reads, repeated names, directories that hold data and payloads from other forks.</summary>
public sealed class ArchiveIntegrityCheck : StandardCheckBase
{
    public override string Id => "archive-integrity";

    private static readonly HashSet<string> OarPrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        ArchiveConstants.CONTROL_FILE_PATH, ArchiveConstants.OBJECTS_PATH, ArchiveConstants.ASSETS_PATH, ArchiveConstants.TERRAINS_PATH,
        ArchiveConstants.SETTINGS_PATH, ArchiveConstants.LANDDATA_PATH, ArchiveConstants.REGIONS_PATH
    };

    private static readonly HashSet<string> IarPrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        ArchiveConstants.CONTROL_FILE_PATH, ArchiveConstants.ASSETS_PATH, ArchiveConstants.INVENTORY_PATH
    };

    private static readonly HashSet<string> UnionPrefixes = new HashSet<string>(OarPrefixes.Concat(IarPrefixes), StringComparer.Ordinal);

    // entries under no prefix either loader reads are all kept (up to the example bound); entries under a prefix only one
    // loader reads are tallied per prefix, with the first few kept, so an OAR's objects are not held when reading an IAR.
    private readonly List<(string path, long size, bool dir)> m_unknown = new List<(string, long, bool)>();
    private long m_unknownCount, m_unknownBytes;
    private readonly Dictionary<string, (long count, long bytes, List<(string path, long size, bool dir)> first)> m_tally =
        new Dictionary<string, (long, long, List<(string, long, bool)>)>(StringComparer.Ordinal);
    private const int TallyExamples = 200;
    private readonly HashSet<ulong> m_names = new HashSet<ulong>();
    private readonly FindingBuilder m_duplicate;
    private readonly FindingBuilder m_dirData;
    private readonly Dictionary<string, (long count, long bytes, string first)> m_legacy = new Dictionary<string, (long, long, string)>();
    private readonly List<(string kind, string asset, string entry, long bytes)> m_legacyExamples = new List<(string, string, string, long)>();
    private bool m_truncated;
    private const int MaxTrackedNames = 2_000_000;

    public ArchiveIntegrityCheck(InspectorOptions options) : base(options)
    {
        m_duplicate = NewBuilder();
        m_dirData = NewBuilder();
    }

    private static ulong Hash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    public override void OnRecord(ArchiveRecord record)
    {
        switch (record)
        {
            case EntryRecord e:
                if (e.Path.Length > 0)
                {
                    if (m_names.Count < MaxTrackedNames)
                    {
                        if (!m_names.Add(Hash(e.Path)))
                            m_duplicate.Add(e.Path, string.Empty, e.Entry, "entry " + StandardChecks.N(e.Index) + " repeats an earlier entry name; size " + StandardChecks.N(e.Size));
                    }
                    else
                        m_truncated = true;
                    if (e.Prefix.Length > 0)
                    {
                        if (!UnionPrefixes.Contains(e.Prefix))
                        {
                            m_unknownCount++;
                            m_unknownBytes += e.Size;
                            if (m_unknown.Count < Options.MaxFindingExamples)
                                m_unknown.Add((e.Entry, e.Size, e.IsDirectory));
                        }
                        else
                        {
                            if (!m_tally.TryGetValue(e.Prefix, out var t))
                                t = (0, 0, new List<(string, long, bool)>());
                            if (t.first.Count < TallyExamples)
                                t.first.Add((e.Entry, e.Size, e.IsDirectory));
                            m_tally[e.Prefix] = (t.count + 1, t.bytes + e.Size, t.first);
                        }
                    }
                }
                if (e.IsDirectory && e.Size > 0)
                    m_dirData.Add(e.Path, string.Empty, e.Entry, StandardChecks.N(e.Size) + " bytes of data in a directory entry");
                break;

            case LegacyPayloadRecord l:
                m_legacy.TryGetValue(l.Kind, out var agg);
                m_legacy[l.Kind] = (agg.count + 1, agg.bytes + l.Bytes, agg.first ?? l.Entry);
                if (m_legacyExamples.Count < Options.MaxFindingExamples)
                    m_legacyExamples.Add((l.Kind, l.AssetId?.ToString(), l.Entry, l.Bytes));
                break;
        }
    }

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        ArchiveKind kind = Archive?.Kind ?? ArchiveKind.Unknown;
        HashSet<string> known = kind == ArchiveKind.Oar ? OarPrefixes : kind == ArchiveKind.Iar ? IarPrefixes : new HashSet<string>(OarPrefixes.Concat(IarPrefixes));

        FindingBuilder unknown = NewBuilder();
        long count = m_unknownCount, bytes = m_unknownBytes;
        foreach ((string path, long size, bool dir) e in m_unknown)
            unknown.Add(e.path, string.Empty, e.path, StandardChecks.N(e.size) + " bytes" + (e.dir ? ", directory" : ""));
        foreach (KeyValuePair<string, (long count, long bytes, List<(string path, long size, bool dir)> first)> kv in m_tally)
        {
            if (known.Contains(kv.Key))
                continue;
            count += kv.Value.count;
            bytes += kv.Value.bytes;
            foreach ((string path, long size, bool dir) e in kv.Value.first)
                unknown.Add(e.path, string.Empty, e.path, StandardChecks.N(e.size) + " bytes" + (e.dir ? ", directory" : "") + ", under " + kv.Key);
        }
        if (unknown.Any)
        {
            Finding f = unknown.ToFinding(Id, "ARCH_UNKNOWN_PATH", FindingSeverity.Notice, "Entries under no path the loaders read",
                StandardChecks.N(count) + " entries (" + StandardChecks.N(bytes) + " bytes) sit under no path that " + (kind == ArchiveKind.Iar ? "the IAR" : kind == ArchiveKind.Oar ? "the OAR" : "either") +
                " loader reads; the loaders ignore them.", Nums(("entries", count), ("bytes", bytes)));
            f.Count = count;
            f.ExamplesTruncated = count > unknown.Examples.Count;
            findings.Add(f);
        }

        if (m_duplicate.Any)
            findings.Add(m_duplicate.ToFinding(Id, "ARCH_DUPLICATE_ENTRY", FindingSeverity.Notice, "Duplicate entry names",
                StandardChecks.N(m_duplicate.Count) + " entries repeat the name of an earlier entry." + (m_truncated ? " Names past " + StandardChecks.N(MaxTrackedNames) + " entries were not tracked." : "")));
        else if (m_truncated)
            findings.Add(new Finding
            {
                Source = Id,
                Code = "ARCH_DUPLICATE_NOT_CHECKED",
                Severity = FindingSeverity.Info,
                Title = "Duplicate entry names were checked for part of the archive",
                Detail = "Entry names past " + StandardChecks.N(MaxTrackedNames) + " entries were not tracked, so duplicates among them would not be found.",
                Count = 0
            });

        if (m_dirData.Any)
            findings.Add(m_dirData.ToFinding(Id, "ARCH_DIRECTORY_DATA", FindingSeverity.Notice, "Directory entries that hold data",
                StandardChecks.N(m_dirData.Count) + " directory entries carry a body; the loaders read it as a file or ignore it."));

        foreach (KeyValuePair<string, (long count, long bytes, string first)> kv in m_legacy.OrderBy(k => k.Key))
        {
            FindingBuilder b = NewBuilder();
            foreach ((string kindName, string asset, string entry, long size) x in m_legacyExamples.Where(x => x.kind == kv.Key))
                b.Add(x.entry, x.asset, x.entry, StandardChecks.N(x.size) + " bytes");
            Finding f = b.ToFinding(Id, "ARCH_LEGACY_PAYLOAD", FindingSeverity.Info, "Payload from another fork, not decoded: " + Sanitize.Text(kv.Key),
                StandardChecks.N(kv.Value.count) + " payloads of kind " + Sanitize.Text(kv.Key) + " (" + StandardChecks.N(kv.Value.bytes) + " bytes) were found and counted; the inspector never decodes them.",
                Nums(("count", kv.Value.count), ("bytes", kv.Value.bytes)));
            f.Count = kv.Value.count;
            findings.Add(f);
        }

        if (report.ScriptStateBlobs > 0)
            findings.Add(new Finding
            {
                Source = Id,
                Code = "ARCH_LEGACY_PAYLOAD",
                Severity = FindingSeverity.Info,
                Title = "Script state present in object XML, not read",
                Detail = StandardChecks.N(report.ScriptStateBlobs) + " saved script states are present in object XML and were not read.",
                Count = report.ScriptStateBlobs,
                Numbers = Nums(("count", report.ScriptStateBlobs))
            });
    }
}
