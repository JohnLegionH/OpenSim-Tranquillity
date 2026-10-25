/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;
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
/// gives (within <see cref="GeometryTolerance"/>) keeps its shape: if only its place or size changed, its position, rotation and
/// scale are updated and it stays the prim (or sculpt, or mesh) it was. A part whose geometry changed becomes a mesh part: the
/// mesh is built from the node's triangles, and the part's object XML is patched in place (<see cref="MeshShape"/>), so its link
/// number, name, description, scripts, contents and texture entry are kept exactly. A part the .glb no longer has is refused, or
/// with <see cref="RestoreOptions.StubDeleted"/> kept as an invisible stub; a node that is no part of the original becomes a new
/// part after the last link number. The original is only read.
/// </summary>
public sealed partial class ArchiveRestorer
{
    private const int PhysShapePrim = 0, PhysShapeNone = 1;
    private const float BoxBelow = PrimPhysics.BoundingBoxBelow;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

    /// <summary>One change the parity check has to expect.</summary>
    private sealed class PlanChange
    {
        public Guid Part;
        public string Field;
        public int Faces;
    }

    /// <summary>Everything one run collects before the archive is written.</summary>
    private sealed class Run
    {
        public ArchiveRewriter Rewriter = new ArchiveRewriter();
        public Dictionary<Guid, byte[]> Meshes = new Dictionary<Guid, byte[]>();
        public List<PlanChange> Plan = new List<PlanChange>();
        public List<Guid> AddedParts = new List<Guid>();
        /// <summary>Parts cut out of the archive because they were deleted in a linkset with no script.</summary>
        public List<Guid> RemovedParts = new List<Guid>();
        public Dictionary<Guid, List<Func<PartView, IEnumerable<ElementEdit>>>> Edits = new Dictionary<Guid, List<Func<PartView, IEnumerable<ElementEdit>>>>();
        public Dictionary<Guid, ParsedPart> Parsed;
        public Dictionary<Guid, EligibilityRow> Rows;
        public Dictionary<Guid, PartRow> ScanRows;
        public string SourcePath;
        public RestoreResult Result;
        public TextureCatalog Textures;
        /// <summary>For each build whose linkset was moved or turned (by the root part's id): the group's new position, given its old one (viewer axes).</summary>
        public Dictionary<Guid, Func<float[], float[]>> GroupShift = new Dictionary<Guid, Func<float[], float[]>>();

        public void Edit(Guid part, Func<PartView, IEnumerable<ElementEdit>> edits)
        {
            if (!Edits.TryGetValue(part, out List<Func<PartView, IEnumerable<ElementEdit>>> list))
                Edits[part] = list = new List<Func<PartView, IEnumerable<ElementEdit>>>();
            list.Add(edits);
        }

        public void Plan1(Guid part, string field, int faces = 0)
        {
            if (!Plan.Any(p => p.Part == part && p.Field == field))
                Plan.Add(new PlanChange { Part = part, Field = field, Faces = faces });
        }
    }

