/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.CharacterPublish;

/// <summary>What a bundle needs to know about a mesh asset.</summary>
/// <param name="FaceCount">The number of faces (submeshes) of the highest level of detail present.</param>
/// <param name="NormalizedScale">
/// The size in metres the model had before the uploader scaled it into a unit box (the face's NormalizedScale), or null
/// when the asset has none (older uploads).
/// </param>
/// <param name="Rigged">True when the asset carries a skin block.</param>
/// <param name="BindShapeScale">
/// The scale of the skin's bind shape matrix (the length of each of its three axis rows), or null without a skin. A rigged
/// model uploaded from Collada may carry no per-face NormalizedScale; its size is then the bind shape matrix's scale.
/// </param>
public sealed record MeshInfo(int FaceCount, Vector3? NormalizedScale, bool Rigged, Vector3? BindShapeScale = null);

public static class MeshInspector
{
    /// <summary>Decodes the asset with the shared mesh library. Throws when it does not decode or has no geometry.</summary>
    public static MeshInfo Inspect(byte[] asset)
    {
        var model = MeshCodec.Decode(asset);
        var lod = model.High ?? model.Medium ?? model.Low ?? model.Lowest
            ?? throw new InvalidDataException("the mesh has no level of detail");
        if (lod.Faces.Count == 0) throw new InvalidDataException("the mesh has no faces");
        // The viewer's uploader writes the model's own size on every face (LLModel::writeModel, NormalizedScale), and the
        // object it makes takes that size. The first face that has one stands for the model.
        var ns = lod.Faces.FirstOrDefault(f => f.NormalizedScale is not null)?.NormalizedScale;
        Vector3? bindShape = null;
        if (model.Skin is { BindShape.Length: 16 } skin)
        {
            double Row(int r) => Math.Sqrt(skin.BindShape[r * 4] * skin.BindShape[r * 4] + skin.BindShape[r * 4 + 1] * skin.BindShape[r * 4 + 1] + skin.BindShape[r * 4 + 2] * skin.BindShape[r * 4 + 2]);
            bindShape = new Vector3((float)Row(0), (float)Row(1), (float)Row(2));
        }
        return new MeshInfo(lod.Faces.Count, ns, model.Skin != null, bindShape);
    }
}
