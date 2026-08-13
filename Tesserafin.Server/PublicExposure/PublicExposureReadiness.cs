using System;
using System.Collections.Generic;

namespace Tesserafin.Server.PublicExposure;

/// <summary>
/// The result of evaluating a <see cref="PublicExposureEvidence"/> record: whether publication may
/// proceed, and if not, every reason at once.
/// </summary>
/// <remarks>
/// Deliberately not a boolean. A caller that is told only "no" has nothing to show an operator, and
/// a caller that is told only the FIRST reason will fix it, ask again, and be told a second reason —
/// which is how a partially configured server ends up published.
/// </remarks>
public sealed class PublicExposureReadiness
{
    private readonly PublicExposureBlocker[] _blockers;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicExposureReadiness"/> class.
    /// </summary>
    /// <param name="blockers">Every reason publication must not proceed. Empty means ready.</param>
    /// <exception cref="ArgumentNullException"><paramref name="blockers"/> is <c>null</c>.</exception>
    public PublicExposureReadiness(IEnumerable<PublicExposureBlocker> blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);
        _blockers = new List<PublicExposureBlocker>(blockers).ToArray();
    }

    /// <summary>
    /// Gets a value indicating whether publication may proceed.
    /// </summary>
    public bool IsReady => _blockers.Length == 0;

    /// <summary>
    /// Gets every reason publication must not proceed, in evaluation order.
    /// </summary>
    public IReadOnlyList<PublicExposureBlocker> Blockers => _blockers;

    /// <summary>
    /// Determines whether a specific blocker is present.
    /// </summary>
    /// <param name="blocker">The blocker to look for.</param>
    /// <returns><c>true</c> if the blocker is present.</returns>
    public bool Has(PublicExposureBlocker blocker) => Array.IndexOf(_blockers, blocker) >= 0;
}
