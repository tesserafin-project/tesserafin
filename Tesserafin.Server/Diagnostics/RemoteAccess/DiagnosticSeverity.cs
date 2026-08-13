namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// How much attention a finding demands, independent of how confident it is.
/// </summary>
/// <remarks>
/// Severity and confidence are separate on purpose. "The backend is bound to a wildcard" is
/// high severity and fully observed; "external reachability is unverified" is unverified and
/// merely informational. Collapsing the two would let a confident-but-harmless fact outrank a
/// dangerous one.
/// </remarks>
public enum DiagnosticSeverity
{
    /// <summary>
    /// Not a severity. The default value of the type, never emitted.
    /// </summary>
    None = 0,

    /// <summary>
    /// States a fact. No action implied.
    /// </summary>
    Informational = 1,

    /// <summary>
    /// Worth reading before changing anything about public exposure.
    /// </summary>
    Advisory = 2,

    /// <summary>
    /// Would make a public deployment unsafe, or already does.
    /// </summary>
    High = 3
}
