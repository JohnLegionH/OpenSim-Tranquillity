/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

/// <summary>What the two loaders do with creators they cannot resolve, as text for the report. Pinned by tests.</summary>
public static class LoaderBehaviour
{
    public const string OarRewrite =
        "The OAR loader replaces a creator it cannot find on the destination grid with the estate owner (or the --default-user account) " +
        "for every part and prim inventory item that has no creator data, unless --no-defaultuser is given or --lookup-aliases finds an alias.";

    public const string IarRewrite =
        "The IAR loader makes the person loading the archive the creator of every item whose creator it cannot resolve, " +
        "and the owner of every item and boxed object.";

    public const string OarOwnerRewrite =
        "The OAR loader also replaces an owner or last owner it cannot find with the same default account, and clears groups that do not exist on the destination.";

    public const string Advisory =
        "An archive that has been loaded on another grid and saved again can credit one person with everything even when that person did not build it; " +
        "check with the person bringing it.";

    public const string EditNote =
        "Archives can be edited, so this report is a record of what the file says, not proof of who built anything.";
}

/// <summary>
/// The creator analysis: groups the records by creator id and by creator name and says when one creator is
/// credited on everything or nearly everything, then lists which known patterns the numbers match.
/// It explains; it does not judge.
/// </summary>
public sealed class CreatorAnalysisCheck : ArchiveCheckBase
{
    public override string Id => "creator-analysis";

    private readonly AnalysisSettings m_settings;
    private ArchiveInfo m_archive;

    private long m_total, m_library, m_blank, m_counted, m_named, m_withId;
    private long m_sameAccount, m_withCreatorData, m_foreignCreatorData;
    private readonly Dictionary<UUID, long> m_byId = new Dictionary<UUID, long>();
    private readonly Dictionary<UUID, string> m_idNames = new Dictionary<UUID, string>();
    private readonly Dictionary<string, long> m_byName = new Dictionary<string, long>();
    private readonly Dictionary<string, string> m_nameDisplay = new Dictionary<string, string>();
    private readonly HashSet<string> m_homeNames = new HashSet<string>();
    private readonly HashSet<UUID> m_owners = new HashSet<UUID>();
    private readonly Dictionary<long, long> m_minutes = new Dictionary<long, long>();
    private long m_dated;

    public CreatorAnalysisCheck(AnalysisSettings settings)
    {
        m_settings = settings ?? new AnalysisSettings();
    }

    public override void Begin(ArchiveInfo archive) { m_archive = archive; }

    public override void OnRecord(ArchiveRecord record)
    {
        if (record is PartRecord p)
            Add(p.Creator, p.OwnerId, p.LastOwnerId, true, p.CreationDate, p.Creator.HasData);
        else if (record is ItemRecord i)
        {
            // An IAR saved with --home stamps creator data on every item whose creator has an account, the
            // owner's own items included, so there it only counts when it names someone other than the owner.
            bool foreign = i.Source == ItemSource.InventoryArchive ? i.Creator.HasData && i.Creator.Id != i.OwnerId : i.Creator.HasData;
            Add(i.Creator, i.OwnerId, i.LastOwnerId, i.HasLastOwner, i.CreationDate, foreign);
        }
    }

    private static string NameKey(CreatorRef c) => c.Name.ToLowerInvariant() + "@" + c.Home.ToLowerInvariant();

    private void Add(CreatorRef c, UUID owner, UUID lastOwner, bool hasLastOwner, int? date, bool foreignData)
    {
        m_total++;
        if (c.IsLibrary) { m_library++; return; }
        if (c.IsBlank) { m_blank++; return; }

        m_counted++;
        m_owners.Add(owner);
        if (c.Id != UUID.Zero)
        {
            m_withId++;
            m_byId.TryGetValue(c.Id, out long n);
            m_byId[c.Id] = n + 1;
            if (c.Name.Length > 0 && !m_idNames.ContainsKey(c.Id))
                m_idNames[c.Id] = c.Name;
        }

        if (c.Name.Length > 0)
        {
            m_named++;
            string key = NameKey(c);
            m_byName.TryGetValue(key, out long n);
            m_byName[key] = n + 1;
            m_nameDisplay.TryAdd(key, c.Home.Length > 0 ? c.Name + " (" + c.Home + ")" : c.Name);
        }

        if (c.HasData)
        {
            m_withCreatorData++;
            m_homeNames.Add(NameKey(c));
        }
        if (foreignData)
            m_foreignCreatorData++;

        if (c.Id != UUID.Zero && owner == c.Id && (!hasLastOwner || lastOwner == c.Id))
            m_sameAccount++;

        if (date.HasValue && date.Value > 0)
        {
            m_dated++;
            long minute = date.Value / 60;
            m_minutes.TryGetValue(minute, out long n);
            m_minutes[minute] = n + 1;
        }
    }

