using System;
using System.Linq;
using System.Net;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// What kind of address an address is.
/// </summary>
/// <remarks>
/// The classification feeds every topology rule, so a mistake here is a mistake in every
/// conclusion drawn downstream. The RFC 6598 cases matter most: shared address space is the only
/// carrier-grade NAT signal a host can observe about itself, and folding it into "private" would
/// destroy the evidence silently.
/// </remarks>
public sealed class AddressClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1", AddressClass.Loopback)]
    [InlineData("127.1.2.3", AddressClass.Loopback)]
    [InlineData("::1", AddressClass.Loopback)]
    [InlineData("169.254.10.1", AddressClass.LinkLocal)]
    [InlineData("fe80::1", AddressClass.LinkLocal)]
    [InlineData("10.0.0.1", AddressClass.Private)]
    [InlineData("10.255.255.254", AddressClass.Private)]
    [InlineData("172.16.0.1", AddressClass.Private)]
    [InlineData("172.31.255.254", AddressClass.Private)]
    [InlineData("192.168.1.1", AddressClass.Private)]
    [InlineData("fd00::1", AddressClass.Private)]
    [InlineData("100.64.0.1", AddressClass.SharedAddressSpace)]
    [InlineData("100.100.50.50", AddressClass.SharedAddressSpace)]
    [InlineData("100.127.255.254", AddressClass.SharedAddressSpace)]
    [InlineData("203.0.113.7", AddressClass.GloballyRoutable)]
    [InlineData("8.8.8.8", AddressClass.GloballyRoutable)]
    [InlineData("2001:db8::1", AddressClass.GloballyRoutable)]
    [InlineData("224.0.0.1", AddressClass.Multicast)]
    [InlineData("ff02::1", AddressClass.Multicast)]
    [InlineData("0.0.0.0", AddressClass.Unspecified)]
    [InlineData("::", AddressClass.Unspecified)]
    [InlineData("240.0.0.1", AddressClass.Other)]
    public void ClassifiesAnAddress(string address, AddressClass expected)
    {
        Assert.Equal(expected, AddressClassifier.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("172.15.255.254")]
    [InlineData("172.32.0.1")]
    public void AddressesAdjacentToRfc1918AreNotPrivate(string address)
    {
        // The 172.16/12 boundary is the one people get wrong, and getting it wrong in the
        // permissive direction would classify a public address as private.
        Assert.Equal(AddressClass.GloballyRoutable, AddressClassifier.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("100.63.255.254")]
    [InlineData("100.128.0.1")]
    public void AddressesAdjacentToSharedSpaceAreNotSharedSpace(string address)
    {
        // 100.64.0.0/10 is a ten-bit prefix, not a whole /8. Treating all of 100/8 as shared space
        // would manufacture a CGNAT signal on ordinary public addresses.
        Assert.Equal(AddressClass.GloballyRoutable, AddressClassifier.Classify(IPAddress.Parse(address)));
    }

    [Fact]
    public void SharedAddressSpaceIsNotFoldedIntoPrivate()
    {
        Assert.NotEqual(AddressClass.Private, AddressClassifier.Classify(IPAddress.Parse("100.64.0.1")));
        Assert.Equal(AddressClass.SharedAddressSpace, AddressClassifier.Classify(IPAddress.Parse("100.64.0.1")));
    }

    [Fact]
    public void IPv4MappedIPv6IsNormalizedToIPv4()
    {
        var mapped = IPAddress.Parse("::ffff:192.168.1.5");

        Assert.Equal(IPAddress.Parse("192.168.1.5"), AddressClassifier.Normalize(mapped));
        Assert.Equal(AddressClass.Private, AddressClassifier.Classify(mapped));
    }

    [Fact]
    public void AMappedAndAnUnmappedFormOfOneAddressCollapseToOneEntry()
    {
        // Otherwise a single host address would be counted twice and could satisfy an IPv4 fact
        // and an IPv6 fact at the same time.
        var result = AddressClassifier.ClassifySet(new[]
        {
            IPAddress.Parse("192.168.1.5"),
            IPAddress.Parse("::ffff:192.168.1.5")
        });

        Assert.Single(result);
        Assert.Equal(IPAddress.Parse("192.168.1.5"), result[0].Address);
    }

    [Fact]
    public void DuplicatesAreRemoved()
    {
        var result = AddressClassifier.ClassifySet(new[]
        {
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("10.0.0.1")
        });

        Assert.Single(result);
    }

    [Fact]
    public void OrderingIsStableRegardlessOfInputOrder()
    {
        var forwards = AddressClassifier.ClassifySet(new[]
        {
            IPAddress.Parse("192.168.1.5"),
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("127.0.0.1")
        });

        var backwards = AddressClassifier.ClassifySet(new[]
        {
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("192.168.1.5")
        });

        Assert.Equal(
            forwards.Select(x => x.Address.ToString()),
            backwards.Select(x => x.Address.ToString()));
    }

    [Fact]
    public void NullEntriesAreSkippedRatherThanThrowing()
    {
        // Interface enumeration is racy; a diagnostic that throws while the network is changing is
        // a diagnostic nobody can run while troubleshooting the network.
        var result = AddressClassifier.ClassifySet(new IPAddress?[] { null, IPAddress.Parse("10.0.0.1"), null }!);

        Assert.Single(result);
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => AddressClassifier.Classify(null!));
        Assert.Throws<ArgumentNullException>(() => AddressClassifier.Normalize(null!));
        Assert.Throws<ArgumentNullException>(() => AddressClassifier.ClassifySet(null!));
    }
}
