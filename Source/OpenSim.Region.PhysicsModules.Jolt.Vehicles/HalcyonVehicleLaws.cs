/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * The stepwise motor ramp and the angular deflection ported from InWorldz Halcyon
 * (InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs: MovementExpDecay, GetGrowthRate, SimulateAngularDeflection;
 * InWorldz.PhysxPhysics/PhysUtil.cs: AngleBetween, RotBetween, Rot2Euler).
 * Adapted 2026 by Legion Builds
 */

using System;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// The laws of the InWorldz Halcyon vehicle code that the controller uses under
    /// <see cref="VehicleBehaviour.InWorldz"/> in place of Second Life's documented ones: the angular motor's stepwise
    /// ramp and the angular deflection push. Each is a per-step formula, so its result depends on the step the engine
    /// takes, as Halcyon's did on its own (a step that was never longer than its shortest timescale; see
    /// <see cref="GrowthRate"/>). Halcyon was read, not run.
    /// </summary>
    public static class HalcyonVehicleLaws
    {
        /// <summary>
        /// The share of a motor's effect left when its decay clock reads <paramref name="timeIndex"/> seconds
        /// (VehicleDynamics.cs:2201-2212): 1 while the clock is at or below zero, e^(-t / T) after, and 0 once that falls
        /// under 0.002 ("In theory, decay is infinitely long. In practice, there is stiction"). An infinite clock is 0.
        /// </summary>
        public static float MovementExpDecay(float timeIndex, float timescale)
        {
            if (timeIndex <= 0)
                return 1.0f;
            float factor = (float)Math.Pow(Math.E, (double)(-timeIndex / timescale));
            if (factor < VehicleLimits.ThresholdStictionFactor)
                factor = 0.0f;
            return factor;
        }

        /// <summary>
        /// The growth rate of a motor's velocity over one step, "an exponential fit between two velocity points in TAU
        /// time" (VehicleDynamics.cs:2236-2288): from velocity <paramref name="startVelocity"/> toward
        /// <paramref name="endVelocity"/> (the motor's direction) with <paramref name="stepsPerTimescale"/> steps in the
        /// motor timescale, the velocity after the step is start (1 + rate). Same signs: ln(|end| / |start|) / steps. From
        /// zero: ln(1 + |end|) / steps. Toward zero: -ln(1 + |start|) / steps. Opposite signs: -ln(1 + |start - end|) / steps
        /// while |start| is over 0.3, and below that the sentinel <see cref="VehicleLimits.ThresholdInverseCrossover"/>
        /// that makes the velocity cross over to growing the other way.
        /// </summary>
        public static float GrowthRate(float startVelocity, float endVelocity, float stepsPerTimescale)
        {
            float svel = startVelocity, evel = endVelocity;
            float elog;
            if (svel * evel > 0)
            {
                evel = Math.Abs(evel);
                svel = Math.Abs(svel);
                elog = (float)Math.Log(evel / svel);
            }
            else
            {
                if (evel == 0 && svel == 0)
                    return 0;
                if (evel == 0)
                {
                    elog = -(float)Math.Log(1.0 + Math.Abs(svel));
                }
                else if (svel == 0)
                {
                    elog = (float)Math.Log(1.0 + Math.Abs(evel));
                }
                else
                {
                    if (Math.Abs(svel) > 0.3f)
                        elog = -(float)Math.Log(1.0f + Math.Abs(svel - evel));
                    else
                        return VehicleLimits.ThresholdInverseCrossover;
                }
            }
            return elog / stepsPerTimescale;
        }

        /// <summary>
        /// The angular deflection push of one step (VehicleDynamics.cs:1114-1169, up to the stall cut-off and the
        /// threshold, which the caller applies): a change of angular velocity
        ///
        ///   speedShare x pi x (h / T) x efficiency x ln(1 + angle) along the direction of the Euler angles of the rotation
        ///   from the nose to the velocity, less a damping term, the angular velocity x (h / T) x efficiency x
        ///   ln(1 + pi - angle) / ln(1 + pi), within pi / 64 of aligned,
        ///
        /// with speedShare = the vehicle's speed (at most the reference speed) over the reference speed, T the timescale
        /// held to at least the step, and the angle the rotation's angle (turned to pi minus it when the vehicle moves
        /// backwards along its nose). The direction is Halcyon's: the normalised Euler angles of the rotation, not its axis.
        /// Zero below the deflection speed (a component of the world velocity of 0.2 m/s), and at a timescale of 1000 s
        /// or more (off).
        /// </summary>
        public static Vector3 AngularDeflectionPush(Quaternion rotation, Vector3 worldLinearVelocity, Vector3 localLinearVelocity,
            Vector3 worldAngularVelocity, float deflectionTimescale, float efficiency, float step, float referenceSpeed)
        {
            if (Math.Abs(worldLinearVelocity.X) < VehicleLimits.ThresholdDeflectionSpeed &&
                Math.Abs(worldLinearVelocity.Y) < VehicleLimits.ThresholdDeflectionSpeed &&
                Math.Abs(worldLinearVelocity.Z) < VehicleLimits.ThresholdDeflectionSpeed)
                return Vector3.Zero;

            float timescale = Math.Max(deflectionTimescale, step);
            if (timescale >= VehicleLimits.MaxTimescale)
                return Vector3.Zero;

            float timepct = step / timescale;
            float speed = Utils.Clamp(Vector3.Mag(localLinearVelocity), 0, referenceSpeed);
            float speedpct = speed / referenceSpeed;

            // The rotation between the nose and the direction of motion.
            Vector3 ahead = new Vector3(1, 0, 0) * rotation;
            Quaternion tween = RotBetween(ahead, Vector3.Normalize(worldLinearVelocity));
            float angle = AngleBetween(tween, Quaternion.Identity);
            Vector3 vtwix = Rot2Euler(tween);

            // "Cheat: if the local X movement is negative, flip the angle."
            if (localLinearVelocity.X < 0)
                angle = (float)Math.PI - angle;

            vtwix = Vector3.Normalize(vtwix) * speedpct * (float)Math.PI * timepct * efficiency * (float)Math.Log(1.0 + angle);

            // Damping: "Most damping occurs closest to the target angle but releases at the sweet spot."
            Vector3 remvel = Vector3.Zero;
            if (angle < VehicleLimits.ThresholdDeflectionAngle)
                remvel = worldAngularVelocity * timepct * efficiency * (float)(Math.Log(1.0 + Math.PI - angle) / Math.Log(1.0 + Math.PI));

            return vtwix - remvel;
        }

        /// <summary>The angle between two rotations (PhysUtil.cs:151-163).</summary>
        public static float AngleBetween(Quaternion a, Quaternion b)
        {
            double aa = (a.X * a.X + a.Y * a.Y + a.Z * a.Z + a.W * a.W);
            double bb = (b.X * b.X + b.Y * b.Y + b.Z * b.Z + b.W * b.W);
            double aabb = aa * bb;
            if (aabb == 0) return 0.0f;

            double ab = (a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            double quotient = (ab * ab) / aabb;
            if (quotient >= 1.0) return 0.0f;

            return (float)Math.Acos(2 * quotient - 1);
        }

        /// <summary>The rotation from one direction to another (PhysUtil.cs:165-186); the identity where it is undefined.</summary>
        public static Quaternion RotBetween(Vector3 a, Vector3 b)
        {
            double dotProduct = Vector3.Dot(a, b);
            Vector3 crossProduct = Vector3.Cross(a, b);
            double magProduct = Vector3.Mag(a) * Vector3.Mag(b);
            double angle = Math.Acos(dotProduct / magProduct);
            Vector3 axis = Vector3.Normalize(crossProduct);
            double s = Math.Sin(angle / 2);

            double x = axis.X * s;
            double y = axis.Y * s;
            double z = axis.Z * s;
            double w = Math.Cos(angle / 2);

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) || double.IsNaN(w))
                return new Quaternion(0.0f, 0.0f, 0.0f, 1.0f);

            return Quaternion.Normalize(new Quaternion((float)x, (float)y, (float)z, (float)w));
        }

        /// <summary>The Euler angles of a rotation, as llRot2Euler gives them (PhysUtil.cs:258-276).</summary>
        public static Vector3 Rot2Euler(Quaternion r)
        {
            Vector3 v = new Vector3(0.0f, 0.0f, 1.0f) * r;   // Z axis unit vector unaffected by Z rotation component of r.
            double m = Vector3.Mag(v);                       // Just in case v isn't normalized, need magnitude for Asin() operation later.
            if (m == 0.0) return new Vector3();
            double x = Math.Atan2(-v.Y, v.Z);
            double sin = v.X / m;
            if (sin < -0.999999 || sin > 0.999999) x = 0.0;  // Force X rotation to 0 at the singularities.
            double y = Math.Asin(sin);
            // Rotate X axis unit vector by r and unwind the X and Y rotations leaving only the Z rotation
            v = new Vector3(1.0f, 0.0f, 0.0f) * ((r * new Quaternion((float)Math.Sin(-x / 2.0), 0.0f, 0.0f, (float)Math.Cos(-x / 2.0))) * new Quaternion(0.0f, (float)Math.Sin(-y / 2.0), 0.0f, (float)Math.Cos(-y / 2.0)));
            double z = Math.Atan2(v.Y, v.X);

            return new Vector3((float)x, (float)y, (float)z);
        }
    }
}
