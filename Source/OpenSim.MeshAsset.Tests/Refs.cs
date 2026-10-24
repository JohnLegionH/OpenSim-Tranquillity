/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// Reference data that is not in the repository (real mesh assets, character exports). The folder comes from the
/// CHARACTER_REFS environment variable; tests that need it are skipped when it is unset or the data is absent.
/// </summary>
static class Refs
{
    public static string Root => Environment.GetEnvironmentVariable("CHARACTER_REFS") ?? "";
    public static string MeshCorpus => Path.Combine(Root, "work", "mesh-corpus");
    public static string Exports => Path.Combine(Root, "exports");

    public static string? SkipIfNoCorpus => Root.Length > 0 && Directory.Exists(MeshCorpus) && Directory.EnumerateFiles(MeshCorpus, "*.llmesh").Any()
        ? null : "no mesh corpus (set CHARACTER_REFS to a folder holding work/mesh-corpus)";

    public static IEnumerable<string> CorpusFiles() =>
        Directory.Exists(MeshCorpus) ? Directory.EnumerateFiles(MeshCorpus, "*.llmesh").OrderBy(x => x, StringComparer.Ordinal) : Enumerable.Empty<string>();

    public static IEnumerable<object[]> CorpusRows() =>
        CorpusFiles().Select(f => new object[] { Path.GetFileName(f) }).DefaultIfEmpty(new object[] { "" });
}

/// <summary>A fact that is skipped when the reference mesh corpus is absent.</summary>
sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute() { Skip = Refs.SkipIfNoCorpus; }
}

/// <summary>A theory that is skipped when the reference mesh corpus is absent.</summary>
sealed class CorpusTheoryAttribute : TheoryAttribute
{
    public CorpusTheoryAttribute() { Skip = Refs.SkipIfNoCorpus; }
}

/// <summary>
/// The viewer's avatar_skeleton.xml (SL_SKELETON) and a mesh asset made from a glTF export (MAX_ASSET), for the skinning
/// check. Tests that need them are skipped when the variable is unset or the file is absent.
/// </summary>
static class SkinRefs
{
    public static string Skeleton => Environment.GetEnvironmentVariable("SL_SKELETON") ?? "";
    /// <summary>avatar_lad.xml, found beside the skeleton file, gives the default shape.</summary>
    public static string AvatarDefinition => Path.Combine(Path.GetDirectoryName(Skeleton) ?? "", "avatar_lad.xml");
    public static RestSkeleton LoadSkeleton() => RestSkeleton.Load(Skeleton).WithDefaultShape(AvatarDefinition);
    public static string GltfModel => Environment.GetEnvironmentVariable("MAX_GLB") ?? "";
    public static string GltfAsset => Environment.GetEnvironmentVariable("MAX_ASSET") ?? "";
    public static string ReportDir => Environment.GetEnvironmentVariable("SKINNING_REPORT_DIR") ?? "";

    public static string? SkipIfNoSkeleton => File.Exists(Skeleton) ? null : "no skeleton (set SL_SKELETON to the viewer's avatar_skeleton.xml)";
    public static string? SkipIfNoGltfAsset => SkipIfNoSkeleton ?? (File.Exists(GltfAsset) ? null : "no asset (set MAX_ASSET)");
}

sealed class SkeletonFactAttribute : FactAttribute
{
    public SkeletonFactAttribute() { Skip = Refs.SkipIfNoCorpus ?? SkinRefs.SkipIfNoSkeleton; }
}
