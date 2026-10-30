/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nwc.XmlRpc;
using OpenSim.Framework.Servers;

namespace OpenSim.Server.GridServer;

public sealed class LocalShutdownService : IHostedService
{
    public LocalShutdownService(ILogger<LocalShutdownService> logger, IServerBase serverBase)
    {
    }

    public bool Listening => false;

    public int Port => 0;

    public IPAddress? ListenAddress => null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public XmlRpcResponse HandleShutdown(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        return new XmlRpcResponse { Value = new Hashtable() };
    }
}
