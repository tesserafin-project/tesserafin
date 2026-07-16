using System;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// One real, observed lifecycle event for a tracked playback session (PR113b): a signal actually
/// received from <see cref="Reefin.Controller.MediaEncoding.ITranscodeManager"/> or
/// <see cref="Reefin.Controller.Session.ISessionManager"/>, stamped with the wall-clock time it was
/// received - never a value approximated or backfilled from some other timestamp. Retained by
/// <see cref="IShadowDiagnosticsStore"/> alongside (but independent of) any
/// <see cref="ShadowDiagnosticRecord"/>, so the admin timeline (docs/pr92-design-playback-api-and-diagnostics.md
/// §4.3) is populated even when shadow mode itself is disabled.
/// </summary>
/// <param name="Stage">
/// The stage name, for example <c>"FfmpegStarted"</c>, <c>"PlaybackStarted"</c>, or
/// <c>"PlaybackStopped"</c>. Free-form; the admin API layer maps it onto its own timeline entry's
/// stage name verbatim.
/// </param>
/// <param name="At">The real time this event was received, captured at the moment of observation.</param>
public sealed record PlaybackLifecycleEvent(string Stage, DateTimeOffset At);
