/*
 * Legion Grid — Vehicle Dynamics Port from InWorldz Halcyon
 * Original Copyright (c) 2015, InWorldz Halcyon Developers
 * Adapted for BulletSim physics engine, April 2026.
 * Extracted VERBATIM into the backend-agnostic Legion.Vehicles assembly (M8) from
 * OpenSim/Region/PhysicsModules/BulletS/LegionVehicleProperties.cs - only the namespace
 * and visibility (internal -> public, for cross-assembly hosts) changed.
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace Legion.Vehicles
{
    // =====================================================================
    // Float parameter keys — maps to Halcyon FloatParams enum.
    // We use our own enum rather than the OpenSim Vehicle enum because
    // the Halcyon motor model needs per-axis vector timescales and
    // additional parameters not in standard LSL.
    // =====================================================================
    public enum VehFloatParam
    {
        AngularDeflectionEfficiency,
        AngularDeflectionTimescale,
        BankingEfficiency,
        BankingMix,
        BankingTimescale,
        Buoyancy,
        HoverEfficiency,
        HoverHeight,
        HoverTimescale,
        LinearDeflectionEfficiency,
        LinearDeflectionTimescale,
        VerticalAttractionEfficiency,
        VerticalAttractionTimescale,
        // InWorldz extensions (stubbed for future use)
        InvertedBankingModifier,
        MouselookAltitude,
        MouselookAzimuth,
        BankingAzimuth,
        DisableMotorsAbove,
        DisableMotorsAfter,
    }

    // =====================================================================
    // Vector parameter keys — the Halcyon motor model uses per-axis vectors
    // for timescales, which gives much better vehicle behavior than BSDynamics'
    // scalar timescales.
    // =====================================================================
    public enum VehVectorParam
    {
        LinearFrictionTimescale,
        AngularFrictionTimescale,
        LinearMotorDirection,
        AngularMotorDirection,
        LinearMotorOffset,
        LinearMotorTimescale,
        AngularMotorTimescale,
        LinearMotorDecayTimescale,
        AngularMotorDecayTimescale,
        // InWorldz extensions (stubbed)
        LinearWindEfficiency,
        AngularWindEfficiency,
    }

    // =====================================================================
    // Rotation parameter keys
    // =====================================================================
    public enum VehRotationParam
    {
        ReferenceFrame,
    }

    // =====================================================================
    // Vehicle type enum matching Halcyon types.
    // Standard LSL types map directly to Vehicle.TYPE_* values.
    // =====================================================================
    public enum LegionVehicleType
    {
        None = 0,
        Sled = 1,
        Car = 2,
        Boat = 3,
        Airplane = 4,
        Balloon = 5,
        // InWorldz extensions — kept for completeness but not settable via standard LSL
        Motorcycle = 6,
        Sailboat = 7,
    }

    // =====================================================================
    // Vehicle flags — superset of OpenSim VehicleFlag plus Halcyon extensions.
    // Standard flags use the same bit values as OpenSim VehicleFlag.
    // =====================================================================
    [Flags]
    public enum LegionVehicleFlags
    {
        None                = 0,
        NoDeflectionUp      = 1,
        LimitRollOnly       = 2,
        HoverWaterOnly      = 4,
        HoverTerrainOnly    = 8,
        HoverGlobalHeight   = 16,
        HoverUpOnly         = 32,
        LimitMotorUp        = 64,
        MouselookSteer      = 128,
        MouselookBank       = 256,
        CameraDecoupled     = 512,
        NoX                 = 1024,
        NoY                 = 2048,
        NoZ                 = 4096,
        LockHoverHeight     = 8192,
        NoDeflection        = 16384,
        LockRotation        = 32768,
        // Halcyon extensions
        LimitMotorDown      = 1 << 16,
        TorqueWorldZ        = 1 << 17,
        MousePointSteer     = 1 << 18,
        MousePointBank      = 1 << 19,
        ReactToWind         = 1 << 20,
        ReactToCurrents     = 1 << 21,
    }

    /// <summary>
    /// Properties that define a vehicle's behavior. Ported from Halcyon VehicleProperties.
    /// Uses dictionaries keyed by enum for flexibility, matching the Halcyon pattern.
    /// </summary>
    public class LegionVehicleProperties
    {
        public LegionVehicleType Type;
        public LegionVehicleFlags Flags;

        public Dictionary<VehFloatParam, float> ParamsFloat;
        public Dictionary<VehVectorParam, Vector3> ParamsVec;
        public Dictionary<VehRotationParam, Quaternion> ParamsRot;

        public LegionVehicleData Dynamics;

        public LegionVehicleProperties()
        {
            Type = LegionVehicleType.None;
            Flags = LegionVehicleFlags.None;
            ParamsFloat = new Dictionary<VehFloatParam, float>();
            ParamsVec = new Dictionary<VehVectorParam, Vector3>();
            ParamsRot = new Dictionary<VehRotationParam, Quaternion>();
            Dynamics = new LegionVehicleData();
        }

        /// <summary>
        /// Merge incoming properties (used for region crossing state transfer).
        /// </summary>
        public void Merge(LegionVehicleProperties other)
        {
            Type = other.Type;
            Flags = other.Flags;

            foreach (var item in other.ParamsFloat)
                ParamsFloat[item.Key] = item.Value;
            foreach (var item in other.ParamsVec)
                ParamsVec[item.Key] = item.Value;
            foreach (var item in other.ParamsRot)
                ParamsRot[item.Key] = item.Value;

            Dynamics = other.Dynamics;
        }

        // =================================================================
        // Helper to safely read a float param with a default
        // =================================================================
        public float GetFloat(VehFloatParam key, float defaultVal = 0f)
        {
            return ParamsFloat.TryGetValue(key, out float val) ? val : defaultVal;
        }

        public Vector3 GetVec(VehVectorParam key)
        {
            return ParamsVec.TryGetValue(key, out Vector3 val) ? val : Vector3.Zero;
        }

        public Quaternion GetRot(VehRotationParam key)
        {
            return ParamsRot.TryGetValue(key, out Quaternion val) ? val : Quaternion.Identity;
        }
    }
}
