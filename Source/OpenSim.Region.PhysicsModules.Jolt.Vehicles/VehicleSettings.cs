/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>Which values llSetVehicleType gives a vehicle type.</summary>
    public enum VehiclePresetSet
    {
        /// <summary>Second Life's documented defaults, from the wiki page of each type (VEHICLE_TYPE_CAR and the rest).</summary>
        Documented,

        /// <summary>The InWorldz Halcyon values the module used before the documented set existed.</summary>
        Legacy,
    }

    /// <summary>
    /// The per-region settings every vehicle controller in a region shares: the preset set and the limits a script's
    /// vehicle parameters are held to. The host builds one from its configuration when the region starts. Each default
    /// is the value the module had as a constant (VehicleLimits), except the motor decay cap, which is Second Life's
    /// documented 120 s, and the preset set, which is the documented one.
    /// </summary>
    public sealed class VehicleSettings
    {
        /// <summary>The settings a controller has when the host gives it none.</summary>
        public static readonly VehicleSettings Default = new VehicleSettings();

        public VehiclePresetSet Presets { get; init; } = VehiclePresetSet.Documented;

        /// <summary>Largest linear motor speed a script can set (m/s, each axis), and the cap on the motor's result.</summary>
        public float MaxLinearSpeed { get; init; } = VehicleLimits.MaxLinearVelocity;

        /// <summary>The forward speed (m/s) at which dynamic banking and angular deflection reach full strength; their
        /// share is speed / this, at most 1.</summary>
        public float ReferenceSpeed { get; init; } = VehicleLimits.MaxLegacyLinearVelocity;

        /// <summary>Largest angular motor speed a script can set (rad/s, each axis), and the cap on the angular result.</summary>
        public float MaxAngularSpeed { get; init; } = VehicleLimits.MaxAngularVelocity;

        /// <summary>Shortest timescale a script can set (s).</summary>
        public float MinTimescale { get; init; } = VehicleLimits.MinPhysicsTimestep;

        /// <summary>Longest friction, motor or deflection timescale a script can set (s). A timescale of 1000 s or more
        /// stays "off" for friction and deflection, so with a cap below 1000 a script cannot turn them off.</summary>
        public float MaxTimescale { get; init; } = VehicleLimits.MaxTimescale;

        /// <summary>Longest motor decay timescale a script can set (s): Second Life documents 120.</summary>
        public float MaxDecayTimescale { get; init; } = VehicleLimits.MaxDecayTimescale;

        /// <summary>A hover timescale at or above this turns hover off (s); also the longest a script can set.</summary>
        public float MaxHoverTimescale { get; init; } = VehicleLimits.MaxHoverTimescale;

        /// <summary>A vertical attraction or banking timescale at or above this turns them off (s); also the longest a
        /// script can set for the attractor.</summary>
        public float MaxAttractTimescale { get; init; } = VehicleLimits.MaxAttractTimescale;

        /// <summary>Largest linear motor offset a script can set (m, each axis).</summary>
        public float MaxMotorOffset { get; init; } = VehicleLimits.MaxLinearOffset;

        /// <summary>The range a script's hover height is held to (m).</summary>
        public float MinHoverHeight { get; init; } = VehicleLimits.MinRegionHeight;
        public float MaxHoverHeight { get; init; } = VehicleLimits.MaxRegionHeight;
    }
}
