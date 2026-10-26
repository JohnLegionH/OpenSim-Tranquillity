/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using Luau.ScriptEngine.Bindings;
using OpenMetaverse;

namespace Luau.ScriptEngine
{
    /// <summary>One request to load a script this engine owns.</summary>
    internal sealed class LoadRequest
    {
        public uint LocalID;
        public UUID ItemID;

        /// <summary>The asset the item had when the request was made; used to fetch the text when <see cref="Source"/> is empty.</summary>
        public UUID AssetID;

        /// <summary>The script text from the rez event; empty when the loader must fetch it.</summary>
        public string Source;
    }

    /// <summary>Fetches a script's text from the asset service.</summary>
    internal interface IScriptTextSource
    {
        /// <summary>Calls <paramref name="done"/> with the asset's text, or null when it is missing or is not script text. May call on any thread.</summary>
        void Fetch(UUID assetId, Action<string> done);
    }

    /// <summary>What the engine asks of a loader; the engine's tests substitute their own.</summary>
    internal interface IScriptLoader : IDisposable
    {
        /// <summary>Queue a load; returns at once. The serial identifies this load for <see cref="WaitForCompileErrors"/>.</summary>
        long Post(LoadRequest request);

        /// <summary>The item is no longer this engine's: forget its load and release its ready script.</summary>
        void Disown(UUID itemID);

        /// <summary>
        /// The compile errors, in the editor's format, of the load this thread last posted for the item (else the
        /// latest), waiting for that load to finish; empty when it compiled. Null when this engine has no load of the item.
        /// </summary>
        List<string> WaitForCompileErrors(UUID itemID, TimeSpan timeout);

        bool IsLoading(UUID itemID);
        bool IsReady(UUID itemID);
    }

    /// <summary>A compiled script held ready: its script object exists and has not been started.</summary>
    internal sealed class ReadyScript : IDisposable
    {
        public UUID ItemID { get; init; }
        public uint LocalID { get; init; }
        public UUID AssetID { get; init; }
        public ScriptLanguage Language { get; init; }
        public long Serial { get; init; }
        public Script Script { get; init; }
        internal ImageLease Lease { get; init; }

        public void Dispose() => throw new NotImplementedException();
    }

    /// <summary>
    /// Fetches a script's text, compiles it off the region's threads, and holds the result ready. All native
    /// virtual-machine work happens on the loader's own thread, which is the only thread that touches its VM.
    /// </summary>
    internal sealed class ScriptLoader : IScriptLoader
    {
        public const string ErrorWaitTimedOut = "timedout waiting for errors";

        public ScriptLoader(ScriptCompiler compiler, IScriptTextSource assets, IEngineLog log, int memoryLimitBytes, Func<Vm> vmFactory)
            => throw new NotImplementedException();

        public long Post(LoadRequest request) => throw new NotImplementedException();
        public void Disown(UUID itemID) => throw new NotImplementedException();
        public List<string> WaitForCompileErrors(UUID itemID, TimeSpan timeout) => throw new NotImplementedException();
        public bool IsLoading(UUID itemID) => throw new NotImplementedException();
        public bool IsReady(UUID itemID) => throw new NotImplementedException();

        /// <summary>The ready script for the item, or null.</summary>
        public ReadyScript GetReady(UUID itemID) => throw new NotImplementedException();

        public int ReadyCount => throw new NotImplementedException();
        public int ImageCount => throw new NotImplementedException();
        public int LoaderThreadId => throw new NotImplementedException();

        public void Dispose() => throw new NotImplementedException();
    }
}
