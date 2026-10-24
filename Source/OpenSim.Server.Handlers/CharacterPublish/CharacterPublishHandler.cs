/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.CharacterPublish;

/// <summary>
/// The character publish endpoint. One handler on <c>/characterpublish</c>:
/// <list type="bullet">
/// <item><c>POST login</c>: first name, last name and password in a JSON body; answers a short-lived token from the grid's authentication service.</item>
/// <item><c>POST publish</c>: the bundle (a zip of bundle.json and its asset files) as the body, the resident and the token in headers.</item>
/// <item><c>GET status</c>: the outfits already published for the resident and the limit.</item>
/// <item><c>POST logout</c>: ends the token.</item>
/// </list>
/// Every request must come over HTTPS (directly, or through a listed proxy) and carry the service key. The resident a publish is for is
/// the one the token proves; a field in the request never names the owner.
/// </summary>
public sealed class CharacterPublishHandler : SimpleStreamHandler
{
    public const string BasePath = "/characterpublish";
    public const string KeyHeader = "X-Publish-Key";
    public const string ResidentHeader = "X-Publish-Resident";
    public const string TokenHeader = "X-Publish-Token";

    const int MaxLoginBodyBytes = 4096;
    const int MaxNameLength = 64;
    const int MaxPasswordLength = 256;
    const int MaxManifestBytes = 1024 * 1024;

    readonly CharacterPublishSettings _s;
    readonly IAuthenticationService _auth;
    readonly IUserAccountService _accounts;
    readonly PublishService _publisher;
    readonly ILogger _log;
    readonly SlidingWindowLimiter _limits;
    readonly byte[] _keyHash;

