/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Archive.Compare;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Converter;

public sealed class ConvertException : Exception
{
    public ConvertException(string message, Exception inner = null) : base(message, inner) { }
}

public sealed class ConvertOptions
{
    /// <summary>Treat a script with no readable source as harmless (see the eligibility command). Off by default.</summary>
    public bool AssumeSafeUnscanned { get; set; }
    /// <summary>Remove duplicate and loose vertices and zero-area triangles from every face before the levels of detail are made (on by default).</summary>
    public bool Clean { get; set; } = true;
    /// <summary>Writes the mesh assets. Null means <see cref="MeshAssetEncoder"/>.</summary>
    public IMeshEncoder Encoder { get; set; }
    /// <summary>Supplies sculpt maps. Null means the maps are read from the source archive.</summary>
    public ISculptMapSource SculptMaps { get; set; }
}

/// <summary>Where the converter gets the pixels of a sculpt map.</summary>
public interface ISculptMapSource
{
    SculptMap Get(Guid textureId, out string problem);
}

/// <summary>A sculpt map source that also holds the map as stored (JPEG 2000), so the region's own sculpt mesher can read it for the physics model.</summary>
public interface ISculptMapBytes
{
    /// <summary>The stored bytes of the map, or null when they are not at hand.</summary>
    byte[] GetBytes(Guid textureId);
}

