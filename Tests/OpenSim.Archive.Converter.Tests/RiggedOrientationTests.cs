/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Restore;
using OpenSim.Archive.Restore.Import;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A character is modelled facing whichever way its tool likes. The viewer's own glTF loader (indra/newview/gltf/llgltfloader.cpp) tells from the two
/// shoulders whether the model lies a quarter turn off the avatar, turns the vertices and the joint positions about the vertical axis together
/// (checkForXYrotation, coord_system_rotationxy, lines 83-89, 455-458, 1532-1536), and writes the inverse bind matrix of a bone as the move from the
/// joint's rest position, in the avatar's axes and with no turn of its own (buildOverrideMatrix and computeGltfToViewerSkeletonTransform, lines
/// 1441-1443, 1526-1584, 1662-1684). The avatar's left is its +y axis. oar-import does the same from any pair of left and right joints, in whole quarter
/// turns. Each test writes its own folders, so the class runs in parallel.
/// </summary>
public class RiggedOrientationTests
{
    // the left shoulder 0.2 m to the side of the spine and 1.4 m up, the right one opposite; glTF axes
    private static readonly string[] Shoulders = { "mShoulderLeft", "mShoulderRight" };

    private static Vector3 Left(float x, float z) => new Vector3(x, 1.4f, z);
    private static Vector3 Right(float x, float z) => new Vector3(-x, 1.4f, -z);

    private static RigKit.Options Kit(Vector3 left, Vector3 right) => new RigKit.Options
    {
        ExtraJoints = Shoulders,
        ExtraJointWorld = new[] { left, right },
        Fixed = new[] { 0f, 0f, 0f, 1f, 0f },   // every vertex rides the left shoulder
        SlabOffset = new Vector2(0.07f, 0.03f),  // off the vertical axis, so that a half turn moves it
    };

    private sealed class Run
    {
        public ImportResult Result;
        public MeshAssetModel Mesh;
        public string Folder;
    }

