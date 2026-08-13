using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// What counts as a hostname.
/// </summary>
/// <remarks>
/// This is the input boundary of the only outbound operation in the slice. Everything it accepts
/// is handed to the system resolver, so every rejection here is a shape a caller cannot smuggle
/// through — a URL, a port, a credential, an IP literal. A validator that extracted the host out
/// of a URL instead of rejecting it would make the API quietly accept a form it does not document.
/// </remarks>
public sealed class HostnameInputTests
{
    [Theory]
    [InlineData("media.example.org", "media.example.org")]
    [InlineData("MEDIA.EXAMPLE.ORG", "media.example.org")]
    [InlineData("a-b.example.com", "a-b.example.com")]
    [InlineData("deep.sub.domain.example.co.uk", "deep.sub.domain.example.co.uk")]
    public void AcceptsAnOrdinaryHostname(string input, string expected)
    {
        Assert.True(HostnameInput.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("bücher.example.org", "xn--bcher-kva.example.org")]
    [InlineData("médias.example.com", "xn--mdias-bsa.example.com")]
    public void NormalizesAnInternationalizedHostname(string input, string expected)
    {
        Assert.True(HostnameInput.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    // Scheme.
    [InlineData("https://media.example.org")]
    [InlineData("http://media.example.org")]
    // Path, query, fragment.
    [InlineData("media.example.org/admin")]
    [InlineData("media.example.org?a=b")]
    [InlineData("media.example.org#frag")]
    // Credentials.
    [InlineData("user:pass@media.example.org")]
    [InlineData("user@media.example.org")]
    // Explicit port.
    [InlineData("media.example.org:8096")]
    [InlineData("media.example.org:443")]
    // Wildcard.
    [InlineData("*.example.org")]
    [InlineData("*")]
    // IP literals.
    [InlineData("192.168.1.1")]
    [InlineData("203.0.113.7")]
    [InlineData("::1")]
    [InlineData("[2001:db8::1]")]
    // Names that cannot be public.
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("server.localhost")]
    [InlineData("nas.local")]
    // Whitespace and control characters.
    [InlineData(" media.example.org")]
    [InlineData("media.example.org ")]
    [InlineData("media example.org")]
    [InlineData("media.example.org\n")]
    [InlineData("media.\texample.org")]
    // Malformed label structure.
    [InlineData("media..example.org")]
    [InlineData(".media.example.org")]
    [InlineData("media.example.org.")]
    [InlineData("-media.example.org")]
    [InlineData("media-.example.org")]
    // Single label: not a public name, and its meaning would depend on the host's search domains.
    [InlineData("media")]
    // Absent.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsAnythingThatIsNotAHostname(string? input)
    {
        Assert.False(HostnameInput.TryNormalize(input, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void RejectsAHostnameLongerThanDnsAllows()
    {
        var label = new string('a', 60);
        var tooLong = string.Join('.', label, label, label, label, label);

        Assert.False(HostnameInput.TryNormalize(tooLong, out _));
    }

    [Fact]
    public void RejectsALabelLongerThanDnsAllows()
    {
        Assert.False(HostnameInput.TryNormalize(new string('a', 64) + ".example.org", out _));
    }

    [Fact]
    public void AUrlIsRejectedRatherThanHavingItsHostExtracted()
    {
        // The distinction matters: extracting would mean the endpoint silently accepts a shape its
        // contract does not describe, and the caller would never learn their input was reshaped.
        Assert.False(HostnameInput.TryNormalize("https://media.example.org/web/index.html", out var normalized));
        Assert.Null(normalized);
    }
}
