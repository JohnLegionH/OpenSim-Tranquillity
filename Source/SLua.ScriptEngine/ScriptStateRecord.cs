/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using InWorldz.Phlox.VM;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>One event that waited in a script's queue.</summary>
    internal sealed class SavedEvent
    {
        public int EventIndex;
        public Value[] Args = Array.Empty<Value>();

        /// <summary>The detected objects of the event (a toucher, a sensed object, a collider); null for none.</summary>
        public DetectedRecord[] Detected;
    }

    /// <summary>One of the script's attributes the API keeps in its runtime state (a repeating sensor, a volume detect, taken controls) with its values.</summary>
    internal sealed class SavedMisc
    {
        public int Key;
        public object[] Values = Array.Empty<object>();
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

        /// <summary>The script's attributes the API keeps (layout 2).</summary>
        public List<SavedMisc> Misc = new List<SavedMisc>();

        /// <summary>The permission record the script holds (layout 2): who granted, what, the owner then, the Experience it came from, and whether it is a claim still waiting for its granter.</summary>
        public string PermsGranter;
        public int PermsMask;
        public string PermsOwner;
        public string PermsExperience;
        public bool PermsUnverified;

        public List<SavedEvent> Events = new List<SavedEvent>();
        public List<ActiveListen> Listens = new List<ActiveListen>();

        /// <summary>The runtime's own state of the script (slua_serialize); empty for a script a fault stopped.</summary>
        public byte[] Native = Array.Empty<byte>();

        private const string Mark = "SLSR";
        private const string Trailer = "END!";

        /// <summary>The record as bytes: the mark and layout, the engine, the fields in the order above, and a trailer that tells a record cut short.</summary>
        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Encoding.ASCII.GetBytes(Mark));
                w.Write(FormatVersion);
                w.Write(Engine ?? "");
                w.Write((int)Language);
                w.Write(Running);
                w.Write(Faulted);
                w.Write(FaultKind);
                w.Write(FaultMessage ?? "");
                w.Write(LslState);
                w.Write(TimerInterval);
                w.Write(TimerLeft);
                w.Write(SluaTimer);
                w.Write(SleepLeft);
                w.Write(MinEventDelay);
                w.Write(NextEventIn);
                w.Write(Events.Count);
                foreach (SavedEvent e in Events)
                {
                    w.Write(e.EventIndex);
                    WriteValues(w, e.Args);
                }
                w.Write(Listens.Count);
                foreach (ActiveListen l in Listens)
                {
                    w.Write(l.Handle);
                    w.Write(l.Channel);
                    WriteText(w, l.Name);
                    WriteText(w, l.Key);
                    WriteText(w, l.Message);
                    w.Write(l.Inactive);
                    w.Write(l.RegexBitfield);
                    WriteText(w, l.HostKey);
                }
                w.Write(Native?.Length ?? 0);
                if (Native != null)
                    w.Write(Native);
                w.Write(Encoding.ASCII.GetBytes(Trailer));
            }
            return stream.ToArray();
        }

        /// <summary>
        /// The record in these bytes, or null with the reason in <paramref name="problem"/>: bytes that are not a record, a record of
        /// another layout, one that was cut short, and one that another engine wrote.
        /// </summary>
        public static ScriptStateRecord Decode(byte[] data, string engine, out string problem) =>
            Inspect(data, engine, out ScriptStateRecord record, out problem) == RecordReading.Ok ? record : null;

        /// <summary>What reading saved bytes found.</summary>
        internal enum RecordReading
        {
            Ok,

            /// <summary>A record, but another engine wrote it: not this engine's to read or to throw away.</summary>
            OtherEngine,

            /// <summary>Not a record this engine can read: damaged, cut short, or of another layout.</summary>
            Unreadable,
        }

        /// <summary>Reads the bytes and says why they could not be read, telling a record of another engine from bytes that are no record at all.</summary>
        internal static RecordReading Inspect(byte[] data, string engine, out ScriptStateRecord record, out string problem)
        {
            record = null;
            problem = null;
            if (data == null || data.Length < Mark.Length + 4)
            {
                problem = "it is too short to be a saved script record";
                return RecordReading.Unreadable;
            }
            if (Encoding.ASCII.GetString(data, 0, Mark.Length) != Mark)
            {
                problem = "it is not a saved script record";
                return RecordReading.Unreadable;
            }
            int layout = BitConverter.ToInt32(data, Mark.Length);
            if (layout != FormatVersion)
            {
                problem = $"it has layout {layout}, this engine reads layout {FormatVersion}";
                return RecordReading.Unreadable;
            }
            try
            {
                using var r = new BinaryReader(new MemoryStream(data, Mark.Length + 4, data.Length - Mark.Length - 4), Encoding.UTF8);
                string owner = r.ReadString();
                if (owner != engine)
                {
                    problem = $"it was written by the engine '{owner}', this is '{engine}'";
                    return RecordReading.OtherEngine;
                }
                record = new ScriptStateRecord { Engine = owner };
                record.Language = (ScriptLanguage)r.ReadInt32();
                record.Running = r.ReadBoolean();
                record.Faulted = r.ReadBoolean();
                record.FaultKind = r.ReadInt32();
                record.FaultMessage = r.ReadString();
                record.LslState = r.ReadInt32();
                record.TimerInterval = r.ReadDouble();
                record.TimerLeft = r.ReadDouble();
                record.SluaTimer = r.ReadDouble();
                record.SleepLeft = r.ReadDouble();
                record.MinEventDelay = r.ReadDouble();
                record.NextEventIn = r.ReadDouble();
                int events = r.ReadInt32();
                CheckCount(events, r);
                for (int i = 0; i < events; i++)
                    record.Events.Add(new SavedEvent { EventIndex = r.ReadInt32(), Args = ReadValues(r) });
                int listens = r.ReadInt32();
                CheckCount(listens, r);
                for (int i = 0; i < listens; i++)
                {
                    record.Listens.Add(new ActiveListen
                    {
                        Handle = r.ReadInt32(),
                        Channel = r.ReadInt32(),
                        Name = ReadText(r),
                        Key = ReadText(r),
                        Message = ReadText(r),
                        Inactive = r.ReadBoolean(),
                        RegexBitfield = r.ReadInt32(),
                        HostKey = ReadText(r),
                    });
                }
                int native = r.ReadInt32();
                CheckCount(native, r);
                record.Native = r.ReadBytes(native);
                if (record.Native.Length != native)
                    throw new EndOfStreamException();
                byte[] trailer = r.ReadBytes(Trailer.Length);
                if (Encoding.ASCII.GetString(trailer) != Trailer)
                    throw new EndOfStreamException();
                return RecordReading.Ok;
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is ArgumentException || e is FormatException || e is OverflowException)
            {
                record = null;
                problem = "it was cut short or damaged (" + e.GetType().Name + ")";
                return RecordReading.Unreadable;
            }
        }

        /// <summary>A count read from the bytes cannot be more than the bytes left, which stops a damaged length from asking for memory.</summary>
        private static void CheckCount(int count, BinaryReader r)
        {
            if (count < 0 || count > r.BaseStream.Length - r.BaseStream.Position)
                throw new FormatException("a length is larger than the record");
        }

        private static void WriteText(BinaryWriter w, string text)
        {
            w.Write(text != null);
            if (text != null)
                w.Write(text);
        }

        private static string ReadText(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

        private static void WriteValues(BinaryWriter w, Value[] values)
        {
            w.Write(values?.Length ?? 0);
            if (values == null)
                return;
            foreach (Value v in values)
                WriteValue(w, v);
        }

        private static Value[] ReadValues(BinaryReader r)
        {
            int count = r.ReadInt32();
            CheckCount(count, r);
            if (count == 0)
                return Array.Empty<Value>();
            var values = new Value[count];
            for (int i = 0; i < count; i++)
                values[i] = ReadValue(r);
            return values;
        }

        private static void WriteValue(BinaryWriter w, Value v)
        {
            w.Write((byte)v.Type);
            switch (v.Type)
            {
                case Bindings.ValueType.Nil:
                    break;
                case Bindings.ValueType.Bool:
                case Bindings.ValueType.Int:
                    w.Write(v.I);
                    break;
                case Bindings.ValueType.Number:
                    w.Write(v.N);
                    break;
                case Bindings.ValueType.String:
                case Bindings.ValueType.Key:
                    WriteText(w, v.S);
                    break;
                case Bindings.ValueType.Vector:
                    w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
                    break;
                case Bindings.ValueType.Rotation:
                    w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W);
                    break;
                case Bindings.ValueType.List:
                    WriteValues(w, v.Items);
                    break;
                default:
                    throw new ArgumentException("a value of type " + v.Type + " cannot be saved");
            }
        }

        private static Value ReadValue(BinaryReader r)
        {
            var type = (Bindings.ValueType)r.ReadByte();
            switch (type)
            {
                case Bindings.ValueType.Nil: return Value.Nil;
                case Bindings.ValueType.Bool: return new Value(type, r.ReadInt64());
                case Bindings.ValueType.Int: return Value.Int(r.ReadInt64());
                case Bindings.ValueType.Number: return Value.Num(r.ReadDouble());
                case Bindings.ValueType.String: return new Value(type, 0, 0, ReadText(r));
                case Bindings.ValueType.Key: return Value.Key(ReadText(r));
                case Bindings.ValueType.Vector: return Value.Vec(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                case Bindings.ValueType.Rotation: return Value.Rot(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                case Bindings.ValueType.List: return Value.List(ReadValues(r));
                default: throw new FormatException("an unknown value type " + (int)type);
            }
        }
    }
}
