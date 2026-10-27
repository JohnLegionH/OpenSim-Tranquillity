/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using InWorldz.Phlox.VM;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>One event that waited in a script's queue.</summary>
    internal sealed class SavedEvent
    {
        public int EventIndex;
        public Value[] Args = Array.Empty<Value>();
    }

    /// <summary>
    /// What the engine saves of a script besides the runtime's own blob: the engine that owns the record, whether the script
    /// runs or was stopped by a fault (and why), its LSL state, the time left on its timer and on a sleep, the events that waited,
    /// and its listens. Times are seconds left, so a restart a day later gives the script the time it had left, not a time that
    /// has gone by.
    /// </summary>
    internal sealed class ScriptStateRecord
    {
        /// <summary>The layout of <see cref="Encode"/>; a record of another layout is not read.</summary>
        public const int FormatVersion = 1;

        /// <summary>The engine that wrote the record and owns it.</summary>
        public string Engine = "";

        public ScriptLanguage Language;

        /// <summary>The Running flag at the time of the save.</summary>
        public bool Running = true;

        /// <summary>The script was stopped by a fault or a kill and had not been reset or set running since.</summary>
        public bool Faulted;

        public int FaultKind;
        public string FaultMessage = "";

        /// <summary>The LSL state the script was in (0 for the first, and for an SLua script).</summary>
        public int LslState;

        /// <summary>The timer interval in force, in seconds (0 for none), and the time left before its next tick.</summary>
        public double TimerInterval;
        public double TimerLeft;

        /// <summary>The interval the runtime's LLTimers last asked for (SLua), which the host compares with after each step.</summary>
        public double SluaTimer;

        /// <summary>What was left of the script's sleep, in seconds (0 when it was not sleeping).</summary>
        public double SleepLeft;

        /// <summary>The floor llMinEventDelay set, and the time left before the next handler may start.</summary>
        public double MinEventDelay;
        public double NextEventIn;

        public List<SavedEvent> Events = new List<SavedEvent>();
        public List<ActiveListen> Listens = new List<ActiveListen>();

        /// <summary>The runtime's own state of the script (slua_serialize); empty for a script a fault stopped.</summary>
        public byte[] Native = Array.Empty<byte>();

        /// <summary>The record as bytes.</summary>
        public byte[] Encode() => Array.Empty<byte>();

        /// <summary>
        /// The record in these bytes, or null with the reason in <paramref name="problem"/>: bytes that are not a record, a record of
        /// another layout, one that was cut short, and one that another engine wrote.
        /// </summary>
        public static ScriptStateRecord Decode(byte[] data, string engine, out string problem)
        {
            problem = null;
            return null;
        }
    }
}
