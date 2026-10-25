/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMetaverse;
using OpenSim.Archive.Compare;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.Archive.Restore;

/// <summary>
/// The way back from Blender. Reads the exporter's .glb files (possibly round-tripped through Blender), matches every node to a
/// part of the original archive, and writes a new archive. A part whose geometry is the one a fresh export of the original
/// gives (within <see cref="GeometryTolerance"/>) is not touched at all. A part whose geometry changed becomes a mesh part:
/// the mesh is built from the node's triangles, and the part's object XML is patched in place (<see cref="MeshShape"/>), so
/// its link number, name, description, scripts, contents and texture entry are kept exactly. The original is only read.
/// </summary>
public sealed class ArchiveRestorer
{
    private const int PhysShapePrim = 0, PhysShapeNone = 1;
    private const float BoxBelow = PrimPhysics.BoundingBoxBelow;

    private readonly RestoreOptions m_options;

    public ArchiveRestorer(RestoreOptions options = null)
    {
        m_options = options ?? new RestoreOptions();
    }

    private sealed class FreshBuild
    {
        public string GlbPath;
        public string Root;
        public string Entry;
        public HashSet<Guid> Parts = new HashSet<Guid>();
    }

    private sealed class PartNode
    {
        public GlbNode Node;
        public Guid PartId;
        public int LinkNum;
        public string Name;
    }

