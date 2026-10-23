/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Compare;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Converter;

/// <summary>
/// Turns every convertible prim and sculpt part of an OAR into a mesh part and writes a new OAR. The steps: decide each part
/// (<see cref="ArchiveEligibility"/>); build its faces in the unit box with the viewer's face order and texture coordinates
/// (<see cref="FaceBuilder"/>); encode them (<see cref="IMeshEncoder"/>); take the asset id from the encoded bytes; patch the part's
/// shape in place (<see cref="MeshShape"/>) and keep its size, texture entry and contents; write the archive; write a plan that
/// states exactly what changed; and check the new archive against the old one with that plan (<see cref="ArchiveComparer"/>).
/// The source is only read. A part is never re-serialised.
/// </summary>
public sealed class ArchiveConverter
{
    private readonly ConvertOptions m_options;

    public ArchiveConverter(ConvertOptions options = null)
    {
        m_options = options ?? new ConvertOptions();
    }

    public ConvertResult Convert(string sourcePath, string outputPath, string planOutPath = null)
    {
        Stopwatch sw = Stopwatch.StartNew();
        IMeshEncoder encoder = m_options.Encoder ?? new StandInMeshEncoder();

        Dictionary<Guid, ParsedPart> parsed = new Dictionary<Guid, ParsedPart>();
        EligibilityResult elig = new ArchiveEligibility(new EligibilityOptions
        {
            AssumeSafeUnscanned = m_options.AssumeSafeUnscanned,
            PartObserver = pp => parsed.TryAdd(pp.Part.PartId.Guid, pp)
        }).Evaluate(sourcePath);

        ConvertResult result = new ConvertResult
        {
            Source = sourcePath,
            Output = outputPath,
            EncoderName = encoder.Name,
            EncoderValid = encoder.ProducesValidMesh,
            Parts = elig.Parts.Count,
            AssumeSafeUnscanned = m_options.AssumeSafeUnscanned
        };

        foreach (EligibilityRow row in elig.Parts)
        {
            if (row.Action == PartAction.Skip)
                result.Skipped[row.Code] = result.Skipped.TryGetValue(row.Code, out int n) ? n + 1 : 1;
            else if (row.Action == PartAction.KeepPrim)
                result.Kept.Add(new KeptPart
                {
                    PartId = row.PartId, Build = row.Build, LinkNum = row.LinkNum, Name = row.Name, Code = row.Code,
                    Detail = string.Join("; ", row.Reasons.Select(r => r.Code + ": " + r.Detail))
                });
        }

        ISculptMapSource sculptMaps = m_options.SculptMaps;
        ArchiveRewriter rewriter = new ArchiveRewriter();
        Dictionary<Guid, byte[]> meshes = new Dictionary<Guid, byte[]>();

        foreach (EligibilityRow row in elig.Parts.Where(r => r.Action == PartAction.Convert))
        {
            if (!parsed.TryGetValue(row.PartId, out ParsedPart pp) || pp.Shape == null)
            {
                Keep(result, row, "NO_PART_DATA", "the part could not be read a second time");
                continue;
            }

            List<EncoderFace> faces;
            int unnumbered = 0;
            try
            {
                if (row.Kind == "sculpt")
                {
                    sculptMaps ??= new ArchiveSculptMapSource(sourcePath, elig.Parts.Where(r => r.Action == PartAction.Convert && r.Kind == "sculpt").Select(r => parsed.TryGetValue(r.PartId, out ParsedPart q) && q.Shape != null ? q.Shape.SculptTexture.Guid : Guid.Empty));
                    SculptMap map = sculptMaps.Get(pp.Shape.SculptTexture.Guid, out string problem);
                    if (map == null)
                    {
                        Keep(result, row, "SCULPT_MAP_UNREADABLE", problem ?? "the sculpt map could not be read");
                        continue;
                    }
                    SculptResult sr = SculptGeometry.BuildVolume(map, pp.Shape.SculptType, PrimParams.FromShape(pp.Shape));
                    if (sr.Faces.Count == 0)
                    {
                        Keep(result, row, "NO_GEOMETRY", sr.Problem ?? "the sculpt gave no geometry");
                        continue;
                    }
                    faces = FaceBuilder.Slots(sr.Faces, sr.ViewerFaceCount);
                }
                else
                    faces = FaceBuilder.FromPrim(pp.Shape, out unnumbered);
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException || e is NullReferenceException)
            {
                Keep(result, row, "GEOMETRY_FAILED", e.GetType().Name + ": " + e.Message);
                continue;
            }

            if (faces.Count != row.TargetFaces)
            {
                Keep(result, row, "FACE_COUNT_BUILT", "the geometry has " + faces.Count + " faces and the part's target is " + row.TargetFaces);
                continue;
            }
            if (faces.All(f => f.IsEmpty))
            {
                Keep(result, row, "NO_GEOMETRY", "none of the part's faces has any triangles");
                continue;
            }

            byte[] bytes = encoder.Encode(faces, new EncodeOptions());
            if (bytes == null || bytes.Length == 0)
                throw new ConvertException("the encoder returned no bytes for part " + row.PartId);
            Guid id = MeshId.FromBytes(bytes);
            if (StandInIds.Is(id))
                throw new ConvertException("a stand-in mesh id remains for part " + row.PartId);
            meshes.TryAdd(id, bytes);

            int faceCount = faces.Count;
            byte[] extra = pp.ExtraParams;
            rewriter.AddPatch(new PartPatch(row.PartId, present => MeshShape.Edits(faceCount, id, extra, present)));

            ConvertedPart cp = new ConvertedPart
            {
                PartId = row.PartId, Build = row.Build, LinkNum = row.LinkNum, Name = row.Name, Kind = row.Kind,
                Faces = faceCount, EmptyFaces = faces.Count(f => f.IsEmpty), Triangles = faces.Sum(f => f.TriangleCount),
                UnnumberedTriangles = unnumbered, MeshId = id, ServerFacesBefore = row.ServerFaces
            };
            foreach (EligibilityReason w in row.Warnings)
                cp.Warnings.Add(w.Code + ": " + w.Detail);
            if (unnumbered > 0)
                cp.Warnings.Add("MESHER_EXTRA_TRIANGLES: " + unnumbered + " triangle(s) the mesher built belong to no viewer face and are not in the mesh");
            result.Converted.Add(cp);
        }

        foreach (KeyValuePair<Guid, byte[]> m in meshes)
            rewriter.AddAsset(m.Key, AssetType.Mesh, m.Value);
        result.MeshesAdded = meshes.Count;

        // The plan states exactly what changes. It is built from the real ids, and nothing is written while a stand-in id is in it.
        string planJson = PlanJson(result.Converted, meshes.Keys);
        if (StandInIds.Is(planJson))
            throw new ConvertException("the plan holds a stand-in mesh id; nothing was written");
        result.PlanJson = planJson;
        if (planOutPath != null)
            File.WriteAllText(planOutPath, planJson);

        result.Rewrite = rewriter.Rewrite(sourcePath, outputPath);
        result.Parity = new ArchiveComparer().Compare(sourcePath, outputPath, ComparePlan.Parse(planJson));
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static void Keep(ConvertResult result, EligibilityRow row, string code, string detail) =>
        result.Kept.Add(new KeptPart { PartId = row.PartId, Build = row.Build, LinkNum = row.LinkNum, Name = row.Name, Code = code, Detail = detail, ByConverter = true });

    /// <summary>The plan (schema version 1 of the compare command) for a rewrite: a shape change for each converted part and the mesh assets added.</summary>
    public static string PlanJson(IEnumerable<ConvertedPart> converted, IEnumerable<Guid> meshIds)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("partChanges");
            foreach (ConvertedPart c in converted)
            {
                w.WriteStartObject();
                w.WriteString("part", c.PartId.ToString());
                w.WriteString("field", "shape");
                w.WriteString("to", "mesh");
                w.WriteNumber("faces", c.Faces);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("addedEntries");
            foreach (Guid id in meshIds.OrderBy(g => g.ToString(), StringComparer.Ordinal))
                w.WriteStringValue("assets/" + id.ToString("D") + "_mesh.llmesh");
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Reads the sculpt maps the converted sculpts name from the source archive, in one pass over it.</summary>
public sealed class ArchiveSculptMapSource : ISculptMapSource
{
    private readonly Dictionary<Guid, byte[]> m_bytes = new Dictionary<Guid, byte[]>();
    private readonly Dictionary<Guid, (SculptMap map, string problem)> m_decoded = new Dictionary<Guid, (SculptMap, string)>();

    public ArchiveSculptMapSource(string archivePath, IEnumerable<Guid> wanted)
    {
        HashSet<Guid> want = new HashSet<Guid>(wanted.Where(g => g != Guid.Empty));
        if (want.Count == 0)
            return;
        using FileStream fs = File.OpenRead(archivePath);
        BufferedStream buffered = new BufferedStream(fs, 1 << 16);
        int b0 = buffered.ReadByte(), b1 = buffered.ReadByte();
        fs.Seek(0, SeekOrigin.Begin);
        Stream tarStream = b0 == 0x1f && b1 == 0x8b ? new GZipStream(new BufferedStream(fs, 1 << 16), CompressionMode.Decompress) : fs;
        TarStreamReader tar = new TarStreamReader(tarStream);
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
        {
            if (!h.IsFile || !h.Name.StartsWith("assets/", StringComparison.Ordinal) || !h.Name.EndsWith("_texture.jp2", StringComparison.OrdinalIgnoreCase))
                continue;
            string idText = h.Name.Substring("assets/".Length, h.Name.Length - "assets/".Length - "_texture.jp2".Length);
            if (Guid.TryParse(idText, out Guid id) && want.Contains(id))
                m_bytes[id] = tar.ReadBody();
        }
    }

    public SculptMap Get(Guid textureId, out string problem)
    {
        if (m_decoded.TryGetValue(textureId, out var cached))
        {
            problem = cached.problem;
            return cached.map;
        }
        SculptMap map = null;
        problem = "the sculpt map is not in the archive";
        if (m_bytes.TryGetValue(textureId, out byte[] data))
            map = SculptMapReader.Decode(data, out problem);
        m_decoded[textureId] = (map, problem);
        return map;
    }
}
