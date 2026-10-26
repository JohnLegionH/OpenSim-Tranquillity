/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using Luau.ScriptEngine.Bindings;

namespace Luau.ScriptEngine
{
    /// <summary>
    /// One loaded image per distinct compiled asset in a VM, shared by every script made from it. Like the VM, it is
    /// used by one thread at a time.
    /// </summary>
    internal sealed class SharedImages
    {
        public SharedImages(Vm vm) => throw new NotImplementedException();

        /// <summary>Images held right now.</summary>
        public int ImageCount => throw new NotImplementedException();

        /// <summary>A hold on the image for this asset, made if there is none yet. Dispose it when the script made from it is gone.</summary>
        public ImageLease Acquire(byte[] asset) => throw new NotImplementedException();
    }

    /// <summary>A hold on a shared image; the image is freed when the last hold is disposed.</summary>
    internal sealed class ImageLease : IDisposable
    {
        /// <summary>A script from the image, not started.</summary>
        public Script NewScript(int memoryLimit = 0, long tag = 0) => throw new NotImplementedException();

        public void Dispose() => throw new NotImplementedException();
    }
}
