/*
 * Legion Grid — Email inbound IMAP fetcher
 *
 * Polls an IMAP mailbox and routes messages addressed to
 * <objectUUID>@<internal_object_host> into the target object's in-world mailbox
 * (EmailModule.InsertEmail), so llGetNextEmail receives external inbound email.
 * This is the inbound counterpart to EmailModule's outbound SMTP path.
 *
 * One fetcher per simulator process (started from EmailModule.PostInitialise,
 * stopped from Close) — EmailModule is an ISharedRegionModule.
 *
 * Routing / server-side disposition (deliberately conservative — never destroy mail
 * that might route later):
 *   - Routed to a registered mailbox      -> delete (or mark seen if IMAP_DeleteRouted=false).
 *   - Well-formed object UUID, but wrong domain OR no registered mailbox yet
 *                                         -> LEAVE on server, debug log ONCE per UID, retry.
 *   - No object-addressable recipient at all (local-part isn't a UUID)
 *                                         -> mark seen (can never route), info log.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using log4net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.Scripting.EmailModules;

internal class EmailInboundFetcher
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly EmailModule m_module;
    private readonly string m_host;
    private readonly int m_port;
    private readonly bool m_tls;
    private readonly string m_login;
    private readonly string m_password;
    private readonly int m_pollSeconds;
    private readonly string m_folder;
    private readonly bool m_deleteRouted;

    private Thread m_thread;
    private volatile bool m_stop;
    private CancellationTokenSource m_cts;

    // UIDs already deferred+logged, so a message left on the server isn't re-logged every
    // poll. Still re-evaluated each cycle (a mailbox may register later); only the log is
    // suppressed. Entries are cleared when a UID is finally routed or marked seen.
    private readonly HashSet<uint> m_deferredLogged = new HashSet<uint>();

    internal EmailInboundFetcher(EmailModule module, string host, int port, bool tls,
        string login, string password, int pollSeconds, string folder, bool deleteRouted)
    {
        m_module = module;
        m_host = host;
        m_port = port;
        m_tls = tls;
        m_login = login;
        m_password = password;
        m_pollSeconds = pollSeconds < 5 ? 5 : pollSeconds;
        m_folder = string.IsNullOrEmpty(folder) ? "INBOX" : folder;
        m_deleteRouted = deleteRouted;
    }

    internal void Start()
    {
        m_stop = false;
        m_cts = new CancellationTokenSource();
        m_thread = new Thread(Run) { Name = "EmailInboundFetcher", IsBackground = true };
        m_thread.Start();
        m_log.InfoFormat("[EMAIL INBOUND]: fetcher started ({0}:{1}, folder {2}, poll {3}s, deleteRouted {4})",
            m_host, m_port, m_folder, m_pollSeconds, m_deleteRouted);
    }

    internal void Stop()
    {
        m_stop = true;
        try { m_cts?.Cancel(); } catch { }
        try { m_thread?.Join(5000); } catch { }
        m_log.Info("[EMAIL INBOUND]: fetcher stopped");
    }

    private void Run()
    {
        int backoff = 5;
        const int maxBackoff = 300;

        while (!m_stop)
        {
            try
            {
                using (var client = new ImapClient())
                {
                    client.Connect(m_host, m_port,
                        m_tls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None, m_cts.Token);

                    if (!string.IsNullOrEmpty(m_login))
                        client.Authenticate(m_login, m_password ?? string.Empty, m_cts.Token);

                    IMailFolder inbox = client.GetFolder(m_folder) ?? client.Inbox;
                    inbox.Open(FolderAccess.ReadWrite, m_cts.Token);

                    bool useIdle = client.Capabilities.HasFlag(ImapCapabilities.Idle);
                    m_log.DebugFormat("[EMAIL INBOUND]: connected; IDLE {0}",
                        useIdle ? "available (opportunistic)" : "unavailable — interval polling");

                    backoff = 5; // reset after a healthy connection

                    while (!m_stop && client.IsConnected && client.IsAuthenticated)
                    {
                        PollOnce(inbox);
                        if (m_stop)
                            break;

                        if (useIdle)
                        {
                            // Opportunistic: wake on new mail, but no longer than the poll interval.
                            try
                            {
                                // MailKit IDLE is on the client (folder must be open). The
                                // done-token ends IDLE after the poll interval; m_cts ends it on
                                // shutdown or new-mail-driven reconnect.
                                using (var idleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(m_pollSeconds)))
                                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(m_cts.Token, idleTimeout.Token))
                                    client.Idle(linked.Token, m_cts.Token);
                            }
                            catch (OperationCanceledException) { /* interval elapsed or shutdown */ }
                        }
                        else
                        {
                            SleepSeconds(m_pollSeconds);
                        }
                    }

                    try { client.Disconnect(true, CancellationToken.None); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown requested via m_cts
            }
            catch (Exception e)
            {
                if (m_stop)
                    break;
                m_log.WarnFormat("[EMAIL INBOUND]: connection error ({0}); reconnecting in {1}s", e.Message, backoff);
                SleepSeconds(backoff);
                backoff = Math.Min(backoff * 2, maxBackoff);
            }
        }
    }

    // Interruptible sleep so shutdown doesn't wait a full interval.
    private void SleepSeconds(int seconds)
    {
        for (int i = 0; i < seconds && !m_stop; i++)
            Thread.Sleep(1000);
    }

    private enum Outcome { Routed, Deferred, Unroutable }

    private void PollOnce(IMailFolder inbox)
    {
        IList<UniqueId> uids;
        try
        {
            uids = inbox.Search(SearchQuery.NotSeen, m_cts.Token);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("[EMAIL INBOUND]: search failed: {0}", e.Message);
            return;
        }

        foreach (UniqueId uid in uids)
        {
            if (m_stop)
                return;

            MimeMessage msg;
            try
            {
                msg = inbox.GetMessage(uid, m_cts.Token);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[EMAIL INBOUND]: fetch of {0} failed: {1}", uid, e.Message);
                continue;
            }

            switch (RouteMessage(msg, out UUID routedTo))
            {
                case Outcome.Routed:
                    if (m_deleteRouted)
                    {
                        inbox.AddFlags(uid, MessageFlags.Deleted, true, m_cts.Token);
                        inbox.Expunge(new[] { uid }, m_cts.Token);
                    }
                    else
                    {
                        inbox.AddFlags(uid, MessageFlags.Seen, true, m_cts.Token);
                    }
                    m_deferredLogged.Remove(uid.Id);
                    m_log.DebugFormat("[EMAIL INBOUND]: {0} routed to {1} (subj '{2}'); {3}",
                        uid, routedTo, msg.Subject ?? string.Empty,
                        m_deleteRouted ? "deleted" : "marked seen");
                    break;

                case Outcome.Deferred:
                    // Leave untouched on the server; the target region/script may not be up yet.
                    // Log only the first time we defer this UID to avoid a line every poll.
                    if (m_deferredLogged.Add(uid.Id))
                        m_log.DebugFormat(
                            "[EMAIL INBOUND]: {0} (to '{1}') left on server — no registered mailbox / not local; will retry",
                            uid, Recipients(msg));
                    break;

                case Outcome.Unroutable:
                    // No object-addressable recipient — can never route. Mark seen so it stops
                    // reappearing, and log at info: a stream of these means misconfiguration.
                    inbox.AddFlags(uid, MessageFlags.Seen, true, m_cts.Token);
                    m_deferredLogged.Remove(uid.Id);
                    m_log.InfoFormat(
                        "[EMAIL INBOUND]: {0} from '{1}' has no object-addressable recipient ('{2}'); marked seen",
                        uid, msg.From?.ToString() ?? "?", Recipients(msg));
                    break;
            }
        }
    }

    private Outcome RouteMessage(MimeMessage msg, out UUID routedTo)
    {
        routedTo = UUID.Zero;
        bool routedAny = false;
        bool deferrable = false;

        foreach (string rcpt in EnumerateRecipients(msg))
        {
            int at = rcpt.IndexOf('@');
            if (at <= 0)
                continue;
            string local = rcpt.Substring(0, at);
            string domain = rcpt.Substring(at + 1);
            if (!UUID.TryParse(local, out UUID toID))
                continue; // non-UUID local part — not an object address

            bool ourDomain = domain.Equals(m_module.InterObjectHostname, StringComparison.InvariantCultureIgnoreCase);
            if (ourDomain && m_module.HasRegisteredMailbox(toID))
            {
                m_module.InsertEmail(toID, BuildEmail(msg));
                routedAny = true;
                if (routedTo == UUID.Zero)
                    routedTo = toID;
            }
            else
            {
                // well-formed object UUID, but wrong domain OR no registered mailbox (yet)
                deferrable = true;
            }
        }

        // Note: if a single message is addressed to several objects and at least one is
        // registered, we route+delete rather than re-delivering to the registered one every
        // poll (avoiding duplicates). Multi-object-addressed inbound mail is not a real case.
        if (routedAny)
            return Outcome.Routed;
        if (deferrable)
            return Outcome.Deferred;
        return Outcome.Unroutable;
    }

    private static IEnumerable<string> EnumerateRecipients(MimeMessage msg)
    {
        foreach (MailboxAddress a in msg.To.Mailboxes)
            yield return a.Address;
        foreach (MailboxAddress a in msg.Cc.Mailboxes)
            yield return a.Address;
    }

    private static string Recipients(MimeMessage msg)
    {
        return string.Join(",", msg.To.Mailboxes.Select(a => a.Address));
    }

    // Populate Email consistently with EmailModule's internal object-to-object path
    // (time = unix seconds as string; sender = the address; subject; message) so a script
    // can't tell inbound-external from inbound-internal. numLeft is set at retrieval by
    // GetNextEmail, not here.
    private Email BuildEmail(MimeMessage msg)
    {
        string sender = msg.From?.Mailboxes?.FirstOrDefault()?.Address
                        ?? (msg.From != null ? msg.From.ToString() : string.Empty);
        string subject = msg.Subject ?? string.Empty;
        string body = msg.TextBody ?? string.Empty;

        // SL external-inbound limits: subject to 78 chars, body to 1000 chars, and the
        // combined size no larger than the module's max email size.
        if (subject.Length > 78)
            subject = subject.Substring(0, 78);
        if (body.Length > 1000)
            body = body.Substring(0, 1000);
        int max = m_module.MaxEmailSize;
        if (subject.Length + body.Length > max)
        {
            int allow = Math.Max(0, max - subject.Length);
            if (body.Length > allow)
                body = body.Substring(0, allow);
        }

        return new Email
        {
            time = Util.UnixTimeSinceEpoch().ToString(),
            sender = sender,
            subject = subject,
            message = body
        };
    }
}