    private static Run Import(RigKit.Options kit, bool jointPositions = false)
    {
        string folder = RigKit.Write(kit);
        string output = ImportKit.OutputPath();
        ImportResult result = null;
        try
        {
            result = ObjectImporter.Import(folder, output, new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600, JointPositions = jointPositions });
        }
        catch (ImportException e) { Assert.Fail("the importer refused the rigged files: " + e.Message); }
        Dictionary<string, byte[]> oar = ImportKit.ReadOar(output);
        string key = oar.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal));
        return new Run { Result = result, Mesh = MeshCodec.Decode(oar[key]), Folder = folder };
    }

    private static Matrix4x4 ToMatrix(double[] m) => new Matrix4x4(
        (float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
        (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);

    /// <summary>The viewer's axes for a glTF position, (x, -z, y), then turned a quarter turn at a time about the vertical axis (counter-clockwise from above).</summary>
    private static Vector3 Placed(Vector3 gltf, int quarterTurns)
    {
        Vector3 p = new Vector3(gltf.X, -gltf.Z, gltf.Y);
        for (int i = 0; i < quarterTurns; i++)
            p = new Vector3(-p.Y, p.X, p.Z);
        return p;
    }

    // where the left shoulder lies in the file, and the quarter turns that bring it to the avatar's left (+y)
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { 0.2f, 0f, 1 },      // glTF +x: the usual export of a model that faces +z
        new object[] { 0f, -0.2f, 0 },     // glTF -z is the viewer's +y already
        new object[] { 0f, 0.2f, 2 },
        new object[] { -0.2f, 0f, 3 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AModelWhoseLeftJointLiesOffTheAvatarsLeftIsTurnedInQuarterTurnsUntilItLiesOnIt(float x, float z, int quarterTurns)
    {
        Run run = Import(Kit(Left(x, z), Right(x, z)));
        Assert.Equal(new[] { "mShoulderLeft" }, run.Mesh.Skin.JointNames.ToArray());
        // the joint now lies at the avatar's left (0, 0.2, 1.4), and the bone's matrix is the move from there, with no turn
        Vector3 at = Placed(Left(x, z), quarterTurns);
        Assert.Equal(0f, at.X, 4);
        Assert.Equal(0.2f, at.Y, 4);
        Matrix4x4 m = ToMatrix(run.Mesh.Skin.InverseBind[0]);
        Assert.Equal(-at.X, m.M41, 4);
        Assert.Equal(-at.Y, m.M42, 4);
        Assert.Equal(-at.Z, m.M43, 4);
        Assert.Equal(1f, m.M11, 4); Assert.Equal(1f, m.M22, 4); Assert.Equal(1f, m.M33, 4);
        Assert.Equal(0f, m.M12 + m.M13 + m.M21 + m.M23 + m.M31 + m.M32, 4);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheVerticesAreTurnedWithTheJointsSoEveryStoredVertexLandsWhereTheTurnedFileHasIt(float x, float z, int quarterTurns)
    {
        Run run = Import(Kit(Left(x, z), Right(x, z)));
        GlbPrimitive prim = GlbFile.Read(File.ReadAllBytes(Path.Combine(run.Folder, "rig.glb"))).Meshes[0].Primitives.Single();
        List<Vector3> wanted = new List<Vector3>();
        for (int v = 0; v < prim.Positions.Length / 3; v++)
            wanted.Add(Placed(new Vector3(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]), quarterTurns));
        Matrix4x4 bind = ToMatrix(run.Mesh.Skin.BindShape);
        int count = 0;
        foreach (MeshFace face in run.Mesh.High.Faces)
            foreach (Vector3 stored in face.Positions)
            {
                Vector3 placed = Vector3.Transform(stored, bind);
                Assert.True(wanted.Any(w => Vector3.Distance(w, placed) < 2e-4f), "a stored vertex lands at " + placed + ", where the turned file has none");
                count++;
            }
        Assert.True(count > 100);
        // the slab is 0.4 m along the file's x and 0.1 m along its z: a quarter turn swaps the two
        bool swapped = quarterTurns % 2 == 1;
        Assert.Equal(swapped ? 0.1f : 0.4f, bind.M11, 3);
        Assert.Equal(swapped ? 0.4f : 0.1f, bind.M22, 3);
        Vector3 centre = Placed(new Vector3(0.07f, RigKit.SlabCentreY, 0.03f), quarterTurns);
        Assert.Equal(centre.X, bind.M41, 3);
        Assert.Equal(centre.Y, bind.M42, 3);
        Assert.Equal(centre.Z, bind.M43, 3);
    }

    [Fact]
    public void AModelThatLiesAQuarterOffIsToldSo()
    {
        Run run = Import(Kit(Left(0.2f, 0f), Right(0.2f, 0f)));
        Assert.Contains(run.Result.Notes, n => n.Contains("90 degrees", StringComparison.Ordinal) && n.Contains("vertical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AModelOnTheAvatarsLeftIsNotTurnedAndSaysNothingAboutIt()
    {
        Run run = Import(Kit(Left(0f, -0.2f), Right(0f, -0.2f)));
        Assert.DoesNotContain(run.Result.Notes, n => n.Contains("degrees", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelWithNoLeftAndRightJointIsLeftWhereItIs()
    {
        // the three stacked joints of the plain kit have no pair: nothing to tell the way the model faces
        string folder = RigKit.Write();
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.Import(folder, output, new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600 });
        MeshAssetModel mesh = MeshCodec.Decode(ImportKit.ReadOar(output).Single(kv => kv.Key.EndsWith("_mesh.llmesh", StringComparison.Ordinal)).Value);
        Matrix4x4 bind = ToMatrix(mesh.Skin.BindShape);
        Assert.Equal(0.4f, bind.M11, 3);
        Assert.Equal(0.1f, bind.M22, 3);
        Assert.DoesNotContain(result.Notes, n => n.Contains("degrees", StringComparison.Ordinal));
    }

    [Fact]
    public void AskedForJointPositionsTheOffsetsAreThoseOfTheTurnedModel()
    {
        Run run = Import(Kit(Left(0.2f, 0f), Right(0.2f, 0f)), jointPositions: true);
        // the left shoulder is 0.2 m to the file's +x and 0.15 m above the chest joint it hangs from: the avatar's (0, 0.2, 0.15) once turned
        List<double[]> alt = run.Mesh.Skin.AltInverseBind;
        Assert.NotNull(alt);
        Assert.Equal(0.0, alt[0][12], 4);
        Assert.Equal(0.2, alt[0][13], 4);
        Assert.Equal(0.15, alt[0][14], 4);
    }

    // ---- --check of a model that was turned

    private static ImportCheckResult CheckOf(RigKit.Options kit, bool withPhysics)
    {
        string folder = RigKit.Write(kit);
        if (withPhysics)
        {
            // an L of two boxes off the vertical axis, so the shape shows a turn
            ImportKit.Geo p = new ImportKit.Geo();
            ImportKit.AddBox(p, 1, _ => true, 0.07f, 1.1f, 0.03f, 0.2f, 0.1f, 0.05f);
            ImportKit.AddBox(p, 1, _ => true, 0.17f, 1.3f, 0.03f, 0.1f, 0.1f, 0.05f);
            File.WriteAllBytes(Path.Combine(folder, "rig_physics.glb"), ImportKit.Glb(new[] { ((ImportMaterial)null, p) }, uvs: false));
        }
        string archive = ImportKit.OutputPath();
        ImportOptions options = new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600 };
        ObjectImporter.Import(folder, archive, options);
        return ImportCheck.Run(archive, folder, options, Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void TheCheckPassesForAModelThatWasTurnedAndComparesItWithTheTurnedFiles()
    {
        ImportCheckResult check = CheckOf(Kit(Left(0.2f, 0f), Right(0.2f, 0f)), withPhysics: false);
        Assert.True(check.Passed, check.ToText());
        Assert.Contains(check.Lines, l => l.What == "high level" && l.Ok);
        Assert.Contains(check.Lines, l => l.What == "skin and weights" && l.Ok);
    }

    [Fact]
    public void TheCheckPassesForATurnedModelWithACollisionShapeThatIsTurnedToo()
    {
        ImportCheckResult check = CheckOf(Kit(Left(0.2f, 0f), Right(0.2f, 0f)), withPhysics: true);
        Assert.True(check.Passed, check.ToText());
        Assert.Contains(check.Lines, l => l.What == "physics shape" && l.Ok);
    }

    // ---- the licence texts named by the notice for the joint names

    [Fact]
    public void TheNoticeForTheJointNamesNamesLicenceTextsThatAreInTheRepository()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "ThirdPartyLicenses")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string notice = File.ReadAllText(Path.Combine(root, "Source", "OpenSim.Archive.Restore", "THIRD-PARTY-NOTICES.md"));
        string[] named = System.Text.RegularExpressions.Regex.Matches(notice, @"ThirdPartyLicenses/(SecondLifeViewer-[A-Za-z0-9._-]+\.txt)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Contains("SecondLifeViewer-LGPL-2.1.txt", named);
        foreach (string file in named)
            Assert.True(File.Exists(Path.Combine(root, "ThirdPartyLicenses", file)), file + " is named by the notice but is not in ThirdPartyLicenses");
        // the LGPL text itself, not a summary
        string lgpl = File.ReadAllText(Path.Combine(root, "ThirdPartyLicenses", "SecondLifeViewer-LGPL-2.1.txt"));
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", lgpl);
        Assert.Contains("Version 2.1, February 1999", lgpl);
    }
}
