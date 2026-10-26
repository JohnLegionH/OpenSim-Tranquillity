/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.ScriptScan;

/// <summary>
/// Reads an OAR or IAR and decides, for every part, whether turning it into a mesh could break a script in its build. The
/// archive is only read. The scan is textual: a clean result means nothing was seen, not that nothing is there.
/// </summary>
public sealed class ArchiveScriptScanner
{
    private const int AssetTypeLslText = 10;
    private const int AssetTypeLslBytecode = 11;
    private const int InvTypeLsl = 10;
    private const int AssetTypeObject = 6;

    private readonly ScanOptions m_options;

    public ArchiveScriptScanner(ScanOptions options = null)
    {
        m_options = options ?? new ScanOptions();
    }

    public ScanResult Scan(string path)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ScanCheck check = new ScanCheck(m_options);
        InspectorOptions io = new InspectorOptions { StandardChecks = false };
        io.Checks.Add(check);
        io.Checks.AddRange(m_options.ExtraChecks);
        io.ObjectObserver = m_options.ObjectObserver;

        ArchiveReport report = new ArchiveInspector(io).Inspect(path);
        if (report.Status == InspectionStatus.NotAnArchive)
            throw new ScanException("cannot read '" + path + "': " + report.StopReason);

        ScanResult result = check.Result;
        result.Archive = Path.GetFileName(path);
        result.ReadStatus = report.Status == InspectionStatus.Partial ? "Partial" : "Complete";
        result.ReadNote = report.Status == InspectionStatus.Partial ? report.StopReason : null;
        result.ToolVersion = (typeof(ArchiveScriptScanner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ArchiveScriptScanner).Assembly.GetName().Version?.ToString() ?? "unknown");
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private sealed class Part
    {
        public PartRecord Record;
        public Guid Id;
    }

    private sealed class Build
    {
        public string Key;
        public string Entry;
        public string Name;
        public List<Part> Parts = new List<Part>();
        public List<ScriptItem> Scripts = new List<ScriptItem>();
        public Part Root => Parts.FirstOrDefault(p => p.Record.IsRoot) ?? Parts.FirstOrDefault();
    }

    private sealed class ScriptItem
    {
        public string Name;
        public Guid AssetId;
        public int AssetType;
        public Guid PartId;
    }

    private sealed class ScanCheck : ArchiveCheckBase
    {
        private readonly ScanOptions m_opt;
        private readonly Dictionary<string, Build> m_builds = new Dictionary<string, Build>(StringComparer.Ordinal);
        private readonly List<string> m_order = new List<string>();
        private readonly Dictionary<Guid, ScriptAnalysis> m_analysed = new Dictionary<Guid, ScriptAnalysis>();
        private readonly HashSet<Guid> m_scriptAssetsInArchive = new HashSet<Guid>();
        private ArchiveKind m_kind;

        public ScanResult Result { get; } = new ScanResult();

        public ScanCheck(ScanOptions opt)
        {
            m_opt = opt;
            Result.AssumeSafeUnscanned = opt.AssumeSafeUnscanned;
        }

        public override string Id => "script-scan";

        public override void Begin(ArchiveInfo archive)
        {
            m_kind = archive.Kind;
            Result.ArchiveKind = archive.Kind == ArchiveKind.Iar ? "iar" : archive.Kind == ArchiveKind.Oar ? "oar" : "unknown";
        }

        // In an OAR the scene objects are archive-level; objects found in prim contents are boxed and not assessed.
        // Every object in an IAR is a boxed asset, and those are the ones to assess.
        private bool Assessed(ArchiveRecord r) => m_kind == ArchiveKind.Iar || r.Context == RecordContext.Archive;

        private static string KeyOf(ArchiveRecord r, UUID linkset) => r.Entry + "|" + linkset;

        public override void OnRecord(ArchiveRecord record)
        {
            switch (record)
            {
                case PartRecord p:
                    if (!Assessed(p))
                    {
                        Result.NestedObjectParts++;
                        return;
                    }
                    BuildFor(p, p.LinksetId, p.LinksetName).Parts.Add(new Part { Record = p, Id = p.PartId.Guid });
                    break;

                case ItemRecord it when it.Source == ItemSource.PrimInventory:
                    bool lsl = it.InvType == InvTypeLsl || it.AssetType == AssetTypeLslText || it.AssetType == AssetTypeLslBytecode;
                    if (!lsl)
                        return;
                    if (!Assessed(it))
                    {
                        Result.NestedObjectScripts++;
                        return;
                    }
                    Result.ScriptItems++;
                    BuildFor(it, it.LinksetId, null).Scripts.Add(new ScriptItem
                    {
                        Name = it.Name,
                        AssetId = it.AssetId.Guid,
                        AssetType = it.AssetType ?? AssetTypeLslText,
                        PartId = it.PartId.Guid
                    });
                    break;

                case AssetRecord a when a.AssetType == AssetTypeLslText || a.AssetType == AssetTypeLslBytecode:
                    m_scriptAssetsInArchive.Add(a.AssetId.Guid);
                    break;
            }
        }

