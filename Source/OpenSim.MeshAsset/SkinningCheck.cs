/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;

namespace OpenSim.MeshAsset;

/// <summary>Posed-versus-rest error of the vertices in one group, in metres.</summary>
public sealed class PoseGroupStats
{
    public int Vertices { get; set; }
    public float Max { get; set; }
    public float Mean { get; set; }
    public float P99 { get; set; }
}

public sealed class FacePose
{
    public Vector3[] Rest { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Posed { get; init; } = Array.Empty<Vector3>();
    public int[] Indices { get; init; } = Array.Empty<int>();
    /// <summary>"bones", "collision volumes" or "unweighted" for each vertex.</summary>
    public string[] Group { get; init; } = Array.Empty<string>();
}

public sealed class PoseResult
{
    public List<FacePose> Faces { get; } = new();
    public Dictionary<string, PoseGroupStats> Groups { get; } = new();
    /// <summary>The largest error of any vertex whose heaviest influence is the joint.</summary>
    public Dictionary<string, float> MaxErrorByJoint { get; } = new();
    public float MaxError { get; set; }
    /// <summary>
    /// How well the mesh sits on the skeleton: the 99th percentile of the distance from a vertex (in its bind pose) to the
    /// world position of the joint that weighs most on it. A matching inverse bind matrix makes <see cref="MaxError"/>
    /// zero for a mesh built in any orientation, but a mesh that faces another way than the skeleton still stands far from
    /// its joints, and this shows it.
    /// </summary>
    public float JointDistanceP99 { get; set; }
    /// <summary>Joints of the asset that the skeleton does not have.</summary>
    public List<string> UnknownJoints { get; } = new();
}

/// <summary>
/// Poses a rigged mesh asset the way the Second Life viewer does and compares it with the mesh's own bind pose.
/// The viewer takes each vertex through the bind shape matrix, then through the weighted sum of
/// (inverse bind matrix of the joint) times (the joint's world matrix on the avatar), with the weights renormalised
/// to sum to one; a vertex with no weights is bound to joint 0 with all of the weight. The avatar here is the
/// skeleton file at its default pose. If the inverse bind matrices are the inverse of the joints' world matrices,
/// the posed vertex equals the vertex after the bind shape matrix, and a rigged body looks as authored.
/// </summary>
public static class SkinningCheck
{
    public const string Bones = "bones", CollisionVolumes = "collision volumes", Unweighted = "unweighted";

    /// <summary>The viewer ignores a joint position override within this distance of the joint's default position.</summary>
    public const float JointPositionThreshold = 0.0001f;

    /// <param name="useJointPositions">
    /// True: the asset's alternate bind matrices (the "Include joint positions" form) move the joints, as the viewer does.
    /// False: the skeleton stays at its defaults.
    /// </param>
    public static PoseResult Pose(MeshAssetModel model, RestSkeleton skeleton, int lod = 3, bool useJointPositions = true)
    {
        var skin = model.Skin ?? throw new ArgumentException("the asset has no skin block");
        var l = model.Lod(lod) ?? throw new ArgumentException("no such level of detail");
        var result = new PoseResult();

        var overrides = useJointPositions ? JointPositionOverrides(skin, skeleton) : null;
        int n = skin.JointNames.Count;
        var skinMatrix = new Matrix4x4[n];
        var kind = new string[n];
        for (int i = 0; i < n; i++)
        {
            var j = skeleton.Find(skin.JointNames[i]);
            if (j == null)
            {
                result.UnknownJoints.Add(skin.JointNames[i]);
                skinMatrix[i] = Matrix4x4.Identity;
                kind[i] = Bones;
                continue;
            }
            skinMatrix[i] = ToMatrix(skin.InverseBind[i]) * skeleton.World(j.Name, overrides);
            kind[i] = j.IsCollisionVolume ? CollisionVolumes : Bones;
        }
        var bindShape = ToMatrix(skin.BindShape);

        var errors = new Dictionary<string, List<float>>();
        var distances = new List<float>();
        var jointPos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var jj = skeleton.Find(skin.JointNames[i]);
            if (jj != null) { var wm = skeleton.World(jj.Name, overrides); jointPos[i] = new Vector3(wm.M41, wm.M42, wm.M43); }
        }
        foreach (var face in l.Faces)
        {
            if (face.NoGeometry || face.VertexCount == 0) continue;
            var rest = new Vector3[face.VertexCount];
            var posed = new Vector3[face.VertexCount];
            var group = new string[face.VertexCount];
            for (int v = 0; v < face.VertexCount; v++)
            {
                rest[v] = Vector3.Transform(face.Positions[v], bindShape);
                var w = face.Weights?[v];
                string g;
                string jointName;
                Vector3 p;
                if (w == null || w.Length == 0 || w.Sum(x => x.Weight) <= 0)
                {
                    p = Vector3.Transform(rest[v], skinMatrix[0]);
                    g = Unweighted;
                    jointName = skin.JointNames[0];
                }
                else
                {
                    float total = w.Sum(x => x.Weight);
                    p = Vector3.Zero;
                    foreach (var x in w) p += Vector3.Transform(rest[v], skinMatrix[x.Joint]) * (x.Weight / total);
                    var heaviest = w.OrderByDescending(x => x.Weight).First();
                    distances.Add((rest[v] - jointPos[heaviest.Joint]).Length());
                    g = kind[heaviest.Joint];
                    jointName = skin.JointNames[heaviest.Joint];
                }
                posed[v] = p;
                group[v] = g;
                float e = (p - rest[v]).Length();
                if (!errors.TryGetValue(g, out var list)) errors[g] = list = new();
                list.Add(e);
                if (e > result.MaxError) result.MaxError = e;
                result.MaxErrorByJoint[jointName] = result.MaxErrorByJoint.TryGetValue(jointName, out var old) ? Math.Max(old, e) : e;
            }
            result.Faces.Add(new FacePose { Rest = rest, Posed = posed, Indices = face.Indices, Group = group });
        }
        if (distances.Count > 0) { distances.Sort(); result.JointDistanceP99 = distances[(int)Math.Min(distances.Count - 1, Math.Floor(distances.Count * 0.99))]; }
        foreach (var (g, list) in errors)
        {
            list.Sort();
            result.Groups[g] = new PoseGroupStats
            {
                Vertices = list.Count,
                Max = list[^1],
                Mean = list.Average(),
                P99 = list[(int)Math.Min(list.Count - 1, Math.Floor(list.Count * 0.99))],
            };
        }
        return result;
    }

    /// <summary>
    /// The position each joint gets from the asset's alternate bind matrices, for the joints the viewer would
    /// override: those more than 0.1 mm from their default position. Null when the asset has none.
    /// </summary>
    public static Dictionary<string, Vector3>? JointPositionOverrides(MeshSkin skin, RestSkeleton skeleton)
    {
        if (skin.AltInverseBind == null || skin.AltInverseBind.Count != skin.JointNames.Count) return null;
        var d = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (int i = 0; i < skin.JointNames.Count; i++)
        {
            var j = skeleton.Find(skin.JointNames[i]);
            if (j == null) continue;
            var a = skin.AltInverseBind[i];
            var pos = new Vector3((float)a[12], (float)a[13], (float)a[14]);
            if ((pos - j.Position).Length() > JointPositionThreshold) d[j.Name] = pos;
        }
        return d;
    }

    public static Matrix4x4 ToMatrix(double[] m) => new(
        (float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
        (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
}