    public CreatorAnalysisResult Result { get; private set; }

    public override void Complete(ArchiveReport report, IList<Finding> findings)
    {
        CreatorAnalysisResult res = new CreatorAnalysisResult
        {
            TotalRecords = m_total,
            LibraryRecords = m_library,
            BlankRecords = m_blank,
            CountedRecords = m_counted,
            NamedRecords = m_named
        };

        FlagByGroup(res, "uuid", m_byId, m_withId, k => m_idNames.TryGetValue(k, out string nm) ? nm + " " + k : k.ToString(), k => (UUID?)k);
        FlagByGroup(res, "name", m_byName, m_named, k => m_nameDisplay[k], k => null);

        if (res.Fired)
        {
            BuildMatches(res);
            res.Advisory = LoaderBehaviour.Advisory;
            foreach (CreatorFlag f in res.Flags)
                findings.Add(new Finding
                {
                    Code = f.Code,
                    Severity = FindingSeverity.Warning,
                    Title = f.Code == "SINGLE_CREATOR"
                        ? "One creator is credited on every part and item"
                        : "One creator is credited on most parts and items",
                    Detail = string.Format(CultureInfo.InvariantCulture, "{0}: {1} of {2} counted records ({3:P1}), grouped by {4}.", f.Creator, f.Count, f.Total, f.Share, f.Grouping),
                    Numbers = new Dictionary<string, string>
                    {
                        ["count"] = f.Count.ToString(CultureInfo.InvariantCulture),
                        ["total"] = f.Total.ToString(CultureInfo.InvariantCulture),
                        ["grouping"] = f.Grouping
                    }
                });
            foreach (AnalysisMatch m in res.Matches)
                findings.Add(new Finding { Code = m.Id, Severity = FindingSeverity.Info, Title = "Pattern match", Detail = m.Statement, Numbers = m.Numbers });
        }

        Result = res;
        report.CreatorAnalysis = res;
    }

    private void FlagByGroup<TKey>(CreatorAnalysisResult res, string grouping, Dictionary<TKey, long> groups, long total,
        Func<TKey, string> display, Func<TKey, UUID?> idOf)
    {
        if (total < m_settings.MinRecords || groups.Count == 0)
            return;

        TKey topKey = default;
        long top = 0;
        foreach (KeyValuePair<TKey, long> kv in groups)
            if (kv.Value > top) { top = kv.Value; topKey = kv.Key; }

        double share = (double)top / total;
        string code = null;
        if (top == total)
            code = "SINGLE_CREATOR";
        else if (share >= m_settings.DominantShare)
            code = "DOMINANT_CREATOR";
        if (code == null)
            return;

        res.Flags.Add(new CreatorFlag
        {
            Code = code,
            Grouping = grouping,
            Creator = display(topKey),
            CreatorId = idOf(topKey),
            Count = top,
            Total = total,
            Share = share
        });
    }

    private (long count, long start) BestWindow()
    {
        if (m_minutes.Count == 0)
            return (0, 0);

        long windowMinutes = Math.Max(1, m_settings.WindowSeconds / 60);
        long[] keys = m_minutes.Keys.ToArray();
        Array.Sort(keys);

        long best = 0, bestStart = keys[0], running = 0;
        int lo = 0;
        for (int hi = 0; hi < keys.Length; hi++)
        {
            running += m_minutes[keys[hi]];
            while (keys[hi] - keys[lo] >= windowMinutes)
            {
                running -= m_minutes[keys[lo]];
                lo++;
            }
            if (running > best)
            {
                best = running;
                bestStart = keys[lo];
            }
        }
        return (best, bestStart);
    }

