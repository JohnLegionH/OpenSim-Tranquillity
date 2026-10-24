/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSim.Archive.Compare;

/// <summary>
/// One planned change to one part. <see cref="Field"/> names what changes (see <see cref="ComparePlan.PartFields"/>).
/// <see cref="To"/>, when given, is the value the rewritten archive must hold; without it any new value passes.
/// </summary>
public sealed class PartChange
{
    /// <summary>The part's UUID.</summary>
    public string Part { get; set; }
    public string Field { get; set; }
    /// <summary>The new value as the report prints it. For field "shape" it is "mesh".</summary>
    public string To { get; set; }
    /// <summary>For field "shape": the face count the rewritten part must report.</summary>
    public int? Faces { get; set; }
    /// <summary>For field "contents": the item's UUID; without it every change to the part's contents passes.</summary>
    public string Item { get; set; }

    [JsonIgnore] internal Guid PartId;
    [JsonIgnore] internal Guid? ItemId;
    [JsonIgnore] public bool Met { get; internal set; }
}

/// <summary>
/// The changes a rewrite is meant to make. Anything the comparison finds that the plan does not name is a failure, and so
/// is a planned change that the comparison does not find. The schema is described in the OAR return design notes.
/// </summary>
public sealed class ComparePlan
{
    public static readonly string[] PartFields =
    {
        "name", "description", "linkNum", "position", "groupPosition", "rotation", "scale", "shape", "textureEntry",
        "faces", "creator", "owner", "permissions", "physics", "contents", "scriptStates"
    };

    /// <summary>Fields that hold one value, for which "to" can state it.</summary>
    private static readonly HashSet<string> SingleValued = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "description", "linkNum", "position", "groupPosition", "rotation", "scale", "textureEntry", "faces", "scriptStates"
    };

    public int Version { get; set; } = 1;
    public List<PartChange> PartChanges { get; set; } = new List<PartChange>();
    /// <summary>Parts (UUIDs) that exist only in the rewritten archive.</summary>
    public List<string> AddedParts { get; set; } = new List<string>();
    /// <summary>Parts (UUIDs) that exist only in the original archive.</summary>
    public List<string> RemovedParts { get; set; } = new List<string>();
    /// <summary>Entries that exist only in the rewritten archive: a full entry name, or a bare asset UUID for assets/&lt;uuid&gt;_*.</summary>
    public List<string> AddedEntries { get; set; } = new List<string>();
    public List<string> RemovedEntries { get; set; } = new List<string>();
    /// <summary>Entries present in both archives whose bytes differ.</summary>
    public List<string> ChangedEntries { get; set; } = new List<string>();

    [JsonIgnore] internal HashSet<Guid> AddedPartIds = new HashSet<Guid>();
    [JsonIgnore] internal HashSet<Guid> RemovedPartIds = new HashSet<Guid>();

    public static ComparePlan Empty() => new ComparePlan();

    public static ComparePlan Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            throw new CompareException("cannot read the plan '" + path + "': " + e.Message, e);
        }
    }

    public static ComparePlan Parse(string json)
    {
        ComparePlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<ComparePlan>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException e)
        {
            throw new CompareException("the plan is not valid: " + e.Message, e);
        }
        if (plan == null)
            throw new CompareException("the plan is empty");
        plan.Validate();
        return plan;
    }

    private void Validate()
    {
        if (Version != 1)
            throw new CompareException("the plan's version is " + Version + "; this tool reads version 1");
        PartChanges ??= new List<PartChange>();
        AddedParts ??= new List<string>();
        RemovedParts ??= new List<string>();
        AddedEntries ??= new List<string>();
        RemovedEntries ??= new List<string>();
        ChangedEntries ??= new List<string>();

        foreach (PartChange c in PartChanges)
        {
            string at = "plan change for part '" + c.Part + "' field '" + c.Field + "'";
            if (!Guid.TryParse(c.Part, out c.PartId))
                throw new CompareException(at + ": 'part' is not a UUID");
            if (c.Field == null || Array.IndexOf(PartFields, c.Field) < 0)
                throw new CompareException(at + ": 'field' must be one of " + string.Join(", ", PartFields));
            if (c.Item != null)
            {
                if (c.Field != "contents")
                    throw new CompareException(at + ": 'item' belongs to field 'contents' only");
                if (!Guid.TryParse(c.Item, out Guid item))
                    throw new CompareException(at + ": 'item' is not a UUID");
                c.ItemId = item;
            }
            if (c.Field == "shape")
            {
                if (c.To != "mesh")
                    throw new CompareException(at + ": 'to' must be \"mesh\" (the only shape change a plan can state)");
                if (c.Faces == null || c.Faces < 0)
                    throw new CompareException(at + ": 'faces' (the planned face count) is required");
            }
            else
            {
                if (c.Faces != null)
                    throw new CompareException(at + ": 'faces' belongs to field 'shape' only");
                if (c.To != null && !SingleValued.Contains(c.Field))
                    throw new CompareException(at + ": 'to' cannot be used with this field");
                if (c.Field == "contents" && c.To != null)
                    throw new CompareException(at + ": 'to' cannot be used with this field");
            }
            for (int other = PartChanges.IndexOf(c) + 1; other < PartChanges.Count; other++)
                if (PartChanges[other].Part != null && string.Equals(PartChanges[other].Part, c.Part, StringComparison.OrdinalIgnoreCase)
                    && PartChanges[other].Field == c.Field && PartChanges[other].Item == c.Item)
                    throw new CompareException(at + " is listed twice");
        }

        foreach (string s in AddedParts)
            AddedPartIds.Add(ParseId(s, "addedParts"));
        foreach (string s in RemovedParts)
            RemovedPartIds.Add(ParseId(s, "removedParts"));
    }

    private static Guid ParseId(string s, string list)
    {
        if (!Guid.TryParse(s, out Guid id))
            throw new CompareException("'" + s + "' in " + list + " is not a UUID");
        return id;
    }

    /// <summary>The list item that names the entry, or null.</summary>
    internal static string Match(List<string> list, string entry)
    {
        foreach (string s in list)
        {
            if (string.Equals(s, entry, StringComparison.Ordinal))
                return s;
            // A bare asset id stands for assets/<id>_<type>.<ext>
            if (Guid.TryParse(s, out Guid id) && entry.StartsWith("assets/" + id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    internal static string Describe(PartChange c) =>
        "part " + c.Part + " " + c.Field + (c.Item != null ? " item " + c.Item : string.Empty)
        + (c.To != null ? " -> " + c.To : string.Empty) + (c.Faces != null ? " with " + c.Faces.Value.ToString(CultureInfo.InvariantCulture) + " faces" : string.Empty);
}
