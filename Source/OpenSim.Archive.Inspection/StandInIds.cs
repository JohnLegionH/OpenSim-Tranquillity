/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// Stand-in mesh asset ids. A draft plan names the mesh it expects to add before the mesh exists; the id it uses is a
/// stand-in with a fixed prefix (not a random version-4 id), so a later step can tell a stand-in from a real,
/// content-derived id. No archive may be written, and no check may pass, while a stand-in id is still in use.
/// </summary>
public static class StandInIds
{
    public const string Prefix = "00000000-0000-4000-8000-";

    public static Guid Make(int ordinal) =>
        Guid.Parse(Prefix + ordinal.ToString("x12", CultureInfo.InvariantCulture));

    public static bool Is(Guid id) => Is(id.ToString());

    /// <summary>True when the text is, or contains, a stand-in id (an asset entry name, a plan line, a field value).</summary>
    public static bool Is(string text) =>
        text != null && text.Contains(Prefix, StringComparison.OrdinalIgnoreCase);
}