    private static string Pct(long a, long b) => b == 0 ? "0%" : (100.0 * a / b).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Utc(long minute) =>
        DateTimeOffset.FromUnixTimeSeconds(minute * 60).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private void BuildMatches(CreatorAnalysisResult res)
    {
        ArchiveKind kind = m_archive?.Kind ?? ArchiveKind.Unknown;
        (long windowCount, long windowStart) = BestWindow();
        double windowShare = m_dated == 0 ? 0 : (double)windowCount / m_dated;
        bool windowMatch = m_dated > 0 && windowShare >= m_settings.WindowShare;

        // 1. creator, owner and last owner are one account and there is no creator data
        double sameShare = m_counted == 0 ? 0 : (double)m_sameAccount / m_counted;
        if (sameShare >= m_settings.SameAccountShare && m_foreignCreatorData == 0)
        {
            string loader = kind == ArchiveKind.Iar ? LoaderBehaviour.IarRewrite
                : kind == ArchiveKind.Oar ? LoaderBehaviour.OarRewrite + " " + LoaderBehaviour.OarOwnerRewrite
                : LoaderBehaviour.OarRewrite + " " + LoaderBehaviour.IarRewrite;
            res.Matches.Add(new AnalysisMatch
            {
                Id = "MATCH_LOADER_REWRITE",
                Statement = string.Format(CultureInfo.InvariantCulture,
                    "Creator, owner and last owner are the same account on {0} of {1} records ({2}) and no record carries creator data naming a different creator ({4} records carry creator data). This matches what a loader leaves when it replaces creators it cannot resolve. {3}",
                    m_sameAccount, m_counted, Pct(m_sameAccount, m_counted), loader, m_withCreatorData),
                Numbers = new Dictionary<string, string>
                {
                    ["sameAccountRecords"] = m_sameAccount.ToString(CultureInfo.InvariantCulture),
                    ["countedRecords"] = m_counted.ToString(CultureInfo.InvariantCulture),
                    ["recordsWithCreatorData"] = m_withCreatorData.ToString(CultureInfo.InvariantCulture),
                    ["recordsWithOtherCreatorData"] = m_foreignCreatorData.ToString(CultureInfo.InvariantCulture)
                }
            });
        }

        // 2. most CreationDates inside one window
        if (windowMatch)
        {
            res.Matches.Add(new AnalysisMatch
            {
                Id = "MATCH_BULK_DATES",
                Statement = string.Format(CultureInfo.InvariantCulture,
                    "{0} of {1} dated records ({2}) fall inside one {3}-hour window starting {4}. Neither loader changes CreationDate, so this matches bulk creation by an import tool or mass re-creation, not an effect of loading.",
                    windowCount, m_dated, Pct(windowCount, m_dated), m_settings.WindowSeconds / 3600, Utc(windowStart)),
                Numbers = new Dictionary<string, string>
                {
                    ["recordsInWindow"] = windowCount.ToString(CultureInfo.InvariantCulture),
                    ["datedRecords"] = m_dated.ToString(CultureInfo.InvariantCulture),
                    ["windowStartUtc"] = Utc(windowStart)
                }
            });
        }

        // 3. creator data with one home and name, spread dates, varied owners
        double dataShare = m_counted == 0 ? 0 : (double)m_withCreatorData / m_counted;
        if (dataShare >= m_settings.CreatorDataShare && m_homeNames.Count == 1 && !windowMatch && m_owners.Count >= 2)
        {
            res.Matches.Add(new AnalysisMatch
            {
                Id = "MATCH_ONE_BUILDER",
                Statement = string.Format(CultureInfo.InvariantCulture,
                    "Creator data is present on {0} of {1} records ({2}) and names one home grid and one name, the dates are spread out (at most {3} of {4} dated records share a {5}-hour window) and the records have {6} different owners. This matches one builder's own work.",
                    m_withCreatorData, m_counted, Pct(m_withCreatorData, m_counted), windowCount, m_dated, m_settings.WindowSeconds / 3600, m_owners.Count),
                Numbers = new Dictionary<string, string>
                {
                    ["recordsWithCreatorData"] = m_withCreatorData.ToString(CultureInfo.InvariantCulture),
                    ["countedRecords"] = m_counted.ToString(CultureInfo.InvariantCulture),
                    ["distinctOwners"] = m_owners.Count.ToString(CultureInfo.InvariantCulture)
                }
            });
        }

        if (res.Matches.Count == 0)
        {
            res.Matches.Add(new AnalysisMatch
            {
                Id = "MATCH_NONE",
                Statement = string.Format(CultureInfo.InvariantCulture,
                    "None of the three patterns matches: same account on {0} of {1} records, creator data on {2}, {3} of {4} dated records in the busiest {5}-hour window, {6} owners.",
                    Pct(m_sameAccount, m_counted), m_counted, Pct(m_withCreatorData, m_counted), windowCount, m_dated, m_settings.WindowSeconds / 3600, m_owners.Count),
                Numbers = new Dictionary<string, string>()
            });
        }
    }
}
