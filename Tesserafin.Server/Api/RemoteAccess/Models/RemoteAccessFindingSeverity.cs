namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// How much attention a finding deserves. Mirrors the internal <c>DiagnosticSeverity</c>.
/// </summary>
/// <remarks>
/// Deliberately not a scale that adds up to anything. There is no total, no score and no
/// percentage, because a number invites a caller to act on the number instead of the findings.
/// </remarks>
public enum RemoteAccessFindingSeverity
{
    /// <summary>Reserved. Never emitted, never success.</summary>
    None = 0,

    /// <summary>Context.</summary>
    Informational = 1,

    /// <summary>Worth an operator's attention.</summary>
    Advisory = 2,

    /// <summary>Likely to prevent what the operator is trying to do.</summary>
    High = 3
}
