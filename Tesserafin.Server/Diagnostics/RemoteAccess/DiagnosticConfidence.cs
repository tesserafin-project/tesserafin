namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// How much weight a single diagnostic finding may carry.
/// </summary>
/// <remarks>
/// This is the whole point of the R1 layer. A networking diagnostic that reports one flat
/// "status" invites the reader to treat a local socket check as proof of Internet reachability,
/// which is the mistake tesserafin-project/tesserafin#241 exists to prevent. Every finding
/// therefore says how it was arrived at, and two of the five values are ways of saying
/// "not known" — deliberately, because missing evidence must never round up to success.
/// </remarks>
public enum DiagnosticConfidence
{
    /// <summary>
    /// Not a confidence. The default value of the type, never emitted.
    /// </summary>
    None = 0,

    /// <summary>
    /// Read directly from this process's effective configuration or from operating-system state.
    /// </summary>
    Observed = 1,

    /// <summary>
    /// Calculated deterministically from observed facts, by rules that add no new evidence.
    /// </summary>
    Derived = 2,

    /// <summary>
    /// Requires evidence this layer cannot obtain — typically something only a vantage point
    /// outside the host could establish.
    /// </summary>
    Unverified = 3,

    /// <summary>
    /// Collection failed, was unsupported, timed out, or produced too little to classify.
    /// </summary>
    Unknown = 4,

    /// <summary>
    /// Two observations cannot both satisfy the declared policy.
    /// </summary>
    Contradictory = 5
}
