using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>Fixed posture. Reads no configuration.</summary>
public sealed class FakeNetworkPostureSource : INetworkPostureSource
{
    public BackendPostureObservation Backend { get; set; }
        = new(true, BackendBindPosture.LoopbackOnly, false, 8096, 8920);

    public ProxyTrustObservation Proxy { get; set; }
        = new(new[] { "10.0.0.1" }, 1, true);

    public BackendPostureObservation GetBackendPosture() => Backend;

    public ProxyTrustObservation GetProxyTrust() => Proxy;
}
