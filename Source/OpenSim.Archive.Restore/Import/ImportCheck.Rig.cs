/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Restore.Import;

public static partial class ImportCheck
{
    /// <summary>The most a weight read from the asset may differ from the file's (the asset stores 16 bits; the viewer's own floor is 0.001).</summary>
    private const float WeightTolerance = 1e-4f;
    private const double MatrixTolerance = 1e-6;

    /// <summary>
    /// The skin block and the weights of every level of the mesh asset against the files they were made from: the same joints, the same matrices and
    /// bind shape, and for every stored vertex the same influences as the file's vertex at that place.
    /// </summary>
    private static CheckLine CompareSkin(RigPlan rig, MeshAssetModel model, GlbFile[] files, GlbNode[] nodes)
    {
        CheckLine line = new CheckLine { What = "skin and weights", Measured = false };
        MeshSkin want = rig.Skin, got = model.Skin;
        if (got == null)
            return Fail(line, "the mesh asset has no skin block");
        List<string> onlyFile = want.JointNames.Except(got.JointNames).ToList(), onlyArchive = got.JointNames.Except(want.JointNames).ToList();
        if (onlyFile.Count > 0 || onlyArchive.Count > 0 || want.JointNames.Count != got.JointNames.Count)
            return Fail(line, "the joints differ: only in the files " + (onlyFile.Count == 0 ? "none" : string.Join(", ", onlyFile)) + "; only in the archive " + (onlyArchive.Count == 0 ? "none" : string.Join(", ", onlyArchive)));
        for (int i = 0; i < want.JointNames.Count; i++)
        {
            int at = got.JointNames.IndexOf(want.JointNames[i]);
            if (!Same(want.InverseBind[i], got.InverseBind[at]))
                return Fail(line, "the inverse bind matrix of " + want.JointNames[i] + " differs");
            if ((want.AltInverseBind == null) != (got.AltInverseBind == null) || (want.AltInverseBind != null && !Same(want.AltInverseBind[i], got.AltInverseBind[at])))
                return Fail(line, "the joint position of " + want.JointNames[i] + " differs");
        }
        if (!Same(want.BindShape, got.BindShape))
            return Fail(line, "the bind shape matrix differs");

        Matrix4x4 bind = new Matrix4x4(
            (float)got.BindShape[0], (float)got.BindShape[1], (float)got.BindShape[2], (float)got.BindShape[3], (float)got.BindShape[4], (float)got.BindShape[5], (float)got.BindShape[6], (float)got.BindShape[7],
            (float)got.BindShape[8], (float)got.BindShape[9], (float)got.BindShape[10], (float)got.BindShape[11], (float)got.BindShape[12], (float)got.BindShape[13], (float)got.BindShape[14], (float)got.BindShape[15]);
        int vertices = 0;
        for (int level = 0; level < 4; level++)
        {
            // the files' vertices of this level by place (1 mm cells), each with its influences by joint name
            Dictionary<(int, int, int), List<Dictionary<string, float>>> byPlace = new Dictionary<(int, int, int), List<Dictionary<string, float>>>();
            foreach (GlbPrimitive prim in files[level].Meshes[nodes[level].Mesh].Primitives)
                for (int v = 0; v < prim.Positions.Length / 3; v++)
                {
                    Vector3 p = new Vector3(prim.Positions[v * 3], -prim.Positions[v * 3 + 2], prim.Positions[v * 3 + 1]);
                    Dictionary<string, float> influences = new Dictionary<string, float>();
                    for (int k = 0; k < 4; k++)
                        if (prim.Weights[v * 4 + k] > 0f)
                            influences[rig.Skin.JointNames[prim.Joints[v * 4 + k]]] = prim.Weights[v * 4 + k];
                    (int, int, int) cell = Cell(p);
                    if (!byPlace.TryGetValue(cell, out List<Dictionary<string, float>> list))
                        byPlace[cell] = list = new List<Dictionary<string, float>>();
                    list.Add(influences);
                }
            MeshLod lod = model.Lod(3 - level);
            if (lod == null)
                return Fail(line, "the mesh asset has no level " + level.ToString(CultureInfo.InvariantCulture));
            foreach (MeshFace face in lod.Faces.Where(f => !f.NoGeometry && f.VertexCount > 0))
            {
                if (face.Weights == null)
                    return Fail(line, "a face of level " + level.ToString(CultureInfo.InvariantCulture) + " has no weights");
                for (int v = 0; v < face.VertexCount; v++)
                {
                    Vector3 placed = Vector3.Transform(face.Positions[v], bind);
                    Dictionary<string, float> stored = new Dictionary<string, float>();
                    foreach (Influence influence in face.Weights[v])
                        stored[got.JointNames[influence.Joint]] = influence.Weight;
                    bool matched = false;
                    for (int dx = -1; dx <= 1 && !matched; dx++)
                        for (int dy = -1; dy <= 1 && !matched; dy++)
                            for (int dz = -1; dz <= 1 && !matched; dz++)
                            {
                                (int cx, int cy, int cz) = Cell(placed);
                                if (byPlace.TryGetValue((cx + dx, cy + dy, cz + dz), out List<Dictionary<string, float>> candidates))
                                    matched = candidates.Any(c => c.Count == stored.Count && c.All(kv => stored.TryGetValue(kv.Key, out float w) && Math.Abs(w - kv.Value) <= WeightTolerance));
                            }
                    if (!matched)
                        return Fail(line, "a vertex of level " + level.ToString(CultureInfo.InvariantCulture) + " at " + placed.X.ToString("0.###", CultureInfo.InvariantCulture) + ", " + placed.Y.ToString("0.###", CultureInfo.InvariantCulture) + ", "
                            + placed.Z.ToString("0.###", CultureInfo.InvariantCulture) + " carries weights that no vertex of the file at that place has");
                    vertices++;
                }
            }
        }
        line.Ok = true;
        line.Detail = want.JointNames.Count.ToString(CultureInfo.InvariantCulture) + " joints, matrices equal; " + vertices.ToString(CultureInfo.InvariantCulture) + " stored vertices of the four levels carry the weights of the file's vertices";
        return line;
    }

    private static (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X * 1000f), (int)MathF.Floor(p.Y * 1000f), (int)MathF.Floor(p.Z * 1000f));

    private static CheckLine Fail(CheckLine line, string detail)
    {
        line.Ok = false;
        line.Detail = detail;
        return line;
    }

    private static bool Same(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > MatrixTolerance)
                return false;
        return true;
    }
}
