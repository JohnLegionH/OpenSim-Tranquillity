/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// A damped spring, as Second Life documents hover and the vertical attractor: a timescale and an efficiency that
    /// slides from 0 (bouncy: the spring wobbles around its rest) to 1 (critically damped: it reaches its rest with
    /// exponential decay). For an error e from the rest:
    ///
    ///   e'' = -w^2 e - 2 z w e'          w = 1 / T,  z = efficiency
    ///
    /// With z = 1 the error decays as e^(-t / T) (1 + t / T). <see cref="Step"/> is the exact solution over a step,
    /// so a span of time gives the same motion however it is cut into steps.
    /// </summary>
    public static class VehicleSpring
    {
        /// <summary>The error and its rate of change after h seconds, from e0 and v0.</summary>
        public static (double e, double v) Step(double e0, double v0, double timescale, double efficiency, double h)
        {
            double w = 1.0 / timescale;
            double z = Math.Clamp(efficiency, 0.0, 1.0);
            double decay = Math.Exp(-z * w * h);

            // Critically damped: e = e^(-w t) (e0 + (v0 + w e0) t).
            if (1.0 - z < 1e-9)
            {
                double c = v0 + w * e0;
                return (decay * (e0 + c * h), decay * (v0 - w * c * h));
            }

            // Under-damped: e = e^(-z w t) (e0 cos(wd t) + (v0 + z w e0) / wd sin(wd t)), wd = w sqrt(1 - z^2).
            double wd = w * Math.Sqrt(1.0 - z * z);
            double cos = Math.Cos(wd * h), sin = Math.Sin(wd * h);
            double e = decay * (e0 * cos + (v0 + z * w * e0) / wd * sin);
            double v = decay * (v0 * cos - (w * w * e0 + z * w * v0) / wd * sin);
            return (e, v);
        }

        /// <summary>
        /// The step for a body that moves at the velocity it is given over a step (the engine's x += v h): the
        /// velocity to give it so that it ends the step where the spring would, and what the spring's own velocity
        /// will be there minus that (to add back to the velocity read at the next step, so the spring carries on from
        /// its true state).
        /// </summary>
        public static (double move, double carry) MoveOver(double e0, double v0, double timescale, double efficiency, double h)
        {
            (double e, double v) = Step(e0, v0, timescale, efficiency, h);
            double move = (e - e0) / h;
            return (move, v - move);
        }
    }
}