        private Build BuildFor(ArchiveRecord r, UUID linkset, string name)
        {
            string key = KeyOf(r, linkset);
            if (!m_builds.TryGetValue(key, out Build b))
            {
                b = new Build { Key = key, Entry = r.Entry, Name = name };
                m_builds[key] = b;
                m_order.Add(key);
            }
            if (b.Name == null && name != null)
                b.Name = name;
            return b;
        }

        public override void OnTextAsset(TextAssetBody body)
        {
            if (body.AssetType != AssetTypeLslText)
                return;
            Guid id = body.AssetId.Guid;
            if (m_analysed.ContainsKey(id))
                return;
            ReadOnlySpan<byte> bytes = body.Bytes.Span;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                bytes = bytes.Slice(3);
            m_analysed[id] = LslScriptAnalyzer.Analyze(Encoding.UTF8.GetString(bytes));
        }

        public override void Complete(ArchiveReport report, IList<Finding> findings)
        {
            Result.ScriptAssetsRead = m_analysed.Count;

            foreach (string key in m_order)
            {
                Build b = m_builds[key];
                if (b.Parts.Count == 0)
                    continue;
                Resolve(b);
            }
        }

        private sealed class Acc
        {
            public PartRow Row;
            public HitLevel Worst = HitLevel.Note;
            public bool HasLevel;
            public HashSet<string> Codes = new HashSet<string>();
        }

