using System.Reflection;
using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Services.MembershipService;

public class MembershipService : MembershipServiceBase, IMembershipService
{
    private static readonly ILogger m_log =
            LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private IUserAccountService m_UserService = null;

    public MembershipService(IConfigSource config)
        : base(config)
    {
        m_log.LogDebug("[MEMBERSHIP SERVICE]: Starting membership service");

        IConfig svcConfig = config.Configs["MembershipService"];
        if (svcConfig == null)
            throw new Exception("No MembershipService configuration");

        string userServiceDll = svcConfig.GetString("UserAccountService", string.Empty);
        if (userServiceDll != string.Empty)
            m_UserService = LoadPlugin<IUserAccountService>(userServiceDll, new Object[] { config });

        if (MainConsole.Instance != null)
        {
            MainConsole.Instance.Commands.AddCommand("Membership", false,
                    "membership show",
                    "membership show <first> <last>",
                    "Show a user's membership and resolved tier.", HandleShow);

            MainConsole.Instance.Commands.AddCommand("Membership", false,
                    "membership set",
                    "membership set <first> <last> <tier> [expires]",
                    "Assign a user to a membership tier. expires = unix seconds, or omit for none.", HandleSet);

            MainConsole.Instance.Commands.AddCommand("Membership", false,
                    "membership list tiers",
                    "membership list tiers",
                    "List the configured membership tiers.", HandleListTiers);

            MainConsole.Instance.Commands.AddCommand("Membership", false,
                    "membership resync",
                    "membership resync <first> <last>",
                    "Rewrite a user's UserTitle from their current tier (repairs a title the admin API overwrote).", HandleResync);
        }
    }

    // ---------------------------------------------------------------------
    // IMembershipService
    // ---------------------------------------------------------------------

    public MembershipTier GetMembership(UUID agentID)
    {
        UserMembership um = m_Database.GetUserMembership(agentID);
        if (um != null && !string.IsNullOrEmpty(um.tier_name))
        {
            bool live = um.expires == 0 || um.expires > Util.UnixTimeSinceEpoch();
            if (live)
            {
                MembershipTier tier = m_Database.GetTier(um.tier_name);
                if (tier != null)
                    return tier;
            }
        }
        // The Basic-equivalent fallback tracks the compiled Constants -> today's behaviour when unconfigured.
        return MembershipTier.Basic();
    }

    public MembershipTier GetTier(string tierName)
    {
        return m_Database.GetTier(tierName);
    }

    public MembershipTier[] GetTiers()
    {
        return m_Database.GetTiers();
    }

    public bool SetMembership(UUID agentID, string tierName, int expires, UUID grantedBy)
    {
        UserMembership m = new UserMembership
        {
            agent_id = agentID,
            tier_name = tierName,
            started = Util.UnixTimeSinceEpoch(),
            expires = expires,
            auto_renew = false,
            stipend_last_paid = 0,
            signup_bonus_paid = false,
            granted_by = grantedBy,
            notes = string.Empty,
        };
        bool ok = m_Database.StoreUserMembership(m);
        if (ok)
            ApplyTitle(agentID);   // PART A: sync the profile badge to the new tier
        return ok;
    }

    public bool RemoveMembership(UUID agentID)
    {
        bool ok = m_Database.RemoveUserMembership(agentID);
        // PART A: with the row gone the agent resolves to Basic (empty display_title) -> clear the badge.
        // Run regardless of `ok` (idempotent), so a redundant remove still normalises the title.
        ApplyTitle(agentID);
        return ok;
    }

    // PART A — profile tier badge (Docs/membership-findings.md §6; field = UserTitle, decided).
    // Write the RESOLVED tier's display_title to the account's UserTitle so the viewer profile "account
    // type" line shows the tier. Rules:
    //  - Badge ONLY local accounts: GetUserAccount must succeed. HG visitors have no local account row
    //    (their profile module builds a transient "HG Visitor" stand-in), so they are never written.
    //  - An empty display_title (Basic / no tier) writes UserTitle="" which StoreUserAccount omits, and
    //    the REPLACE resets the column to '' -> the account falls through to the existing UserFlags byte
    //    path. (Verified: StoreUserAccount is a real REPLACE-INTO write, not a stub, and preserves
    //    DisplayName.)
    //  - Inert when no UserAccountService is configured.
    //  - We do NOT invalidate the region-side profile cache (PROFILECACHEEXPIRE = 300s). The change is
    //    visible after <=5 minutes (next profile fetch after TTL) or a relog; cross-process eviction is
    //    not worth the complexity. See Docs/membership-CLAUDE.md.
    private void ApplyTitle(UUID agentID)
    {
        if (m_UserService == null)
            return;   // inert: no account service wired

        UserAccount acc = m_UserService.GetUserAccount(UUID.Zero, agentID);
        if (acc == null)
            return;   // not a local account (e.g. an HG visitor) -> never badge

        MembershipTier tier = GetMembership(agentID);   // resolved, never null
        string title = tier.display_title ?? string.Empty;
        if (string.Equals(acc.UserTitle ?? string.Empty, title, StringComparison.Ordinal))
            return;   // no change

        acc.UserTitle = title;   // "" clears it -> byte-path fallback via REPLACE default
        if (!m_UserService.StoreUserAccount(acc))
            m_log.LogWarning("[MEMBERSHIP SERVICE]: failed to store UserTitle for {AgentId}", agentID);
    }

