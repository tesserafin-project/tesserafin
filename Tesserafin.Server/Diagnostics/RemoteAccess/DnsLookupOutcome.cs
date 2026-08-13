namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// How a hostname lookup ended.
/// </summary>
/// <remarks>
/// These four failure shapes stay distinct because they mean different things to whoever has to
/// fix the problem. A timeout is a resolver or network problem; no answer is a DNS-record
/// problem; a resolver failure is a configuration problem; cancellation is not a problem at all.
/// Collapsing them into "lookup failed" would send an operator to edit a zone file when their
/// resolver was simply slow.
/// </remarks>
public enum DnsLookupOutcome
{
    /// <summary>Not an outcome. The default value of the type, never emitted.</summary>
    None = 0,

    /// <summary>The lookup was never attempted, because the hostname was absent or rejected.</summary>
    NotAttempted = 1,

    /// <summary>At least one A or AAAA record came back.</summary>
    Answered = 2,

    /// <summary>The resolver answered authoritatively with no A or AAAA record.</summary>
    NoAddressRecords = 3,

    /// <summary>The bounded deadline expired first.</summary>
    TimedOut = 4,

    /// <summary>The caller cancelled first.</summary>
    Cancelled = 5,

    /// <summary>The resolver reported an error.</summary>
    ResolverFailure = 6
}
