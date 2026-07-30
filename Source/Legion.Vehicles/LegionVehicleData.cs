/*
 * Legion Grid — Vehicle Dynamics Port from InWorldz Halcyon
 * Original Copyright (c) 2015, InWorldz Halcyon Developers
 * Adapted for BulletSim physics engine, April 2026.
 * Extracted VERBATIM into the backend-agnostic Legion.Vehicles assembly (M8) from
 * OpenSim/Region/PhysicsModules/BulletS/LegionVehicleData.cs - only the namespace
 * and visibility (internal -> public) changed.
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using System;
using OpenMetaverse;

namespace Legion.Vehicles
{
    /// <summary>
    /// Internal dynamics values used by the vehicle simulator.
    /// These are runtime state values that feed forward between simulation frames.
    /// Ported from Halcyon DynamicsSimulationData (protobuf removed).
    /// </summary>
    public class LegionVehicleData
    {
        public Vector3 LocalLinearVelocity;
        public Vector3 LocalAngularVelocity;

        // Motor dynamics (in local coordinates)
        public Vector3 LinearTargetVelocity;
        public float LinearDecayIndex;
        public Vector3 AngularTargetVelocity;
        public float AngularDecayIndex;

        public DateTime LastAccessTOD;

        public Vector3 AngularDirection;
        public Vector3 LinearDirection;

        public Vector3 LastPosition;
        public Vector3 ShortTermPositionDelta;

        public float LastVerticalAngle;
        public float VerticalForceAdjust;

        public Vector3 TargetAngularDelta;
        public Vector3 TargetLinearDelta;

        public float Timestep;

        public float BankingDirection;
        public float BankingTargetVelocity;

        public uint LastVerticalFrameNumber;

        public Vector3 WindDirection;
        public Vector3 WaterDirection;

        public LegionVehicleData()
        {
            LocalLinearVelocity = Vector3.Zero;
            LocalAngularVelocity = Vector3.Zero;
            LinearTargetVelocity = Vector3.Zero;
            LinearDecayIndex = 0f;
            AngularTargetVelocity = Vector3.Zero;
            AngularDecayIndex = 0f;
            LastAccessTOD = DateTime.Now;
            AngularDirection = Vector3.Zero;
            LinearDirection = Vector3.Zero;
            LastPosition = Vector3.Zero;
            ShortTermPositionDelta = Vector3.Zero;
            LastVerticalAngle = 0f;
            VerticalForceAdjust = 1.0f;
            TargetAngularDelta = Vector3.Zero;
            TargetLinearDelta = Vector3.Zero;
            Timestep = LegionVehicleLimits.MinPhysicsTimestep;
            BankingDirection = 0f;
            BankingTargetVelocity = 0f;
            LastVerticalFrameNumber = 0;
            WindDirection = Vector3.Zero;
            WaterDirection = Vector3.Zero;
        }

        /// <summary>
        /// Reset all dynamics state to defaults. Called on vehicle type change or motor reset.
        /// </summary>
        public void Reset()
        {
            LocalLinearVelocity = Vector3.Zero;
            LocalAngularVelocity = Vector3.Zero;
            LinearTargetVelocity = Vector3.Zero;
            LinearDecayIndex = 0f;
            AngularTargetVelocity = Vector3.Zero;
            AngularDecayIndex = 0f;
            LastAccessTOD = DateTime.Now;
            AngularDirection = Vector3.Zero;
            LinearDirection = Vector3.Zero;
            ShortTermPositionDelta = Vector3.Zero;
            LastVerticalAngle = 0f;
            VerticalForceAdjust = 1.0f;
            TargetAngularDelta = Vector3.Zero;
            TargetLinearDelta = Vector3.Zero;
            Timestep = LegionVehicleLimits.MinPhysicsTimestep;
            BankingDirection = 0f;
            BankingTargetVelocity = 0f;
            LastVerticalFrameNumber = 0;
            WindDirection = Vector3.Zero;
            WaterDirection = Vector3.Zero;
        }
    }
}
