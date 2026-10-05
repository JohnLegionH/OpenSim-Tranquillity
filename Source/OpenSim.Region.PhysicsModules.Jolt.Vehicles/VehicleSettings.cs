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
    /// The per-region settings every vehicle controller in a region shares. The host builds one from its configuration
    /// when the region starts.
    /// </summary>
    public sealed class VehicleSettings
    {
        /// <summary>The settings a controller has when the host gives it none.</summary>
        public static readonly VehicleSettings Default = new VehicleSettings();

        public VehiclePresetSet Presets { get; init; } = VehiclePresetSet.Documented;
    }
}
