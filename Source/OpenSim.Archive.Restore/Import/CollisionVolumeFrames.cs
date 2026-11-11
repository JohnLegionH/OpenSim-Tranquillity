/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Restore.Import;

/// <summary>Where a collision volume stands in the viewer's skeleton: its parent bone, its offset from that bone, its rotation and its scale.</summary>
public sealed record CollisionVolumeFrame(string Parent, Vector3 Position, Vector3 RotationDegrees, Vector3 Scale);

/// <summary>
/// The frames of the viewer's 26 collision volumes at the skeleton's rest, as the viewer's skeleton definition gives them
/// (character/avatar_skeleton.xml; see THIRD-PARTY-NOTICES.md). Axes are the viewer's: x forward, y left, z up. A fitted-mesh joint is
/// skinned with the volume's own rotation and scale, which a file exported from a modelling tool does not carry.
/// </summary>
public static class CollisionVolumeFrames
{
    public static readonly IReadOnlyDictionary<string, CollisionVolumeFrame> Frames = new Dictionary<string, CollisionVolumeFrame>(StringComparer.Ordinal)
    {
        ["PELVIS"] = new CollisionVolumeFrame("mPelvis", new Vector3(-0.01f, 0f, -0.02f), new Vector3(0f, 8f, 0f), new Vector3(0.12f, 0.16f, 0.17f)),
        ["BUTT"] = new CollisionVolumeFrame("mPelvis", new Vector3(-0.06f, 0f, -0.1f), new Vector3(0f, 0f, 0f), new Vector3(0.1f, 0.1f, 0.1f)),
        ["BELLY"] = new CollisionVolumeFrame("mTorso", new Vector3(0.028f, 0f, 0.04f), new Vector3(0f, 8f, 0f), new Vector3(0.09f, 0.13f, 0.15f)),
        ["LEFT_HANDLE"] = new CollisionVolumeFrame("mTorso", new Vector3(0f, 0.1f, 0.058f), new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.05f, 0.05f)),
        ["RIGHT_HANDLE"] = new CollisionVolumeFrame("mTorso", new Vector3(0f, -0.1f, 0.058f), new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.05f, 0.05f)),
        ["LOWER_BACK"] = new CollisionVolumeFrame("mTorso", new Vector3(0f, 0f, 0.023f), new Vector3(0f, 0f, 0f), new Vector3(0.09f, 0.13f, 0.15f)),
        ["CHEST"] = new CollisionVolumeFrame("mChest", new Vector3(0.028f, 0f, 0.07f), new Vector3(0f, -10f, 0f), new Vector3(0.11f, 0.15f, 0.2f)),
        ["LEFT_PEC"] = new CollisionVolumeFrame("mChest", new Vector3(0.119f, 0.082f, 0.042f), new Vector3(0f, 4.29f, 0f), new Vector3(0.05f, 0.05f, 0.05f)),
        ["RIGHT_PEC"] = new CollisionVolumeFrame("mChest", new Vector3(0.119f, -0.082f, 0.042f), new Vector3(0f, 4.29f, 0f), new Vector3(0.05f, 0.05f, 0.05f)),
        ["UPPER_BACK"] = new CollisionVolumeFrame("mChest", new Vector3(0f, 0f, 0.017f), new Vector3(0f, 0f, 0f), new Vector3(0.09f, 0.13f, 0.15f)),
        ["NECK"] = new CollisionVolumeFrame("mNeck", new Vector3(0f, 0f, 0.02f), new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.06f, 0.08f)),
        ["HEAD"] = new CollisionVolumeFrame("mHead", new Vector3(0.02f, 0f, 0.07f), new Vector3(0f, 0f, 0f), new Vector3(0.11f, 0.09f, 0.12f)),
        ["L_CLAVICLE"] = new CollisionVolumeFrame("mCollarLeft", new Vector3(0.02f, 0f, 0.02f), new Vector3(0f, 0f, 0f), new Vector3(0.07f, 0.14f, 0.05f)),
        ["L_UPPER_ARM"] = new CollisionVolumeFrame("mShoulderLeft", new Vector3(0f, 0.12f, 0.01f), new Vector3(-5f, 0f, 0f), new Vector3(0.05f, 0.17f, 0.05f)),
        ["L_LOWER_ARM"] = new CollisionVolumeFrame("mElbowLeft", new Vector3(0f, 0.1f, 0f), new Vector3(-3f, 0f, 0f), new Vector3(0.04f, 0.14f, 0.04f)),
        ["L_HAND"] = new CollisionVolumeFrame("mWristLeft", new Vector3(0.01f, 0.05f, 0f), new Vector3(-3f, 0f, -10f), new Vector3(0.05f, 0.08f, 0.03f)),
        ["R_CLAVICLE"] = new CollisionVolumeFrame("mCollarRight", new Vector3(0.02f, 0f, 0.02f), new Vector3(0f, 0f, 0f), new Vector3(0.07f, 0.14f, 0.05f)),
        ["R_UPPER_ARM"] = new CollisionVolumeFrame("mShoulderRight", new Vector3(0f, -0.12f, 0.01f), new Vector3(5f, 0f, 0f), new Vector3(0.05f, 0.17f, 0.05f)),
        ["R_LOWER_ARM"] = new CollisionVolumeFrame("mElbowRight", new Vector3(0f, -0.1f, 0f), new Vector3(3f, 0f, 0f), new Vector3(0.04f, 0.14f, 0.04f)),
        ["R_HAND"] = new CollisionVolumeFrame("mWristRight", new Vector3(0.01f, -0.05f, 0f), new Vector3(3f, 0f, 10f), new Vector3(0.05f, 0.08f, 0.03f)),
        ["R_UPPER_LEG"] = new CollisionVolumeFrame("mHipRight", new Vector3(-0.02f, 0.05f, -0.22f), new Vector3(0f, 0f, 0f), new Vector3(0.09f, 0.09f, 0.32f)),
        ["R_LOWER_LEG"] = new CollisionVolumeFrame("mKneeRight", new Vector3(-0.02f, 0f, -0.2f), new Vector3(0f, 0f, 0f), new Vector3(0.06f, 0.06f, 0.25f)),
        ["R_FOOT"] = new CollisionVolumeFrame("mAnkleRight", new Vector3(0.077f, 0f, -0.041f), new Vector3(0f, 10f, 0f), new Vector3(0.13f, 0.05f, 0.05f)),
        ["L_UPPER_LEG"] = new CollisionVolumeFrame("mHipLeft", new Vector3(-0.02f, -0.05f, -0.22f), new Vector3(0f, 0f, 0f), new Vector3(0.09f, 0.09f, 0.32f)),
        ["L_LOWER_LEG"] = new CollisionVolumeFrame("mKneeLeft", new Vector3(-0.02f, 0f, -0.2f), new Vector3(0f, 0f, 0f), new Vector3(0.06f, 0.06f, 0.25f)),
        ["L_FOOT"] = new CollisionVolumeFrame("mAnkleLeft", new Vector3(0.077f, 0f, -0.041f), new Vector3(0f, 10f, 0f), new Vector3(0.13f, 0.05f, 0.05f)),
    };
}
