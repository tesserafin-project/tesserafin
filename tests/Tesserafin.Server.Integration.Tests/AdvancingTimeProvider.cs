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

/// <summary>Time that advances by a fixed step per read, so two reports never share a timestamp.</summary>
public sealed class AdvancingTimeProvider : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