public sealed class ConvertedPart
{
    public Guid PartId { get; set; }
    public string Build { get; set; }
    public int LinkNum { get; set; }
    public string Name { get; set; }
    public string Kind { get; set; }
    public int Faces { get; set; }
    public int EmptyFaces { get; set; }
    public int Triangles { get; set; }
    /// <summary>Triangles the mesher built that belong to no viewer face; left out of the mesh.</summary>
    public int UnnumberedTriangles { get; set; }
    /// <summary>Vertices and triangles of the physics_mesh block written for the part (the region's physics mesher at unit size).</summary>
    public int PhysicsVertices { get; set; }
    public int PhysicsTriangles { get; set; }
    /// <summary>The part's PhysicsShapeType in the source (0 prim, 1 none, 2 convex; -1 when the part had no such element) and as converted.</summary>
    public int PhysicsShapeTypeBefore { get; set; } = -1;
    public int PhysicsShapeTypeAfter { get; set; } = -1;
    public Guid MeshId { get; set; }
    public int ServerFacesBefore { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>A converted part whose mesh failed the validity check; nothing is written while there is one.</summary>
public sealed class InvalidMeshPart
{
    public Guid PartId { get; set; }
    public string Build { get; set; }
    public int LinkNum { get; set; }
    public string Name { get; set; }
    public Guid MeshId { get; set; }
    public List<string> Problems { get; set; } = new List<string>();
}

/// <summary>A part the converter left as it was: the eligibility rules kept it, or the geometry step could not give it a mesh.</summary>
public sealed class KeptPart
{
    public Guid PartId { get; set; }
    public string Build { get; set; }
    public int LinkNum { get; set; }
    public string Name { get; set; }
    public string Code { get; set; }
    public string Detail { get; set; }
    /// <summary>True when the geometry step kept it after eligibility had passed it.</summary>
    public bool ByConverter { get; set; }
}

public sealed class ConvertResult
{
    public string Source { get; set; }
    public string Output { get; set; }
    public string EncoderName { get; set; }
    public bool EncoderValid { get; set; }
    public int Parts { get; set; }
    public List<ConvertedPart> Converted { get; } = new List<ConvertedPart>();
    public List<KeptPart> Kept { get; } = new List<KeptPart>();
    public SortedDictionary<string, int> Skipped { get; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
    public int SkippedTotal => Skipped.Values.Sum();
    /// <summary>Distinct mesh assets written (identical meshes share one).</summary>
    public int MeshesAdded { get; set; }
    /// <summary>Total bytes of the distinct mesh assets.</summary>
    public long MeshBytes { get; set; }
    /// <summary>Meshes that passed the validity check (the encoder's output decoded and read back as encoded).</summary>
    public int MeshesValidated { get; set; }
    /// <summary>Parts whose mesh failed the validity check. When there are any, no archive is written.</summary>
    public List<InvalidMeshPart> Invalid { get; } = new List<InvalidMeshPart>();
    public long SourceBytes { get; set; }
    public long OutputBytes { get; set; }
    public RewriteSummary Rewrite { get; set; }
    public CompareResult Parity { get; set; }
    public string PlanJson { get; set; }
    public bool AssumeSafeUnscanned { get; set; }
    public double Seconds { get; set; }

    public bool Pass => Parity != null && Parity.Pass;

    public SortedDictionary<string, int> KeptByCode()
    {
        SortedDictionary<string, int> d = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (KeptPart k in Kept)
            d[k.Code] = d.TryGetValue(k.Code, out int n) ? n + 1 : 1;
        return d;
    }

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (Invalid.Count > 0)
            sb.AppendLine("FAIL: " + Invalid.Count + " converted part(s) have a mesh that failed the validity check; NOTHING WAS WRITTEN");
        else
            sb.AppendLine(Pass ? "PASS: the rewritten archive matches the plan" : "FAIL: the rewritten archive does not match the plan");
        sb.AppendLine("source: " + System.IO.Path.GetFileName(Source) + "; output: " + System.IO.Path.GetFileName(Output));
        sb.AppendLine("encoder: " + EncoderName + (EncoderValid ? string.Empty : "  -- THE MESH ASSETS ARE NOT VALID MESHES; do not load this archive"));
        if (AssumeSafeUnscanned)
            sb.AppendLine("WARNING: --assume-safe-unscanned was used; scripts with no source were treated as harmless");
        sb.AppendLine(string.Format(ci, "parts: {0}; converted: {1}; kept: {2}; skipped: {3}; mesh assets added: {4}",
            Parts, Converted.Count, Kept.Count, SkippedTotal, MeshesAdded));
        sb.AppendLine(string.Format(ci, "meshes: {0} validated of {1}; {2} bytes in all", MeshesValidated, MeshesAdded, MeshBytes));
        if (OutputBytes > 0)
            sb.AppendLine(string.Format(ci, "archive size: {0} bytes written, {1} bytes in the source ({2:0.0}%)", OutputBytes, SourceBytes, SourceBytes == 0 ? 0 : 100.0 * OutputBytes / SourceBytes));
        if (Skipped.Count > 0)
            sb.AppendLine("skipped: " + string.Join(", ", Skipped.Select(k => k.Key + " " + k.Value)));
        if (Kept.Count > 0)
            sb.AppendLine("kept: " + string.Join(", ", KeptByCode().Select(k => k.Key + " " + k.Value)));
        if (Parity != null)
            sb.AppendLine(string.Format(ci, "parity: {0} parts matched, {1} unplanned difference(s), {2} planned difference(s) matched, {3} planned change(s) not found",
                Parity.PartsMatched, Parity.Unplanned.Count, Parity.Planned.Count, Parity.UnmetPlan.Count));
        sb.AppendLine(string.Format(ci, "{0:0.0} s", Seconds));

        if (Invalid.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parts whose mesh failed the validity check:");
            foreach (InvalidMeshPart p in Invalid)
                sb.AppendLine(string.Format(ci, "  {0} link {1} '{2}' (mesh {3}): {4}", p.Build, p.LinkNum, p.Name, p.MeshId, string.Join("; ", p.Problems)));
        }
        if (Kept.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Kept parts:");
            foreach (KeptPart k in Kept)
                sb.AppendLine(string.Format(ci, "  {0} link {1} '{2}': {3}{4}: {5}", k.Build, k.LinkNum, k.Name, k.Code, k.ByConverter ? " (geometry step)" : string.Empty, k.Detail));
        }
        int warned = Converted.Count(c => c.Warnings.Count > 0);
        if (warned > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Converted parts with a warning:");
            foreach (ConvertedPart c in Converted.Where(c => c.Warnings.Count > 0))
                sb.AppendLine(string.Format(ci, "  {0} link {1} '{2}': {3}", c.Build, c.LinkNum, c.Name, string.Join("; ", c.Warnings)));
        }
        if (Parity != null && !Parity.Pass)
        {
            sb.AppendLine();
            sb.Append(Parity.ToText());
        }
        return sb.ToString();
    }

    public string ToJson()
    {
        var doc = new
        {
            schema = "oar-convert",
            version = 1,
            source = System.IO.Path.GetFileName(Source),
            output = System.IO.Path.GetFileName(Output),
            encoder = EncoderName,
            encoderProducesValidMesh = EncoderValid,
            assumeSafeUnscanned = AssumeSafeUnscanned,
            parity = Parity == null ? null : new { pass = Parity.Pass, partsMatched = Parity.PartsMatched, unplanned = Parity.Unplanned.Count, planned = Parity.Planned.Count, unmetPlan = Parity.UnmetPlan.Count },
            parts = Parts,
            converted = Converted.Count,
            kept = Kept.Count,
            skipped = SkippedTotal,
            skippedByCode = Skipped,
            keptByCode = KeptByCode(),
            meshesAdded = MeshesAdded,
            meshesValidated = MeshesValidated,
            meshBytes = MeshBytes,
            sourceBytes = SourceBytes,
            outputBytes = OutputBytes,
            invalidMeshParts = Invalid.Select(p => new { part = p.PartId, build = p.Build, link = p.LinkNum, name = p.Name, mesh = p.MeshId, problems = p.Problems }),
            seconds = Math.Round(Seconds, 1),
            convertedParts = Converted.Select(c => new { part = c.PartId, build = c.Build, link = c.LinkNum, name = c.Name, kind = c.Kind, faces = c.Faces, emptyFaces = c.EmptyFaces, triangles = c.Triangles, unnumberedTriangles = c.UnnumberedTriangles, physicsVertices = c.PhysicsVertices, physicsTriangles = c.PhysicsTriangles, physicsShapeTypeBefore = c.PhysicsShapeTypeBefore, physicsShapeTypeAfter = c.PhysicsShapeTypeAfter, mesh = c.MeshId, serverFacesBefore = c.ServerFacesBefore, warnings = c.Warnings }),
            keptParts = Kept.Select(k => new { part = k.PartId, build = k.Build, link = k.LinkNum, name = k.Name, code = k.Code, detail = k.Detail, byConverter = k.ByConverter })
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
}
