/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Decides whether a local voice provision may go ahead on the resolved parcel.
 *
 * Two rules, neither of which depends on the estate's TaxFree flag (the inverse of "allow access override"):
 *  - A parcel with voice turned off never gets voice. The older Vivox and FreeSwitch modules skip this check
 *    under TaxFree, and this module inherited that.
 *  - A parcel ban is honoured. The sim's own LandObject.IsBannedFromLand and IsRestrictedFromLand return false
 *    under TaxFree, so the sim's answer (coreBars) is not enough on its own; the ban list is also evaluated
 *    without that short-circuit, the same way the visibility matrix does (LandBan). Access-list
 *    restrictions are left to the sim's answer.
 *
 * Pure and Scene-free so the decision unit-tests without a Scene.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public enum ProvisionAccess
    {
        Allowed,
        /// <summary>The parcel has voice turned off.</summary>
        VoiceOff,
        /// <summary>The avatar is banned from, or restricted from, the parcel.</summary>
        Barred,
    }

    public static class ProvisionParcelAccess
    {
        /// <param name="land">The resolved parcel.</param>
        /// <param name="coreBars">The sim's answer: IsRestrictedFromLand || IsBannedFromLand.</param>
        public static ProvisionAccess Check(
            LandData land,
            bool coreBars,
            Func<UUID, bool> isAdministrator,
            Func<UUID, bool> isEstateManagerOrOwner,
            UUID avatar,
            int nowUnix)
        {
            if ((land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                return ProvisionAccess.VoiceOff;

            if (coreBars || LandBan.IsBannedIgnoringTaxFree(land, isAdministrator, isEstateManagerOrOwner, avatar, nowUnix))
                return ProvisionAccess.Barred;

            return ProvisionAccess.Allowed;
        }
    }
}
