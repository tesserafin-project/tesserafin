using System;
using System.Net;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// How a derived bind set becomes a posture.
/// </summary>
/// <remarks>
/// The input here is whatever the server's own network manager produced, so these cases mirror
/// the three shapes it can return: a dual-stack wildcard, an IPv4 wildcard, or a list of concrete
/// interface addresses.
/// </remarks>
public sealed class ServerNetworkPostureSourceTests
{
    private static IPData Ip(string address) => new(IPAddress.Parse(address), null, "eth0");

    [Fact]
    public void ADualStackWildcardIsWildcard()
    {
        var result = ServerNetworkPostureSource.ClassifyBindSet(new[] { new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any) });
        Assert.Equal(BackendBindPosture.Wildcard, result);
    }

    [Fact]
    public void AnIPv4WildcardIsWildcard()
    {
        var result = ServerNetworkPostureSource.ClassifyBindSet(new[] { new IPData(IPAddress.Any, NetworkConstants.IPv4Any) });
        Assert.Equal(BackendBindPosture.Wildcard, result);
    }

    [Fact]
    public void AWildcardAnywhereInTheSetDecidesTheAnswer()
    {
        // One wildcard listener accepts on every interface no matter how narrow its neighbours
        // are, so a mixed set is not "mostly loopback".
        var result = ServerNetworkPostureSource.ClassifyBindSet(new[]
        {
            Ip("127.0.0.1"),
            new IPData(IPAddress.Any, NetworkConstants.IPv4Any)
        });

        Assert.Equal(BackendBindPosture.Wildcard, result);
    }

    [Fact]
    public void OnlyLoopbackAddressesAreLoopbackOnly()
    {
        var result = ServerNetworkPostureSource.ClassifyBindSet(new[] { Ip("127.0.0.1"), Ip("::1") });
        Assert.Equal(BackendBindPosture.LoopbackOnly, result);
    }

    [Fact]
    public void ConcreteNonLoopbackAddressesAreExplicit()
    {
        var result = ServerNetworkPostureSource.ClassifyBindSet(new[] { Ip("192.168.1.10") });
        Assert.Equal(BackendBindPosture.ExplicitAddresses, result);
    }

    [Fact]
    public void AMixOfLoopbackAndLanIsExplicitRatherThanLoopbackOnly()
    {
        var result = ServerNetworkPostureSource.ClassifyBindSet(new[] { Ip("127.0.0.1"), Ip("192.168.1.10") });
        Assert.Equal(BackendBindPosture.ExplicitAddresses, result);
    }

    [Fact]
    public void AnEmptyOrAbsentBindSetIsUnknownRatherThanSafe()
    {
        // Nothing derived is not the same as nothing exposed, and reporting it as loopback-only
        // would invent a constraint that was never established.
        Assert.Equal(BackendBindPosture.Unknown, ServerNetworkPostureSource.ClassifyBindSet(Array.Empty<IPData>()));
        Assert.Equal(BackendBindPosture.Unknown, ServerNetworkPostureSource.ClassifyBindSet(null!));
    }
}
