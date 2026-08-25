using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Membership;

// M3 — advertise the agent's membership-tier caps in the SimulatorFeatures cap.
//
// Closes the login/SimulatorFeatures inconsistency M2 deliberately left: login already advertises the
// account's resolved tier max_groups (LLLoginService, response.MaxAgentGroups), but SimulatorFeatures
// still showed the grid-wide Constants defaults. This module makes SimulatorFeatures agree with login
// for MaxAgentGroups and additionally advertises MaxAgentAttachments, MaxProfilePicks, and the nested
// AnimatedObjects.MaxAgentAnimatedObjectAttachments.
//
// SimulatorFeatures is fetched ONCE per region arrival (see HandleSimulatorFeaturesRequest / DeepCopy),
// so a mid-session tier change is not reflected until the agent crosses to another region or relogs.
// Documented in Docs/MembershipSimulatorFeatures.md.
//
// Disabled by default. Enable with [SimulatorFeaturesMembership] Enabled = true. Fully inert when the
// section is absent, or when no IMembershipService is registered in the region.
public class MembershipFeaturesModule : INonSharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private bool m_enabled = false;
    private IMembershipService m_membership;
    private IUserAccountService m_userAccounts;

    #region INonSharedRegionModule

    public string Name => "MembershipFeaturesModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig cfg = source.Configs["SimulatorFeaturesMembership"];
        m_enabled = cfg != null && cfg.GetBoolean("Enabled", false);
        if (m_enabled)
            m_log.LogInformation("[MEMBERSHIP FEATURES]: Enabled — will advertise membership-tier caps in SimulatorFeatures");
    }

    public void AddRegion(Scene scene) { }

    public void RegionLoaded(Scene scene)
    {
        if (!m_enabled)
            return;

        m_membership = scene.RequestModuleInterface<IMembershipService>();
        m_userAccounts = scene.UserAccountService;

        if (m_membership == null)
        {
            m_log.LogWarning(
                "[MEMBERSHIP FEATURES]: no IMembershipService registered in region {RegionName} — tier caps will NOT be advertised (inert).",
                scene.RegionInfo.RegionName);
            return;
        }

        ISimulatorFeaturesModule featuresModule = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
        if (featuresModule == null)
        {
            m_log.LogWarning(
                "[MEMBERSHIP FEATURES]: no ISimulatorFeaturesModule in region {RegionName} — cannot advertise tier caps.",
                scene.RegionInfo.RegionName);
            return;
        }

        featuresModule.OnSimulatorFeaturesRequest += OnSimulatorFeaturesRequest;
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        ISimulatorFeaturesModule featuresModule = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
        if (featuresModule != null)
            featuresModule.OnSimulatorFeaturesRequest -= OnSimulatorFeaturesRequest;
    }

    public void Close() { }

    #endregion

    private void OnSimulatorFeaturesRequest(UUID agentID, ref OSDMap features)
    {
        // HandleSimulatorFeaturesRequest invokes this inside `try { } catch { }` and DISCARDS any
        // exception, so a throw here would silently degrade the agent to grid-wide defaults with no
        // trace at all. Never throw out of this delegate: catch internally and log a warning.
        try
        {
            if (m_membership == null || m_userAccounts == null)
                return;   // inert (should not happen once subscribed, but cheap to guard)

            // HG visitors have no local account row. Leave the map at grid-wide defaults for them — do
            // NOT substitute tier caps. (GetMembership would resolve an unknown agent to Basic, which is
            // exactly the "substitute defaults" behaviour we must avoid here, so gate on the account.)
            UserAccount account = m_userAccounts.GetUserAccount(UUID.Zero, agentID);
            if (account == null)
                return;

            MembershipTier tier = m_membership.GetMembership(agentID);   // resolved; never null for a local account
            if (tier == null)
                return;

            // 1. Attachments.
            features["MaxAgentAttachments"] = OSD.FromInteger(tier.max_attachments);

            // 2. Profile picks (not in the grid-wide defaults — added here for member accounts).
            features["MaxProfilePicks"] = OSD.FromInteger(tier.max_picks);

            // 3. Animated-object attachments — a nested OSDMap. Reuse the existing map (a true deep copy
            //    per DeepCopy()) so sibling keys such as AnimatedObjectMaxTris are preserved; only create
            //    one if it is somehow absent.
            OSDMap animated;
            if (features.TryGetValue("AnimatedObjects", out OSD existing) && existing is OSDMap existingMap)
            {
                animated = existingMap;
            }
            else
            {
                animated = new OSDMap();
                features["AnimatedObjects"] = animated;
            }
            animated["MaxAgentAnimatedObjectAttachments"] = OSD.FromInteger(tier.max_animesh);

            // 4. Groups, plus the Basic/Premium variants this tree emits. A max_groups of 0 means
            //    UNLIMITED and is passed through unchanged — identical to the login path
            //    (response.MaxAgentGroups), so login and SimulatorFeatures now advertise the SAME number.
            //    Current Firestorm reads only the login value; the Basic/Premium variants are set for
            //    consistency and for other viewers.
            features["MaxAgentGroups"] = OSD.FromInteger(tier.max_groups);
            features["MaxAgentGroupsBasic"] = OSD.FromInteger(tier.max_groups);
            features["MaxAgentGroupsPremium"] = OSD.FromInteger(tier.max_groups);
        }
        catch (Exception e)
        {
            m_log.LogWarning("[MEMBERSHIP FEATURES]: failed to apply tier caps for {AgentId}: {Error}", agentID, e);
        }
    }
}
