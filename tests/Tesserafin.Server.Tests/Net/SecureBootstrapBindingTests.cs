using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;
using Xunit;
using TesserafinConfiguration = Tesserafin.Controller.Extensions.ConfigurationExtensions;
using TesserafinWebHost = Tesserafin.Server.Extensions.WebHostBuilderExtensions;

namespace Tesserafin.Server.Tests.Net;

/// <summary>
/// What Kestrel is actually told to listen on.
/// </summary>
/// <remarks>
/// <c>WebHostBuilderExtensions.SetupTesserafinWebServer</c> is the single path to
/// <c>KestrelServerOptions.Listen</c> for BOTH listeners the server opens — the pre-startup setup
/// server and the main host. These tests drive that method and read back the endpoints it
/// configured, so they judge the real effect rather than the intent.
/// </remarks>
public sealed class SecureBootstrapBindingTests
{
    private static readonly IPData[] _dualStackWildcard = { new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any) };

    private static IConfiguration StartupConfig(bool secureBootstrap, bool remoteAccessAlsoRequested = false)
    {
        var values = new Dictionary<string, string?>
        {
            [TesserafinConfiguration.SecureBootstrapKey] = secureBootstrap ? bool.TrueString : bool.FalseString,
            [TesserafinConfiguration.BindToUnixSocketKey] = bool.FalseString
        };

        if (remoteAccessAlsoRequested)
        {
            // Both spellings an implementation might reach for. Neither may influence the bind.
            values["EnableRemoteAccess"] = bool.TrueString;
            values["network:EnableRemoteAccess"] = bool.TrueString;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// Reads back the endpoints a <see cref="KestrelServerOptions"/> was configured with.
    /// </summary>
    /// <remarks>
    /// Kestrel keeps its configured endpoints internal, so this reflects. It asserts that it FOUND
    /// them: if a future Kestrel renames the member, this throws and the gate goes red rather than
    /// quietly comparing an empty list against an empty expectation.
    /// </remarks>
    private static IReadOnlyList<IPEndPoint> ConfiguredEndpoints(KestrelServerOptions options)
    {
        var found = new List<IPEndPoint>();
        var inspected = 0;

        foreach (var member in typeof(KestrelServerOptions)
                     .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value;
            switch (member)
            {
                case PropertyInfo property when property.GetIndexParameters().Length == 0:
                    value = property.GetValue(options);
                    break;
                case FieldInfo field:
                    value = field.GetValue(options);
                    break;
                default:
                    continue;
            }

            if (value is not IEnumerable enumerable || value is string)
            {
                continue;
            }

            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                var endpointProperty = item.GetType().GetProperty("IPEndPoint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (endpointProperty?.GetValue(item) is IPEndPoint endpoint)
                {
                    inspected++;
                    found.Add(endpoint);
                }
            }
        }

        Assert.True(
            inspected > 0,
            "Could not read any configured endpoint back from KestrelServerOptions. This gate cannot be trusted until the reader is repaired.");

        return found.Distinct().ToList();
    }

    private static IReadOnlyList<IPEndPoint> Configure(
        IReadOnlyList<IPData> addresses,
        bool secureBootstrap,
        int httpPort = 8096,
        bool remoteAccessAlsoRequested = false)
    {
        var options = new KestrelServerOptions();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");

        var startupConfig = StartupConfig(secureBootstrap, remoteAccessAlsoRequested);
        var context = new WebHostBuilderContext
        {
            HostingEnvironment = environment.Object,
            Configuration = startupConfig
        };

        TesserafinWebHost.SetupTesserafinWebServer(
            addresses,
            httpPort,
            null,
            null,
            startupConfig,
            Mock.Of<IApplicationPaths>(),
            NullLogger.Instance,
            context,
            options);

        return ConfiguredEndpoints(options);
    }

    [Fact]
    public void SecureMode_BindsIPv4LoopbackOnly()
    {
        var endpoints = Configure(new[] { new IPData(IPAddress.Any, NetworkConstants.IPv4Any) }, secureBootstrap: true);

        Assert.Single(endpoints);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 8096), endpoints[0]);
    }

    [Fact]
    public void SecureMode_BindsBothLoopbacksForADualStackWildcard()
    {
        var endpoints = Configure(_dualStackWildcard, secureBootstrap: true);

        Assert.Equal(2, endpoints.Count);
        Assert.Contains(new IPEndPoint(IPAddress.Loopback, 8096), endpoints);
        Assert.Contains(new IPEndPoint(IPAddress.IPv6Loopback, 8096), endpoints);
    }

    [Fact]
    public void SecureMode_NeverBindsAWildcard()
    {
        var endpoints = Configure(_dualStackWildcard, secureBootstrap: true);

        Assert.DoesNotContain(endpoints, e => e.Address.Equals(IPAddress.Any));
        Assert.DoesNotContain(endpoints, e => e.Address.Equals(IPAddress.IPv6Any));
    }

    [Fact]
    public void SecureMode_NeverBindsAConfiguredLanOrPublicAddress()
    {
        var endpoints = Configure(
            new[]
            {
                new IPData(IPAddress.Parse("192.168.1.10"), null, "eth0"),
                new IPData(IPAddress.Parse("203.0.113.7"), null, "eth1")
            },
            secureBootstrap: true);

        Assert.All(endpoints, e => Assert.True(IPAddress.IsLoopback(e.Address)));
    }

    [Fact]
    public void OrdinaryMode_KeepsTheDerivedBindSetExactly()
    {
        // The compatibility gate. Without the activation key the server binds precisely what
        // NetworkManager derived, wildcard included.
        var endpoints = Configure(_dualStackWildcard, secureBootstrap: false);

        Assert.Single(endpoints);
        Assert.Equal(new IPEndPoint(IPAddress.IPv6Any, 8096), endpoints[0]);
    }

    [Fact]
    public void OrdinaryMode_KeepsConfiguredLanAddresses()
    {
        var lan = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 8096);
        var endpoints = Configure(new[] { new IPData(IPAddress.Parse("192.168.1.10"), null, "eth0") }, secureBootstrap: false);

        Assert.Single(endpoints);
        Assert.Equal(lan, endpoints[0]);
    }

    [Fact]
    public void SecureMode_IsIndependentOfEnableRemoteAccess()
    {
        // EnableRemoteAccess is an application-layer allow/deny for non-LAN source addresses. It is
        // not a bind setting and must never widen a listener. This asks for both at once — secure
        // bootstrap AND remote access — and requires the confinement to win.
        var endpoints = Configure(_dualStackWildcard, secureBootstrap: true, remoteAccessAlsoRequested: true);

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.True(IPAddress.IsLoopback(e.Address)));
        Assert.DoesNotContain(endpoints, e => e.Address.Equals(IPAddress.IPv6Any));
    }

    [Fact]
    public void SecureMode_SurvivesRepeatedConfigurationWithTheSameStartupConfiguration()
    {
        // The restart path. Program.StartApp captures the startup configuration once and reuses it
        // across every iteration of the restart loop; both the setup server and the main host
        // rebuild their Kestrel options and come back through here. Configuring twice from one
        // configuration must give the same confinement both times.
        var first = Configure(_dualStackWildcard, secureBootstrap: true);
        var second = Configure(_dualStackWildcard, secureBootstrap: true);

        Assert.Equal(
            first.Select(e => e.ToString()).OrderBy(x => x, StringComparer.Ordinal),
            second.Select(e => e.ToString()).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(second, e => Assert.True(IPAddress.IsLoopback(e.Address)));
    }
}
