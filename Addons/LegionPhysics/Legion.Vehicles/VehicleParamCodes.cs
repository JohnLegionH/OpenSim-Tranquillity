/*
 * Legion Grid - backend-agnostic vehicle controller (M8).
 *
 * The LSL vehicle parameter wire codes, copied from OpenSim's
 * OpenSim.Region.PhysicsModules.SharedBase.VehicleConstants so this assembly does not link
 * OpenSim. The values are the Second Life protocol constants and are frozen; keeping the enum
 * NAME "Vehicle" lets the extracted controller switches stay byte-identical to the BulletSim
 * reference (LegionVehicleDynamics.cs). Hosts cast their int params to this enum.
 */

namespace Legion.Vehicles
{
    public enum Vehicle : int
    {
        TYPE_NONE = 0,
        TYPE_SLED = 1,
        TYPE_CAR = 2,
        TYPE_BOAT = 3,
        TYPE_AIRPLANE = 4,
        TYPE_BALLOON = 5,
        LINEAR_FRICTION_TIMESCALE = 16,
        ANGULAR_FRICTION_TIMESCALE = 17,
        LINEAR_MOTOR_DIRECTION = 18,
        ANGULAR_MOTOR_DIRECTION = 19,
        LINEAR_MOTOR_OFFSET = 20,
        HOVER_HEIGHT = 24,
        HOVER_EFFICIENCY = 25,
        HOVER_TIMESCALE = 26,
        BUOYANCY = 27,
        LINEAR_DEFLECTION_EFFICIENCY = 28,
        LINEAR_DEFLECTION_TIMESCALE = 29,
        LINEAR_MOTOR_TIMESCALE = 30,
        LINEAR_MOTOR_DECAY_TIMESCALE = 31,
        ANGULAR_DEFLECTION_EFFICIENCY = 32,
        ANGULAR_DEFLECTION_TIMESCALE = 33,
        ANGULAR_MOTOR_TIMESCALE = 34,
        ANGULAR_MOTOR_DECAY_TIMESCALE = 35,
        VERTICAL_ATTRACTION_EFFICIENCY = 36,
        VERTICAL_ATTRACTION_TIMESCALE = 37,
        BANKING_EFFICIENCY = 38,
        BANKING_MIX = 39,
        BANKING_TIMESCALE = 40,
        REFERENCE_FRAME = 44,
        BLOCK_EXIT = 45,
        ROLL_FRAME = 46
    }
}
