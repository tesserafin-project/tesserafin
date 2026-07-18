using System;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// One entry in a session's lifecycle timeline (§4.3).
/// </summary>
/// <param name="Stage">The stage name, e.g. <c>"Created"</c> or <c>"Updated"</c>.</param>
/// <param name="At">When this stage occurred.</param>
/// <param name="RequestId">
/// Issue #42: the correlation id of the HTTP request in flight when this entry was observed, or
/// <c>null</c> when none was — which is the normal case for entries derived from ffmpeg/session
/// callbacks and for the synthetic <c>Created</c>/<c>Updated</c> entries. Optional and additive:
/// existing clients that ignore it are unaffected.
/// </param>
public sealed record DiagnosticTimelineEntry(string Stage, DateTimeOffset At, string? RequestId = null);
