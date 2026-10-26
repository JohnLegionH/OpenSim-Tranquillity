/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Compiled assets kept on disk, one file per (source, language, runtime stamp). A stamp change gives every
    /// source a new file name, so entries from an older runtime are never read; the code does not delete them (an
    /// operator can move them aside). An entry carries its stamp and a checksum, so a file cut short by a crash or
    /// damaged on disk is a miss, never a bad asset.
    /// </summary>
    internal sealed class CompileCache
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LCC1");
        private const string FileExtension = ".luc";

        private readonly string m_directory;
        private readonly string m_stamp;
        private readonly byte[] m_stampBytes;
        private readonly IEngineLog m_log;
        private int m_warned;

        public CompileCache(string directory, string stamp, IEngineLog log = null)
        {
            m_directory = directory;
            m_stamp = stamp;
            m_stampBytes = Encoding.UTF8.GetBytes(stamp);
            m_log = log;
        }

        public string Directory => m_directory;

        /// <summary>The runtime's version text (runtime=&lt;pin&gt; api=&lt;n&gt;) plus a hash of the bound function list.</summary>
        public static string MakeStamp(string runtimeVersion, string boundFunctionHash) => $"{runtimeVersion} fns={boundFunctionHash}";

        /// <summary>The engine's cache folder under its own state folder.</summary>
        public static string DirectoryFor(LuauSettings settings) => Path.Combine(settings.StateRoot, "cache");

        /// <summary>The entry's file: the SHA-256 of the stamp, the language and the source text.</summary>
        public string PathFor(string text, ScriptLanguage language)
        {
            using var sha = SHA256.Create();
            sha.TransformBlock(m_stampBytes, 0, m_stampBytes.Length, null, 0);
            byte[] lang = Encoding.ASCII.GetBytes("\0" + language + "\0");
            sha.TransformBlock(lang, 0, lang.Length, null, 0);
            byte[] body = Encoding.UTF8.GetBytes(text);
            sha.TransformFinalBlock(body, 0, body.Length);
            return System.IO.Path.Combine(m_directory, Convert.ToHexString(sha.Hash).ToLowerInvariant() + FileExtension);
        }

        public bool TryGet(string text, ScriptLanguage language, out byte[] asset)
        {
            asset = null;
            try
            {
                string path = PathFor(text, language);
                if (!File.Exists(path))
                    return false;
                byte[] file;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    file = new byte[stream.Length];
                    stream.ReadExactly(file);
                }
                asset = Decode(file);
                return asset != null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Put(string text, ScriptLanguage language, byte[] asset)
        {
            try
            {
                System.IO.Directory.CreateDirectory(m_directory);
                // Written in place: an entry cut short is caught by its checksum on the next read.
                using var stream = new FileStream(PathFor(text, language), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                byte[] file = Encode(asset);
                stream.Write(file, 0, file.Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (System.Threading.Interlocked.Exchange(ref m_warned, 1) == 0)
                    m_log?.Warn($"The compile cache folder {m_directory} cannot be written ({e.GetType().Name}); scripts are compiled each time they load");
            }
        }

        private byte[] Encode(byte[] asset)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(m_stampBytes.Length);
                w.Write(m_stampBytes);
                w.Write(asset.Length);
                w.Write(asset);
            }
            byte[] body = ms.ToArray();
            byte[] checksum = SHA256.HashData(body);
            byte[] file = new byte[body.Length + checksum.Length];
            body.CopyTo(file, 0);
            checksum.CopyTo(file, body.Length);
            return file;
        }

        /// <summary>The asset in a cache file, or null when the file is not an intact entry for this stamp.</summary>
        private byte[] Decode(byte[] file)
        {
            const int HashSize = 32;
            if (file.Length < Magic.Length + 4 + 4 + HashSize)
                return null;
            int bodyLength = file.Length - HashSize;
            if (!SHA256.HashData(file.AsSpan(0, bodyLength)).AsSpan().SequenceEqual(file.AsSpan(bodyLength)))
                return null;

            using var r = new BinaryReader(new MemoryStream(file, 0, bodyLength));
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                return null;
            int stampLength = r.ReadInt32();
            if (stampLength != m_stampBytes.Length || !r.ReadBytes(stampLength).AsSpan().SequenceEqual(m_stampBytes))
                return null;
            int assetLength = r.ReadInt32();
            if (assetLength < 0 || assetLength != bodyLength - r.BaseStream.Position)
                return null;
            return r.ReadBytes(assetLength);
        }
    }
}
