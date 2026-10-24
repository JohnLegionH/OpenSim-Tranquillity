/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Net;
using Nini.Config;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.CharacterPublish;

/// <summary>
/// The [CharacterPublish] section. Every setting has a default; the ones that have none (the service key, the creator account and
/// the four service modules) are required, and <see cref="Read"/> reports each one that is missing so the connector can refuse to start.
/// </summary>
public sealed class CharacterPublishSettings
{
    public const string DefaultSection = "CharacterPublish";

    /// <summary>The key a caller sends in the X-Publish-Key header. Required unless <see cref="RequireServiceKey"/> is turned off.</summary>
    public string ServiceKey { get; init; } = "";
    /// <summary>On by default. Turning it off lets any caller that can reach the port use the endpoint; the connector says so in the log at startup.</summary>
    public bool RequireServiceKey { get; init; } = true;
    /// <summary>Off by default: every request (login, which carries a password, and the others, which carry the key or a token) is refused unless it arrived over HTTPS.</summary>
    public bool AllowInsecureHttp { get; init; }
    /// <summary>Addresses or CIDR ranges of reverse proxies whose X-Forwarded-Proto and X-Forwarded-For headers are believed.</summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = Array.Empty<IPNetwork>();

    /// <summary>How long a login token lives, and how long each successful use extends it.</summary>
    public int TokenMinutes { get; init; } = 30;

    public string AuthenticationServiceModule { get; init; } = "";
    public string UserAccountServiceModule { get; init; } = "";
    public string AssetServiceModule { get; init; } = "";
    public string InventoryServiceModule { get; init; } = "";

    /// <summary>The grid account every published item names as its creator; a bundle made by anyone else is refused.</summary>
    public UUID CreatorAccount { get; init; }

    // ---- size limits
    public long MaxBundleBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxAssetBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Files in the bundle zip, bundle.json included.</summary>
    public int MaxFiles { get; init; } = 65;

    // ---- outfit rules
    public int MaxOutfitsPerResident { get; init; } = 20;
    public bool SaveCurrentLook { get; init; } = true;
    public bool MissingTextureIsWarning { get; init; }

    // ---- rate limits (a hit past the limit is HTTP 429 with Retry-After)
    public int RequestsPerAddressPerMinute { get; init; } = 120;
    public int LoginAttemptsPerAddressPerMinute { get; init; } = 10;
    public int LoginAttemptsPerNamePerMinute { get; init; } = 5;
    public int PublishesPerResidentPerHour { get; init; } = 10;

    /// <summary>
    /// Reads the section. Returns the settings and every problem found (empty when the settings are usable). With no section at all the
    /// only problem is that, and the settings are the defaults.
    /// </summary>
    public static CharacterPublishSettings Read(IConfigSource config, string section, out List<string> problems)
    {
        problems = new List<string>();
        var c = config.Configs[section];
        if (c == null)
        {
            problems.Add($"There is no [{section}] section in the configuration.");
            return new CharacterPublishSettings();
        }

        var found = problems;
        bool Flag(string name, bool dflt) => c.GetBoolean(name, dflt);
        string Text(string name) => (c.GetString(name, "") ?? "").Trim();
        int Num(string name, int dflt, int min, int max)
        {
            var v = c.GetInt(name, dflt);
            if (v < min || v > max) { found.Add($"[{section}] {name} = {v} is outside {min} to {max}."); return dflt; }
            return v;
        }
        long Big(string name, long dflt, long min, long max)
        {
            var v = c.GetLong(name, dflt);
            if (v < min || v > max) { found.Add($"[{section}] {name} = {v} is outside {min} to {max}."); return dflt; }
            return v;
        }
        string Required(string name, string why)
        {
            var v = Text(name);
            if (v.Length == 0) found.Add($"[{section}] {name} is not set; {why}.");
            return v;
        }

        var requireKey = Flag("RequireServiceKey", true);
        var key = Text("ServiceKey");
        if (requireKey && key.Length == 0)
            problems.Add($"[{section}] ServiceKey is not set; every request must carry a service key (set one, or set RequireServiceKey = false to run without, which is logged as a warning).");
        else if (requireKey && key.Length < 16)
            problems.Add($"[{section}] ServiceKey is shorter than 16 characters.");

        var proxies = new List<IPNetwork>();
        foreach (var raw in Text("TrustedProxies").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPNetwork.TryParse(raw, out var net)) proxies.Add(net);
            else if (IPAddress.TryParse(raw, out var ip)) proxies.Add(new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else problems.Add($"[{section}] TrustedProxies has '{raw}', which is not an address or a CIDR range.");
        }

        var creatorText = Required("CreatorAccount", "it is the grid account named as creator on every published item");
        UUID creator = UUID.Zero;
        if (creatorText.Length > 0 && (!UUID.TryParse(creatorText, out creator) || creator.IsZero()))
            problems.Add($"[{section}] CreatorAccount is not an account id.");

        return new CharacterPublishSettings
        {
            ServiceKey = key,
            RequireServiceKey = requireKey,
            AllowInsecureHttp = Flag("AllowInsecureHttp", false),
            TrustedProxies = proxies,
            TokenMinutes = Num("TokenMinutes", 30, 1, 24 * 60),
            AuthenticationServiceModule = Required("AuthenticationServiceModule", "login needs the grid's authentication service, for example \"OpenSim.Services.AuthenticationService.dll:PasswordAuthenticationService\""),
            UserAccountServiceModule = Required("UserAccountServiceModule", "login finds the account by name, for example \"OpenSim.Services.UserAccountService.dll:UserAccountService\""),
            AssetServiceModule = Required("AssetServiceModule", "for example \"OpenSim.Services.AssetService.dll:AssetService\""),
            InventoryServiceModule = Required("InventoryServiceModule", "for example \"OpenSim.Services.InventoryService.dll:XInventoryService\""),
            CreatorAccount = creator,
            MaxBundleBytes = Big("MaxBundleBytes", 64L * 1024 * 1024, 1024, 1L << 31),
            MaxAssetBytes = Num("MaxAssetBytes", 16 * 1024 * 1024, 1024, 256 * 1024 * 1024),
            MaxFiles = Num("MaxFiles", 65, 2, 1000),
            MaxOutfitsPerResident = Num("MaxOutfitsPerResident", 20, 1, 10000),
            SaveCurrentLook = Flag("SaveCurrentLook", true),
            MissingTextureIsWarning = Flag("MissingTextureIsWarning", false),
            RequestsPerAddressPerMinute = Num("RequestsPerAddressPerMinute", 120, 1, 100000),
            LoginAttemptsPerAddressPerMinute = Num("LoginAttemptsPerAddressPerMinute", 10, 1, 100000),
            LoginAttemptsPerNamePerMinute = Num("LoginAttemptsPerNamePerMinute", 5, 1, 100000),
            PublishesPerResidentPerHour = Num("PublishesPerResidentPerHour", 10, 1, 100000),
        };
    }
}
