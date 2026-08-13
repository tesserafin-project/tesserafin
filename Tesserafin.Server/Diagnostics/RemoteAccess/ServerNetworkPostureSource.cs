using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Extensions;
using Tesserafin.Server.Extensions;
using Tesserafin.Server.Net;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Reads the server's own effective networking posture by asking the code that decides it.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is produced by the same production path the server itself takes at startup:
/// the network manager derives the bind set, <see cref="SecureBootstrap"/> constrains it exactly
/// as <c>SetupTesserafinWebServer</c> would, and the forwarded-header posture comes from running
/// the real <c>ConfigureForwardHeaders</c> against a throwaway options object.
/// </para>
/// <para>
/// That indirection is the point. A diagnostic that re-implemented "empty known proxies means
/// forwarded headers off" would be a second interpretation of the trust boundary, and the first
/// time the two drifted apart the diagnostic would confidently describe a server that no longer
/// exists. Running the real code and reading the result cannot drift.
/// </para>
/// <para>
/// Nothing here writes. The options object is local, is discarded, and is never handed to the
/// pipeline.
/// </para>
/// </remarks>
public sealed class ServerNetworkPostureSource : INetworkPostureSource
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly INetworkManager _networkManager;
    private readonly IConfiguration _startupConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerNetworkPostureSource"/> class.
    /// </summary>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="networkManager">The network manager.</param>
    /// <param name="startupConfiguration">The startup configuration.</param>
    public ServerNetworkPostureSource(
        IServerConfigurationManager configurationManager,
        INetworkManager networkManager,
        IConfiguration startupConfiguration)
    {
        _configurationManager = configurationManager;
        _networkManager = networkManager;
        _startupConfiguration = startupConfiguration;
    }

    /// <inheritdoc />
    public BackendPostureObservation GetBackendPosture()
    {
        var networkConfiguration = _configurationManager.GetNetworkConfiguration();
        var secureBootstrapActive = SecureBootstrap.IsEnabled(_startupConfiguration);

        BackendBindPosture posture;
        try
        {
            var derived = _networkManager.GetAllBindInterfaces(false);
            if (secureBootstrapActive)
            {
                derived = SecureBootstrap.ConstrainToLoopback(derived);
            }

            posture = ClassifyBindSet(derived);
        }
        catch (InvalidOperationException)
        {
            posture = BackendBindPosture.Unknown;
        }

        return new BackendPostureObservation(
            secureBootstrapActive,
            posture,
            _startupConfiguration.UseUnixSocket(),
            networkConfiguration.InternalHttpPort,
            networkConfiguration.InternalHttpsPort);
    }

    /// <inheritdoc />
    public ProxyTrustObservation GetProxyTrust()
    {
        var networkConfiguration = _configurationManager.GetNetworkConfiguration();
        var configured = networkConfiguration.KnownProxies ?? Array.Empty<string>();

        // The real configuration routine, run against a throwaway options object purely so its
        // decision can be read back. Discarded immediately; nothing downstream ever sees it.
        var probe = new ForwardedHeadersOptions();
        probe.KnownProxies.Clear();
        probe.KnownIPNetworks.Clear();
        ApiServiceCollectionExtensions.ConfigureForwardHeaders(networkConfiguration, probe);

        var parsed = probe.KnownProxies.Count + probe.KnownIPNetworks.Count;

        return new ProxyTrustObservation(
            configured,
            parsed,
            probe.ForwardedHeaders != ForwardedHeaders.None);
    }

    /// <summary>
    /// Classifies a derived bind set.
    /// </summary>
    /// <remarks>
    /// A wildcard anywhere in the set decides the answer: one wildcard listener accepts on every
    /// interface regardless of how narrow its neighbours are.
    /// </remarks>
    /// <param name="bindSet">The bind set the server would use.</param>
    /// <returns>The posture.</returns>
    internal static BackendBindPosture ClassifyBindSet(IReadOnlyList<Tesserafin.Model.Net.IPData> bindSet)
    {
        if (bindSet is null || bindSet.Count == 0)
        {
            return BackendBindPosture.Unknown;
        }

        var allLoopback = true;
        var anyGloballyRoutable = false;

        foreach (var entry in bindSet)
        {
            var address = entry.Address;

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return BackendBindPosture.Wildcard;
            }

            allLoopback &= IPAddress.IsLoopback(address);

            // One globally routable entry decides the answer for the same reason one wildcard
            // does: the most exposed listener in the set is what the set actually offers.
            anyGloballyRoutable |= AddressClassifier.Classify(address) == AddressClass.GloballyRoutable;
        }

        if (allLoopback)
        {
            return BackendBindPosture.LoopbackOnly;
        }

        return anyGloballyRoutable
            ? BackendBindPosture.ExplicitGloballyRoutableAddresses
            : BackendBindPosture.ExplicitPrivateAddresses;
    }
}
