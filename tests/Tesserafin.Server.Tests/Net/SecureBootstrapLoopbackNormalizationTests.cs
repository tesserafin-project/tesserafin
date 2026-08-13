using System;
using System.Linq;
using System.Net;
using Tesserafin.Common.Net;
using Tesserafin.Model.Net;
using Tesserafin.Server.Net;
using Xunit;

namespace Tesserafin.Server.Tests.Net;

/// <summary>
/// Secure bootstrap mode: what a derived bind set becomes once the mode is active.
/// </summary>
public sealed class SecureBootstrapLoopbackNormalizationTests
{
    private static IPData Ip(string address, string name = "eth0")
        => new IPData(IPAddress.Parse(address), null, name);

    [Fact]
    public void DualStackWildcard_BecomesBothLoopbacks()
    {
        // NetworkManager returns a single IPv6Any entry when both families are enabled, and Kestrel
        // binds it dual-mode. Reducing it to ::1 alone would break the documented bootstrap path,
        // which is an SSH forward to 127.0.0.1.
        var result = SecureBootstrap.ConstrainToLoopback(new[] { new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any) });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.Address.Equals(IPAddress.Loopback));
        Assert.Contains(result, x => x.Address.Equals(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void IPv4Wildcard_BecomesIPv4LoopbackOnly()
    {
        var result = SecureBootstrap.ConstrainToLoopback(new[] { new IPData(IPAddress.Any, NetworkConstants.IPv4Any) });

        Assert.Single(result);
        Assert.Equal(IPAddress.Loopback, result[0].Address);
    }

    [Fact]
    public void IPv6OnlyInterfaces_BecomeIPv6LoopbackOnly()
    {
        var result = SecureBootstrap.ConstrainToLoopback(new[] { Ip("fe80::1"), Ip("2001:db8::5") });

        Assert.Single(result);
        Assert.Equal(IPAddress.IPv6Loopback, result[0].Address);
    }

    [Fact]
    public void NoWildcardSurvivesNormalization()
    {
        var result = SecureBootstrap.ConstrainToLoopback(new[]
        {
            new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any),
            new IPData(IPAddress.Any, NetworkConstants.IPv4Any)
        });

        Assert.DoesNotContain(result, x => x.Address.Equals(IPAddress.Any));
        Assert.DoesNotContain(result, x => x.Address.Equals(IPAddress.IPv6Any));
        Assert.All(result, x => Assert.True(IPAddress.IsLoopback(x.Address)));
    }

    [Fact]
    public void NoConfiguredLanOrPublicAddressSurvivesNormalization()
    {
        var result = SecureBootstrap.ConstrainToLoopback(new[]
        {
            Ip("192.168.1.10"),
            Ip("10.4.4.4"),
            Ip("172.16.9.9"),
            Ip("203.0.113.7"),
            Ip("2001:db8::dead")
        });

        Assert.All(result, x => Assert.True(IPAddress.IsLoopback(x.Address)));
        Assert.DoesNotContain(result, x => x.Address.Equals(IPAddress.Parse("192.168.1.10")));
        Assert.DoesNotContain(result, x => x.Address.Equals(IPAddress.Parse("203.0.113.7")));
        Assert.DoesNotContain(result, x => x.Address.Equals(IPAddress.Parse("2001:db8::dead")));
    }

    [Fact]
    public void MixedFamilies_YieldOneLoopbackPerFamily()
    {
        var result = SecureBootstrap.ConstrainToLoopback(new[] { Ip("192.168.1.10"), Ip("2001:db8::dead") });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.Address.Equals(IPAddress.Loopback));
        Assert.Contains(result, x => x.Address.Equals(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void AnEmptyBindSetStaysEmpty()
    {
        // The mode NARROWS what the ordinary derivation produced. It never invents a listener that
        // would not otherwise have existed.
        Assert.Empty(SecureBootstrap.ConstrainToLoopback(Array.Empty<IPData>()));
    }

    [Fact]
    public void LoopbackInputIsIdempotent()
    {
        var once = SecureBootstrap.ConstrainToLoopback(new[] { new IPData(IPAddress.IPv6Any, NetworkConstants.IPv6Any) });
        var twice = SecureBootstrap.ConstrainToLoopback(once);

        Assert.Equal(
            once.Select(x => x.Address.ToString()).OrderBy(x => x, StringComparer.Ordinal),
            twice.Select(x => x.Address.ToString()).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void NullInputThrows()
    {
        Assert.Throws<ArgumentNullException>(() => SecureBootstrap.ConstrainToLoopback(null!));
    }
}
