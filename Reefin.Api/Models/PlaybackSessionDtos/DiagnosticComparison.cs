using System.Collections.Generic;
using Reefin.Playback.Decision;
using Reefin.Playback.Shadow;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The legacy-vs-v2 shadow comparison for a session's retained diagnostic (§4.3): what legacy (the
/// source of truth) actually decided, alongside the heuristic classification of how it compares to
/// the shadowed v2 decision.
/// </summary>
/// <param name="LegacyMethod">The playback method legacy chose.</param>
/// <param name="LegacyReasons">
/// The reason codes behind the legacy decision, best-effort recovered from the retained
/// <c>DecisionVector.ReasonCategories</c> - see <see cref="PlaybackDiagnosticDetailMapper"/> for the
/// documented lossiness of this mapping.
/// </param>
/// <param name="DivergenceClass">The heuristic classification of the legacy/v2 divergence.</param>
public sealed record DiagnosticComparison(
    PlaybackMethod LegacyMethod,
    IReadOnlyList<ReasonCode> LegacyReasons,
    DivergenceClass DivergenceClass);
