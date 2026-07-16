using System.Collections.Generic;
using Reefin.Playback.Decision;
using Reefin.Playback.Shadow;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The legacy-vs-v2 shadow comparison for a session's retained diagnostic (§4.3): what legacy (the
/// source of truth) actually decided, the v2 engine's own decision shape, and the heuristic
/// classification of how the two compare. PR114: <see cref="V2Method"/>/<see cref="V2Output"/>/
/// <see cref="V2SelectedStreams"/>/<see cref="V2Transforms"/> were added so the admin diagnostics
/// UI can render a real "live legacy vs. v2 decision" comparison - before this, only v2's
/// <see cref="PlaybackDiagnosticDetail.Reasoning"/> tree was exposed, never its concrete decision
/// shape (the parent <see cref="PlaybackDiagnosticDetail"/>'s own <c>Method</c>/<c>Output</c>/
/// <c>SelectedStreams</c>/<c>Transforms</c> are always legacy-sourced, per
/// <see cref="PlaybackSessionResponseMapper"/> - never v2's).
/// </summary>
/// <param name="LegacyMethod">The playback method legacy chose.</param>
/// <param name="LegacyReasons">
/// The reason codes behind the legacy decision, best-effort recovered from the retained
/// <c>DecisionVector.ReasonCategories</c> - see <see cref="PlaybackDiagnosticDetailMapper"/> for the
/// documented lossiness of this mapping.
/// </param>
/// <param name="DivergenceClass">The heuristic classification of the legacy/v2 divergence.</param>
/// <param name="DivergenceSummary">
/// A short, human-readable summary of the divergence (<c>ShadowDivergence.Summary</c>), suitable
/// for display alongside <see cref="DivergenceClass"/> without the reader having to know the
/// classification vocabulary.
/// </param>
/// <param name="V2Method">The playback method the v2 engine chose.</param>
/// <param name="V2Output">The shape of the output the v2 engine's decision produces.</param>
/// <param name="V2SelectedStreams">The streams the v2 engine selected for playback.</param>
/// <param name="V2Transforms">The pipeline transforms the v2 engine's decision implies.</param>
public sealed record DiagnosticComparison(
    PlaybackMethod LegacyMethod,
    IReadOnlyList<ReasonCode> LegacyReasons,
    DivergenceClass DivergenceClass,
    string DivergenceSummary,
    PlaybackMethod V2Method,
    OutputSpec V2Output,
    SelectedStreams V2SelectedStreams,
    IReadOnlyList<TransformKind> V2Transforms);
