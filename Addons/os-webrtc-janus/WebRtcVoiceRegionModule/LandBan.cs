/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Ban-list evaluation that deliberately IGNORES the estate TaxFree short-circuit.
 *
 * Used by the visibility feeder so that parcel bans are still enforced in voice under a
 * TaxFree estate, where the sim itself skips them. It reproduces
 * LandObject.IsBannedFromLand's exemptions (admin / estate-manager-or-owner / parcel-owner) and
 * its private IsBannedFromLand_inner ban-list scan (LandObject.cs:826), MINUS the TaxFree line.
 * Access-restriction is intentionally NOT mirrored here — the void fix is ban-only; restrict
 * keeps the sim's TaxFree behavior (see FeederWorldFromScene).
 *
 * Pure and Scene-free (operates on a LandData snapshot + exemption delegates) so it unit-tests
 * against real LandData/LandAccessEntry shapes without a Scene.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public static class LandBan
    {
        public static bool IsBannedIgnoringTaxFree(
            LandData land,
            Func<UUID, bool> isAdministrator,
            Func<UUID, bool> isEstateManagerOrOwner,
            UUID avatar,
            int nowUnix)
        {
            if (land == null)
                return false;

            // Exemptions kept (everything the sim exempts EXCEPT TaxFree).
            if (isAdministrator != null && isAdministrator(avatar))
                return false;
            if (isEstateManagerOrOwner != null && isEstateManagerOrOwner(avatar))
                return false;
            if (avatar.Equals(land.OwnerID))
                return false;

            // Ban-list scan — mirrors IsBannedFromLand_inner (LandObject.cs:826).
            if ((land.Flags & (uint)ParcelFlags.UseBanList) == 0)
                return false;

            foreach (LandAccessEntry e in land.ParcelAccessList)
            {
                if (e.Flags == AccessList.Ban && e.AgentID.Equals(avatar))
                    return e.Expires == 0 || e.Expires > nowUnix;
            }
            return false;
        }
    }
}