        private void Resolve(Build b)
        {
            string buildLabel = (b.Name ?? b.Root?.Record.Name ?? "?") + " [" + (b.Root?.Id.ToString("N").Substring(0, 8) ?? "?") + "]";
            Dictionary<Guid, Acc> rows = new Dictionary<Guid, Acc>();
            foreach (Part p in b.Parts)
            {
                if (rows.ContainsKey(p.Id))
                    continue;
                PartRecord r = p.Record;
                rows[p.Id] = new Acc
                {
                    Row = new PartRow
                    {
                        PartId = p.Id,
                        LinksetId = r.LinksetId.Guid,
                        Build = buildLabel,
                        Entry = r.Entry,
                        RegionDir = r.RegionDir,
                        BoxedAssetId = r.BoxedAssetId?.Guid ?? Guid.Empty,
                        LinkNum = r.LinkNum,
                        IsRoot = r.IsRoot,
                        Name = r.Name,
                    }
                };
            }

            Dictionary<Guid, Part> byId = new Dictionary<Guid, Part>();
            foreach (Part p in b.Parts)
                byId.TryAdd(p.Id, p);
            Part root = b.Root;

            foreach (ScriptItem s in b.Scripts)
            {
                byId.TryGetValue(s.PartId, out Part host);
                if (!m_analysed.TryGetValue(s.AssetId, out ScriptAnalysis analysis))
                {
                    string why = s.AssetType == AssetTypeLslBytecode ? "compiled bytecode only, no source"
                        : s.AssetId == Guid.Empty ? "the item names no asset"
                        : m_scriptAssetsInArchive.Contains(s.AssetId) ? "source present but not read (over the size cap)"
                        : "asset missing from the archive";
                    Result.Unscanned.Add(new UnscannedScript
                    {
                        Script = s.Name, AssetId = s.AssetId, Part = host?.Record.Name, PartId = s.PartId, Build = buildLabel, Reason = why
                    });
                    if (!m_opt.AssumeSafeUnscanned)
                        foreach (Acc a in rows.Values)
                            AddReason(a, new PartReason
                            {
                                Code = LslScriptAnalyzer.ScriptUnscanned, Level = "uncertain", Detail = why, Reach = "whole build",
                                Literal = false
                            }, HitLevel.Uncertain, s, host);
                    continue;
                }

                ScriptLinkUses links = new ScriptLinkUses
                {
                    LinksetId = host?.Record.LinksetId.Guid ?? Guid.Empty, Entry = b.Entry, HostPartId = s.PartId, Script = s.Name, Truncated = analysis.Truncated
                };
                links.Uses.AddRange(analysis.LinkUses);
                Result.ScriptLinks.Add(links);

                if (analysis.Truncated && !m_opt.AssumeSafeUnscanned)
                    foreach (Acc a in rows.Values)
                        AddReason(a, new PartReason
                        {
                            Code = LslScriptAnalyzer.ScriptTruncated, Level = "uncertain", Detail = "more findings than the scan keeps", Reach = "whole build",
                            Literal = false
                        }, HitLevel.Uncertain, s, host);

                foreach (ScriptHit h in analysis.Hits)
                {
                    foreach ((Part target, string reach, HitLevel level, bool literal) in Targets(b, root, host, h))
                    {
                        if (!rows.TryGetValue(target.Id, out Acc acc))
                            continue;
                        AddReason(acc, new PartReason
                        {
                            Code = h.Code,
                            Level = LevelName(level),
                            Line = h.Line,
                            Function = h.Function,
                            Detail = h.Detail,
                            Count = h.Count,
                            Reach = reach,
                            Literal = literal
                        }, level, s, host);
                    }
                }
            }

            foreach (Acc a in rows.Values)
            {
                PartRow row = a.Row;
                row.Decision = !a.HasLevel || a.Worst == HitLevel.Note ? PartDecision.Convertible
                    : a.Worst == HitLevel.Warn ? PartDecision.ConvertibleWithWarnings
                    : a.Worst == HitLevel.Uncertain ? PartDecision.KeepPrimUncertain
                    : PartDecision.KeepPrimUnsafe;
                row.FaceCountDependent = a.Codes.Contains(LslScriptAnalyzer.SidesQuery) || a.Codes.Contains(LslScriptAnalyzer.FaceIndexed)
                    || a.Codes.Contains(LslScriptAnalyzer.FaceVariable);
                foreach (string code in a.Codes)
                {
                    // Notes are for the face-count flag; the reason tally lists what changes a decision or warns.
                    if (code == LslScriptAnalyzer.SidesQuery || code == LslScriptAnalyzer.FaceIndexed || code == LslScriptAnalyzer.FaceVariable)
                        continue;
                    Result.ReasonCounts[code] = Result.ReasonCounts.TryGetValue(code, out int n) ? n + 1 : 1;
                }
                // Notes are not listed as reasons.
                row.Reasons.RemoveAll(r => r.Level == "note");
                Result.Parts.Add(row);
            }

            // Keep the order of the archive: the parts as the inspector produced them.
            Dictionary<Guid, int> order = b.Parts.Select((p, i) => (p.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);
            int start = Result.Parts.Count - rows.Count;
            Result.Parts.Sort(start, rows.Count, Comparer<PartRow>.Create((x, y) => order[x.PartId].CompareTo(order[y.PartId])));
        }

        private static string LevelName(HitLevel l)
        {
            switch (l)
            {
                case HitLevel.Unsafe: return "unsafe";
                case HitLevel.Uncertain: return "uncertain";
                case HitLevel.Warn: return "warning";
                default: return "note";
            }
        }

        private void AddReason(Acc acc, PartReason reason, HitLevel level, ScriptItem script, Part host)
        {
            acc.Codes.Add(reason.Code);
            if (!acc.HasLevel || level > acc.Worst)
                acc.Worst = level;
            acc.HasLevel = true;

            reason.Script = script.Name;
            reason.ScriptAssetId = script.AssetId;
            reason.HostPartName = host?.Record.Name;
            reason.HostPartId = host?.Id ?? script.PartId;
            reason.HostLinkNum = host?.Record.LinkNum ?? 0;

            // Notes only set the face-count flag; the list is capped for the other levels.
            if (level == HitLevel.Note)
                return;
            if (acc.Row.Reasons.Count >= m_opt.MaxReasonsPerPart)
                acc.Row.ReasonsOmitted++;
            else
                acc.Row.Reasons.Add(reason);
        }

        /// <summary>
        /// The parts a finding reaches, how it reaches them, and how serious it is for each. A shape change whose target cannot
        /// be named reaches every part of the build as uncertain, not unsafe (a possible hit, not a proven one).
        /// </summary>
        private static IEnumerable<(Part, string, HitLevel, bool)> Targets(Build b, Part root, Part host, ScriptHit h)
        {
            LinkTarget t = h.Target;
            string reach = t.ToString();
            bool literal = h.Code != LslScriptAnalyzer.ParamsUnknown;
            switch (t.Kind)
            {
                case LinkKind.Self:
                    if (host != null) yield return (host, reach, h.Level, literal);
                    break;
                case LinkKind.Root:
                    if (root != null) yield return (root, reach, h.Level, literal);
                    break;
                case LinkKind.Literal:
                    foreach (Part p in b.Parts)
                        if (p.Record.LinkNum == t.Number && !p.Record.IsRoot)
                            yield return (p, reach, h.Level, literal);
                    break;
                case LinkKind.Set:
                    foreach (Part p in b.Parts) yield return (p, reach, h.Level, literal);
                    break;
                case LinkKind.AllOthers:
                    foreach (Part p in b.Parts)
                        if (host == null || p.Id != host.Id) yield return (p, reach, h.Level, literal);
                    break;
                case LinkKind.AllChildren:
                    foreach (Part p in b.Parts)
                        if (root == null || p.Id != root.Id) yield return (p, reach, h.Level, literal);
                    break;
                default:
                    // Unknown link, or another object by key.
                    if (h.Level >= HitLevel.Uncertain)
                    {
                        HitLevel level = HitLevel.Uncertain;
                        foreach (Part p in b.Parts) yield return (p, reach, level, false);
                    }
                    else if (host != null)
                        yield return (host, reach, h.Level, false);
                    break;
            }
        }
    }
}