    public RestoreResult Restore(string originalPath, string glbFolder, string outputPath, string reportJsonPath = null)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Action<string> say = m_options.Progress ?? (_ => { });
        string fullSource = Path.GetFullPath(originalPath), fullOut = Path.GetFullPath(outputPath);
        if (!File.Exists(fullSource))
            throw new RestoreException("the original archive '" + originalPath + "' does not exist");
        if (string.Equals(fullSource, fullOut, StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("the output is the original archive itself");
        if (File.Exists(fullOut))
            throw new RestoreException("the output '" + outputPath + "' exists already; it is never overwritten");
        if (!Directory.Exists(glbFolder))
            throw new RestoreException("the .glb folder '" + glbFolder + "' does not exist");

        List<string> glbPaths = Directory.EnumerateFiles(glbFolder, "*.glb", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (glbPaths.Count == 0)
            throw new RestoreException("there is no .glb file in '" + glbFolder + "'");

        RestoreResult result = new RestoreResult
        {
            Source = fullSource, Output = fullOut, GlbFolder = Path.GetFullPath(glbFolder), AllowUnsafe = m_options.AllowUnsafe, GlbFiles = glbPaths.Count,
            TolerancePosition = m_options.Tolerance.Position, ToleranceNormal = m_options.Tolerance.Normal, ToleranceUv = m_options.Tolerance.Uv
        };

        // what the original holds, part by part
        Dictionary<Guid, ParsedPart> parsed = new Dictionary<Guid, ParsedPart>();
        EligibilityResult elig = new ArchiveEligibility(new EligibilityOptions
        {
            AssumeSafeUnscanned = m_options.AssumeSafeUnscanned,
            PartObserver = pp => parsed.TryAdd(pp.Part.PartId.Guid, pp)
        }).Evaluate(fullSource);
        Dictionary<Guid, EligibilityRow> rows = new Dictionary<Guid, EligibilityRow>();
        foreach (EligibilityRow r in elig.Parts)
            rows.TryAdd(r.PartId, r);

        // a fresh export of the original: what the .glb files would hold if nothing had been edited
        string work = Path.GetFullPath(m_options.WorkFolder ?? (fullOut + ".work"));
        result.WorkFolder = work;
        if (string.Equals(work, Path.GetFullPath(glbFolder), StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("the folder for the comparison export is the .glb folder; give --work a different one");
        say("exporting the original into " + work + " to compare against");
        ExportSummary exported = ArchiveExporter.Export(fullSource, new ExportOptions { OutputDirectory = work, Progress = say });
        if (exported.ExitCode == ExitCodes.Unreadable || exported.ExitCode == ExitCodes.EveryBuildFailed || exported.ExitCode == ExitCodes.LowDiskSpace)
            throw new RestoreException("the original could not be exported for the comparison (exit code " + exported.ExitCode.ToString(CultureInfo.InvariantCulture) + ")");
        List<FreshBuild> fresh = IndexFresh(work);
        result.BuildsInArchive = fresh.Count;

        ArchiveRewriter rewriter = new ArchiveRewriter();
        List<ConvertedPart> converted = new List<ConvertedPart>();
        Dictionary<Guid, byte[]> meshes = new Dictionary<Guid, byte[]>();
        HashSet<FreshBuild> covered = new HashSet<FreshBuild>();
        Dictionary<Guid, PartRow> scanRows = null;
        HashSet<Guid> seenParts = new HashSet<Guid>();

        foreach (string glbPath in glbPaths)
        {
            string glbName = Path.GetRelativePath(glbFolder, glbPath);
            GlbFile user;
            Stopwatch fileClock = Stopwatch.StartNew();
            try { user = GlbFile.Read(File.ReadAllBytes(glbPath)); }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is JsonException || e is FormatException || e is InvalidCastException || e is KeyNotFoundException)
            { throw new RestoreException("'" + glbName + "' could not be read: " + e.Message, e); }

            List<PartNode> userParts = PartNodes(user, glbName);
            FreshBuild build = MatchBuild(user, userParts, fresh, glbName);
            if (!covered.Add(build))
                throw new RestoreException("'" + glbName + "' is the second .glb for the build '" + build.Root + "'; give one file for each build");

            if (fileClock.ElapsedMilliseconds > 2000)
                say("'" + glbName + "': read in " + (fileClock.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            GlbFile original = GlbFile.Read(File.ReadAllBytes(build.GlbPath));
            if (fileClock.ElapsedMilliseconds > 2000)
                say("'" + glbName + "': original read, total " + (fileClock.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            List<PartNode> originalParts = PartNodes(original, build.GlbPath);
            CheckLinkset(glbName, userParts, originalParts);

            Dictionary<Guid, PartNode> originalById = originalParts.ToDictionary(p => p.PartId);
            foreach (PartNode up in userParts)
            {
                PartNode op = originalById[up.PartId];
                PartReport report = new PartReport { Glb = glbName, PartId = up.PartId, LinkNum = op.LinkNum, Name = op.Name };
                result.Parts.Add(report);
                seenParts.Add(up.PartId);
                Judge(report, up, op, user, original, parsed, rows, rewriter, converted, meshes, ref scanRows, fullSource, result);
            }
            say("'" + glbName + "': " + userParts.Count.ToString(CultureInfo.InvariantCulture) + " parts read");
        }

        foreach (FreshBuild b in fresh)
            if (!covered.Contains(b))
                result.BuildsWithoutGlb.Add(b.Root + (string.IsNullOrEmpty(b.Entry) ? string.Empty : " (" + b.Entry + ")"));

        result.MeshesAdded = meshes.Count;
        result.MeshBytes = meshes.Values.Sum(b => (long)b.Length);
        if (result.Invalid.Count > 0)
        {
            result.Seconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        foreach (KeyValuePair<Guid, byte[]> m in meshes)
            rewriter.AddAsset(m.Key, AssetType.Mesh, m.Value);

        string planJson = ArchiveConverter.PlanJson(converted, meshes.Keys);
        result.PlanJson = planJson;
        result.Rewrite = rewriter.Rewrite(fullSource, fullOut);
        result.Written = true;
        result.Parity = new ArchiveComparer().Compare(fullSource, fullOut, ComparePlan.Parse(planJson));
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    // ------------------------------------------------------------------ one part

    private void Judge(PartReport report, PartNode up, PartNode op, GlbFile user, GlbFile original, Dictionary<Guid, ParsedPart> parsed,
        Dictionary<Guid, EligibilityRow> rows, ArchiveRewriter rewriter, List<ConvertedPart> converted, Dictionary<Guid, byte[]> meshes,
        ref Dictionary<Guid, PartRow> scanRows, string sourcePath, RestoreResult result)
    {
        Stopwatch readClock = Stopwatch.StartNew();
        PartGeometry before = PartGeometry.FromNode(original, op.Node);
        PartGeometry after = PartGeometry.FromNode(user, up.Node);
        if (readClock.ElapsedMilliseconds > 2000)
            m_options.Progress?.Invoke("link " + op.LinkNum.ToString(CultureInfo.InvariantCulture) + " '" + op.Name + "': read " + before.TriangleCount.ToString(CultureInfo.InvariantCulture) + " + " + after.TriangleCount.ToString(CultureInfo.InvariantCulture) + " triangles in " + (readClock.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s");

        void Refuse(string code, string detail)
        {
            report.Outcome = PartOutcome.Refused;
            report.Code = code;
            report.Detail = detail;
        }

        string moved = TransformChange(op.Node, up.Node, m_options.Tolerance);
        if (before.TriangleCount == 0 && after.TriangleCount == 0)
        {
            if (moved != null) { Refuse("PART_MOVED", moved); return; }
            report.Outcome = PartOutcome.Unchanged;
            return;
        }
        if (after.PrimitivesWithoutFaceNumbers > 0 && after.TriangleCount > 0 && before.PrimitivesWithoutFaceNumbers == 0)
        {
            Refuse("FACE_NUMBERS_LOST", "the .glb no longer says which prim face each triangle belongs to (the _PRIM_FACE attribute is missing); in Blender's export settings turn Attributes on");
            return;
        }

        Stopwatch compareClock = Stopwatch.StartNew();
        GeometryComparison cmp = GeometryComparer.Compare(before, after, m_options.Tolerance);
        if (compareClock.ElapsedMilliseconds > 2000)
            m_options.Progress?.Invoke("link " + op.LinkNum.ToString(CultureInfo.InvariantCulture) + " '" + op.Name + "': compared " + before.TriangleCount.ToString(CultureInfo.InvariantCulture) + " triangles in " + (compareClock.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s");
        report.MaxPosition = cmp.MaxPosition;
        report.MaxNormal = cmp.MaxNormal;
        report.MaxUv = cmp.MaxUv;
        if (cmp.Equal)
        {
            if (moved != null) { Refuse("PART_MOVED", moved); return; }
            report.Outcome = PartOutcome.Unchanged;
            return;
        }
        if (moved != null)
        {
            Refuse("PART_MOVED", moved + " (and its geometry changed: " + cmp.Difference + ")");
            return;
        }

        // ---- the part's geometry changed: it can only become a mesh part
        m_options.Progress?.Invoke("link " + op.LinkNum.ToString(CultureInfo.InvariantCulture) + " '" + op.Name + "': geometry changed (" + cmp.Difference + ")");
        if (!parsed.TryGetValue(up.PartId, out ParsedPart pp) || pp.Shape == null || !rows.TryGetValue(up.PartId, out EligibilityRow row))
        {
            Refuse("NO_PART_DATA", "the original archive holds no shape for this part");
            return;
        }
        if (GlbFile.Bool(op.Node.Extras, "rigged") == true)
        {
            Refuse("RIGGED_MESH", "the part is a rigged mesh; its changes cannot be put back in this version");
            return;
        }
        if (row.Kind == "plant" || row.Kind == "other")
        {
            Refuse("NOT_A_PRIM", "the part is a " + row.Kind + ", not a prim, sculpt or mesh");
            return;
        }
        string blocked = BlockingReason(row, report, ref scanRows, sourcePath, up.PartId);
        if (blocked != null)
        {
            Refuse(SplitCode(blocked), SplitDetail(blocked));
            return;
        }
        if (after.PrimitivesWithoutFaceNumbers > 0)
        {
            Refuse("FACE_NUMBERS_LOST", "the .glb no longer says which prim face each triangle belongs to (the _PRIM_FACE attribute is missing); in Blender's export settings turn Attributes on");
            return;
        }
        if (after.MixedTriangles > 0)
        {
            Refuse("FACES_JOINED", after.MixedTriangles + " triangles take their corners from more than one prim face; keep each face's triangles whole");
            return;
        }
        if (after.TriangleCount == 0)
        {
            Refuse("NO_GEOMETRY", "the part has no triangles left; deleting a part is not restored in this version");
            return;
        }
        int maxFace = after.Faces.Keys.Max();
        if (after.Faces.ContainsKey(-1) || maxFace < 0)
        {
            Refuse("FACE_NUMBERS_LOST", "a triangle carries no prim face number");
            return;
        }
        int freshFaces = (int)(GlbFile.Num(original.Meshes.Count > op.Node.Mesh && op.Node.Mesh >= 0 ? original.Meshes[op.Node.Mesh].Extras : null, "primFaceCount") ?? 0);
        int faceCount = Math.Max(Math.Max(freshFaces, maxFace + 1), 1);
        if (faceCount > MeshShape.MaxFaces)
        {
            Refuse("FACES_GT8", "the part would have " + faceCount + " faces and a mesh part has at most " + MeshShape.MaxFaces);
            return;
        }

        List<EncoderFace> faces;
        try { faces = BuildFaces(after, user, pp.Scale, faceCount); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException)
        {
            Refuse("GEOMETRY_FAILED", e.GetType().Name + ": " + e.Message);
            return;
        }

        PhysicsGeometry physics = PhysicsFrom(faces, pp.Scale);
        byte[] bytes;
        try { bytes = new MeshAssetEncoder().Encode(faces, new EncodeOptions { Physics = physics }); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is OverflowException || e is IndexOutOfRangeException || e is MeshAsset.MeshFormatException)
        {
            Refuse("ENCODE_FAILED", e.GetType().Name + ": " + e.Message);
            return;
        }
        List<string> problems = MeshValidator.Check(bytes, faces, out _, physics);
        if (problems.Count > 0)
        {
            result.Invalid.Add("link " + op.LinkNum.ToString(CultureInfo.InvariantCulture) + " '" + op.Name + "': " + string.Join("; ", problems));
            Refuse("MESH_INVALID", string.Join("; ", problems));
            return;
        }
        Guid id = MeshId.FromBytes(bytes);
        if (StandInIds.Is(id))
            throw new RestoreException("a stand-in mesh id remains for part " + up.PartId);
        meshes.TryAdd(id, bytes);

        byte[] extra = pp.ExtraParams;
        int shapeTypeBefore = pp.PhysicsShapeType;
        // As the converter does for a prim: the region gives a root part of type None its default type when it applies
        // physics, which for the prim was Prim and would be convex for a mesh, so a converted root is set to Prim.
        bool rootNone = shapeTypeBefore == PhysShapeNone && pp.Part.IsRoot && row.Kind != "mesh";
        int shapeTypeAfter = rootNone ? PhysShapePrim : shapeTypeBefore;
        int faceTotal = faces.Count;
        rewriter.AddPatch(new PartPatch(up.PartId, present =>
        {
            List<ElementEdit> edits = new List<ElementEdit>(MeshShape.Edits(faceTotal, id, extra, present));
            if (rootNone && present.Contains("PhysicsShapeType"))
                edits.Add(ElementEdit.SetText("PhysicsShapeType", PhysShapePrim.ToString(CultureInfo.InvariantCulture)));
            return edits;
        }));

        converted.Add(new ConvertedPart
        {
            PartId = up.PartId, Build = row.Build, LinkNum = row.LinkNum, Name = row.Name, Kind = row.Kind, Faces = faceTotal,
            EmptyFaces = faces.Count(f => f.IsEmpty), Triangles = faces.Sum(f => f.TriangleCount), MeshId = id, ServerFacesBefore = row.ServerFaces,
            PhysicsVertices = physics?.VertexCount ?? 0, PhysicsTriangles = physics?.TriangleCount ?? 0,
            PhysicsShapeTypeBefore = shapeTypeBefore, PhysicsShapeTypeAfter = shapeTypeAfter
        });
        report.Outcome = PartOutcome.Rebuilt;
        report.Faces = faceTotal;
        report.Triangles = faces.Sum(f => f.TriangleCount);
        report.MeshId = id;
        report.Detail = cmp.Difference;
        if (physics == null)
            report.Notes.Add("the physics mesh has more vertices than a mesh asset can index; the part collides as its bounding hull");
    }

    private static string SplitCode(string blocked) => blocked.Substring(0, blocked.IndexOf('|'));
    private static string SplitDetail(string blocked) => blocked.Substring(blocked.IndexOf('|') + 1);

    /// <summary>
    /// The reason a changed part may not be rebuilt, as "CODE|detail", or null. Script reasons are overridden by
    /// --allow-unsafe and recorded in the part's notes; every other reason stands.
    /// </summary>
    private string BlockingReason(EligibilityRow row, PartReport report, ref Dictionary<Guid, PartRow> scanRows, string sourcePath, Guid partId)
    {
        string[] hard = { ReasonCodes.ExportDenied, ReasonCodes.Physical, ReasonCodes.PhysicalScript, ReasonCodes.Flexi };
        EligibilityReason stop = row.Reasons.FirstOrDefault(r => hard.Contains(r.Code));
        if (stop != null)
            return stop.Code + "|" + stop.Detail;

        string scriptCode = null, scriptDetail = null;
        EligibilityReason script = row.Reasons.FirstOrDefault(r => r.Code == ReasonCodes.ScriptUnsafe || r.Code == ReasonCodes.ScriptUncertain);
        if (script != null)
        {
            scriptCode = script.Code;
            scriptDetail = script.Detail;
        }
        else if (row.Reasons.Any(r => r.Code == ReasonCodes.AlreadyMesh))
        {
            // the eligibility rules stop at "already a mesh" and do not look at its scripts: look at them here
            scanRows ??= new ArchiveScriptScanner(new ScanOptions { AssumeSafeUnscanned = m_options.AssumeSafeUnscanned }).Scan(sourcePath).Parts.GroupBy(p => p.PartId).ToDictionary(g => g.Key, g => g.First());
            if (scanRows.TryGetValue(partId, out PartRow sr) && PartDecisionNames.IsKept(sr.Decision))
            {
                bool unsafeHit = sr.Decision == PartDecision.KeepPrimUnsafe;
                string level = unsafeHit ? "unsafe" : "uncertain";
                scriptCode = unsafeHit ? ReasonCodes.ScriptUnsafe : ReasonCodes.ScriptUncertain;
                scriptDetail = "script scan: " + string.Join("; ", sr.Reasons.Where(r => r.Level == level).Take(6).Select(r =>
                    r.Code + (string.IsNullOrEmpty(r.Script) ? string.Empty : " in script '" + Sanitize.Text(r.Script) + "'" + (r.Line > 0 ? " line " + r.Line.ToString(CultureInfo.InvariantCulture) : string.Empty))));
            }
        }
        if (scriptCode != null)
        {
            if (!m_options.AllowUnsafe)
                return scriptCode + "|" + scriptDetail + " (--allow-unsafe rebuilds it anyway)";
            report.Notes.Add("rebuilt with --allow-unsafe although the scripts are rated " + (scriptCode == ReasonCodes.ScriptUnsafe ? "UNSAFE" : "UNCERTAIN") + ": " + scriptDetail);
        }
        return null;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The part's triangles as faces in the unit box: the node's metres, back in viewer axes, divided by the part's size.</summary>
    private static List<EncoderFace> BuildFaces(PartGeometry g, GlbFile user, Vector3 size, int faceCount)
    {
        float sx = size.X != 0 ? size.X : 1f, sy = size.Y != 0 ? size.Y : 1f, sz = size.Z != 0 ? size.Z : 1f;
        List<PrimFace> primFaces = new List<PrimFace>();
        foreach (KeyValuePair<int, List<float[]>> kv in g.Faces)
        {
            List<float[]> tris = kv.Value;
            List<int> materials = g.Materials[kv.Key];
            float[] pos = new float[tris.Count * 9], nor = new float[tris.Count * 9], uv = new float[tris.Count * 6];
            for (int t = 0; t < tris.Count; t++)
            {
                float[] tri = tris[t];
                JsonObject mat = materials[t] >= 0 && materials[t] < user.MaterialExtras.Count ? user.MaterialExtras[materials[t]] : null;
                for (int c = 0; c < 3; c++)
                {
                    int o = c * 8, p = t * 9 + c * 3, q = t * 6 + c * 2;
                    // glTF (x, y, z) is the viewer's (x, -z, y)
                    pos[p] = tri[o] / sx; pos[p + 1] = -tri[o + 2] / sy; pos[p + 2] = tri[o + 1] / sz;
                    float nx = tri[o + 3] * sx, ny = -tri[o + 5] * sy, nz = tri[o + 4] * sz;
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; }
                    nor[p] = nx; nor[p + 1] = ny; nor[p + 2] = nz;
                    uv[q] = tri[o + 6]; uv[q + 1] = tri[o + 7];
                }
                if (GlbFile.Bool(mat, "uvTransformBaked") == true)
                    Unbake(uv, t * 6, mat);
            }
            primFaces.Add(new PrimFace { Number = kv.Key, Kind = PrimFaceKind.Outer, MesherNumber = kv.Key, Positions = pos, Normals = nor, Uvs = uv });
        }
        return FaceBuilder.Slots(primFaces, faceCount);
    }

    /// <summary>Undoes <c>UvTransform.Bake</c> for the three corners at <paramref name="at"/>: the exporter wrote the viewer's repeat, offset and rotation into the coordinates of such a face.</summary>
    private static void Unbake(float[] uvs, int at, JsonObject material)
    {
        float[] repeat = Pair(material, "repeat"), offset = Pair(material, "offset");
        float rotation = (float)(GlbFile.Num(material, "rotation") ?? 0);
        if (repeat == null || offset == null || repeat[0] == 0 || repeat[1] == 0)
            return;
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        for (int i = at; i < at + 6; i += 2)
        {
            float ru = uvs[i] - offset[0] - 0.5f, rt = (1f - uvs[i + 1]) - offset[1] - 0.5f;
            ru /= repeat[0];
            rt /= repeat[1];
            float u = ru * c - rt * s, t = ru * s + rt * c;
            uvs[i] = u + 0.5f;
            uvs[i + 1] = 1f - (t + 0.5f);
        }
    }

    private static float[] Pair(JsonObject o, string key)
    {
        if (o == null || o[key] is not JsonArray a || a.Count != 2)
            return null;
        return new[] { (float)(double)a[0], (float)(double)a[1] };
    }

    /// <summary>
    /// The collision shape of an edited part: its own triangles, in the unit box, as one mesh. A part under 0.2 m in all three
    /// directions gets its bounding box, as the region's mesher gives a small mesh (<see cref="PrimPhysics.RegionBoxesIt"/>).
    /// Null when the vertices would not fit a mesh asset's 16-bit indices; the asset then keeps only its bounding hull.
    /// </summary>
    private static PhysicsGeometry PhysicsFrom(List<EncoderFace> faces, Vector3 size)
    {
        List<float> positions = new List<float>();
        List<int> indices = new List<int>();
        foreach (EncoderFace f in faces)
        {
            if (f.IsEmpty)
                continue;
            int baseIndex = positions.Count / 3;
            positions.AddRange(f.Positions);
            foreach (uint i in f.Indices)
                indices.Add(baseIndex + (int)i);
        }
        if (indices.Count == 0)
            return null;
        PhysicsGeometry g = new PhysicsGeometry { Positions = positions.ToArray(), Indices = indices.ToArray() };
        if (size.X < BoxBelow && size.Y < BoxBelow && size.Z < BoxBelow)
            return PrimPhysics.BoundingBox(g);
        return g.VertexCount > 65535 ? null : g;
    }

    private static string TransformChange(GlbNode before, GlbNode after, GeometryTolerance tol)
    {
        float[] ta = before.Translation ?? new float[3], tb = after.Translation ?? new float[3];
        float dt = Math.Max(Math.Abs(ta[0] - tb[0]), Math.Max(Math.Abs(ta[1] - tb[1]), Math.Abs(ta[2] - tb[2])));
        if (dt > tol.Position)
            return "the part was moved by " + dt.ToString("0.####", CultureInfo.InvariantCulture) + " m inside its build; moving a part is not restored in this version";
        float[] ra = before.Rotation ?? new float[] { 0, 0, 0, 1 }, rb = after.Rotation ?? new float[] { 0, 0, 0, 1 };
        float dot = Math.Abs(ra[0] * rb[0] + ra[1] * rb[1] + ra[2] * rb[2] + ra[3] * rb[3]);
        float la = MathF.Sqrt(ra[0] * ra[0] + ra[1] * ra[1] + ra[2] * ra[2] + ra[3] * ra[3]), lb = MathF.Sqrt(rb[0] * rb[0] + rb[1] * rb[1] + rb[2] * rb[2] + rb[3] * rb[3]);
        if (la > 0 && lb > 0 && dot / (la * lb) < 1f - 1e-6f)
            return "the part was turned inside its build; turning a part is not restored in this version";
        return null;
    }

    // ------------------------------------------------------------------ matching

    private static List<PartNode> PartNodes(GlbFile file, string label)
    {
        List<PartNode> list = new List<PartNode>();
        foreach (GlbNode n in file.Nodes)
        {
            string id = GlbFile.Str(n.Extras, "partUuid");
            if (id == null)
                continue;
            if (!Guid.TryParse(id, out Guid g))
                throw new RestoreException("'" + label + "' has a node whose partUuid '" + id + "' is not a UUID");
            list.Add(new PartNode
            {
                Node = n,
                PartId = g,
                LinkNum = (int)Math.Round(GlbFile.Num(n.Extras, "linkNumber") ?? 0),
                Name = GlbFile.Str(n.Extras, "partName") ?? n.Name
            });
        }
        return list;
    }

    private static FreshBuild MatchBuild(GlbFile user, List<PartNode> userParts, List<FreshBuild> fresh, string label)
    {
        if (userParts.Count == 0)
            throw new RestoreException("'" + label + "' has no part nodes (no node carries a partUuid); it is not a file of the exporter's, or Blender dropped the custom properties (turn Custom Properties on in Blender's export settings)");
        GlbNode objectNode = user.Nodes.FirstOrDefault(n => GlbFile.Str(n.Extras, "kind") == "object");
        string root = GlbFile.Str(objectNode?.Extras, "rootPartUuid");
        string entry = GlbFile.Str(objectNode?.Extras, "archiveEntry");
        List<FreshBuild> candidates = root != null && Guid.TryParse(root, out Guid rootId)
            ? fresh.Where(b => Guid.TryParse(b.Root, out Guid g) && g == rootId).ToList()
            : fresh.Where(b => b.Parts.Contains(userParts[0].PartId)).ToList();
        if (candidates.Count > 1 && entry != null)
        {
            List<FreshBuild> byEntry = candidates.Where(b => b.Entry == entry).ToList();
            if (byEntry.Count > 0)
                candidates = byEntry;
        }
        if (candidates.Count == 0)
            throw new RestoreException("'" + label + "' belongs to no build of the original archive (its root part is " + (root ?? userParts[0].PartId.ToString()) + "); it was exported from another archive, or the original is not the one it came from");
        return candidates[0];
    }

    /// <summary>A .glb whose parts are not the original build's parts, one for one, is refused (added and deleted parts are not restored in this version).</summary>
    private static void CheckLinkset(string label, List<PartNode> user, List<PartNode> original)
    {
        List<string> problems = new List<string>();
        foreach (IGrouping<Guid, PartNode> dup in user.GroupBy(p => p.PartId).Where(g => g.Count() > 1))
            problems.Add("part " + dup.Key + " appears " + dup.Count() + " times (a copied part is a new part)");
        HashSet<Guid> have = user.Select(p => p.PartId).ToHashSet(), want = original.Select(p => p.PartId).ToHashSet();
        foreach (PartNode p in original.Where(p => !have.Contains(p.PartId)))
            problems.Add("the part '" + p.Name + "' (link " + p.LinkNum.ToString(CultureInfo.InvariantCulture) + ", " + p.PartId + ") is missing");
        foreach (PartNode p in user.Where(p => !want.Contains(p.PartId)))
            problems.Add("the part '" + p.Name + "' (" + p.PartId + ") is not in the original build");
        Dictionary<Guid, int> links = original.GroupBy(p => p.PartId).ToDictionary(g => g.Key, g => g.First().LinkNum);
        foreach (PartNode p in user.Where(p => links.TryGetValue(p.PartId, out int l) && l != p.LinkNum))
            problems.Add("the part '" + p.Name + "' has link number " + p.LinkNum.ToString(CultureInfo.InvariantCulture) + " and had " + links[p.PartId].ToString(CultureInfo.InvariantCulture));
        if (problems.Count > 0)
            throw new RestoreException("'" + label + "' does not match the original build part for part; nothing was written. Adding, deleting and renumbering parts is not restored in this version. " + string.Join(" ", problems.Take(8)) + (problems.Count > 8 ? " (and " + (problems.Count - 8) + " more)" : string.Empty));
    }

    private static List<FreshBuild> IndexFresh(string work)
    {
        List<FreshBuild> list = new List<FreshBuild>();
        foreach (string manifest in Directory.EnumerateFiles(work, "manifest.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            JsonElement root = doc.RootElement;
            string file = Prop(root, "file");
            if (string.IsNullOrEmpty(file))
                continue;
            string glb = Path.Combine(Path.GetDirectoryName(manifest), file);
            if (!File.Exists(glb))
                continue;
            FreshBuild b = new FreshBuild { GlbPath = glb, Root = Prop(root, "rootPartUuid"), Entry = Prop(root, "archiveEntry") };
            foreach (JsonProperty p in root.EnumerateObject())
                if (string.Equals(p.Name, "parts", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement part in p.Value.EnumerateArray())
                        if (Guid.TryParse(Prop(part, "partUuid"), out Guid g))
                            b.Parts.Add(g);
            list.Add(b);
        }
        return list;
    }

    private static string Prop(JsonElement e, string name)
    {
        foreach (JsonProperty p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }
}
