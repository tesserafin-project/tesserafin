using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>
/// The real application, with only the environment-facing diagnostic sources replaced (R1-P, #248).
/// </summary>
/// <remarks>
/// WHAT IS REAL HERE, DELIBERATELY: the MVC application-part configuration and therefore controller
/// discovery, routing, the JSON options, <c>CustomAuthenticationHandler</c>, the authorization
/// policy, <c>AuthService</c>, the controller, the projector, and the collector's singleton
/// lifetime. Replacing any of those would turn an HTTP proof into an assertion about a mock.
///
/// WHAT IS FAKED, AND ONLY THIS: the four sources that would otherwise read the machine — local
/// addresses, listening sockets, hostname resolution and network posture — plus time. That is what
/// makes the suite deterministic, and it is also what guarantees no test performs a DNS lookup or
/// binds a port.
///
/// The replacement follows the pattern <see cref="HealthApplicationFactory"/> already establishes:
/// call the base configuration first, then re-register through the ordinary container so the later
/// registration wins.
/// </remarks>
public sealed class RemoteAccessDiagnosticsApplicationFactory : TesserafinApplicationFactory
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _adminToken;

    public FakeHostnameResolver Resolver { get; } = new();

    public FakeLocalAddressSource Addresses { get; } = new();

    public FakeTcpListenerSource Listeners { get; } = new();

    public FakeNetworkPostureSource Posture { get; } = new();

    public AdvancingTimeProvider Time { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>
    /// The elevated-administrator access token, obtained once through the real startup wizard and
    /// the real authentication endpoint.
    /// </summary>
    /// <remarks>
    /// Cached deliberately. <c>AuthHelper.CompleteStartupAsync</c> completes the first-time-setup
    /// wizard, which by construction can only happen once per host; calling it per test against a
    /// shared fixture makes every test after the first fail for a reason that has nothing to do
    /// with the endpoint. One token, obtained through the canonical header path, reused.
    /// </remarks>
    public async Task<string> AdminTokenAsync(HttpClient client)
    {
        await _tokenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _adminToken ??= await AuthHelper.CompleteStartupAsync(client).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILocalAddressSource>(Addresses);
            services.AddSingleton<ITcpListenerSource>(Listeners);
            services.AddSingleton<IHostnameResolver>(Resolver);
            services.AddSingleton<INetworkPostureSource>(Posture);
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }
}
