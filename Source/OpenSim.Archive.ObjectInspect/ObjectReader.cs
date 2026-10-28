/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.ObjectInspect;

/// <summary>Reads an OAR or an IAR and lists every part in link order and every permission mask. The archive is only read.</summary>
public static class ObjectReader
{
    public static ObjectReport Read(string path, ReadOptions options = null)
    {
        Collector collector = new Collector();
        ArchiveReport inspected = new Archive.Inspection.ArchiveInspector(collector.Options()).Inspect(path);
        return collector.Finish(inspected, options ?? new ReadOptions());
    }

    public static ObjectReport Read(Stream input, ReadOptions options = null)
    {
        Collector collector = new Collector();
        ArchiveReport inspected = new Archive.Inspection.ArchiveInspector(collector.Options()).Inspect(input);
        return collector.Finish(inspected, options ?? new ReadOptions());
    }

    /// <summary>Gathers what the inspector's single pass hands over, in a form that outlives the pass.</summary>
    private sealed class Collector
    {
        private readonly List<ObjectEntry> m_objects = new List<ObjectEntry>();

        public InspectorOptions Options() => new InspectorOptions
        {
            StandardChecks = false,
            MaxRetainedRecords = 1,
            ObjectObserver = OnArchiveObject
        };

        private void OnArchiveObject(ParsedObject obj, string entry, string regionDir)
        {
            foreach (ParsedGroup g in obj.Groups)
                m_objects.Add(ConvertGroup(g, entry, regionDir));
        }

        public ObjectReport Finish(ArchiveReport inspected, ReadOptions options)
        {
            ObjectReport report = new ObjectReport
            {
                Kind = inspected.Archive.Kind,
                Version = inspected.Archive.Version,
                Status = inspected.Status,
                StopReason = inspected.StopReason
            };
            foreach (ProblemRecord p in inspected.Problems)
                report.Problems.Add((p.Entry == null ? "" : p.Entry + ": ") + p.Message);

            if (inspected.Status == InspectionStatus.NotAnArchive)
                return report;

            report.ObjectsBeforeFilter = m_objects.Count;
            string filter = options.NameFilter;
            bool filtering = !string.IsNullOrEmpty(filter);
            report.Filter = filtering ? filter : null;
            List<ObjectEntry> kept = filtering
                ? m_objects.Where(o => Contains(o.Name, filter)).ToList()
                : m_objects;

            NameMap names = NameMap.From(m_objects);
            for (int i = 0; i < kept.Count; i++)
            {
                kept[i].Index = i + 1;
                names.Apply(kept[i]);
            }
            report.Objects = kept;
            Summarise(report);
            return report;
        }
    }

    internal static bool Contains(string text, string filter) =>
        text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    // ------------------------------------------------------------------ parsed linkset to report entry

    internal static ObjectEntry ConvertGroup(ParsedGroup g, string entry, string regionDir)
    {
        ObjectEntry o = new ObjectEntry
        {
            Name = Sanitize.Text(g.Name),
            Entry = Sanitize.Text(entry),
            RegionDir = Sanitize.Text(regionDir ?? string.Empty),
            GroupPosition = new Vector3(g.X, g.Y, g.Z),
            HasOffset = g.HasOffset,
            Offset = g.HasOffset ? new Vector3(g.OffsetX, g.OffsetY, g.OffsetZ) : Vector3.Zero
        };

        int order = 0;
        foreach (ParsedPart pp in g.Parts)
        {
            PartRecord rec = pp.Part;
            PartEntry p = new PartEntry
            {
                FileOrder = ++order,
                LinkNum = rec.LinkNum,
                Name = Sanitize.Text(rec.Name),
                Description = Sanitize.Text(pp.Description),
                PartId = rec.PartId,
                Position = pp.OffsetPosition,
                ShapeType = pp.PhysicsShapeType,
                ShapeTypeName = ShapeTypeName(pp.PhysicsShapeType),
                Phantom = pp.Phantom,
                ShapeKey = ShapeKey(pp),
                CreatorId = rec.Creator.Id,
                CreatorName = Sanitize.Text(rec.Creator.Name),
                OwnerId = rec.OwnerId,
                LastOwnerId = rec.LastOwnerId,
                GroupId = rec.GroupId,
                Masks = ToMasks(rec.Masks)
            };
            p.Lacks = p.Masks.Lacks();
            foreach (ItemRecord item in pp.Items)
                p.Items.Add(ConvertItem(item));
            o.Parts.Add(p);
        }

        CheckLinkOrder(o);
        return o;
    }

