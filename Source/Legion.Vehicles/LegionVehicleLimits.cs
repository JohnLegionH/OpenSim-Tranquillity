/*
 * Legion Grid — Vehicle Dynamics Port from InWorldz Halcyon
 * Original Copyright (c) 2015, InWorldz Halcyon Developers
 * Adapted for BulletSim physics engine, April 2026.
 * Extracted VERBATIM into the backend-agnostic Legion.Vehicles assembly (M8) from
 * OpenSim/Region/PhysicsModules/BulletS/LegionVehicleLimits.cs - only the namespace
 * and visibility (internal -> public) changed.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *   * Redistributions of source code must retain the above copyright notice
 *   * Redistributions in binary form must reproduce the above copyright notice
 *   * Neither the name of halcyon nor the names of its contributors may be used
 *     to endorse or promote products derived from this software
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using System;

namespace Legion.Vehicles
{
    /// <summary>
    /// Internal limits to various vehicle parameters.
    /// Ported from Halcyon VehicleLimits. These constants were tuned for PhysX
    /// and may need adjustment for BulletSim.
    /// </summary>
    public static class LegionVehicleLimits
    {
        // The interaction between these limits are nonintuitive. Test all cases before settling on any changes.

        public const float MinPhysicsTimestep     = 0.0156f;
        public const float MinPhysicsForce        = 0.001f;
        public const int   NumTimeStepsPerRayCast  = 8;
        public const int   NumTimeStepsPerWindChk  = 8;

        // Threshold values below which activity is considered ended or effectively zero.
        // Most of these values are tuned to be above the squirm point.
        public const float ThresholdLinearMotorDeltaV    = 0.005f;
        public const float ThresholdAngularMotorDeltaV   = (float)(Math.PI / 512.0);
        public const float ThresholdLinearFrictionDeltaV  = 0.002f;
        public const float ThresholdAngularFrictionDeltaV = 0.004f;
        public const float ThresholdDeflectionSpeed      = 0.2f;
        public const float ThresholdDeflectionAngle      = (float)(Math.PI / 64.0);
        public const float ThresholdStictionFactor       = 0.002f;
        public const float ThresholdAngularMotorEngaged  = 0.9f;
        public const float ThresholdLinearMotorEngaged   = 1.0f;
        public const float ThresholdLinearMotorUnstuck   = 0.025f;
        public const float ThresholdAttractorAngle       = (float)(Math.PI / 256.0);
        public const float ThresholdOverturnAngle        = (float)(Math.PI * 0.60);
        public const float ThresholdHoverDragHeight      = 0.3f;
        public const float ThresholdDelayFubar           = 0.06f;
        public const float ThresholdBankAngle            = (float)(Math.PI / 128.0);
        public const float ThresholdMouselookAngle       = (float)(Math.PI / 32.0);
        public const float ThresholdInverseCrossover     = -2.010203f;

        // Maximum values for various forces and time scales.
        public const float MaxLegacyLinearVelocity   = 30.0f;
        public const float MaxLegacyAngularVelocity  = (float)Math.PI;
        public const float MaxLinearVelocity         = 200.0f;
        public const float MaxLinearOffset           = 100.0f;
        public const float MaxAngularVelocity        = (float)(Math.PI * 4.0);
        public const float MaxDecayTimescale         = 120.0f;
        public const float MaxAttractTimescale       = 500.0f;
        public const float MaxHoverTimescale         = 300.0f;
        public const float MaxTimescale              = 1000.0f;
        public const float MaxBankingSpeed           = 10.0f;
        public const float MaxGroundPenetration      = -0.5f;
        public const float MinRegionHeight           = -128.0f;
        public const float MaxRegionHeight           = 10000.0f;
        public const float MaxAttractDormancy        = 1.5f;

        // Enablement switches, useful for debugging specific features.
        // All should be true in production.
        public static bool DoAngularDeflection   = true;
        public static bool DoLinearDeflection    = true;
        public static bool DoMotors              = true;
        public static bool DoAngularFriction     = true;
        public static bool DoLinearFriction      = true;
        public static bool DoVerticalAttractor   = true;
        public static bool DoBanking             = true;
        public static bool DoSpikeDetection      = true;

        // Debugging switches — all should be false in production.
        public static bool DebugPrintParams      = false;
        public static bool DebugTimestep         = false;
        public static bool DebugSpikeDetection   = false;
        public static bool DebugVehicleChange    = false;
        public static bool DebugAngular          = false;
        public static bool DebugBanking          = false;
        public static bool DebugBlendedZ         = false;
        public static bool DebugLinear           = false;
        public static bool DebugAttractor        = false;
        public static bool DebugRayCast          = false;
        public static bool DebugWind             = false;
        public static bool DebugAngularFriction  = false;
        public static bool DebugLinearFriction   = false;
        public static bool DebugAngularMotor     = false;
        public static bool DebugLinearMotor      = false;
        public static bool DebugDeflection       = false;
        public static bool DebugRegionChange     = false;
    }

    /// <summary>
    /// Utility math for vehicle dynamics.
    /// </summary>
    public static class VehicleMath
    {
        /// <summary>
        /// Returns -1 if negative, +1 if zero or positive.
        /// </summary>
        public static int PosNeg(float f)
        {
            int sign = Math.Sign(f);
            if (sign == 0) return 1;
            return sign;
        }
    }
}
