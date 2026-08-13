using System.Net;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// The local address source, driven against this machine.
/// </summary>
public sealed class SystemLocalAddressSourceTests
{
    [Fact]
    public void EnumerationSucceedsAndYieldsOnlyAddresses()
    {
        var source = new SystemLocalAddressSource();

        var addresses = source.GetUnicastAddresses();

        Assert.NotNull(addresses);
        Assert.All(addresses, a => Assert.NotNull(a));
    }

    [Fact]
    public void EveryAddressClassifiesWithoutThrowing()
    {
        var addresses = new SystemLocalAddressSource().GetUnicastAddresses();
        var classified = AddressClassifier.ClassifySet(addresses);

        Assert.All(classified, c => Assert.NotEqual(AddressClass.None, c.Class));
    }

    [Fact]
    public void TheResultIsAFlatAddressListWithNoInterfaceMetadataAttached()
    {
        // The interface holds IPAddress and nothing else, so there is no route by which a MAC
        // address or an interface name could reach a report.
        var elementType = typeof(ILocalAddressSource)
            .GetMethod(nameof(ILocalAddressSource.GetUnicastAddresses))!
            .ReturnType
            .GetGenericArguments()[0];

        Assert.Equal(typeof(IPAddress), elementType);
    }
}
