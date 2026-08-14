namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// How much weight a finding carries. Mirrors the internal <c>DiagnosticConfidence</c>.
/// </summary>
public enum RemoteAccessFindingConfidence
{
    /// <summary>Reserved. Never emitted, never success.</summary>
    None = 0,

    /// <summary>Read directly from the system.</summary>
    Observed = 1,

    /// <summary>Inferred from observations by a stated rule.</summary>
    Derived = 2,

    /// <summary>Stated as unverifiable from this vantage point.</summary>
    Unverified = 3,

    /// <summary>Could not be determined.</summary>
    Unknown = 4,

    /// <summary>Observations disagree with each other or with the stated intent.</summary>
    Contradictory = 5
}
