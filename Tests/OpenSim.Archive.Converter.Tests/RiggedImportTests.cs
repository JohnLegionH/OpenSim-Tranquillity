/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Restore;
using OpenSim.Archive.Restore.Import;
using OpenSim.ArchiveInspector;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A rigged part: the skin of the .glb (joints, inverse bind matrices, weights) goes into the mesh asset's skin block and the weights block of every
/// level of detail, so a viewer can wear the mesh on a body. RigKit writes the files; every expected value is worked out from the vertex's height or
/// from the joint's rest height, not read back from the code under test. Each test writes its own folders, so the class runs in parallel.
/// </summary>
public class RiggedImportTests
{
    private static ImportOptions Options(bool jointPositions = false) => new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600, JointPositions = jointPositions };

    private sealed class Run
    {
        public ImportResult Result;
        public MeshAssetModel Mesh;
        public Dictionary<string, byte[]> Oar;
    }

    private static Run Import(RigKit.Options kit = null, bool jointPositions = false)
    {
        string folder = RigKit.Write(kit);
        string output = ImportKit.OutputPath();
        ImportResult result = null;
        try { result = ObjectImporter.Import(folder, output, Options(jointPositions)); }
        catch (ImportException e) { Assert.Fail("the importer refused the rigged files: " + e.Message); }
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        string meshKey = oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal));
        return new Run { Result = result, Mesh = MeshCodec.Decode(oar[meshKey]), Oar = oar };
    }

    private static Matrix4x4 ToMatrix(double[] m) => new Matrix4x4(
        (float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
        (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);

    /// <summary>The viewer's axes for a glTF position: (x, -z, y).</summary>
    private static Vector3 ViewerAxes(float x, float y, float z) => new Vector3(x, -z, y);

    /// <summary>The expected inverse bind matrix of a deform bone: no turn, the joint's rest position taken away (viewer axes, row vectors, translation in 12 to 14).</summary>
    private static double[] ExpectedBone(float restHeight) => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -restHeight, 1 };

    private static void AssertMatrix(double[] expected, double[] actual, double tolerance = 1e-5)
    {
        Assert.Equal(16, actual.Length);
        for (int i = 0; i < 16; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, "matrix value " + i + ": expected " + expected[i] + ", got " + actual[i]);
    }

    // ---- reading the skin out of the .glb

    [Fact]
    public void TheSkinOfAGlbIsReadWithItsJointsItsMatricesAndTheInfluencesOfEveryVertex()
    {
        string folder = RigKit.Write();
        GlbFile file = GlbFile.Read(File.ReadAllBytes(Path.Combine(folder, "rig.glb")));
        GlbSkin skin = Assert.Single(file.Skins);
        Assert.Equal(RigKit.DefaultNames, skin.Joints.Select(n => file.Nodes[n].Name).ToArray());
        Assert.Equal(3, skin.InverseBind.Count);
        Assert.Equal(-RigKit.JointY[1], skin.InverseBind[1][13], 5);
        GlbNode meshNode = file.Nodes.Single(n => n.Mesh >= 0);
        Assert.Equal(0, meshNode.Skin);
        GlbPrimitive prim = file.Meshes[meshNode.Mesh].Primitives.Single();
        Assert.NotNull(prim.Joints);
        Assert.NotNull(prim.Weights);
        int vertices = prim.Positions.Length / 3;
        Assert.Equal(vertices * 4, prim.Joints.Length);
        for (int v = 0; v < vertices; v++)
        {
            (int Joint, float Weight)[] want = RigKit.WeightsAt(prim.Positions[v * 3 + 1]);
            for (int k = 0; k < want.Length; k++)
            {
                Assert.Equal(want[k].Joint, prim.Joints[v * 4 + k]);
                Assert.Equal(want[k].Weight, prim.Weights[v * 4 + k], 5);
            }
        }
    }

    [Fact]
    public void JointsAndWeightsWrittenAsUnsignedShortsAreReadToo()
    {
        string folder = RigKit.Write(new RigKit.Options { WideAttributes = true });
        GlbPrimitive prim = GlbFile.Read(File.ReadAllBytes(Path.Combine(folder, "rig.glb"))).Meshes[0].Primitives.Single();
        Assert.NotNull(prim.Joints);
        Assert.NotNull(prim.Weights);
        (int Joint, float Weight)[] want = RigKit.WeightsAt(prim.Positions[1]);
        Assert.Equal(want[0].Joint, prim.Joints[0]);
        Assert.Equal(want[0].Weight, prim.Weights[0], 3);
    }

    // ---- the skin block

    [Fact]
    public void TheMeshAssetCarriesTheSkinWithItsJointNamesInTheOrderOfTheFile()
    {
        Run run = Import();
        Assert.NotNull(run.Mesh.Skin);
        Assert.Equal(RigKit.DefaultNames, run.Mesh.Skin.JointNames.ToArray());
        Assert.Equal(3, run.Mesh.Skin.InverseBind.Count);
    }

    [Fact]
    public void AnInverseBindMatrixIsTheInverseOfTheBonesRestPositionInTheViewersAxes()
    {
        Run run = Import();
        for (int j = 0; j < 3; j++)
            AssertMatrix(ExpectedBone(RigKit.JointY[j]), run.Mesh.Skin.InverseBind[j]);
    }

    [Fact]
    public void TheBindShapeMatrixScalesTheUnitBoxToThePartAndMovesItToItsCentre()
    {
        Run run = Import();
        // the slab: 0.4 m along x, 0.1 m deep (z in the file), 0.4 m high (y in the file), centred 1.15 m up
        double[] expected = { 0.4, 0, 0, 0, 0, 0.1, 0, 0, 0, 0, 0.4, 0, 0, 0, RigKit.SlabCentreY, 1 };
        AssertMatrix(expected, run.Mesh.Skin.BindShape, 1e-4);
    }

    [Fact]
    public void TheBindShapeMatrixPutsEveryStoredVertexWhereTheFileHadIt()
    {
        Run run = Import();
        GlbFile source = GlbFile.Read(File.ReadAllBytes(Path.Combine(RigKit.Write(), "rig.glb")));
        GlbPrimitive prim = source.Meshes[0].Primitives.Single();
        Matrix4x4 bind = ToMatrix(run.Mesh.Skin.BindShape);
        List<Vector3> wanted = new List<Vector3>();
        for (int v = 0; v < prim.Positions.Length / 3; v++)
            wanted.Add(ViewerAxes(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]));
        MeshLod high = run.Mesh.High;
        int checkedVertices = 0;
        foreach (MeshFace face in high.Faces)
            foreach (Vector3 stored in face.Positions)
            {
                Vector3 placed = Vector3.Transform(stored, bind);
                Assert.True(wanted.Any(w => Vector3.Distance(w, placed) < 2e-4f), "a stored vertex lands at " + placed + ", where the file has none");
                checkedVertices++;
            }
        Assert.True(checkedVertices > 100);
    }

    [Fact]
    public void AnUnriggedMeshHasNoSkinBlock()
    {
        ImportKit.Folder folder = ImportKit.Write();
        string output = ImportKit.OutputPath();
        ObjectImporter.Import(folder.Path, output, Options());
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        MeshAssetModel mesh = MeshCodec.Decode(oar[oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.Null(mesh.Skin);
        Assert.All(mesh.High.Faces, f => Assert.Null(f.Weights));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMeshWhoseNodeNamesNoSkinIsImportedAsAnUnriggedMeshWhateverItsPrimitivesCarry(bool moreThanFourInfluences)
    {
        RigKit.Options kit = new RigKit.Options { NodeWithoutSkin = true };
        if (moreThanFourInfluences)
        {
            kit.ExtraJoints = new[] { "mSpine1", "mSpine2" };
            kit.Fixed = new[] { 0.4f, 0.25f, 0.15f, 0.12f, 0.08f };
        }
        Run run = Import(kit);
        Assert.Null(run.Mesh.Skin);
        Assert.All(run.Mesh.High.Faces, f => Assert.Null(f.Weights));
    }

    // ---- the weights

    private static void AssertWeightsOfEveryLevel(Run run, string[] names, float tolerance)
    {
        Matrix4x4 bind = ToMatrix(run.Mesh.Skin.BindShape);
        for (int level = 0; level < 4; level++)
        {
            MeshLod lod = run.Mesh.Lod(level);
            Assert.NotNull(lod);
            int checkedVertices = 0;
            foreach (MeshFace face in lod.Faces.Where(f => !f.NoGeometry))
            {
                Assert.NotNull(face.Weights);
                Assert.Equal(face.VertexCount, face.Weights.Length);
                for (int v = 0; v < face.VertexCount; v++)
                {
                    float height = Vector3.Transform(face.Positions[v], bind).Z;   // the viewer's z is the file's y
                    Dictionary<string, float> want = RigKit.WeightsAt(height).Where(w => w.Weight > 1e-4f).ToDictionary(w => names[w.Joint], w => w.Weight);
                    Dictionary<string, float> got = face.Weights[v].Where(w => w.Weight > 2e-3f).ToDictionary(w => run.Mesh.Skin.JointNames[w.Joint], w => w.Weight);
                    foreach (KeyValuePair<string, float> w in want.Where(w => w.Value > 2e-3f))
                    {
                        Assert.True(got.TryGetValue(w.Key, out float actual), "level " + level + " vertex " + v + " has no weight on " + w.Key);
                        Assert.True(Math.Abs(actual - w.Value) <= tolerance, "level " + level + " vertex " + v + " weight on " + w.Key + ": expected " + w.Value + ", got " + actual);
                    }
                    Assert.True(got.Keys.All(want.ContainsKey), "level " + level + " vertex " + v + " has a weight on a joint the file gives it none on");
                    Assert.True(Math.Abs(face.Weights[v].Sum(w => w.Weight) - 1f) < 2e-3f, "the weights of level " + level + " vertex " + v + " do not sum to one");
                    checkedVertices++;
                }
            }
            Assert.True(checkedVertices >= 24, "level " + level + " checked " + checkedVertices + " vertices");
        }
    }

    [Fact]
    public void EveryLevelOfDetailCarriesTheWeightsOfItsOwnFile()
    {
        Run run = Import();
        AssertWeightsOfEveryLevel(run, RigKit.DefaultNames, 2e-4f);
    }

    [Fact]
    public void TheWeightsOfUnsignedShortAttributesComeThroughToo()
    {
        Run run = Import(new RigKit.Options { WideAttributes = true });
        AssertWeightsOfEveryLevel(run, RigKit.DefaultNames, 3e-4f);
    }

    [Fact]
    public void ALevelWhoseSkinListsTheJointsInAnotherOrderStillWeightsTheRightJoints()
    {
        Run run = Import(new RigKit.Options { Orders = new[] { new[] { 0, 1, 2 }, new[] { 2, 0, 1 }, new[] { 1, 2, 0 }, new[] { 2, 1, 0 } } });
        Assert.Equal(RigKit.DefaultNames, run.Mesh.Skin.JointNames.ToArray());
        AssertWeightsOfEveryLevel(run, RigKit.DefaultNames, 2e-4f);
    }

    [Fact]
    public void MoreThanFourInfluencesKeepTheHeaviestFourAndTheirWeightsAddUpToOne()
    {
        Run run = Import(new RigKit.Options { ExtraJoints = new[] { "mSpine1", "mSpine2" }, Fixed = new[] { 0.4f, 0.25f, 0.15f, 0.12f, 0.08f } });
        float[] expected = { 0.4f / 0.92f, 0.25f / 0.92f, 0.15f / 0.92f, 0.12f / 0.92f };
        foreach (MeshFace face in run.Mesh.High.Faces)
            foreach (Influence[] vertex in face.Weights)
            {
                Assert.Equal(4, vertex.Length);
                Assert.Equal(new[] { "mPelvis", "mTorso", "mChest", "mSpine1" }, vertex.Select(w => run.Mesh.Skin.JointNames[w.Joint]).ToArray());
                for (int k = 0; k < 4; k++)
                    Assert.Equal(expected[k], vertex[k].Weight, 3);
            }
        Assert.Contains(run.Result.Notes, n => n.Contains("more than four", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("mSpine2", run.Mesh.Skin.JointNames);
    }

    [Fact]
    public void JointsNoVertexUsesAreLeftOutOfTheSkin()
    {
        Run run = Import(new RigKit.Options { ExtraJoints = new[] { "mSpine1" } });
        Assert.Equal(RigKit.DefaultNames, run.Mesh.Skin.JointNames.ToArray());
        Assert.Equal(3, run.Mesh.Skin.InverseBind.Count);
    }

    [Fact]
    public void AVertexWithNoWeightStopsTheFileAndNothingIsWritten()
    {
        string folder = RigKit.Write(new RigKit.Options { ZeroWeightVertex = true });
        string output = ImportKit.OutputPath();
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, output, Options()));
        Assert.Contains("weight", e.Message);
        Assert.False(File.Exists(output));
    }

    // ---- the joint names

    [Fact]
    public void AJointNamedByAnAliasIsWrittenUnderItsViewerName()
    {
        Run run = Import(new RigKit.Options { Names = new[] { "hip", "mTorso", "mChest" } });
        Assert.Equal(RigKit.DefaultNames, run.Mesh.Skin.JointNames.ToArray());
    }

    [Fact]
    public void ACollisionVolumeNameIsKept()
    {
        Run run = Import(new RigKit.Options { Names = new[] { "PELVIS", "mTorso", "mChest" } });
        Assert.Equal("PELVIS", run.Mesh.Skin.JointNames[0]);
    }

    [Fact]
    public void ABoneThatIsNoJointOfTheSkeletonStopsTheFileAndEveryUnmappedNameIsReported()
    {
        string folder = RigKit.Write(new RigKit.Options { Names = new[] { "mPelvis", "Spine_ctrl", "Hand.L" } });
        string output = ImportKit.OutputPath();
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, output, Options()));
        Assert.Contains("Spine_ctrl", e.Message);
        Assert.Contains("Hand.L", e.Message);
        Assert.Contains("2 bones are not a joint", e.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AnUnusedBoneThatIsNoJointIsDroppedWithAWarningAndTheFileIsWritten()
    {
        Run run = Import(new RigKit.Options { ExtraJoints = new[] { "root_ctrl" } });
        Assert.Equal(RigKit.DefaultNames, run.Mesh.Skin.JointNames.ToArray());
        Assert.Contains(run.Result.Notes, n => n.Contains("root_ctrl") && n.Contains("not a joint", StringComparison.Ordinal) && n.Contains("no weight", StringComparison.Ordinal));
    }

    [Fact]
    public void ABoneThatIsNoJointAndCarriesWeightInOnlyTheLowestLevelStopsTheFileAndNamesTheLevel()
    {
        // joint 0 of the lowest level is renamed to something that is no joint; the vertices of that level weight it
        string folder = RigKit.Write(new RigKit.Options { LowestLevelOnlyJoint = "Spine_ctrl" });
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, ImportKit.OutputPath(), Options()));
        Assert.Contains("rig_lod0.glb", e.Message);
        Assert.Contains("Spine_ctrl", e.Message);
    }

    [Fact]
    public void AnUnweightedVertexStopsTheFileAndTheMessageGivesTheNumberOfSuchVertices()
    {
        string folder = RigKit.Write(new RigKit.Options { ZeroWeightVertex = true });
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, ImportKit.OutputPath(), Options()));
        Assert.Contains("1 vertices carry no weight", e.Message);
    }

    [Fact]
    public void ALevelThatWeightsAJointTheFullMeshDoesNotStopsTheFileAndNamesTheLevel()
    {
        string folder = RigKit.Write(new RigKit.Options { LowestLevelOnlyJoint = "mSpine1" });
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, ImportKit.OutputPath(), Options()));
        Assert.Contains("rig_lod0.glb", e.Message);
        Assert.Contains("mSpine1", e.Message);
    }

    [Fact]
    public void MoreThan110WeightedJointsStopTheFile()
    {
        string[] bones = ViewerJointNames.Bones.OrderBy(b => b, StringComparer.Ordinal).ToArray();
        string folder = RigKit.Write(new RigKit.Options { Names = bones.Take(3).ToArray(), ExtraJoints = bones.Skip(3).Take(108).ToArray(), RoundRobin = true });
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, ImportKit.OutputPath(), Options()));
        Assert.Contains("110", e.Message);
    }

    [Fact]
    public void ASkinWithoutInverseBindMatricesStopsTheFile()
    {
        string folder = RigKit.Write(new RigKit.Options { OmitInverseBind = true });
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.Import(folder, ImportKit.OutputPath(), Options()));
        Assert.Contains("inverse bind", e.Message);
    }

    // ---- bone orientation and joint positions

    [Fact]
    public void ABonesTurnedRestFrameIsDroppedBecauseTheViewersBonesAreNotTurnedAndTheFileIsToldSo()
    {
        Run run = Import(new RigKit.Options { RotateDegrees = new[] { 0.0, 40.0, 0.0 } });
        AssertMatrix(ExpectedBone(RigKit.JointY[1]), run.Mesh.Skin.InverseBind[1]);
        Assert.Contains(run.Result.Notes, n => n.Contains("mTorso") && n.Contains("turn", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(run.Result.Notes, n => n.Contains("mPelvis") && n.Contains("turn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JointPositionsAreLeftOutByDefault()
    {
        Run run = Import();
        Assert.Null(run.Mesh.Skin.AltInverseBind);
        Assert.Null(run.Mesh.Skin.PelvisOffset);
    }

    [Fact]
    public void AskedForJointPositionsTheSkinCarriesEachJointsOffsetFromItsParent()
    {
        Run run = Import(jointPositions: true);
        List<double[]> alt = run.Mesh.Skin.AltInverseBind;
        Assert.NotNull(alt);
        Assert.Equal(3, alt.Count);
        // pelvis 1.0 m above the armature, torso 0.1 above the pelvis, chest 0.15 above the torso; the rest of each matrix is the inverse bind matrix
        float[] offsets = { 1.0f, 0.1f, 0.15f };
        for (int j = 0; j < 3; j++)
        {
            double[] expected = ExpectedBone(RigKit.JointY[j]);
            expected[12] = 0; expected[13] = 0; expected[14] = offsets[j];
            AssertMatrix(expected, alt[j]);
        }
        Assert.Equal(0.0, run.Mesh.Skin.PelvisOffset);
        Assert.False(run.Mesh.Skin.LockScaleIfJointPosition);
    }

    // ---- the command and the manifest

    [Fact]
    public void TheCommandsJointPositionsOptionWritesTheJointPositions()
    {
        string folder = RigKit.Write();
        string output = ImportKit.OutputPath();
        int exit = ImportCommand.Run(new[] { folder, "--out", output, "--avatar", ImportKit.Avatar.ToString("D"), "--joint-positions" });
        Assert.Equal(0, exit);
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        MeshAssetModel mesh = MeshCodec.Decode(oar[oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.NotNull(mesh.Skin?.AltInverseBind);
    }

    [Fact]
    public void TheCommandWithoutTheOptionLeavesTheJointPositionsOut()
    {
        string folder = RigKit.Write();
        string output = ImportKit.OutputPath();
        Assert.Equal(0, ImportCommand.Run(new[] { folder, "--out", output, "--avatar", ImportKit.Avatar.ToString("D") }));
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        MeshAssetModel mesh = MeshCodec.Decode(oar[oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.NotNull(mesh.Skin);
        Assert.Null(mesh.Skin.AltInverseBind);
    }

    [Fact]
    public void TheManifestKeyJointPositionsIsReadAndMustBeTrueOrFalse()
    {
        string root = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        RigKit.Write(into: Path.Combine(root, "part"));
        string manifest = Path.Combine(root, "linkset.json");
        File.WriteAllText(manifest, "{ \"name\": \"Vest\", \"jointPositions\": true, \"parts\": [ { \"name\": \"Vest\", \"folder\": \"part\" } ] }");
        Exception loadError = Record.Exception(() => LinksetManifest.Load(manifest));
        Assert.Null(loadError);
        Assert.True(LinksetManifest.Load(manifest).JointPositions);
        File.WriteAllText(manifest, "{ \"name\": \"Vest\", \"jointPositions\": \"yes\", \"parts\": [ { \"name\": \"Vest\", \"folder\": \"part\" } ] }");
        Assert.Throws<ImportException>(() => LinksetManifest.Load(manifest));
    }

    [Fact]
    public void ARiggedPartOfALinkedObjectCarriesItsSkinToo()
    {
        string root = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        RigKit.Write(into: Path.Combine(root, "part"));
        string manifest = Path.Combine(root, "linkset.json");
        File.WriteAllText(manifest, "{ \"name\": \"Vest\", \"parts\": [ { \"name\": \"Vest\", \"folder\": \"part\" } ] }");
        string output = ImportKit.OutputPath();
        try { ObjectImporter.ImportLinkset(LinksetManifest.Load(manifest), output, Options()); }
        catch (ImportException e) { Assert.Fail("the importer refused the rigged files: " + e.Message); }
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        MeshAssetModel mesh = MeshCodec.Decode(oar[oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.NotNull(mesh.Skin);
        Assert.Equal(RigKit.DefaultNames, mesh.Skin.JointNames.ToArray());
    }
}

/// <summary>
/// The round trip check (oar-import --check) on a rigged part: the geometry still matches the files, and the skin block and the weights of every level are
/// compared with the files' too. In the Console output collection with the other round trip tests, which share the process-wide Console.Out.
/// </summary>
[Collection("Console output")]
public class RiggedRoundTripTests
{
    private static ImportOptions Options() => new ImportOptions { Name = "Rig Round Trip", Avatar = ImportKit.Avatar, Position = new Vector3(40f, 50f, 60f), Created = 1767225600 };

    private static (ImportCheckResult Check, string Folder) Run(Action<string> afterImport = null, RigKit.Options kit = null)
    {
        string folder = RigKit.Write(kit);
        string archive = ImportKit.OutputPath();
        ImportOptions options = Options();
        ObjectImporter.Import(folder, archive, options);
        afterImport?.Invoke(folder);
        string work = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        return (ImportCheck.Run(archive, folder, options, work), folder);
    }

    [Fact]
    public void TheCheckOfARiggedPartPassesAndComparesTheGeometryOfEveryLevel()
    {
        ImportCheckResult check = Run().Check;
        Assert.True(check.Passed, check.ToText());
        foreach (string level in new[] { "high level", "medium level", "low level", "lowest level" })
            Assert.Contains(check.Lines, l => l.What == level && l.Ok);
    }

    [Fact]
    public void TheCheckComparesTheSkinAndTheWeightsOfEveryLevelWithTheFiles()
    {
        ImportCheckResult check = Run().Check;
        CheckLine skin = Assert.Single(check.Lines, l => l.What == "skin and weights");
        Assert.True(skin.Ok, skin.Detail);
        Assert.Contains("3 joints", skin.Detail);
    }

    [Fact]
    public void TheCheckFailsWhenTheWeightsInTheFilesAreNotTheOnesInTheArchive()
    {
        // the files are written again after the import, with every vertex weighted 60 to 40 on the first two joints
        ImportCheckResult check = Run(folder => RigKit.Write(new RigKit.Options { Fixed = new[] { 0.6f, 0.4f } }, into: folder)).Check;
        CheckLine skin = Assert.Single(check.Lines, l => l.What == "skin and weights");
        Assert.False(skin.Ok);
        Assert.False(check.Passed);
    }

    [Fact]
    public void TheCheckFailsWhenTheJointsInTheFilesAreNotTheOnesInTheArchive()
    {
        ImportCheckResult check = Run(folder => RigKit.Write(new RigKit.Options { Names = new[] { "mPelvis", "mTorso", "mNeck" } }, into: folder)).Check;
        CheckLine skin = Assert.Single(check.Lines, l => l.What == "skin and weights");
        Assert.False(skin.Ok);
        Assert.Contains("mNeck", skin.Detail);
    }
}
