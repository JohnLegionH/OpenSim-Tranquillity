/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.CharacterPublish;

/// <summary>
/// Robust connector for the character publish endpoint (see <see cref="CharacterPublishHandler"/>). It does nothing unless an operator
/// adds it to [ServiceList]:
/// <code>
///     CharacterPublishConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:CharacterPublishServerConnector"
/// </code>
/// and, once added, it starts only if the [CharacterPublish] section holds a service key (or says plainly that there is none), the
/// creator account, and the four service modules. Otherwise it logs each missing setting and throws; Robust's connector loader logs
/// that the connector failed to load and goes on without it, so the endpoint is simply not there.
/// </summary>
public class CharacterPublishServerConnector : ServiceConnector
{
    public CharacterPublishServerConnector(IConfigSource config, IHttpServer server, string configName) :
            base(config, server, configName)
    {
        var log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);
        Start(config, string.IsNullOrEmpty(configName) ? CharacterPublishSettings.DefaultSection : configName, log,
            handler => server.AddSimpleStreamHandler(handler, true));
    }

    /// <summary>
    /// Reads and checks the settings, loads the four services, and hands the handler to <paramref name="register"/>.
    /// Throws <see cref="InvalidOperationException"/> (after logging every problem) when the endpoint cannot start safely.
    /// </summary>
    public static CharacterPublishHandler Start(IConfigSource config, string section, ILogger log, Action<ISimpleStreamHandler> register, Func<DateTime>? clock = null)
    {
        var settings = CharacterPublishSettings.Read(config, section, out var problems);
        if (problems.Count > 0)
        {
            foreach (var p in problems) log.LogError("[CHARACTER PUBLISH]: not started: {0}", p);
            throw new InvalidOperationException("The character publish endpoint is not started: " + string.Join(" ", problems));
        }

        T Load<T>(string setting, string module) where T : class
        {
            var service = ServerUtils.LoadPlugin<T>(module, new object[] { config });
            if (service == null)
            {
                var why = $"[{section}] {setting} = \"{module}\" did not load a {typeof(T).Name}.";
                log.LogError("[CHARACTER PUBLISH]: not started: {0}", why);
                throw new InvalidOperationException("The character publish endpoint is not started: " + why);
            }
            return service;
        }

        var auth = Load<IAuthenticationService>("AuthenticationServiceModule", settings.AuthenticationServiceModule);
        var accounts = Load<IUserAccountService>("UserAccountServiceModule", settings.UserAccountServiceModule);
        var assets = Load<IAssetService>("AssetServiceModule", settings.AssetServiceModule);
        var inventory = Load<IInventoryService>("InventoryServiceModule", settings.InventoryServiceModule);

        if (!settings.RequireServiceKey)
            log.LogWarning("[CHARACTER PUBLISH]: *** WARNING *** RequireServiceKey is off: any caller that can reach this port may use the publish endpoint (it still needs a resident's login). Turn it on unless the port is closed to everyone but your publishing service.");
        if (settings.AllowInsecureHttp)
            log.LogWarning("[CHARACTER PUBLISH]: *** WARNING *** AllowInsecureHttp is on: passwords, tokens and the service key may cross the network unencrypted. Use it only to test on a closed network.");

        var handler = new CharacterPublishHandler(settings, auth, accounts, assets, inventory, log, clock);
        register(handler);
        log.LogInformation("[CHARACTER PUBLISH]: ready on {0}: HTTPS required {1}, service key required {2}, trusted proxies {3}, token {4} minutes, up to {5} outfits per resident",
            CharacterPublishHandler.BasePath, !settings.AllowInsecureHttp, settings.RequireServiceKey, settings.TrustedProxies.Count, settings.TokenMinutes, settings.MaxOutfitsPerResident);
        return handler;
    }
}
