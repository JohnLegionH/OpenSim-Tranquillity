/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSimNGC.Appearance.Baking;

/// <summary>A character folder could not be used for baking; the message says which part is wrong.</summary>
public sealed class CharacterFolderException : Exception
{
    public CharacterFolderException(string message, IReadOnlyList<string>? problemFiles = null) : base(message)
    {
        ProblemFiles = problemFiles ?? Array.Empty<string>();
    }

    /// <summary>The names of the files that are missing or cannot be decoded (empty when the folder itself is the problem).</summary>
    public IReadOnlyList<string> ProblemFiles { get; }
}

public sealed partial class AvatarBakeCompositor
{
    /// <summary>Stub: filled in with the implementation.</summary>
    public static AvatarBakeCompositor FromCharacterFolder(string folder) => null!;
}
