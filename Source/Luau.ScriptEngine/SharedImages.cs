/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Luau.ScriptEngine.Bindings;

namespace Luau.ScriptEngine
{
    /// <summary>
    /// One loaded image per distinct compiled asset in a VM, shared by every script made from it. Like the VM, it is
    /// used by one thread at a time. Images are told apart by a hash of the compiled bytes, so two scripts compiled
    /// to the same asset share one image whichever inventory items they came from.
    /// </summary>
    internal sealed class SharedImages
    {
        private sealed class Entry
        {
            public Image Image;
            public int Holds;
        }

        private readonly Vm m_vm;
        private readonly Dictionary<string, Entry> m_images = new Dictionary<string, Entry>();
        private readonly object m_lock = new object();

        public SharedImages(Vm vm) => m_vm = vm;

        /// <summary>Images held right now.</summary>
        public int ImageCount
        {
            get { lock (m_lock) return m_images.Count; }
        }

        /// <summary>A hold on the image for this asset, made if there is none yet. Dispose it when the script made from it is gone.</summary>
        public ImageLease Acquire(byte[] asset)
        {
            string key = Convert.ToHexString(SHA256.HashData(asset));
            lock (m_lock)
            {
                if (!m_images.TryGetValue(key, out Entry entry))
                {
                    entry = new Entry { Image = m_vm.NewImage(asset) };
                    m_images[key] = entry;
                }
                entry.Holds++;
                return new ImageLease(this, key, entry.Image);
            }
        }

        private void Release(string key)
        {
            lock (m_lock)
            {
                if (!m_images.TryGetValue(key, out Entry entry))
                    return;
                if (--entry.Holds > 0)
                    return;
                m_images.Remove(key);
                // Scripts already made from the image keep it alive inside the library until they are freed.
                entry.Image.Dispose();
            }
        }

        internal void ReleaseHold(string key) => Release(key);
    }

    /// <summary>A hold on a shared image; the image is freed when the last hold is disposed.</summary>
    internal sealed class ImageLease : IDisposable
    {
        private readonly SharedImages m_owner;
        private readonly string m_key;
        private readonly Image m_image;
        private int m_disposed;

        internal ImageLease(SharedImages owner, string key, Image image)
        {
            m_owner = owner;
            m_key = key;
            m_image = image;
        }

        /// <summary>A script from the image, not started.</summary>
        public Script NewScript(int memoryLimit = 0, long tag = 0)
        {
            if (m_disposed != 0)
                throw new ObjectDisposedException(nameof(ImageLease));
            return m_image.NewScript(memoryLimit, tag);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref m_disposed, 1) == 0)
                m_owner.ReleaseHold(m_key);
        }
    }
}
