/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Tests
{
    /// <summary>
    /// The version of the server-side bake the sending region applied to an agent travels with the agent's data to
    /// the next region, so the region it arrives in can describe the carried bakes from its first appearance
    /// message. The field is optional: it is written only when there is a bake, and agent data without it (from a
    /// simulator that does not send it) means no bake.
    /// </summary>
    // Pure packing and unpacking; no process-wide state.
    public class AgentDataBakedCofVersionTests
    {
        private static AgentData NewData() => new AgentData
        {
            AgentID = UUID.Random(),
            RegionID = UUID.Random(),
            SessionID = UUID.Random(),
        };

        private static AgentData RoundTrip(OSDMap packed)
        {
            var back = new AgentData();
            back.Unpack(packed, null, new EntityTransferContext());
            return back;
        }

        [Fact]
        public void ANewAgentDataHasNoBake()
        {
            Assert.Equal(-1, new AgentData().BakedCofVersion);
        }

        [Fact]
        public void AVersionPacksAndUnpacks()
        {
            var data = NewData();
            data.BakedCofVersion = 7;

            Assert.Equal(7, RoundTrip(data.Pack(new EntityTransferContext())).BakedCofVersion);
        }

        [Fact]
        public void AVersionOfZeroSurvivesBecauseZeroIsAVersion()
        {
            var data = NewData();
            data.BakedCofVersion = 0;

            Assert.Equal(0, RoundTrip(data.Pack(new EntityTransferContext())).BakedCofVersion);
        }

        [Fact]
        public void NoBakeWritesNothingAndReadsBackAsNoBake()
        {
            var data = NewData();

            var packed = data.Pack(new EntityTransferContext());

            Assert.False(packed.ContainsKey("baked_cof_version"));
            Assert.Equal(-1, RoundTrip(packed).BakedCofVersion);
        }

        [Fact]
        public void AgentDataFromASimulatorThatDoesNotSendTheFieldMeansNoBake()
        {
            var data = NewData();
            data.BakedCofVersion = 5;
            var packed = data.Pack(new EntityTransferContext());
            packed.Remove("baked_cof_version");

            Assert.Equal(-1, RoundTrip(packed).BakedCofVersion);
        }
    }
}
