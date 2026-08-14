namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>What the hostname lookup produced. Mirrors <c>DnsLookupOutcome</c>.</summary>
public enum RemoteAccessDnsOutcome
{
    /// <summary>Reserved.</summary>
    None = 0,

    /// <summary>No lookup was attempted — no hostname, or one that cannot be a hostname.</summary>
    NotAttempted = 1,

    /// <summary>The resolver answered.</summary>
    Answered = 2,

    /// <summary>The resolver answered with no address records.</summary>
    NoAddressRecords = 3,

    /// <summary>The lookup timed out.</summary>
    TimedOut = 4,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled = 5,

    /// <summary>The resolver failed.</summary>
    ResolverFailure = 6
}
