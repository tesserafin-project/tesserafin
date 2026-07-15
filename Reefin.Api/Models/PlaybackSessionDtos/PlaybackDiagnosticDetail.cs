using System;
using System.Collections.Generic;
using Reefin.Playback.Decision;
using Reefin.Playback.Shadow;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The richer, admin-only diagnostic projection of a tracked playback session
/// (docs/pr92-design-playback-api-and-diagnostics.md §4.3, PR113): every
/// <see cref="PlaybackSessionResponse"/> field, plus the request context, client capabilities,
/// source snapshot, full reasoning tree, and legacy/v2 comparison retained from a shadow run - when
/// one was retained at all.
/// </summary>
/// <remarks>
/// Filtering rule (§4.3, tested in PR113): never a file path, transcoding URL, session token, or API
/// key. Every field here is either a primitive, or drawn from the <see cref="Reefin.Playback.Decision"/>
/// vocabulary (PR91) or the shadow comparison vocabulary (<see cref="DivergenceClass"/>, PR98) - never
/// <c>Reefin.Model.Dlna.MediaSourceInfo</c>, <c>StreamInfo</c>, <c>DeviceProfile</c>, or the internal
/// <c>Reefin.Controller.MediaEncoding.PlaybackSession</c>/<c>PlaybackSessionRequest</c> records. See
/// <see cref="PlaybackDiagnosticDetailMapper"/> for the derivation.
/// </remarks>
/// <param name="Id">The session identifier.</param>
/// <param name="Kind">Whether this is an audio or video session.</param>
/// <param name="DecisionVersion">See <see cref="PlaybackSessionResponse.DecisionVersion"/>.</param>
/// <param name="Method">The chosen playback method.</param>
/// <param name="Output">The shape of the output the client will receive.</param>
/// <param name="SelectedStreams">The streams selected for playback.</param>
/// <param name="Transforms">The pipeline transforms this decision implies.</param>
/// <param name="Reasons">A flat summary of the reason codes behind this decision.</param>
/// <param name="CreatedAt">When the session was first created.</param>
/// <param name="UpdatedAt">When the session was last created or replaced.</param>
/// <param name="RequestContext">
/// The request context a retained shadow run captured, or <see langword="null"/> when no
/// diagnostic was retained for this session (shadow mode off is the common case: it defaults to
/// disabled, per <see cref="Reefin.MediaEncoding.Playback.ShadowPlaybackSessionPlanner"/>).
/// <see cref="PlaybackRequestContext.UserId"/> is always <see cref="Guid.Empty"/> here - the legacy
/// <c>MediaOptions</c> the shadow run maps from carries no user id, and plumbing one through is out
/// of scope for this slice.
/// </param>
/// <param name="Capabilities">The client capabilities a retained shadow run captured, or <see langword="null"/> if none was retained.</param>
/// <param name="SourceSnapshot">
/// The media source characteristics (codecs/streams/protocol) a retained shadow run captured, or
/// <see langword="null"/> if none was retained. Never a file path or URL -
/// <see cref="MediaSourceSnapshot"/> carries none by construction.
/// </param>
/// <param name="Reasoning">The v2 engine's full structured reasoning tree, or <see langword="null"/> if no diagnostic was retained.</param>
/// <param name="Comparison">The legacy-vs-v2 shadow comparison, or <see langword="null"/> if no diagnostic was retained.</param>
/// <param name="Timeline">
/// The lifecycle timeline for this session. Scoped to <c>Created</c>/<c>Updated</c> for this slice -
/// there is no retained signal yet for "ffmpeg launched" or "playback started" (deferred).
/// </param>
public sealed record PlaybackDiagnosticDetail(
    Guid Id,
    MediaKind Kind,
    int DecisionVersion,
    PlaybackMethod Method,
    OutputSpec Output,
    SelectedStreams SelectedStreams,
    IReadOnlyList<TransformKind> Transforms,
    IReadOnlyList<ReasonCode> Reasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PlaybackRequestContext? RequestContext,
    ClientCapabilities? Capabilities,
    IReadOnlyList<MediaSourceSnapshot>? SourceSnapshot,
    ReasonNode? Reasoning,
    DiagnosticComparison? Comparison,
    IReadOnlyList<DiagnosticTimelineEntry> Timeline);
