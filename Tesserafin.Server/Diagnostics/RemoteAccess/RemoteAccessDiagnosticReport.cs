using System;
using System.Collections.Generic;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What this layer is prepared to say about the host's networking posture.
/// </summary>
/// <remarks>
/// <para>
/// There is no overall status here — no ready, no secure, no healthy, no reachable. That is not
/// an oversight to be corrected later: a single roll-up is precisely the affordance that lets a
/// caller skip the findings and act on a word, and every word available would be a claim this
/// layer cannot support. A reader who wants a verdict has to read the findings.
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> exists so a future consumer can refuse a shape it does not
/// understand rather than misread it.
/// </para>
/// </remarks>
public sealed record RemoteAccessDiagnosticReport
{
    /// <summary>The schema this build emits.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteAccessDiagnosticReport"/> class.
    /// </summary>
    /// <param name="snapshot">The observations the findings were drawn from.</param>
    /// <param name="findings">The findings, in a stable order.</param>
    /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
    public RemoteAccessDiagnosticReport(RemoteAccessDiagnosticSnapshot snapshot, IReadOnlyList<RemoteAccessFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(findings);

        Snapshot = snapshot;
        Findings = findings;
    }

    /// <summary>Gets the schema version of this report.</summary>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the observations the findings were drawn from.</summary>
    public RemoteAccessDiagnosticSnapshot Snapshot { get; }

    /// <summary>Gets the findings, in a stable order.</summary>
    public IReadOnlyList<RemoteAccessFinding> Findings { get; }

    /// <summary>
    /// Determines whether a given code is present.
    /// </summary>
    /// <param name="code">The code to look for.</param>
    /// <returns><c>true</c> if the report contains that finding.</returns>
    public bool Has(RemoteAccessDiagnosticCode code)
    {
        for (var i = 0; i < Findings.Count; i++)
        {
            if (Findings[i].Code == code)
            {
                return true;
            }
        }

        return false;
    }
}
