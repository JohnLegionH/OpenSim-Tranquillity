/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How a mesh prim with an opening (a gateway, an open trough) collides today. A mesh asset can carry a physics mesh
/// ("physics_mesh"), a convex decomposition ("physics_convex": a hull list and one bounding hull) and its display LODs.
/// The viewer's upload drops the physics mesh when it writes a decomposition. The module cooks what the mesher
/// returns: for a physical prim one convex hull of every point, for a non-physical prim the triangle mesh. These tests
/// record the outcome, with the same shape built from boxes (what the asset's own hull list would give) beside it.
/// Each result is a line "MESH-SHAPES ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MeshShapesTodayTests
{
    private readonly ITestOutputHelper _out;
    public MeshShapesTodayTests(ITestOutputHelper output) { _out = output; }

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float CentreX = 128f, CentreY = 128f;

    // ---------------------------------------------------------------- geometry (metres, centred on the prim)

    private readonly record struct Box(float X0, float X1, float Y0, float Y1, float Z0, float Z1);

    // A gateway 4 m wide, 2 m deep and 3 m tall, open along y: two pillars and a lintel. The opening is 1.6 m wide and
    // 2.1 m tall. Its centre is 1.5 m above its foot.
    private static readonly Box[] Gateway =
    {
        new(-2f, -0.8f, -1f, 1f, -1.5f, 1.5f),
        new(0.8f, 2f, -1f, 1f, -1.5f, 1.5f),
        new(-0.8f, 0.8f, -1f, 1f, 0.6f, 1.5f),
    };
    private const float GatewayHeight = 3f;

    // A trough 4 m long, 2 m deep and 1.5 m tall, open on top: a floor and two walls. The slot is 3.4 m wide; its floor
    // is 0.3 m thick. Its centre is 0.75 m above its foot.
    private static readonly Box[] Trough =
    {
        new(-2f, 2f, -1f, 1f, -0.75f, -0.45f),
        new(-2f, -1.7f, -1f, 1f, -0.45f, 0.75f),
        new(1.7f, 2f, -1f, 1f, -0.45f, 0.75f),
    };
    private const float TroughHeight = 1.5f, TroughFloorTop = 0.3f;

    private static (Vector3[] verts, int[] tris) Soup(Box[] boxes)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        foreach (Box b in boxes)
        {
            int o = v.Count;
            for (int i = 0; i < 8; i++)
                v.Add(new Vector3((i & 1) == 0 ? b.X0 : b.X1, (i & 2) == 0 ? b.Y0 : b.Y1, (i & 4) == 0 ? b.Z0 : b.Z1));
            int[][] quads = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (int[] q in quads)
            {
                t.AddRange(new[] { o + q[0], o + q[1], o + q[2], o + q[0], o + q[2], o + q[3] });
            }
        }
        return (v.ToArray(), t.ToArray());
    }

    // An open bowl, a grid of n x n quads (2 n^2 triangles), 4 m across and 1 m deep.
    private static (Vector3[] verts, int[] tris) Bowl(int n)
    {
        var v = new Vector3[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                float fx = (x / (float)n - 0.5f) * 2f, fy = (y / (float)n - 0.5f) * 2f;
                v[y * (n + 1) + x] = new Vector3(fx * 2f, fy * 2f, -0.5f + 1f * Math.Min(1f, fx * fx + fy * fy));
            }
        var t = new List<int>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                t.AddRange(new[] { a, b, c, b, d, c });
            }
        return (v, t.ToArray());
    }

    // ---------------------------------------------------------------- the asset

    private static byte[] Zlib(OSD osd)
    {
        byte[] raw = OSDParser.SerializeLLSDBinary(osd, false);
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);   // the mesher skips the two header bytes
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true))
            d.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static ushort Q(float v, float min, float max) => (ushort)Math.Clamp(MathF.Round((v - min) / (max - min) * 65535f), 0f, 65535f);

    private static byte[] U16(IEnumerable<ushort> values)
    {
        var b = new List<byte>();
        foreach (ushort u in values) { b.Add((byte)(u & 0xFF)); b.Add((byte)(u >> 8)); }
        return b.ToArray();
    }

    private static (Vector3 min, Vector3 max) Bounds(IEnumerable<Vector3> pts)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (Vector3 p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    private static OSDArray V3(Vector3 v) => new() { OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z) };

    // A submesh block: the array of maps a "physics_mesh" or a display LOD holds.
    private static byte[] MeshBlock(Vector3[] verts, int[] tris)
    {
        (Vector3 mn, Vector3 mx) = Bounds(verts);
        var pos = new List<ushort>();
        foreach (Vector3 p in verts) { pos.Add(Q(p.X, mn.X, mx.X)); pos.Add(Q(p.Y, mn.Y, mx.Y)); pos.Add(Q(p.Z, mn.Z, mx.Z)); }
        var map = new OSDMap
        {
            ["PositionDomain"] = new OSDMap { ["Min"] = V3(mn), ["Max"] = V3(mx) },
            ["Position"] = OSD.FromBinary(U16(pos)),
            ["TriangleList"] = OSD.FromBinary(U16(tris.Select(i => (ushort)i))),
        };
        return Zlib(new OSDArray { map });
    }

    // The "physics_convex" block: one hull per box (8 points each) and a bounding hull of all the points.
    private static byte[] ConvexBlock(Box[] boxes)
    {
        var hulls = new List<Vector3[]>();
        foreach (Box b in boxes)
        {
            var h = new Vector3[8];
            for (int i = 0; i < 8; i++)
                h[i] = new Vector3((i & 1) == 0 ? b.X0 : b.X1, (i & 2) == 0 ? b.Y0 : b.Y1, (i & 4) == 0 ? b.Z0 : b.Z1);
            hulls.Add(h);
        }
        (Vector3 mn, Vector3 mx) = Bounds(hulls.SelectMany(h => h));
        var pos = new List<ushort>();
        foreach (Vector3[] h in hulls)
            foreach (Vector3 p in h) { pos.Add(Q(p.X, mn.X, mx.X)); pos.Add(Q(p.Y, mn.Y, mx.Y)); pos.Add(Q(p.Z, mn.Z, mx.Z)); }
        var bounding = new List<ushort>();
        for (int i = 0; i < 8; i++)
        {
            bounding.Add(Q((i & 1) == 0 ? mn.X : mx.X, mn.X, mx.X));
            bounding.Add(Q((i & 2) == 0 ? mn.Y : mx.Y, mn.Y, mx.Y));
            bounding.Add(Q((i & 4) == 0 ? mn.Z : mx.Z, mn.Z, mx.Z));
        }
        var map = new OSDMap
        {
            ["Min"] = V3(mn),
            ["Max"] = V3(mx),
            ["HullList"] = OSD.FromBinary(hulls.Select(h => (byte)h.Length).ToArray()),
            ["Positions"] = OSD.FromBinary(U16(pos)),
            ["BoundingVerts"] = OSD.FromBinary(U16(bounding)),
        };
        return Zlib(map);
    }

    /// <summary>The asset as the viewer's upload writes it: a header of offsets, then the blocks. With a physics mesh
    /// the decomposition is just the bounding hull (an upload with a physics file); without one (an upload that used
    /// Analyze) the hull list is there and no physics mesh is.</summary>
    private static byte[] Asset((Vector3[] verts, int[] tris) display, Box[] boxes, bool withPhysicsMesh)
    {
        var blocks = new List<(string name, byte[] data)>();
        byte[] mesh = MeshBlock(display.verts, display.tris);
        blocks.Add(("medium_lod", mesh));
        blocks.Add(("high_lod", mesh));
        if (withPhysicsMesh)
            blocks.Add(("physics_mesh", mesh));
        else
            blocks.Add(("physics_convex", ConvexBlock(boxes)));
        var header = new OSDMap();
        int offset = 0;
        foreach ((string name, byte[] data) in blocks)
        {
            header[name] = new OSDMap { ["offset"] = offset, ["size"] = data.Length };
            offset += data.Length;
        }
        using var ms = new MemoryStream();
        byte[] h = OSDParser.SerializeLLSDBinary(header, true);
        ms.Write(h, 0, h.Length);
        foreach ((_, byte[] data) in blocks)
            ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static PrimitiveBaseShape MeshPbs(byte[] asset)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;   // as the viewer sends a mesh: circle profile, curved path (never the box fast path)
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Mesh;
        pbs.SculptTexture = UUID.Random();   // the mesher caches by this
        pbs.SculptData = asset;
        return pbs;
    }

    // ---------------------------------------------------------------- the scene

    private static JoltScene NewScene(string scratch)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", scratch);
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", "45");
        var scene = new JoltScene();
        scene.Initialise(config);
        float[] heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        typeof(JoltScene).GetField("m_mesher", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, mesher);
        return scene;
    }

    private static string Scratch()
    {
        string d = Path.Combine(Path.GetTempPath(), "mesh-shapes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Step(JoltScene scene, float seconds)
    {
        for (int i = 0; i < (int)MathF.Ceiling(seconds / Heartbeat); i++)
            scene.Simulate(Heartbeat);
    }

    private static uint _id = 1000;

    private static PhysicsActor AddMesh(JoltScene scene, byte[] asset, float height, bool physical, byte shapeType, out string kind)
    {
        PhysicsActor a = scene.AddPrimShape("mesh", MeshPbs(asset), new Vector3(CentreX, CentreY, Ground + height * 0.5f),
                                            Vector3.One, Quaternion.Identity, physical, false, shapeType, _id++);
        kind = (string)typeof(JoltPrim).GetField("_shapeKind", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a)!;
        return a;
    }

    private static void AddBoxes(JoltScene scene, Box[] boxes, float height)
    {
        foreach (Box b in boxes)
        {
            Vector3 size = new(b.X1 - b.X0, b.Y1 - b.Y0, b.Z1 - b.Z0);
            Vector3 centre = new((b.X0 + b.X1) * 0.5f + CentreX, (b.Y0 + b.Y1) * 0.5f + CentreY, (b.Z0 + b.Z1) * 0.5f + Ground + height * 0.5f);
            scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), centre, size, Quaternion.Identity, false, _id++);
        }
    }

    // A 0.4 m ball dropped from 2 m over the trough's middle; where it rests, above the ground.
    private float DropBall(JoltScene scene, float top)
    {
        PhysicsActor ball = scene.AddPrimShape("ball", sphere(), new Vector3(CentreX, CentreY, Ground + top + 2f), new Vector3(0.4f, 0.4f, 0.4f),
                                               Quaternion.Identity, true, _id++);
        Step(scene, 4f);
        return ball.Position.Z - Ground;

        static PrimitiveBaseShape sphere()
        {
            PrimitiveBaseShape p = PrimitiveBaseShape.CreateSphere();
            return p;
        }
    }

    // A 0.5 m physical box fired along +y at 8 m/s from 3 m before a gateway; how far past the gateway's far side
    // (y = +1 m) it got in 5 s. Positive means it passed through the opening.
    private float FireBox(JoltScene scene)
    {
        PhysicsActor box = scene.AddPrimShape("probe", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY - 3f, Ground + 0.4f),
                                              new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
        Step(scene, Heartbeat);
        box.Velocity = new Vector3(0f, 8f, 0f);
        Step(scene, 5f);
        return box.Position.Y - (CentreY + 1f);
    }

    // An avatar set 4 m before the gateway and walked along +y at 3 m/s for 6 s; how far past the far side it got.
    private float WalkAvatar(JoltScene scene)
    {
        PhysicsActor av = scene.AddAvatar(777u, "Test User", new Vector3(CentreX, CentreY - 4f, Ground + Run.AvatarSize.Z * 0.5f), Run.AvatarSize, 0f, false);
        Step(scene, 1f);
        av.TargetVelocity = new Vector3(0f, 3f, 0f);
        Step(scene, 6f);
        return av.Position.Y - (CentreY + 1f);
    }

    private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- what a dropped ball rests on, and what passes

    [Fact]
    public void Trough_and_gateway_today()
    {
        string scratch = Scratch();
        var lines = new List<string>();
        (Vector3[] verts, int[] tris) trough = Soup(Trough), gate = Soup(Gateway);
        foreach (bool withPhysicsMesh in new[] { false, true })
            foreach (bool physical in new[] { false, true })
                foreach (byte type in new byte[] { (byte)PhysShapeType.prim, (byte)PhysShapeType.convex })
                {
                    string asset = withPhysicsMesh ? "physics_mesh" : "hull_list_only";
                    string shape = type == (byte)PhysShapeType.prim ? "PRIM" : "CONVEX";

                    JoltScene s1 = NewScene(scratch);
                    float rest, kindOk;
                    string kind;
                    try
                    {
                        AddMesh(s1, Asset(trough, Trough, withPhysicsMesh), TroughHeight, physical, type, out kind);
                        Step(s1, 2f);   // a physical trough settles first
                        rest = DropBall(s1, TroughHeight);
                    }
                    finally { s1.Dispose(); }

                    JoltScene s2 = NewScene(scratch);
                    float boxPast, avPast;
                    string kind2;
                    try
                    {
                        AddMesh(s2, Asset(gate, Gateway, withPhysicsMesh), GatewayHeight, physical, type, out kind2);
                        Step(s2, 2f);
                        boxPast = FireBox(s2);
                    }
                    finally { s2.Dispose(); }

                    JoltScene s3 = NewScene(scratch);
                    try
                    {
                        AddMesh(s3, Asset(gate, Gateway, withPhysicsMesh), GatewayHeight, physical, type, out _);
                        Step(s3, 2f);
                        avPast = WalkAvatar(s3);
                    }
                    finally { s3.Dispose(); }

                    lines.Add($"MESH-SHAPES asset={asset} physical={physical} shape={shape} cooked_trough={kind} cooked_gateway={kind2} " +
                              $"ball_rest_z={F(rest)} box_past_far_side={F(boxPast)} avatar_past_far_side={F(avPast)}");

                    // Today: a non-physical mesh keeps its triangle mesh (the ball rests on the trough floor, the box and
                    // the avatar pass the opening); a physical one is one hull of every point (the ball rests on top, the
                    // opening is closed). The shape type and which blocks the asset carries make no difference.
                    if (physical)
                    {
                        Assert.Equal("hull(mesher)", kind);
                        Assert.InRange(rest, TroughHeight, TroughHeight + 0.4f);
                        Assert.True(boxPast < 0f && avPast < 0f, $"physical gateway let something through: box {boxPast}, avatar {avPast}");
                    }
                    else
                    {
                        Assert.Equal("mesh(mesher)", kind);
                        Assert.InRange(rest, TroughFloorTop + 0.1f, TroughFloorTop + 0.3f);
                        Assert.True(boxPast > 0f && avPast > 0f, $"non-physical gateway blocked: box {boxPast}, avatar {avPast}");
                    }
                }

        // The hull list's own answer, built from the same boxes: static prims.
        JoltScene r1 = NewScene(scratch);
        float refRest;
        try { AddBoxes(r1, Trough, TroughHeight); refRest = DropBall(r1, TroughHeight); } finally { r1.Dispose(); }
        JoltScene r2 = NewScene(scratch);
        float refBox;
        try { AddBoxes(r2, Gateway, GatewayHeight); refBox = FireBox(r2); } finally { r2.Dispose(); }
        JoltScene r3 = NewScene(scratch);
        float refAv;
        try { AddBoxes(r3, Gateway, GatewayHeight); refAv = WalkAvatar(r3); } finally { r3.Dispose(); }
        lines.Add($"MESH-SHAPES reference=boxes(hull list) ball_rest_z={F(refRest)} box_past_far_side={F(refBox)} avatar_past_far_side={F(refAv)}");
        lines.Add($"MESH-SHAPES expected trough floor top z={F(TroughFloorTop)} ball rest on floor={F(TroughFloorTop + 0.2f)} ball rest on rim/hull top={F(TroughHeight + 0.2f)}");

        foreach (string l in lines) _out.WriteLine(l);
        Console.WriteLine(string.Join("\n", lines));
    }

    // What the module does with a mesh prim whose asset bytes have not been loaded into the shape (SculptData empty):
    // it never asks for the asset (the request-asset delegate it is given is not used), so the mesher returns nothing and
    // the prim collides as its bounding box.
    [Fact]
    public void Mesh_prim_without_loaded_asset_collides_as_a_box()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            foreach (bool physical in new[] { false, true })
            {
                PrimitiveBaseShape pbs = MeshPbs(Array.Empty<byte>());
                PhysicsActor a = scene.AddPrimShape("unloaded", pbs, new Vector3(CentreX, CentreY, Ground + 5f), Vector3.One, Quaternion.Identity,
                                                    physical, false, (byte)PhysShapeType.convex, _id++);
                string kind = (string)typeof(JoltPrim).GetField("_shapeKind", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a)!;
                string line = $"MESH-SHAPES asset_not_loaded physical={physical} cooked={kind}";
                _out.WriteLine(line);
                Console.WriteLine(line);
                Assert.Equal("bbox(fallback)", kind);
            }
        }
        finally { scene.Dispose(); }
    }

    // ---------------------------------------------------------------- memory and cook time of identical prims

    private static long NativeBytes()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 - GC.GetGCMemoryInfo().TotalCommittedBytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hundred_identical_bowls(bool physical)
    {
        string scratch = Scratch();
        byte[] asset = Asset(Bowl(40), Trough, true);
        var pbsList = new List<PrimitiveBaseShape>();   // the same asset bytes, one prim each; all carry the same sculpt id
        UUID id = UUID.Random();
        JoltScene scene = NewScene(scratch);
        try
        {
            // Warm: one bowl cooked and removed, so one-time native growth is not counted.
            PhysicsActor warm = scene.AddPrimShape("warm", WithId(asset, id), new Vector3(10f, 10f, Ground + 5f), new Vector3(4f, 4f, 1f), Quaternion.Identity, physical, false, 0, 1u);
            scene.RemovePrim(warm);
            long before = NativeBytes();
            var times = new List<double>();
            for (int i = 0; i < 100; i++)
            {
                var sw = Stopwatch.StartNew();
                scene.AddPrimShape("bowl", WithId(asset, id), new Vector3(20f + (i % 10) * 6f, 20f + (i / 10) * 6f, Ground + 5f), new Vector3(4f, 4f, 1f),
                                   Quaternion.Identity, physical, false, 0, 2000u + (uint)i);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            long after = NativeBytes();
            double first = times[0], rest = times.Skip(1).Average();
            string line = $"MESH-SHAPES copies=100 physical={physical} triangles=3200 growth_kb={(after - before) / 1024} per_copy_kb={(after - before) / 1024 / 100} " +
                          $"first_add_ms={first:0.0} later_adds_mean_ms={rest:0.0} total_ms={times.Sum():0.0}";
            _out.WriteLine(line);
            Console.WriteLine(line);
        }
        finally { scene.Dispose(); }

        static PrimitiveBaseShape WithId(byte[] a, UUID id)
        {
            PrimitiveBaseShape p = MeshPbs(a);
            p.SculptTexture = id;
            return p;
        }
    }

    // The mesher keeps the hull list of the asset it decoded last in fields of its own, and hands it out through
    // GetConvexHulls on the concrete class. A second request for a mesh it already holds is answered from its cache
    // without decoding, so the hulls read after it are those of whatever asset was decoded last.
    [Fact]
    public void Mesher_hull_list_belongs_to_the_last_decode_not_the_last_request()
    {
        string scratch = Scratch();
        var config = new IniConfigSource();
        config.AddConfig("Startup").Set("meshing", "Meshmerizer");
        config.Configs["Startup"].Set("DecodedSculptMapPath", scratch);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        PrimitiveBaseShape three = MeshPbs(Asset(Soup(Gateway), Gateway, false));
        PrimitiveBaseShape one = MeshPbs(Asset(Soup(Trough.Take(1).ToArray()), Trough.Take(1).ToArray(), false));
        mesher.CreateMesh("a", three, Vector3.One, 32f, false, false, false);
        int afterThree = mesher.GetConvexHulls(Vector3.One)!.Count;
        mesher.CreateMesh("b", one, Vector3.One, 32f, false, false, false);
        int afterOne = mesher.GetConvexHulls(Vector3.One)!.Count;
        mesher.CreateMesh("a again", three, Vector3.One, 32f, false, false, false);   // answered from the cache
        int afterThreeAgain = mesher.GetConvexHulls(Vector3.One)!.Count;
        string line = $"MESH-SHAPES mesher hull_list_hulls: after asset of 3 = {afterThree}, after asset of 1 = {afterOne}, after asset of 3 again (cache hit) = {afterThreeAgain}";
        _out.WriteLine(line);
        Console.WriteLine(line);
        Assert.Equal(3, afterThree);
        Assert.Equal(1, afterOne);
        Assert.Equal(1, afterThreeAgain);   // stale: the hulls of the asset decoded last
    }

    // ---------------------------------------------------------------- the mesher's bounding-box substitution

    [Fact]
    public void Small_non_physical_mesh_from_the_mesher()
    {
        string scratch = Scratch();
        var config = new IniConfigSource();
        config.AddConfig("Startup").Set("meshing", "Meshmerizer");
        config.Configs["Startup"].Set("DecodedSculptMapPath", scratch);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        byte[] asset = Asset(Soup(Trough), Trough, true);
        foreach (float scale in new[] { 0.1f, 0.19f, 0.25f, 1f })
        {
            IMesh m = mesher.CreateMesh("probe", MeshPbs(asset), new Vector3(scale, scale, scale), 32f, false, false, false);
            int vertices = m.getVertexListAsFloat().Length / 3;
            // The mesher replaces a non-physical mesh with every side under 0.2 m by its bounding box (8 corners); the
            // module asks the mesher for isPhysical = false for every mesh, so this reaches a non-physical or physical prim.
            Assert.Equal(scale < 0.2f, vertices == 8);
            string line = $"MESH-SHAPES mesher scale={scale} isPhysical=false vertices={vertices} triangles={m.getIndexListAsInt().Length / 3}";
            _out.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
