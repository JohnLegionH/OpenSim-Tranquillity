/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Eligibility;

/// <summary>
/// The shapes whose texture coordinates, as the mesher builds them, are not the ones the viewer gives the prim. The viewer's
/// rule is in indra/llmath/llvolume.cpp: a side face has u = the profile point's texture position and v = the path point's
/// parameter t (LLVolumeFace::createSide 6795-6890, LLPath::genNGon 1217-1375), and a cap has the profile point itself
/// (LLVolumeFace::createCap 6165-6250). The tests in ViewerUvRuleTests compare the mesher with that rule for every shape in the
/// probe matrix; a shape listed here is one where they differ, and a part with such a shape stays a prim (UV_UNVERIFIED).
/// The one case that used to be listed, a circular path with a y shear, is gone: in viewer mode the mesher now shears the path
/// point and keeps the angle, as the viewer does (llvolume.cpp:1296-1299, 1331-1334, 1358-1361), so the list is empty.
/// </summary>
public static class UvRules
{
    /// <summary>Why this shape's texture coordinates are not the viewer's, or null when they are (every probe shape).</summary>
    public static string Unverified(ShapeData shape) => null;
}
