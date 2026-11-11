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
    public static readonly IReadOnlyDictionary<string, CollisionVolumeFrame> Frames = new Dictionary<string, CollisionVolumeFrame>(StringComparer.Ordinal);
}
