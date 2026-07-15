using System;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// One entry in a session's lifecycle timeline (§4.3).
/// </summary>
/// <param name="Stage">The stage name, e.g. <c>"Created"</c> or <c>"Updated"</c>.</param>
/// <param name="At">When this stage occurred.</param>
public sealed record DiagnosticTimelineEntry(string Stage, DateTimeOffset At);
