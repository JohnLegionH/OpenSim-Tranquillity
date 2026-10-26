/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Converter.Merge;

/// <summary>Decides, for each linkset, which parts merge into which merged parts and which stay as they are, and why. Writes nothing.</summary>
public static class MergePlanner
{
    public static LinksetPlan PlanLinkset(MergeLinkset linkset, MergeOptions options)
    {
        throw new NotImplementedException();
    }

    public static MergePlan Plan(IEnumerable<MergeLinkset> linksets, MergeOptions options, string archive)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Decides whether a face's transform can be baked into merged texture coordinates (merge rule 12).</summary>
public static class FaceBaking
{
    public static bool Bakeable(FaceTransform transform, int texGen, string materialId, LegacyMaterialInfo material, bool pbr, bool media, out string why)
    {
        throw new NotImplementedException();
    }
}
