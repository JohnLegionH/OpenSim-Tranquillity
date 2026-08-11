using OpenMetaverse;

namespace OpenSim.Framework;

// Wire/data DTOs for the membership subsystem. Plain fields + ToDictionary()/from-dictionary ctor so
// they round-trip over the Robust POST protocol (ServerUtils.BuildXmlResponse/ParseXmlResponse hand back
// every value as a string, so the ctors parse defensively). Mirrors OpenSim.Framework/ExperienceData.cs.
// NO MySQL types here — the data interface (IMembershipData) stays storage-agnostic.

public class MembershipTier
{
    public string tier_name = string.Empty;
    public string display_title = string.Empty;
    public int max_groups;
    public int max_attachments;
    public int max_animesh;
    public int max_picks;
    public int upload_cost;
    public int group_create_cost;
    public int stipend_amount;
    public int stipend_period_days;
    public int signup_bonus;
    public int land_allowance;
    public int sort_order;

    public MembershipTier() { }

    // The Basic-equivalent fallback, single source of truth. Limits mirror the compiled Constants so that
    // — with an empty tiers table, no user row, or a failed lookup — callers see today's effective numbers
    // and nothing changes. display_title is empty so it never alters a profile if ever consumed.
    public static MembershipTier Basic()
    {
        return new MembershipTier
        {
            tier_name = "Basic",
            display_title = string.Empty,
            max_groups = Constants.MaxAgentGroups,
            max_attachments = Constants.MaxAgentAttachments,
            max_animesh = Constants.MaxAgentAnimatedObjectAttachments,
            max_picks = Constants.MaxProfilePicks,
        };
    }

    public MembershipTier(Dictionary<string, object> d)
    {
        tier_name           = MembershipDataUtil.Str(d, "tier_name");
        display_title       = MembershipDataUtil.Str(d, "display_title");
        max_groups          = MembershipDataUtil.Int(d, "max_groups");
        max_attachments     = MembershipDataUtil.Int(d, "max_attachments");
        max_animesh         = MembershipDataUtil.Int(d, "max_animesh");
        max_picks           = MembershipDataUtil.Int(d, "max_picks");
        upload_cost         = MembershipDataUtil.Int(d, "upload_cost");
        group_create_cost   = MembershipDataUtil.Int(d, "group_create_cost");
        stipend_amount      = MembershipDataUtil.Int(d, "stipend_amount");
        stipend_period_days = MembershipDataUtil.Int(d, "stipend_period_days");
        signup_bonus        = MembershipDataUtil.Int(d, "signup_bonus");
        land_allowance      = MembershipDataUtil.Int(d, "land_allowance");
        sort_order          = MembershipDataUtil.Int(d, "sort_order");
    }

    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            ["tier_name"] = tier_name,
            ["display_title"] = display_title,
            ["max_groups"] = max_groups,
            ["max_attachments"] = max_attachments,
            ["max_animesh"] = max_animesh,
            ["max_picks"] = max_picks,
            ["upload_cost"] = upload_cost,
            ["group_create_cost"] = group_create_cost,
            ["stipend_amount"] = stipend_amount,
            ["stipend_period_days"] = stipend_period_days,
            ["signup_bonus"] = signup_bonus,
            ["land_allowance"] = land_allowance,
            ["sort_order"] = sort_order,
        };
    }
}

public class UserMembership
{
    public UUID agent_id = UUID.Zero;
    public string tier_name = string.Empty;
    public int started;                 // unix seconds
    public int expires;                 // unix seconds; 0 = no expiry (stored NULL)
    public bool auto_renew;
    public int stipend_last_paid;       // unix seconds; 0 = never (stored NULL)
    public bool signup_bonus_paid;
    public UUID granted_by = UUID.Zero;
    public string notes = string.Empty;

    public UserMembership() { }

    public UserMembership(Dictionary<string, object> d)
    {
        agent_id          = MembershipDataUtil.Uuid(d, "agent_id");
        tier_name         = MembershipDataUtil.Str(d, "tier_name");
        started           = MembershipDataUtil.Int(d, "started");
        expires           = MembershipDataUtil.Int(d, "expires");
        auto_renew        = MembershipDataUtil.Bool(d, "auto_renew");
        stipend_last_paid = MembershipDataUtil.Int(d, "stipend_last_paid");
        signup_bonus_paid = MembershipDataUtil.Bool(d, "signup_bonus_paid");
        granted_by        = MembershipDataUtil.Uuid(d, "granted_by");
        notes             = MembershipDataUtil.Str(d, "notes");
    }

    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            ["agent_id"] = agent_id.ToString(),
            ["tier_name"] = tier_name,
            ["started"] = started,
            ["expires"] = expires,
            ["auto_renew"] = auto_renew ? 1 : 0,
            ["stipend_last_paid"] = stipend_last_paid,
            ["signup_bonus_paid"] = signup_bonus_paid ? 1 : 0,
            ["granted_by"] = granted_by.ToString(),
            ["notes"] = notes,
        };
    }
}

internal static class MembershipDataUtil
{
    public static string Str(Dictionary<string, object> d, string k)
        => d.TryGetValue(k, out object v) && v != null ? v.ToString() : string.Empty;

    public static int Int(Dictionary<string, object> d, string k)
        => d.TryGetValue(k, out object v) && v != null && int.TryParse(v.ToString(), out int i) ? i : 0;

    public static bool Bool(Dictionary<string, object> d, string k)
    {
        if (!d.TryGetValue(k, out object v) || v is null)
            return false;
        string s = v.ToString();
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static UUID Uuid(Dictionary<string, object> d, string k)
        => d.TryGetValue(k, out object v) && v != null && UUID.TryParse(v.ToString(), out UUID u) ? u : UUID.Zero;
}