    public RestoreResult Restore(string originalPath, string glbFolder, string outputPath, string reportJsonPath = null)
    {
        string fullSource = Path.GetFullPath(originalPath), fullOut = Path.GetFullPath(outputPath);
        if (!File.Exists(fullSource))
            throw new RestoreException("the original archive '" + originalPath + "' does not exist");
        if (string.Equals(fullSource, fullOut, StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("the output is the original archive itself");
        if (File.Exists(fullOut))
            throw new RestoreException("the output '" + outputPath + "' exists already; it is never overwritten");
        if (!Directory.Exists(glbFolder))
            throw new RestoreException("the .glb folder '" + glbFolder + "' does not exist");

        string work = Path.GetFullPath(m_options.WorkFolder ?? Path.Combine(Path.GetTempPath(), "oar-restore-" + Guid.NewGuid().ToString("N")));
        if (string.Equals(work, Path.GetFullPath(glbFolder), StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("the folder for the comparison export is the .glb folder; give --work a different one");
        // The folder is the tool's own to remove only if this run makes it: a folder that already held files is never emptied.
        bool ownsWork = !Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work).Any();
        RestoreResult result = new RestoreResult();
        try
        {
            return RestoreCore(fullSource, glbFolder, fullOut, work, result);
        }
        finally
        {
            if (!m_options.KeepWork && ownsWork && Directory.Exists(work))
            {
                try
                {
                    Directory.Delete(work, true);
                    result.WorkFolderRemoved = true;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    m_options.Progress?.Invoke("the work folder '" + work + "' could not be removed: " + e.Message);
                }
            }
        }
    }

    private RestoreResult RestoreCore(string fullSource, string glbFolder, string fullOut, string work, RestoreResult result)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Action<string> say = m_options.Progress ?? (_ => { });

        List<string> glbPaths = Directory.EnumerateFiles(glbFolder, "*.glb", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (glbPaths.Count == 0)
            throw new RestoreException("there is no .glb file in '" + glbFolder + "'");

        result.Source = fullSource;
        result.Output = fullOut;
        result.GlbFolder = Path.GetFullPath(glbFolder);
        result.AllowUnsafe = m_options.AllowUnsafe;
        result.StubDeleted = m_options.StubDeleted;
        result.GlbFiles = glbPaths.Count;
        result.TolerancePosition = m_options.Tolerance.Position;
        result.ToleranceNormal = m_options.Tolerance.Normal;
        result.ToleranceUv = m_options.Tolerance.Uv;

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

        // how the files were made decides how the fresh export is made: a file the exporter stamped as cleaned is compared with a
        // cleaned export, a file with no stamp (made before cleaning existed) or one stamped as not cleaned with an uncleaned one
        int cleanedFiles = 0;
        foreach (string glbPath in glbPaths)
        {
            try
            {
                if (GlbFile.PeekStamp(glbPath)?.Clean == true)
                    cleanedFiles++;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is JsonException || e is FormatException)
            { throw new RestoreException("'" + Path.GetRelativePath(glbFolder, glbPath) + "' could not be read: " + e.Message, e); }
        }
        if (cleanedFiles != 0 && cleanedFiles != glbPaths.Count)
            throw new RestoreException(cleanedFiles + " of the " + glbPaths.Count + " .glb files were exported with cleaning and the others without; export them all the same way");
        result.ComparedAsCleaned = cleanedFiles > 0;

        // a fresh export of the original: what the .glb files would hold if nothing had been edited
        result.WorkFolder = work;
        say("exporting the original into " + work + " to compare against" + (result.ComparedAsCleaned ? string.Empty : " (without cleaning, as the .glb files were made)"));
        ExportSummary exported = ArchiveExporter.Export(fullSource, new ExportOptions { OutputDirectory = work, Progress = say, Clean = result.ComparedAsCleaned });
        if (exported.ExitCode == ExitCodes.Unreadable || exported.ExitCode == ExitCodes.EveryBuildFailed || exported.ExitCode == ExitCodes.LowDiskSpace)
            throw new RestoreException("the original could not be exported for the comparison (exit code " + exported.ExitCode.ToString(Inv) + ")");
        List<FreshBuild> fresh = IndexFresh(work);
        result.BuildsInArchive = fresh.Count;

        Run run = new Run { Parsed = parsed, Rows = rows, SourcePath = fullSource, Result = result, Textures = new TextureCatalog(work) };
        HashSet<FreshBuild> covered = new HashSet<FreshBuild>();

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
                say("'" + glbName + "': read in " + (fileClock.ElapsedMilliseconds / 1000.0).ToString("0.0", Inv) + " s");
            GlbFile original = GlbFile.Read(File.ReadAllBytes(build.GlbPath));
            List<PartNode> originalParts = PartNodes(original, build.GlbPath);

            SortNodes(user, userParts, originalParts, glbName, out List<PartNode> kept, out List<PartNode> addedNodes);
            NodeFrame userFrame = new NodeFrame(user), originFrame = new NodeFrame(original);
            Dictionary<Guid, PartNode> originalById = originalParts.ToDictionary(p => p.PartId);
            Guid.TryParse(build.Root, out Guid rootId);

            // In a linkset where no part holds a script, a part deleted in the .glb is deleted for real (the root part never is);
            // otherwise a deleted part is refused, or kept as a stub, because scripts address their linkset's parts by link number.
            HashSet<Guid> haveIds = kept.Select(p => p.PartId).ToHashSet();
            List<PartNode> gone = originalParts.Where(p => !haveIds.Contains(p.PartId)).ToList();
            bool scriptFree = !build.Parts.Concat(originalParts.Select(p => p.PartId)).Any(id => run.Parsed.TryGetValue(id, out ParsedPart held) && HoldsScript(held));
            HashSet<Guid> removing = scriptFree
                ? gone.Where(p => run.Parsed.TryGetValue(p.PartId, out ParsedPart q) && !q.Part.IsRoot).Select(p => p.PartId).ToHashSet()
                : new HashSet<Guid>();
            string groupMove = MoveGroup(run, build, rootId, kept, originalById, user, original, userFrame, originFrame, removing);
            int userRootNode = kept.FirstOrDefault(p => p.PartId == rootId)?.Node.Index ?? -1;
            int originRootNode = originalById.TryGetValue(rootId, out PartNode originalRootNode) ? originalRootNode.Node.Index : -1;

            foreach (PartNode up in kept)
            {
                PartNode op = originalById[up.PartId];
                PartReport report = new PartReport { Glb = glbName, PartId = up.PartId, LinkNum = op.LinkNum, Name = op.Name };
                result.Parts.Add(report);
                Judge(run, report, up, op, user, original, userFrame, originFrame, up.PartId == rootId ? groupMove : null, userRootNode, originRootNode);
                WarnTextureTransform(report, user, up.Node);
            }
            foreach (PartNode op in gone)
            {
                PartReport report = new PartReport { Glb = glbName, PartId = op.PartId, LinkNum = op.LinkNum, Name = op.Name };
                result.Parts.Add(report);
                Delete(run, report, op, removing.Contains(op.PartId));
            }
            int linksAfter = removing.Count > 0 ? Renumber(run, build, removing) : -1;
            if (addedNodes.Count > 0)
            {
                int next = linksAfter >= 0 ? Math.Max(1, linksAfter)
                    : Math.Max(1, build.Parts.Select(g => run.Parsed.TryGetValue(g, out ParsedPart pp) ? pp.Part.LinkNum : 0).DefaultIfEmpty(0).Max());
                foreach (PartNode node in addedNodes)
                {
                    PartReport report = new PartReport { Glb = glbName, Name = node.Name, Outcome = PartOutcome.Refused };
                    result.Parts.Add(report);
                    if (Add(run, report, node, user, userFrame, rootId, userRootNode, next + 1, result.Parts.Count(p => p.Outcome == PartOutcome.Added)))
                        next++;
                }
            }
            say("'" + glbName + "': " + userParts.Count.ToString(Inv) + " parts read");
        }

        foreach (FreshBuild b in fresh)
            if (!covered.Contains(b))
                result.BuildsWithoutGlb.Add(b.Root + (string.IsNullOrEmpty(b.Entry) ? string.Empty : " (" + b.Entry + ")"));

        result.MeshesAdded = run.Meshes.Count;
        result.MeshBytes = run.Meshes.Values.Sum(b => (long)b.Length);
        result.TexturesAdded = run.Textures.NewAssets.Count();
        if (result.Invalid.Count > 0)
        {
            result.Seconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        foreach (KeyValuePair<Guid, List<Func<PartView, IEnumerable<ElementEdit>>>> e in run.Edits)
        {
            List<Func<PartView, IEnumerable<ElementEdit>>> makers = e.Value;
            run.Rewriter.AddPatch(new PartPatch(e.Key, (PartView view) =>
            {
                List<ElementEdit> all = new List<ElementEdit>();
                foreach (Func<PartView, IEnumerable<ElementEdit>> make in makers)
                    all.AddRange(make(view));
                return all;
            }));
        }
        foreach (Guid id in run.RemovedParts)
            run.Rewriter.RemovePart(id);
        foreach (KeyValuePair<Guid, byte[]> m in run.Meshes)
            run.Rewriter.AddAsset(m.Key, AssetType.Mesh, m.Value);
        foreach (KeyValuePair<Guid, byte[]> t in run.Textures.NewAssets)
            run.Rewriter.AddAsset(t.Key, AssetType.Texture, t.Value);

        string planJson = PlanJson(run);
        result.PlanJson = planJson;
        result.Rewrite = run.Rewriter.Rewrite(fullSource, fullOut);
        result.Written = true;
        result.Parity = new ArchiveComparer().Compare(fullSource, fullOut, ComparePlan.Parse(planJson));
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    // ------------------------------------------------------------------ the whole linkset

    /// <summary>
    /// The root part moved or turned, or the build was: the whole linkset moves. Every part of the build gets the new place
    /// (GroupPosition), the root its new rotation. Returns the sentence for the root's report, or null when nothing moved.
    /// </summary>
    private string MoveGroup(Run run, FreshBuild build, Guid rootId, List<PartNode> kept, Dictionary<Guid, PartNode> originalById,
        GlbFile user, GlbFile original, NodeFrame userFrame, NodeFrame originFrame, HashSet<Guid> removing)
    {
        PartNode userRoot = kept.FirstOrDefault(p => p.PartId == rootId);
        if (userRoot == null || !originalById.TryGetValue(rootId, out PartNode originalRoot) || !run.Parsed.TryGetValue(rootId, out ParsedPart rootPart))
            return null;
        GlbNode userObject = userFrame.ObjectNode >= 0 ? user.Nodes[userFrame.ObjectNode] : null;
        bool world = GlbFile.Str(userObject?.Extras, "placement") == "world";
        // The comparison export always has the build at the root part's own frame. A .glb in world placement carries the build at its
        // place in the region, so what it is compared with is the place the original's object node records.
        NodePose before = PoseMath.RootPlace(originFrame.Relative(originalRoot.Node.Index), originFrame.ObjectPose);
        if (world && RegionPlace(original, originFrame) is NodePose recorded)
            before = recorded;
        NodePose after = PoseMath.RootPlace(userFrame.Relative(userRoot.Node.Index), userFrame.ObjectPose);
        PoseChange change = PoseMath.Compare(before, after, m_options.Tolerance.Position);
        if (!change.Moved && !change.Turned)
            return null;

        float[] oldRotation = { rootPart.RotationOffset.X, rootPart.RotationOffset.Y, rootPart.RotationOffset.Z, rootPart.RotationOffset.W };
        PoseMath.GroupPlace(world, new[] { 0f, 0f, 0f }, oldRotation, after, out _, out float[] newRotation);
        float[] PlaceFrom(float[] old)
        {
            // in world placement the answer is the root's place in the .glb; otherwise the change is in the root's own frame and is added to the old place
            PoseMath.GroupPlace(world, old, oldRotation, after, out float[] position, out _);
            return position;
        }
        run.GroupShift[rootId] = PlaceFrom;

        // a group that was only turned keeps its position, so no part's group position is written
        foreach (Guid id in change.Moved ? build.Parts : new HashSet<Guid>())
        {
            if (!run.Parsed.ContainsKey(id) || removing.Contains(id))
                continue;
            run.Edit(id, view => VectorEdits("GroupPosition", PlaceFrom(new[] { Number(view, "GroupPosition/X"), Number(view, "GroupPosition/Y"), Number(view, "GroupPosition/Z") })));
            run.Plan1(id, "groupPosition");
        }
        if (change.Turned)
        {
            run.Edit(rootId, view => QuatEdits("RotationOffset", newRotation));
            run.Plan1(rootId, "rotation");
        }
        return "the whole linkset " + change.Describe(true, true) + " with its root part";
    }

    /// <summary>Where the exporter recorded the build in the region (viewer axes), as a glTF-axes pose; null when the file does not say.</summary>
    private static NodePose? RegionPlace(GlbFile file, NodeFrame frame)
    {
        if (frame.ObjectNode < 0)
            return null;
        JsonObject extras = file.Nodes[frame.ObjectNode].Extras;
        float[] position = Numbers(extras?["regionPosition"], 3), rotation = Numbers(extras?["regionRotation"], 4);
        if (position == null || rotation == null)
            return null;
        return new NodePose(PoseMath.GltVector(position), PoseMath.GltQuat(rotation), System.Numerics.Vector3.One);
    }

    private static float[] Numbers(JsonNode node, int count)
    {
        if (node is not JsonArray a || a.Count != count)
            return null;
        float[] result = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (a[i] is not JsonValue v || !v.TryGetValue(out double d))
                return null;
            result[i] = (float)d;
        }
        return result;
    }

    private static float Number(PartView view, string path) =>
        float.TryParse(view.Text(path), NumberStyles.Float, Inv, out float v) ? v : throw new InvalidDataException("the part has no number at '" + path + "'");

    private static string Text(float v) => (Math.Abs(v) < 1e-7f ? 0f : (float)Math.Round(v, 6)).ToString("R", Inv);

    private static IEnumerable<ElementEdit> VectorEdits(string element, float[] v)
    {
        yield return ElementEdit.SetText(element + "/X", Text(v[0]));
        yield return ElementEdit.SetText(element + "/Y", Text(v[1]));
        yield return ElementEdit.SetText(element + "/Z", Text(v[2]));
    }

    private static IEnumerable<ElementEdit> QuatEdits(string element, float[] q)
    {
        yield return ElementEdit.SetText(element + "/X", Text(q[0]));
        yield return ElementEdit.SetText(element + "/Y", Text(q[1]));
        yield return ElementEdit.SetText(element + "/Z", Text(q[2]));
        yield return ElementEdit.SetText(element + "/W", Text(q[3]));
    }

    // ------------------------------------------------------------------ one part

    private void Judge(Run run, PartReport report, PartNode up, PartNode op, GlbFile user, GlbFile original,
        NodeFrame userFrame, NodeFrame originFrame, string groupMove, int userRootNode, int originRootNode)
    {
        Stopwatch readClock = Stopwatch.StartNew();
        run.Parsed.TryGetValue(up.PartId, out ParsedPart pp);
        bool isRoot = pp?.Part.IsRoot == true;
        // a child is judged by its place in the root part's frame, wherever its parents are in the .glb
        NodePose poseBefore = isRoot ? originFrame.Relative(op.Node.Index) : originFrame.InRootFrame(op.Node.Index, originRootNode);
        NodePose poseAfter = isRoot ? userFrame.Relative(up.Node.Index) : userFrame.InRootFrame(up.Node.Index, userRootNode);
        PoseChange pose = PoseMath.Compare(poseBefore, poseAfter, m_options.Tolerance.Position);
        if (isRoot)
        {
            // a root part's place is the linkset's place, handled with the whole group; only a resize is the part's own
            pose.Moved = false;
            pose.Turned = false;
        }
        PartGeometry before = PartGeometry.FromNode(original, op.Node, null, false);
        PartGeometry after = PartGeometry.FromNode(user, up.Node, null, false);
        if (readClock.ElapsedMilliseconds > 2000)
            m_options.Progress?.Invoke("link " + op.LinkNum.ToString(Inv) + " '" + op.Name + "': read " + before.TriangleCount.ToString(Inv) + " + " + after.TriangleCount.ToString(Inv) + " triangles in " + (readClock.ElapsedMilliseconds / 1000.0).ToString("0.0", Inv) + " s");

        void Refuse(string code, string detail)
        {
            report.Outcome = PartOutcome.Refused;
            report.Code = code;
            report.Detail = detail;
        }

        if (before.TriangleCount == 0 && after.TriangleCount == 0)
        {
            PlaceOnly(run, report, up.PartId, pp, pose, poseAfter, groupMove);
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
            m_options.Progress?.Invoke("link " + op.LinkNum.ToString(Inv) + " '" + op.Name + "': compared " + before.TriangleCount.ToString(Inv) + " triangles in " + (compareClock.ElapsedMilliseconds / 1000.0).ToString("0.0", Inv) + " s");
        report.MaxPosition = cmp.MaxPosition;
        report.MaxNormal = cmp.MaxNormal;
        report.MaxUv = cmp.MaxUv;
        if (cmp.Equal)
        {
            PlaceOnly(run, report, up.PartId, pp, pose, poseAfter, groupMove);
            return;
        }

        // ---- the part's geometry changed: it can only become a mesh part
        m_options.Progress?.Invoke("link " + op.LinkNum.ToString(Inv) + " '" + op.Name + "': geometry changed (" + cmp.Difference + ")");
        if (pp == null || pp.Shape == null || !run.Rows.TryGetValue(up.PartId, out EligibilityRow row))
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
        string blocked = BlockingReason(run, row, report, up.PartId);
        if (blocked != null)
        {
            Refuse(SplitCode(blocked), SplitDetail(blocked));
            return;
        }
        // the node's scale is part of the edited geometry here: the mesh keeps the part's size and holds the scaled shape
        float[] scale = { pose.ScaleRatio.X, pose.ScaleRatio.Y, pose.ScaleRatio.Z };
        PartGeometry edited = PartGeometry.FromNode(user, up.Node, scale, false);
        if (edited.PrimitivesWithoutFaceNumbers > 0)
        {
            Refuse("FACE_NUMBERS_LOST", "the .glb no longer says which prim face each triangle belongs to (the _PRIM_FACE attribute is missing); in Blender's export settings turn Attributes on");
            return;
        }
        if (edited.MixedTriangles > 0)
        {
            Refuse("FACES_JOINED", edited.MixedTriangles + " triangles take their corners from more than one prim face; keep each face's triangles whole");
            return;
        }
        if (edited.TriangleCount == 0)
        {
            Refuse("NO_GEOMETRY", "the part has no triangles left; to delete a part, delete its node (--stub-deleted keeps it as an invisible stub)");
            return;
        }
        int maxFace = edited.Faces.Keys.Max();
        if (edited.Faces.ContainsKey(-1) || maxFace < 0)
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

        Stopwatch buildClock = Stopwatch.StartNew();
        string Lap(string what, Stopwatch clock)
        {
            double seconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            return seconds >= 1.0 ? what + " " + seconds.ToString("0.0", Inv) + " s; " : string.Empty;
        }
        string laps = string.Empty;
        List<EncoderFace> faces;
        List<int> sourceSlot;
        try { faces = BuildFaces(edited, user, pp.Scale, Vector3.Zero, faceCount, out sourceSlot); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException)
        {
            Refuse("GEOMETRY_FAILED", e.GetType().Name + ": " + e.Message);
            return;
        }
        laps += Lap("faces", buildClock);
        bool split = faces.Count > faceCount;
        if (faces.Count > MeshShape.MaxFaces)
        {
            Refuse("FACES_GT8", "a face has more than " + FaceBuilder.SplitAtVertices + " vertices and splitting it, as the viewer's uploader does, would give the part " + faces.Count + " faces; a mesh part has at most " + MeshShape.MaxFaces);
            return;
        }

        PhysicsGeometry physics = PhysicsFrom(faces, pp.Scale);
        laps += Lap("physics", buildClock);
        byte[] bytes;
        if (faces.Sum(x => x.TriangleCount) > 50000)
            m_options.Progress?.Invoke("link " + op.LinkNum.ToString(Inv) + " '" + op.Name + "': encoding " + faces.Sum(x => x.TriangleCount).ToString(Inv) + " triangles in " + faces.Count.ToString(Inv) + " faces");
        try { bytes = new MeshAssetEncoder { Trace = faces.Sum(x => x.TriangleCount) > 50000 ? m_options.Progress : null }.Encode(faces, new EncodeOptions { Physics = physics }); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is OverflowException || e is IndexOutOfRangeException || e is MeshAsset.MeshFormatException)
        {
            Refuse("ENCODE_FAILED", e.GetType().Name + ": " + e.Message);
            return;
        }
        laps += Lap("encode", buildClock);
        List<string> problems = MeshValidator.Check(bytes, faces, out _, physics);
        laps += Lap("check", buildClock);
        if (laps.Length > 0)
            m_options.Progress?.Invoke("link " + op.LinkNum.ToString(Inv) + " '" + op.Name + "': " + laps.TrimEnd(' ', ';'));
        if (problems.Count > 0)
        {
            run.Result.Invalid.Add("link " + op.LinkNum.ToString(Inv) + " '" + op.Name + "': " + string.Join("; ", problems));
            Refuse("MESH_INVALID", string.Join("; ", problems));
            return;
        }
        Guid id = MeshId.FromBytes(bytes);
        if (StandInIds.Is(id))
            throw new RestoreException("a stand-in mesh id remains for part " + up.PartId);
        run.Meshes.TryAdd(id, bytes);

        byte[] extra = pp.ExtraParams;
        int shapeTypeBefore = pp.PhysicsShapeType;
        // As the converter does for a prim: the region gives a root part of type None its default type when it applies
        // physics, which for the prim was Prim and would be convex for a mesh, so a converted root is set to Prim.
        bool rootNone = shapeTypeBefore == PhysShapeNone && pp.Part.IsRoot && row.Kind != "mesh";
        int faceTotal = faces.Count;
        byte[] newTextureEntry = split ? ExpandTextureEntry(pp.TextureEntry, sourceSlot) : null;
        run.Edit(up.PartId, view =>
        {
            List<ElementEdit> edits = new List<ElementEdit>(MeshShape.Edits(faceTotal, id, extra, view.Present));
            if (rootNone && view.Present.Contains("PhysicsShapeType"))
                edits.Add(ElementEdit.SetText("PhysicsShapeType", PhysShapePrim.ToString(Inv)));
            if (newTextureEntry != null)
                edits.Add(ElementEdit.SetText("Shape/TextureEntry", Convert.ToBase64String(newTextureEntry)));
            return edits;
        });
        run.Plan1(up.PartId, "shape", faceTotal);
        if (rootNone)
            run.Plan1(up.PartId, "physics");
        if (newTextureEntry != null)
            run.Plan1(up.PartId, "textureEntry");
        string placed = PlaceEdits(run, up.PartId, pp, pose, poseAfter, false);

        report.Outcome = PartOutcome.Rebuilt;
        report.Faces = faceTotal;
        report.Triangles = faces.Sum(f => f.TriangleCount);
        report.MeshId = id;
        report.Detail = cmp.Difference + (placed.Length > 0 ? "; " + placed : string.Empty);
        if (split)
            report.Notes.Add("a face had more than " + FaceBuilder.SplitAtVertices + " vertices and was split into parts, as the viewer's uploader does; the part has " + faceTotal + " faces and each part keeps the texture settings of its face");
        if (physics == null)
            report.Notes.Add("the physics mesh has more vertices than a mesh asset can index; the part collides as its bounding hull");
    }

    /// <summary>A glTF texture transform on a part's material is never dropped without a word: the report says it is not applied.</summary>
    private static void WarnTextureTransform(PartReport report, GlbFile user, GlbNode node)
    {
        if (node.Mesh < 0 || node.Mesh >= user.Meshes.Count)
            return;
        if (user.Meshes[node.Mesh].Primitives.Any(p => p.Material >= 0 && p.Material < user.Materials.Count && user.Materials[p.Material].HasTextureTransform))
            report.Notes.Add("the .glb gives this part's material a texture transform (offset, scale or rotation); it is not applied, the part keeps its texture settings");
    }

    /// <summary>The geometry is the original's: only the place or size may have changed, and the shape stays as it was.</summary>
    private void PlaceOnly(Run run, PartReport report, Guid partId, ParsedPart pp, PoseChange pose, NodePose poseAfter, string groupMove)
    {
        string placed = pp == null ? string.Empty : PlaceEdits(run, partId, pp, pose, poseAfter, true);
        if (groupMove != null)
            placed = placed.Length > 0 ? groupMove + "; " + placed : groupMove;
        if (placed.Length == 0)
        {
            report.Outcome = PartOutcome.Unchanged;
            return;
        }
        report.Outcome = PartOutcome.Moved;
        report.Detail = placed;
    }

    /// <summary>
    /// The edits for a part that moved, turned or was resized. A moved or turned child gets its position or rotation inside the
    /// linkset; a resized part its new size (the size multiplied by the node's scale change). Returns the words for the report.
    /// </summary>
    private string PlaceEdits(Run run, Guid partId, ParsedPart pp, PoseChange pose, NodePose poseAfter, bool withScale)
    {
        List<string> said = new List<string>();
        if (pose.Moved)
        {
            float[] position = PoseMath.ViewerVector(poseAfter.Translation);
            run.Edit(partId, view => VectorEdits("OffsetPosition", position));
            run.Plan1(partId, "position");
            said.Add("moved " + pose.Distance.ToString("0.####", Inv) + " m");
        }
        if (pose.Turned)
        {
            float[] rotation = PoseMath.ViewerQuat(poseAfter.Rotation);
            run.Edit(partId, view => QuatEdits("RotationOffset", rotation));
            run.Plan1(partId, "rotation");
            said.Add("turned");
        }
        if (pose.Scaled && withScale && pp != null)
        {
            float[] ratio = PoseMath.ViewerScale(pose.ScaleRatio);
            float[] size = { pp.Scale.X * ratio[0], pp.Scale.Y * ratio[1], pp.Scale.Z * ratio[2] };
            run.Edit(partId, view => VectorEdits("Scale", size));
            run.Plan1(partId, "scale");
            said.Add("resized to " + size[0].ToString("0.####", Inv) + " x " + size[1].ToString("0.####", Inv) + " x " + size[2].ToString("0.####", Inv) + " m");
        }
        return string.Join(", ", said);
    }

    private static string SplitCode(string blocked) => blocked.Substring(0, blocked.IndexOf('|'));
    private static string SplitDetail(string blocked) => blocked.Substring(blocked.IndexOf('|') + 1);

    /// <summary>
    /// The reason a changed part may not be rebuilt, as "CODE|detail", or null. Script reasons are overridden by
    /// --allow-unsafe and recorded in the part's notes; every other reason stands.
    /// </summary>
    private string BlockingReason(Run run, EligibilityRow row, PartReport report, Guid partId)
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
            run.ScanRows ??= new ArchiveScriptScanner(new ScanOptions { AssumeSafeUnscanned = m_options.AssumeSafeUnscanned }).Scan(run.SourcePath).Parts.GroupBy(p => p.PartId).ToDictionary(g => g.Key, g => g.First());
            if (run.ScanRows.TryGetValue(partId, out PartRow sr) && PartDecisionNames.IsKept(sr.Decision))
            {
                bool unsafeHit = sr.Decision == PartDecision.KeepPrimUnsafe;
                string level = unsafeHit ? "unsafe" : "uncertain";
                scriptCode = unsafeHit ? ReasonCodes.ScriptUnsafe : ReasonCodes.ScriptUncertain;
                scriptDetail = "script scan: " + string.Join("; ", sr.Reasons.Where(r => r.Level == level).Take(6).Select(r =>
                    r.Code + (string.IsNullOrEmpty(r.Script) ? string.Empty : " in script '" + Sanitize.Text(r.Script) + "'" + (r.Line > 0 ? " line " + r.Line.ToString(Inv) : string.Empty))));
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

    /// <summary>
    /// The part's triangles as faces in the unit box: the node's metres, back in viewer axes, the centre taken away and divided by
    /// the part's size. A face of 65 532 vertices or more is split as the viewer's uploader splits it
    /// (<see cref="FaceBuilder.SlotsSplit"/>); <paramref name="sourceSlot"/> says which slot each resulting face came from.
    /// </summary>
    private static List<EncoderFace> BuildFaces(PartGeometry g, GlbFile user, Vector3 size, Vector3 center, int faceCount, out List<int> sourceSlot)
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
                    pos[p] = (tri[o] - center.X) / sx; pos[p + 1] = (-tri[o + 2] - center.Y) / sy; pos[p + 2] = (tri[o + 1] - center.Z) / sz;
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
        return FaceBuilder.SlotsSplit(primFaces, faceCount, out sourceSlot);
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
        int vertexTotal = 0, indexTotal = 0;
        foreach (EncoderFace f in faces)
            if (!f.IsEmpty)
            {
                vertexTotal += f.VertexCount;
                indexTotal += f.Indices.Length;
            }
        if (indexTotal == 0)
            return null;
        float[] positions = new float[vertexTotal * 3];
        int[] indices = new int[indexTotal];
        int vertexAt = 0, indexAt = 0;
        foreach (EncoderFace f in faces)
        {
            if (f.IsEmpty)
                continue;
            Array.Copy(f.Positions, 0, positions, vertexAt * 3, f.Positions.Length);
            foreach (uint i in f.Indices)
                indices[indexAt++] = vertexAt + (int)i;
            vertexAt += f.VertexCount;
        }
        PhysicsGeometry g = new PhysicsGeometry { Positions = positions, Indices = indices };
        if (size.X < BoxBelow && size.Y < BoxBelow && size.Z < BoxBelow)
            return PrimPhysics.BoundingBox(g);
        return g.VertexCount > 65535 ? null : g;
    }

    /// <summary>
    /// The texture entry of a part whose faces were split: the new face j takes the settings of the face it was cut from.
    /// </summary>
    private static byte[] ExpandTextureEntry(byte[] textureEntry, List<int> sourceSlot)
    {
        if (textureEntry == null || textureEntry.Length <= 16)
            return null;
        try
        {
            Primitive.TextureEntry old = new Primitive.TextureEntry(textureEntry, 0, textureEntry.Length);
            Primitive.TextureEntry made = new Primitive.TextureEntry(old.DefaultTexture.TextureID);
            CopyFace(old.DefaultTexture, made.DefaultTexture);
            for (int j = 0; j < sourceSlot.Count && j < made.FaceTextures.Length; j++)
            {
                Primitive.TextureEntryFace from = sourceSlot[j] < old.FaceTextures.Length ? old.FaceTextures[sourceSlot[j]] : null;
                if (from != null)
                    CopyFace(from, made.CreateFace((uint)j));
            }
            return made.GetBytes();
        }
        catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException || e is InvalidOperationException || e is NullReferenceException)
        {
            // a texture entry that cannot be read stays as it is; the new faces then use its default
            return null;
        }
    }

    private static void CopyFace(Primitive.TextureEntryFace from, Primitive.TextureEntryFace to)
    {
        to.TextureID = from.TextureID;
        to.RGBA = from.RGBA;
        to.RepeatU = from.RepeatU; to.RepeatV = from.RepeatV;
        to.OffsetU = from.OffsetU; to.OffsetV = from.OffsetV;
        to.Rotation = from.Rotation;
        to.Glow = from.Glow;
        to.Bump = from.Bump;
        to.Shiny = from.Shiny;
        to.Fullbright = from.Fullbright;
        to.MediaFlags = from.MediaFlags;
        to.TexMapType = from.TexMapType;
        to.MaterialID = from.MaterialID;
    }

    // ------------------------------------------------------------------ the plan the parity check uses

    private static string PlanJson(Run run)
    {
        JsonObject root = new JsonObject { ["version"] = 1 };
        JsonArray changes = new JsonArray();
        foreach (PlanChange c in run.Plan)
        {
            JsonObject o = new JsonObject { ["part"] = c.Part.ToString(), ["field"] = c.Field };
            if (c.Field == "shape")
            {
                o["to"] = "mesh";
                o["faces"] = c.Faces;
            }
            changes.Add(o);
        }
        root["partChanges"] = changes;
        JsonArray added = new JsonArray();
        foreach (Guid id in run.AddedParts)
            added.Add(id.ToString());
        root["addedParts"] = added;
        JsonArray removed = new JsonArray();
        foreach (Guid id in run.RemovedParts)
            removed.Add(id.ToString());
        root["removedParts"] = removed;
        JsonArray entries = new JsonArray();
        foreach (Guid id in run.Meshes.Keys.OrderBy(g => g.ToString(), StringComparer.Ordinal))
            entries.Add("assets/" + id.ToString("D") + "_mesh.llmesh");
        foreach (Guid id in run.Textures.NewAssets.Select(a => a.Key).OrderBy(g => g.ToString(), StringComparer.Ordinal))
            entries.Add("assets/" + id.ToString("D") + "_texture.jp2");
        root["addedEntries"] = entries;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

    /// <summary>
    /// Sorts the .glb's nodes into the parts the original had and the nodes that are new: a node with a mesh and no part id; a copy
    /// of a part (Blender copies the custom properties, so two nodes carry one part id; the one named as the original is the part
    /// and the others are new); a node whose part id is not in the original build. A part whose link number changed refuses the
    /// whole file, because link numbers are what scripts and the region's link messages use.
    /// </summary>
    private static void SortNodes(GlbFile user, List<PartNode> userParts, List<PartNode> original, string label, out List<PartNode> kept, out List<PartNode> added)
    {
        kept = new List<PartNode>();
        added = new List<PartNode>();
        Dictionary<Guid, PartNode> originalById = original.ToDictionary(p => p.PartId);
        foreach (IGrouping<Guid, PartNode> group in userParts.GroupBy(p => p.PartId))
        {
            List<PartNode> nodes = group.ToList();
            if (!originalById.TryGetValue(group.Key, out PartNode op))
            {
                added.AddRange(nodes);
                continue;
            }
            PartNode keep = nodes.FirstOrDefault(n => string.Equals(StripCopySuffix(n.Node.Name), op.Name, StringComparison.Ordinal) && n.Node.Name == StripCopySuffix(n.Node.Name))
                ?? nodes.FirstOrDefault(n => string.Equals(StripCopySuffix(n.Node.Name), op.Name, StringComparison.Ordinal)) ?? nodes[0];
            kept.Add(keep);
            added.AddRange(nodes.Where(n => n != keep));
        }
        List<string> problems = new List<string>();
        foreach (PartNode p in kept)
        {
            int was = originalById[p.PartId].LinkNum;
            if (was != p.LinkNum)
                problems.Add("the part '" + p.Name + "' has link number " + p.LinkNum.ToString(Inv) + " and had " + was.ToString(Inv));
        }
        if (problems.Count > 0)
            throw new RestoreException("'" + label + "' does not match the original build: link numbers are not renumbered; nothing was written. " + string.Join(" ", problems.Take(8)) + (problems.Count > 8 ? " (and " + (problems.Count - 8) + " more)" : string.Empty));

        foreach (GlbNode n in user.Nodes)
        {
            if (n.Mesh < 0 || GlbFile.Str(n.Extras, "partUuid") != null || GlbFile.Str(n.Extras, "kind") == "object")
                continue;
            added.Add(new PartNode { Node = n, PartId = Guid.Empty, Name = n.Name });
        }
        added.Sort((a, b) => a.Node.Index.CompareTo(b.Node.Index));
    }

    /// <summary>The name without Blender's ".001" suffix for a second object of one name.</summary>
    private static string StripCopySuffix(string name)
    {
        if (name == null)
            return string.Empty;
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && name.Skip(dot + 1).All(char.IsAsciiDigit))
            return name.Substring(0, dot);
        return name;
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
