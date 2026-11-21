/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// For a test of the library's own answer to an os, iw or bot call when its host gave it no list of those functions: the list is
/// process-wide in the library, so it is taken off for the test and put back afterwards. Use it only in a test of the
/// "native-library" collection, which does not run beside another native test.
/// </summary>
internal sealed class NoHostList : IDisposable
{
    public NoHostList() => Host.SetHostFunctions(null);

    public void Dispose() => OsBindings.ApplyHostList();
}
