using System.Reflection;
using System.Data;
using MySqlConnector;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Data.MySQL;

// MySQL implementation of IMembershipData. Mirrors MySqlExperienceData: extends MySqlFramework, runs its
// own "Membership" migration set on construction, one short-lived connection per call. The only concrete
// data implementation in this tree (Experience is likewise MySQL-only); the service layer talks solely to
// IMembershipData so a SQLite variant can be dropped in later without changes above this class.
public class MySqlMembershipData : MySqlFramework, IMembershipData
{
    protected virtual Assembly Assembly
    {
        get { return GetType().Assembly; }
    }

    public MySqlMembershipData(string connectionString)
            : base(connectionString)
    {
        m_connectionString = connectionString;

        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            Migration m = new Migration(dbcon, Assembly, "Membership");
            m.Update();
            dbcon.Close();
        }
    }

    public MembershipTier[] GetTiers()
    {
        List<MembershipTier> tiers = new List<MembershipTier>();

        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand("SELECT * FROM `membership_tiers` ORDER BY `sort_order`, `tier_name`", dbcon))
            using (IDataReader result = cmd.ExecuteReader())
            {
                while (result.Read())
                    tiers.Add(ReadTier(result));
            }
        }

        return tiers.ToArray();
    }

    public MembershipTier GetTier(string tierName)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand("SELECT * FROM `membership_tiers` WHERE `tier_name` = ?tier", dbcon))
            {
                cmd.Parameters.AddWithValue("?tier", tierName);
                using (IDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                        return ReadTier(result);
                }
            }
        }
        return null;
    }

    public UserMembership GetUserMembership(UUID agentID)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand("SELECT * FROM `user_membership` WHERE `agent_id` = ?agent", dbcon))
            {
                cmd.Parameters.AddWithValue("?agent", agentID.ToString());
                using (IDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                        return ReadUserMembership(result);
                }
            }
        }
        return null;
    }

    public bool StoreUserMembership(UserMembership m)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand(
                "REPLACE INTO `user_membership` " +
                "(`agent_id`, `tier_name`, `started`, `expires`, `auto_renew`, `stipend_last_paid`, `signup_bonus_paid`, `granted_by`, `notes`) " +
                "VALUES (?agent, ?tier, ?started, ?expires, ?auto_renew, ?stipend_last_paid, ?bonus_paid, ?granted_by, ?notes)", dbcon))
            {
                cmd.Parameters.AddWithValue("?agent", m.agent_id.ToString());
                cmd.Parameters.AddWithValue("?tier", m.tier_name);
                cmd.Parameters.AddWithValue("?started", m.started);
                cmd.Parameters.AddWithValue("?expires", m.expires == 0 ? (object)DBNull.Value : m.expires);
                cmd.Parameters.AddWithValue("?auto_renew", m.auto_renew ? 1 : 0);
                cmd.Parameters.AddWithValue("?stipend_last_paid", m.stipend_last_paid == 0 ? (object)DBNull.Value : m.stipend_last_paid);
                cmd.Parameters.AddWithValue("?bonus_paid", m.signup_bonus_paid ? 1 : 0);
                cmd.Parameters.AddWithValue("?granted_by", m.granted_by.ToString());
                cmd.Parameters.AddWithValue("?notes", m.notes ?? string.Empty);

                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }

    public bool RemoveUserMembership(UUID agentID)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();
            using (MySqlCommand cmd = new MySqlCommand("DELETE FROM `user_membership` WHERE `agent_id` = ?agent LIMIT 1", dbcon))
            {
                cmd.Parameters.AddWithValue("?agent", agentID.ToString());
                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }

    private static MembershipTier ReadTier(IDataReader r)
    {
        return new MembershipTier
        {
            tier_name           = r["tier_name"].ToString(),
            display_title       = r["display_title"].ToString(),
            max_groups          = Convert.ToInt32(r["max_groups"]),
            max_attachments     = Convert.ToInt32(r["max_attachments"]),
            max_animesh         = Convert.ToInt32(r["max_animesh"]),
            max_picks           = Convert.ToInt32(r["max_picks"]),
            upload_cost         = Convert.ToInt32(r["upload_cost"]),
            group_create_cost   = Convert.ToInt32(r["group_create_cost"]),
            stipend_amount      = Convert.ToInt32(r["stipend_amount"]),
            stipend_period_days = Convert.ToInt32(r["stipend_period_days"]),
            signup_bonus        = Convert.ToInt32(r["signup_bonus"]),
            land_allowance      = Convert.ToInt32(r["land_allowance"]),
            sort_order          = Convert.ToInt32(r["sort_order"]),
        };
    }

    private static UserMembership ReadUserMembership(IDataReader r)
    {
        UserMembership m = new UserMembership
        {
            tier_name         = r["tier_name"].ToString(),
            started           = Convert.ToInt32(r["started"]),
            expires           = r["expires"] is DBNull ? 0 : Convert.ToInt32(r["expires"]),
            auto_renew        = Convert.ToInt32(r["auto_renew"]) != 0,
            stipend_last_paid = r["stipend_last_paid"] is DBNull ? 0 : Convert.ToInt32(r["stipend_last_paid"]),
            signup_bonus_paid = Convert.ToInt32(r["signup_bonus_paid"]) != 0,
            notes             = r["notes"].ToString(),
        };
        UUID.TryParse(r["agent_id"].ToString(), out m.agent_id);
        UUID.TryParse(r["granted_by"].ToString(), out m.granted_by);
        return m;
    }
}
