namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// One thing this layer is prepared to say, with how it knows and how much it matters.
/// </summary>
/// <param name="Code">The stable identity of the finding.</param>
/// <param name="Confidence">How the finding was arrived at.</param>
/// <param name="Severity">How much attention it demands.</param>
public sealed record RemoteAccessFinding(
    RemoteAccessDiagnosticCode Code,
    DiagnosticConfidence Confidence,
    DiagnosticSeverity Severity);
