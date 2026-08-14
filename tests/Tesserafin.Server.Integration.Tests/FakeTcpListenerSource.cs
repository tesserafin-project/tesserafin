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

/// <summary>Fixed listener observations. Binds nothing.</summary>
public sealed class FakeTcpListenerSource : ITcpListenerSource
{
    public IReadOnlyList<PortListenerObservation> Observe(IReadOnlyList<int> ports)
        => ports.Select(p => new PortListenerObservation(
            p,
            p == 443 ? ListenerObservationOutcome.ObservedListener : ListenerObservationOutcome.NoListenerObserved)).ToList();
}
