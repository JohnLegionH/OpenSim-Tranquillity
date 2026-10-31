/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Serialization/SerializedPostedEvent.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    [ProtoContract]
    public class SerializedPostedEvent
    {
        [ProtoMember(1, IsRequired=true)]
        public Types.SupportedEventList.Events EventType;

        [ProtoMember(2)]
        public SerializedLSLPrimitive[] Args;

        [ProtoMember(3)]
        public VM.DetectVariables[] DetectVars;

        [ProtoMember(4)]
        public int TransitionToState;

        /// <summary>The game-controller state a game_control event reports; null for every other event and for rows saved before it existed.</summary>
        public SerializedGameControl GameControl;


        public SerializedPostedEvent()
        {
        }

        /// <summary>
        /// Creates a new serialized event from a posted event
        /// </summary>
        /// <param name="evt"></param>
        /// <returns></returns>
        public static SerializedPostedEvent FromPostedEvent(VM.PostedEvent evt)
        {
            if (evt == null) return null;

            SerializedPostedEvent serEvent = new SerializedPostedEvent();
            serEvent.EventType = evt.EventType;

            serEvent.Args = SerializedLSLPrimitive.FromPrimitiveList(evt.Args);

            serEvent.DetectVars = evt.DetectVars;

            serEvent.TransitionToState = evt.TransitionToState;

            return serEvent;
        }

        internal VM.PostedEvent ToPostedEvent()
        {
            VM.PostedEvent evt = new VM.PostedEvent();
            evt.EventType = this.EventType;

            evt.Args = SerializedLSLPrimitive.ToPrimitiveList(this.Args);

            evt.DetectVars = this.DetectVars;

            evt.TransitionToState = this.TransitionToState;

            return evt;
        }
    }

    /// <summary>
    /// A <see cref="OpenSim.Framework.GameControlSnapshot"/> as it is saved with a queued game_control event.
    /// </summary>
    [ProtoContract]
    public class SerializedGameControl
    {
        [ProtoMember(1)]
        public uint RawButtons;

        [ProtoMember(2)]
        public float[] RawAxes;

        [ProtoMember(3)]
        public int Mode;

        [ProtoMember(4)]
        public uint SemanticButtons;

        [ProtoMember(5)]
        public float[] SemanticAxes;

        public static SerializedGameControl From(OpenSim.Framework.GameControlSnapshot s)
        {
            if (s == null) return null;
            return new SerializedGameControl
            {
                RawButtons = s.RawButtons, RawAxes = s.RawAxes, Mode = s.Mode,
                SemanticButtons = s.SemanticButtons, SemanticAxes = s.SemanticAxes
            };
        }

        /// <summary>The state again, checked as a new one is; null if what was saved does not pass.</summary>
        public OpenSim.Framework.GameControlSnapshot ToSnapshot()
        {
            return OpenSim.Framework.GameControlSnapshot.TryCreate(RawButtons, RawAxes ?? Array.Empty<float>(), Mode,
                SemanticButtons, SemanticAxes ?? Array.Empty<float>(), out var snapshot) ? snapshot : null;
        }
    }
}
