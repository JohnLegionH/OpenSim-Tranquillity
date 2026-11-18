/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    /// <summary>
    /// How much of joltc's temp allocator one physics step needs, and the contact capacity that keeps a step inside it.
    ///
    /// joltc's JPH_Init makes one process-wide TempAllocatorImplWithMallocFallback with an 8 MB stack (joltc.cpp,
    /// JPH_Init; the stock JoltPhysics.Native 1.0.4 win-x64 build passes 0x800000). The stack is allocated once, at
    /// start. A step's scratch memory that does not fit the stack is taken from malloc on every step, and Jolt does not
    /// check that block: ContactConstraintManager::PrepareConstraintBuffer keeps whatever the allocator returns as
    /// mConstraints, and TemplatedAddContactConstraint writes the step's contacts into it (Jolt
    /// ContactConstraintManager.cpp). When the process cannot commit more memory, malloc returns null and the first
    /// contact of the step is an access violation in joltc.
    ///
    /// The contact constraint buffer is most of a step's scratch memory: MaxContactConstraints x sizeof(ContactConstraint)
    /// (864 bytes in this build, the stride of mConstraints), allocated whole every step whether or not the contacts
    /// come. At Jolt's and this module's default of 16384 that is 14.2 MB, so every step of every region goes to
    /// malloc. Kept under the stack, a step allocates nothing.
    /// </summary>
    public static class JoltTempStack
    {
        /// <summary>The temp allocator's stack in joltc's JPH_Init (8 MB).</summary>
        public const long StackBytes = 8L * 1024 * 1024;

        /// <summary>sizeof(JPH::ContactConstraint) in the joltc build: the stride of mConstraints.</summary>
        public const int ContactConstraintBytes = 864;

        /// <summary>IslandBuilder::PrepareContactConstraints: one uint32 link per possible contact constraint.</summary>
        public const int ContactLinkBytes = 4;

        /// <summary>PhysicsSystem::Update's body pair buffer: PhysicsSettings.mMaxInFlightBodyPairs (16384, the module
        /// does not change it) x sizeof(BodyPair) (8).</summary>
        public const long BodyPairBytes = 16384L * 8;

        /// <summary>Kept free for the step's other scratch arrays, which grow with the active bodies and constraints
        /// (island builder, large island splitter, CCD): about 20 bytes per active body, so 1 MB covers some
        /// fifty thousand.</summary>
        public const long OtherScratchBytes = 1L * 1024 * 1024;

        /// <summary>The temp memory one step asks for at this contact capacity, before the per-active-body arrays.</summary>
        public static long StepBytes(int maxContactConstraints)
            => (long)maxContactConstraints * (ContactConstraintBytes + ContactLinkBytes) + BodyPairBytes;

        /// <summary>The largest contact capacity whose step fits the stack with <see cref="OtherScratchBytes"/> to spare.</summary>
        public static int MaxContactConstraintsThatFit
            => (int)((StackBytes - BodyPairBytes - OtherScratchBytes) / (ContactConstraintBytes + ContactLinkBytes));

        /// <summary>True when a step at this capacity fits the stack with room for the other scratch arrays.</summary>
        public static bool Fits(int maxContactConstraints) => maxContactConstraints <= MaxContactConstraintsThatFit;
    }
}