    private static ItemEntry ConvertItem(ItemRecord item)
    {
        ItemEntry e = new ItemEntry
        {
            Name = Sanitize.Text(item.Name),
            ItemId = item.ItemId,
            AssetId = item.AssetId,
            AssetType = item.AssetType,
            AssetTypeName = item.AssetType.HasValue ? Archive.Inspection.ArchiveInspector.TypeName(item.AssetType.Value) : "unknown",
            InvType = item.InvType,
            CreatorId = item.Creator.Id,
            CreatorName = Sanitize.Text(item.Creator.Name),
            OwnerId = item.OwnerId,
            LastOwnerId = item.LastOwnerId,
            GroupId = item.GroupId,
            Masks = ToMasks(item.Masks)
        };
        e.Lacks = e.Masks.Lacks();
        return e;
    }

    internal static MaskSet ToMasks(PermissionMasks m) => new MaskSet
    {
        Base = m.Base,
        Owner = m.Owner,
        Group = m.Group,
        Everyone = m.Everyone,
        NextOwner = m.NextOwner
    };

    internal static string ShapeTypeName(int stored)
    {
        switch (stored)
        {
            case -1:
            case 0: return "Prim";
            case 1: return "None";
            case 2: return "Convex";
            case -2: return "Unreadable";
            default: return "Unknown(" + stored.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    private static string ShapeKey(ParsedPart pp)
    {
        ShapeData s = pp.Shape;
        string scale = string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", pp.Scale.X, pp.Scale.Y, pp.Scale.Z);
        if (s == null)
            return "noshape;scale " + scale;
        string sculpt = s.SculptEntry ? ";sculpt " + s.SculptType + " " + s.SculptTexture : string.Empty;
        return string.Format(CultureInfo.InvariantCulture,
            "pcode {0};profile {1}/{2}-{3}/{4};path {5}/{6}-{7}/{8},{9}/{10},{11}/{12}/{13}/{14},{15}/{16}{17};scale {18}",
            s.PCode, s.ProfileCurve, s.ProfileBegin, s.ProfileEnd, s.ProfileHollow,
            s.PathCurve, s.PathBegin, s.PathEnd, s.PathScaleX, s.PathScaleY, s.PathShearX, s.PathShearY, s.PathTwist, s.PathTwistBegin,
            s.PathRadiusOffset, s.PathRevolutions, s.PathTaperX, sculpt, scale);
    }

    // ------------------------------------------------------------------ link order

    /// <summary>
    /// A linkset's parts are numbered 1, 2, 3 ... in the order the file lists them. The loader keeps a stored non-zero
    /// link number and numbers the others by file order (SceneObjectSerializer.FromXml2Format and FromOriginalXmlFormat),
    /// so numbers that disagree with the file order, repeat, skip or are zero are what a reader needs to see.
    /// </summary>
    internal static void CheckLinkOrder(ObjectEntry o)
    {
        List<string> notes = new List<string>();
        List<int> numbers = o.Parts.Select(p => p.LinkNum).ToList();

        if (numbers.Count == 1)
        {
            if (numbers[0] != 0 && numbers[0] != 1)
                notes.Add("a single part stores link number " + numbers[0]);
        }
        else
        {
            int zeros = numbers.Count(n => n == 0);
            if (zeros > 0)
                notes.Add(zeros + (zeros == 1 ? " part stores no link number" : " parts store no link number") + " (the loader numbers them by file order)");

            List<int> stored = numbers.Where(n => n != 0).ToList();
            List<int> repeated = stored.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n).ToList();
            foreach (int n in repeated)
                notes.Add("link number " + n + " is stored twice");

            if (zeros == 0 && repeated.Count == 0)
            {
                bool complete = stored.OrderBy(n => n).SequenceEqual(Enumerable.Range(1, stored.Count));
                if (!complete)
                    notes.Add("gap in the link numbers: " + string.Join(", ", stored));
                else if (!stored.SequenceEqual(Enumerable.Range(1, stored.Count)))
                    notes.Add("link numbers are not in file order: " + string.Join(", ", stored));
            }
        }

        o.LinkOrderOk = notes.Count == 0;
        o.LinkOrderNote = notes.Count == 0 ? null : string.Join("; ", notes);
    }

    // ------------------------------------------------------------------ names

    /// <summary>The names an archive carries for account ids, taken from every creator record in the whole archive.</summary>
    private sealed class NameMap
    {
        private readonly Dictionary<UUID, string> m_names = new Dictionary<UUID, string>();

        public static NameMap From(IEnumerable<ObjectEntry> objects)
        {
            NameMap map = new NameMap();
            foreach (ObjectEntry o in objects)
                foreach (PartEntry p in o.Parts)
                {
                    map.Learn(p.CreatorId, p.CreatorName);
                    foreach (ItemEntry i in p.Items)
                        map.Learn(i.CreatorId, i.CreatorName);
                }
            return map;
        }

        public void Learn(UUID id, string name)
        {
            if (id != UUID.Zero && !string.IsNullOrEmpty(name) && !m_names.ContainsKey(id))
                m_names[id] = name;
        }

        public string Name(UUID id, string own)
        {
            if (!string.IsNullOrEmpty(own))
                return own;
            return id != UUID.Zero && m_names.TryGetValue(id, out string name) ? name : null;
        }

        public void Apply(ObjectEntry o)
        {
            foreach (PartEntry p in o.Parts)
            {
                p.CreatorName = Name(p.CreatorId, p.CreatorName);
                p.OwnerName = Name(p.OwnerId, null);
                p.LastOwnerName = Name(p.LastOwnerId, null);
                foreach (ItemEntry i in p.Items)
                {
                    i.CreatorName = Name(i.CreatorId, i.CreatorName);
                    i.OwnerName = Name(i.OwnerId, null);
                    i.LastOwnerName = Name(i.LastOwnerId, null);
                }
            }
        }
    }

    // ------------------------------------------------------------------ summary and id counts

    internal static void Summarise(ObjectReport report)
    {
        ReportSummary s = new ReportSummary();
        foreach (string name in MaskSet.Names)
            s.LackingByMask[name] = 0;
        Dictionary<UUID, PartyCount> parties = new Dictionary<UUID, PartyCount>();

        PartyCount Party(UUID id, string name)
        {
            if (!parties.TryGetValue(id, out PartyCount c))
                parties[id] = c = new PartyCount { Id = id };
            if (string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(name))
                c.Name = name;
            return c;
        }

        void Count(UUID creator, string creatorName, UUID owner, string ownerName, UUID lastOwner, string lastOwnerName, UUID group)
        {
            if (creator != UUID.Zero) Party(creator, creatorName).AsCreator++;
            if (owner != UUID.Zero) Party(owner, ownerName).AsOwner++;
            if (lastOwner != UUID.Zero) Party(lastOwner, lastOwnerName).AsLastOwner++;
            if (group != UUID.Zero) Party(group, null).AsGroup++;
        }

        void CountLacks(List<string> lacks)
        {
            foreach (string l in lacks)
                s.LackingByMask[l.Substring(0, l.IndexOf(':'))]++;
        }

        foreach (ObjectEntry o in report.Objects)
        {
            s.Objects++;
            if (o.Flagged) s.FlaggedObjects++;
            if (!o.LinkOrderOk) s.LinkOrderProblems++;
            foreach (PartEntry p in o.Parts)
            {
                s.Parts++;
                if (p.Flagged) s.FlaggedParts++;
                CountLacks(p.Lacks);
                Count(p.CreatorId, p.CreatorName, p.OwnerId, p.OwnerName, p.LastOwnerId, p.LastOwnerName, p.GroupId);
                foreach (ItemEntry i in p.Items)
                {
                    s.PartItems++;
                    if (i.Flagged) s.FlaggedPartItems++;
                    CountLacks(i.Lacks);
                    Count(i.CreatorId, i.CreatorName, i.OwnerId, i.OwnerName, i.LastOwnerId, i.LastOwnerName, i.GroupId);
                }
            }
        }

        report.Summary = s;
        report.Parties = parties.Values.OrderByDescending(c => c.AsCreator + c.AsOwner + c.AsLastOwner + c.AsGroup).ThenBy(c => c.Id.ToString()).ToList();
    }
}
