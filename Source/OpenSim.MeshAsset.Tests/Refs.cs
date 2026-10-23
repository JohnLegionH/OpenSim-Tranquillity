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