    // ---------------------------------------------------------------------
    // Console
    // ---------------------------------------------------------------------

    private UserAccount ResolveAccount(string first, string last)
    {
        if (m_UserService == null)
        {
            MainConsole.Instance.Output("No UserAccountService configured for MembershipService.");
            return null;
        }
        UserAccount account = m_UserService.GetUserAccount(UUID.Zero, first, last);
        if (account == null)
            MainConsole.Instance.Output("No such user as {0} {1}", first, last);
        return account;
    }

    private void HandleShow(string module, string[] cmd)
    {
        // membership show <first> <last>
        if (cmd.Length < 4)
        {
            MainConsole.Instance.Output("Usage: membership show <first> <last>");
            return;
        }
        UserAccount account = ResolveAccount(cmd[2], cmd[3]);
        if (account == null)
            return;

        UserMembership um = m_Database.GetUserMembership(account.PrincipalID);
        MembershipTier tier = GetMembership(account.PrincipalID);

        if (um == null)
            MainConsole.Instance.Output("{0} {1} has no membership row -> resolved tier: {2} (fallback)", cmd[2], cmd[3], tier.tier_name);
        else
            MainConsole.Instance.Output("{0} {1}: tier='{2}' expires={3} -> resolved tier: {4}", cmd[2], cmd[3], um.tier_name,
                um.expires == 0 ? "never" : um.expires.ToString(), tier.tier_name);

        MainConsole.Instance.Output("  limits: groups={0} attachments={1} animesh={2} picks={3}; upload={4} groupcreate={5} stipend={6}/{7}d bonus={8} land={9}",
            tier.max_groups, tier.max_attachments, tier.max_animesh, tier.max_picks,
            tier.upload_cost, tier.group_create_cost, tier.stipend_amount, tier.stipend_period_days, tier.signup_bonus, tier.land_allowance);
    }

    private void HandleSet(string module, string[] cmd)
    {
        // membership set <first> <last> <tier> [expires]
        if (cmd.Length < 5)
        {
            MainConsole.Instance.Output("Usage: membership set <first> <last> <tier> [expires]");
            return;
        }
        UserAccount account = ResolveAccount(cmd[2], cmd[3]);
        if (account == null)
            return;

        string tierName = cmd[4];
        int expires = 0;
        if (cmd.Length >= 6 && !int.TryParse(cmd[5], out expires))
        {
            MainConsole.Instance.Output("expires must be a unix-seconds integer (or omit for none).");
            return;
        }

        if (GetTier(tierName) == null)
            MainConsole.Instance.Output("Warning: tier '{0}' is not defined yet — it will resolve to the Basic fallback until the tier row exists.", tierName);

        if (SetMembership(account.PrincipalID, tierName, expires, UUID.Zero))
            MainConsole.Instance.Output("Set {0} {1} -> '{2}'{3}.", cmd[2], cmd[3], tierName, expires == 0 ? "" : " (expires " + expires + ")");
        else
            MainConsole.Instance.Output("Failed to set membership.");
    }

    private void HandleListTiers(string module, string[] cmd)
    {
        MembershipTier[] tiers = GetTiers();
        if (tiers.Length == 0)
        {
            MainConsole.Instance.Output("No membership tiers defined (the tiers table is empty).");
            return;
        }
        MainConsole.Instance.Output("Membership tiers ({0}):", tiers.Length);
        foreach (MembershipTier t in tiers)
            MainConsole.Instance.Output("  {0,-16} groups={1} attach={2} animesh={3} picks={4} upload={5} groupcreate={6} stipend={7}/{8}d",
                t.tier_name, t.max_groups, t.max_attachments, t.max_animesh, t.max_picks, t.upload_cost, t.group_create_cost, t.stipend_amount, t.stipend_period_days);
    }

    private void HandleResync(string module, string[] cmd)
    {
        // membership resync <first> <last>
        if (cmd.Length < 4)
        {
            MainConsole.Instance.Output("Usage: membership resync <first> <last>");
            return;
        }
        UserAccount account = ResolveAccount(cmd[2], cmd[3]);
        if (account == null)
            return;
        if (m_UserService == null)
        {
            MainConsole.Instance.Output("No UserAccountService configured; cannot write UserTitle.");
            return;
        }
        ApplyTitle(account.PrincipalID);
        UserAccount after = m_UserService.GetUserAccount(UUID.Zero, account.PrincipalID);
        MainConsole.Instance.Output("Resynced {0} {1}: UserTitle now '{2}' (resolved tier '{3}').",
            cmd[2], cmd[3], after?.UserTitle ?? string.Empty, GetMembership(account.PrincipalID).tier_name);
    }
}