    public CharacterPublishHandler(CharacterPublishSettings settings, IAuthenticationService auth, IUserAccountService accounts,
        IAssetService assets, IInventoryService inventory, ILogger log, Func<DateTime>? clock = null) : base(BasePath)
    {
        _s = settings;
        _auth = auth;
        _accounts = accounts;
        _publisher = new PublishService(assets, inventory);
        _log = log;
        _limits = new SlidingWindowLimiter(clock ?? (() => DateTime.UtcNow));
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.ServiceKey));
    }

    // ------------------------------------------------------------------------------------------------------------------
    protected override void ProcessRequest(IOSHttpRequest req, IOSHttpResponse res)
    {
        try { Dispatch(req, res); }
        catch (Exception e)
        {
            _log.LogError("[CHARACTER PUBLISH]: unexpected {0}: {1}", e.GetType().Name, e.Message);
            Reply(res, 500, "internal-error", "The publish service met an unexpected error. Nothing is known to have been written; see the grid log.");
        }
    }

    void Dispatch(IOSHttpRequest req, IOSHttpResponse res)
    {
        var route = GetParam(req.UriPath).Trim('/').ToLowerInvariant();
        var method = req.HttpMethod;
        var expected = route switch { "login" or "logout" or "publish" => "POST", "status" => "GET", _ => null };
        if (expected == null) { Reply(res, 404, "not-found", "There is no such request. The requests are login, logout, publish and status."); return; }
        if (method != expected) { res.AddHeader("Allow", expected); Reply(res, 405, "wrong-method", $"{route} is a {expected} request."); return; }

        var client = ClientAddress(req);
        if (!_limits.TryHit("addr:" + client, _s.RequestsPerAddressPerMinute, TimeSpan.FromMinutes(1), out var wait))
        { TooMany(res, wait, "This address is sending requests too fast."); return; }

        // HTTPS first: a key or a password sent over plain HTTP has already leaked, so say so before looking at either
        if (!_s.AllowInsecureHttp && !IsSecure(req))
        {
            _log.LogWarning("[CHARACTER PUBLISH]: {0} request from {1} refused: not HTTPS", route, client);
            Reply(res, 403, "https-required", "This endpoint only answers over HTTPS. Use an https address (or reach it through the operator's listed proxy).");
            return;
        }

        if (_s.RequireServiceKey)
        {
            var given = req.Headers[KeyHeader];
            if (string.IsNullOrEmpty(given)) { _log.LogWarning("[CHARACTER PUBLISH]: {0} from {1} refused: no service key", route, client); Reply(res, 401, "service-key-missing", $"Send the service key in the {KeyHeader} header."); return; }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), _keyHash))
            { _log.LogWarning("[CHARACTER PUBLISH]: {0} from {1} refused: wrong service key", route, client); Reply(res, 401, "service-key-invalid", "The service key is not right."); return; }
        }

        switch (route)
        {
            case "login": Login(req, res, client); return;
            default: Authenticated(route, req, res, client); return;
        }
    }

    // ---- who is calling and over what ------------------------------------------------------------------------------
    // A proxy in front of Robust ends the TLS connection, so the connection Robust sees is plain. IsSecured (OSHttpRequest.IsSecured,
    // the connection's own TLS state) is true only for a direct HTTPS connection. The proxy's X-Forwarded-Proto and X-Forwarded-For
    // headers are believed only when the connection itself comes from an address the operator listed in TrustedProxies, because any
    // other caller could write them.
    bool Trusted(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var net in _s.TrustedProxies) if (net.Contains(ip)) return true;
        return false;
    }

    bool IsSecure(IOSHttpRequest req)
    {
        if (req.IsSecured) return true;
        var remote = req.RemoteIPEndPoint?.Address;
        if (remote == null || !Trusted(remote)) return false;
        var proto = req.Headers["X-Forwarded-Proto"];
        if (string.IsNullOrWhiteSpace(proto)) return false;
        var last = proto.Split(',').Select(p => p.Trim()).Last(p => p.Length > 0);
        return last.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    string ClientAddress(IOSHttpRequest req)
    {
        var remote = req.RemoteIPEndPoint?.Address;
        if (remote == null) return "unknown";
        if (!Trusted(remote)) return remote.ToString();
        var xff = req.Headers["X-Forwarded-For"];
        if (string.IsNullOrWhiteSpace(xff)) return remote.ToString();
        IPAddress? leftmost = null;
        foreach (var part in xff.Split(',').Reverse())
        {
            if (!IPAddress.TryParse(part.Trim(), out var ip)) continue;
            leftmost = ip;
            if (!Trusted(ip)) return ip.ToString(); // the nearest hop that is not one of our own proxies is the caller
        }
        return (leftmost ?? remote).ToString();
    }

    // ---- login -----------------------------------------------------------------------------------------------------
    void Login(IOSHttpRequest req, IOSHttpResponse res, string client)
    {
        if (!_limits.TryHit("login-addr:" + client, _s.LoginAttemptsPerAddressPerMinute, TimeSpan.FromMinutes(1), out var wait))
        { TooMany(res, wait, "Too many login attempts from this address."); return; }

        var body = ReadBounded(req.InputStream, MaxLoginBodyBytes, out var tooBig);
        if (tooBig) { Reply(res, 413, "request-too-large", $"A login request is at most {MaxLoginBodyBytes} bytes."); return; }

        string first, last, password;
        try
        {
            using var doc = JsonDocument.Parse(body);
            string Field(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            first = Field("firstName").Trim(); last = Field("lastName").Trim(); password = Field("password");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { Reply(res, 400, "bad-request", "The login body must be JSON with firstName, lastName and password."); return; }
        if (first.Length == 0 || last.Length == 0 || password.Length == 0 || first.Length > MaxNameLength || last.Length > MaxNameLength || password.Length > MaxPasswordLength)
        { Reply(res, 400, "bad-request", "Send firstName, lastName and password (names up to 64 characters)."); return; }

        var name = (first + " " + last).ToLowerInvariant();
        if (!_limits.TryHit("login-name:" + name, _s.LoginAttemptsPerNamePerMinute, TimeSpan.FromMinutes(1), out wait))
        { TooMany(res, wait, "Too many login attempts for this name."); return; }

        // An unknown name and a wrong password get the same answer, so the endpoint does not tell a caller which names exist.
        const string NotRight = "The name or the password is not right.";
        var account = _accounts.GetUserAccount(UUID.Zero, first, last);
        if (account == null)
        { _log.LogInformation("[CHARACTER PUBLISH]: login failed from {0}: no such account '{1}'", client, Printable(first + " " + last)); Reply(res, 401, "login-failed", NotRight); return; }

        // Authenticate wants the MD5 of the password, as the login service passes it (LLLoginService.Login: "$1$" + Util.Md5Hash(passwd) then the prefix is removed).
        var token = _auth.Authenticate(account.PrincipalID, Util.Md5Hash(password), _s.TokenMinutes, out var realId);
        if (string.IsNullOrEmpty(token))
        { _log.LogInformation("[CHARACTER PUBLISH]: login failed from {0} for {1}", client, account.PrincipalID); Reply(res, 401, "login-failed", NotRight); return; }
        if (!realId.IsZero() && realId != account.PrincipalID)
        {
            // an administrator's password logged in as someone else (the authentication service allows that for the viewer); a publish never does
            _auth.Release(account.PrincipalID, token);
            _log.LogWarning("[CHARACTER PUBLISH]: login from {0} for {1} refused: it used another account's password", client, account.PrincipalID);
            Reply(res, 401, "login-failed", NotRight);
            return;
        }

        _log.LogInformation("[CHARACTER PUBLISH]: login ok for {0} from {1}, token good for {2} minutes", account.PrincipalID, client, _s.TokenMinutes);
        Reply(res, 200, "ok", "Logged in.", new Dictionary<string, object?>
        {
            ["principalId"] = account.PrincipalID.ToString(), ["token"] = token, ["expiresInMinutes"] = _s.TokenMinutes,
        });
    }

    // ---- the requests that need a token ----------------------------------------------------------------------------
    void Authenticated(string route, IOSHttpRequest req, IOSHttpResponse res, string client)
    {
        if (!UUID.TryParse(req.Headers[ResidentHeader] ?? "", out var resident) || resident.IsZero())
        { Reply(res, 400, "resident-missing", $"Send the resident's id in the {ResidentHeader} header."); return; }
        // The token goes into the authentication data layer's query text, so only a well-formed one is passed on.
        if (!UUID.TryParse(req.Headers[TokenHeader] ?? "", out var tokenId) || tokenId.IsZero())
        { Reply(res, 401, "token-missing", $"Send the login token in the {TokenHeader} header. Log in first."); return; }

        // Verify checks the token belongs to this resident and has not expired, and extends it by TokenMinutes (IAuthenticationService.Verify).
        if (!_auth.Verify(resident, tokenId.ToString(), _s.TokenMinutes))
        {
            _log.LogInformation("[CHARACTER PUBLISH]: {0} from {1} refused: token not valid for {2}", route, client, resident);
            Reply(res, 401, "token-invalid", "The login token is not valid for this resident, or it has expired. Log in again.");
            return;
        }

        switch (route)
        {
            case "logout":
                _auth.Release(resident, tokenId.ToString());
                _log.LogInformation("[CHARACTER PUBLISH]: logout for {0}", resident);
                Reply(res, 200, "ok", "Logged out.");
                return;
            case "status": Status(res, resident); return;
            default: Publish(req, res, resident, client); return;
        }
    }

    void Status(IOSHttpResponse res, UUID resident)
    {
        var outfits = _publisher.ListPublishedOutfits(resident, _s.CreatorAccount);
        Reply(res, 200, "ok", $"{outfits.Count} of {_s.MaxOutfitsPerResident} published outfits.", new Dictionary<string, object?>
        {
            ["residentId"] = resident.ToString(),
            ["count"] = outfits.Count,
            ["limit"] = _s.MaxOutfitsPerResident,
            ["outfits"] = outfits.Select(o => new Dictionary<string, object?> { ["folderId"] = o.FolderId.ToString(), ["name"] = o.Name }).ToList(),
        });
    }

    // ---- publish ---------------------------------------------------------------------------------------------------
    void Publish(IOSHttpRequest req, IOSHttpResponse res, UUID resident, string client)
    {
        if (!_limits.TryHit("publish:" + resident, _s.PublishesPerResidentPerHour, TimeSpan.FromHours(1), out var wait))
        { TooMany(res, wait, $"This resident may publish {_s.PublishesPerResidentPerHour} times an hour."); return; }

        if (req.ContentLength64 > _s.MaxBundleBytes)
        { Reply(res, 413, "bundle-too-large", $"The bundle is {req.ContentLength64} bytes; this grid allows {_s.MaxBundleBytes}."); return; }
        var body = ReadBounded(req.InputStream, _s.MaxBundleBytes, out var tooBig);
        if (tooBig) { Reply(res, 413, "bundle-too-large", $"The bundle is larger than the {_s.MaxBundleBytes} bytes this grid allows."); return; }

        BundleFiles.Loaded loaded;
        try { loaded = Unpack(body); }
        catch (BundleRefusal r) { Reply(res, r.Status, r.Code, r.Message); return; }

        var options = new PublishOptions
        {
            MaxOutfitsPerResident = _s.MaxOutfitsPerResident,
            Limits = new BundleLimits(Math.Max(1, _s.MaxFiles - 1), _s.MaxAssetBytes, _s.MaxBundleBytes),
            SaveCurrentLook = _s.SaveCurrentLook,
            RequiredCreator = _s.CreatorAccount,
            MissingTextureIsWarning = _s.MissingTextureIsWarning,
        };
        var result = _publisher.Publish(loaded, resident, options);

        var bytes = loaded.Content.Values.Sum(v => (long)v.Length);
        _log.LogInformation("[CHARACTER PUBLISH]: publish for {0} from {1}: {2} ({3} assets, {4} bytes, outfit folder {5})",
            resident, client, result.Summary, loaded.Bundle.Assets.Count, bytes, result.OutfitFolderId);

        var extra = new Dictionary<string, object?>
        {
            ["publishId"] = result.PublishId.ToString(),
            ["outfitFolderId"] = result.OutfitFolderId.ToString(),
            ["previousLookFolderId"] = result.PreviousLookFolderId.IsZero() ? null : result.PreviousLookFolderId.ToString(),
            ["itemIds"] = result.ItemIds.Select(i => i.ToString()).ToList(),
            ["warnings"] = result.Warnings,
            ["alreadyPublished"] = result.AlreadyPublished,
        };
        if (result.Refused)
        {
            var first = result.Refusals[0];
            extra["refusals"] = result.Refusals.Select(r => new Dictionary<string, object?> { ["code"] = r.Code, ["message"] = r.Message }).ToList();
            Reply(res, RefusalStatus(first.Code), first.Code, string.Join(" ", result.Refusals.Select(r => r.Message)), extra);
        }
        else if (result.Partial)
        {
            extra["failures"] = result.Failures.Select(f => new Dictionary<string, object?> { ["step"] = f.Step, ["what"] = f.What, ["message"] = f.Message }).ToList();
            Reply(res, 500, "partial", result.Summary, extra);
        }
        else
            Reply(res, 200, "ok", result.Summary, extra);
    }

    static int RefusalStatus(string code) => code switch
    {
        "bundle-too-large" => 413,
        "cap-reached" or "folder-conflict" or "item-conflict" or "asset-conflict" => 409,
        _ => 422, // invalid-bundle, wrong-owner, wrong-creator, texture-missing, no-inventory
    };

    sealed class BundleRefusal : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public BundleRefusal(int status, string code, string message) : base(message) { Status = status; Code = code; }
    }

    /// <summary>The zip's entries as a loaded bundle. Counts, sizes and names are checked before and while reading, so a small zip cannot expand past the limits.</summary>
    BundleFiles.Loaded Unpack(byte[] zipBytes)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new BundleRefusal(400, "bundle-not-a-zip", "The body is not a zip file. Send the bundle folder (bundle.json and its asset files) as a zip."); }
        using (zip)
        {
            if (zip.Entries.Count > _s.MaxFiles)
                throw new BundleRefusal(413, "too-many-files", $"The bundle holds {zip.Entries.Count} files; this grid allows {_s.MaxFiles}.");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.Length == 0 || name.EndsWith('/') || name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Contains('\0'))
                    throw new BundleRefusal(400, "bundle-bad-entry", "The zip holds a folder or a file name with a path in it. Every file must sit at the top level with a plain name.");
                if (files.ContainsKey(name)) throw new BundleRefusal(400, "bundle-bad-entry", $"The zip holds '{name}' twice.");
                long limit = name == BundleFiles.ManifestName ? MaxManifestBytes : _s.MaxAssetBytes;
                if (entry.Length > limit)
                    throw new BundleRefusal(413, "asset-too-large", $"'{name}' is {entry.Length} bytes; this grid allows {limit} for one file.");
                total += entry.Length;
                if (total > _s.MaxBundleBytes)
                    throw new BundleRefusal(413, "bundle-too-large", $"The bundle unpacks to more than the {_s.MaxBundleBytes} bytes this grid allows.");
                using var s = entry.Open();
                var data = ReadBounded(s, limit, out var lied);
                if (lied) throw new BundleRefusal(413, "asset-too-large", $"'{name}' is larger than the {limit} bytes this grid allows for one file.");
                files[name] = data;
            }

            if (!files.TryGetValue(BundleFiles.ManifestName, out var manifest))
                throw new BundleRefusal(400, "bundle-no-manifest", "The zip has no bundle.json.");
            Bundle bundle;
            try { bundle = BundleFiles.FromJson(manifest); }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            { throw new BundleRefusal(400, "bundle-bad-manifest", "bundle.json could not be read: " + e.Message); }

            var content = new Dictionary<UUID, byte[]>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BundleFiles.ManifestName };
            foreach (var a in bundle.Assets)
            {
                listed.Add(a.File);
                if (files.TryGetValue(a.File, out var bytes)) content[a.Id] = bytes;
            }
            var unlisted = files.Keys.Where(n => !listed.Contains(n)).Order(StringComparer.Ordinal).ToList();
            return new BundleFiles.Loaded(bundle, content, unlisted);
        }
    }

    // ---- small helpers ---------------------------------------------------------------------------------------------
    /// <summary>Reads at most <paramref name="max"/> bytes; <paramref name="tooBig"/> says the stream held more (the body is then not used).</summary>
    static byte[] ReadBounded(Stream? input, long max, out bool tooBig)
    {
        tooBig = false;
        if (input == null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max) { tooBig = true; return Array.Empty<byte>(); }
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>A name from a request, made safe to put in one log line (no control characters, bounded).</summary>
    static string Printable(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Take(80)) sb.Append(char.IsControl(ch) ? '?' : ch);
        return sb.ToString();
    }

    void TooMany(IOSHttpResponse res, TimeSpan wait, string what)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        res.AddHeader("Retry-After", seconds.ToString());
        Reply(res, 429, "rate-limited", $"{what} Try again in {seconds} seconds.", new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
    }

    static void Reply(IOSHttpResponse res, int status, string code, string message, Dictionary<string, object?>? extra = null)
    {
        var body = new Dictionary<string, object?> { ["ok"] = status < 300, ["code"] = code, ["message"] = message };
        if (extra != null) foreach (var (k, v) in extra) body[k] = v;
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(body);
    }
}
