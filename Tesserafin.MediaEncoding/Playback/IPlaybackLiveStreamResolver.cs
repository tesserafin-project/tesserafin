using System;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR117 (docs/pr116d-url-contract-design.md §3.3): the live-wiring decision extracted from
/// <c>Tesserafin.Api.Helpers.MediaInfoHelper.ResolveServedStreamInfo</c> into a shared, injectable
/// component, so it can be consumed both by the legacy live streaming path
/// (<c>MediaInfoHelper.SetDeviceSpecificData</c>, behavior unchanged) and by the new
/// <c>Playback/Sessions/{id}/Stream</c> entry point, without duplicating the kill switch/
/// stop-threshold guard/<c>SourceId</c> verification/Dolby Vision exclusion logic - two
/// implementations that must stay identical would diverge sooner or later (design doc §3.3).
/// Deliberately carries no dependency on <c>ClaimsPrincipal</c>/<c>IPAddress</c> - those stay the
/// caller's own concern.
/// </summary>
public interface IPlaybackLiveStreamResolver
{
    /// <summary>
    /// PR115c: the live-wiring decision. Legacy (<paramref name="legacyStreamInfo"/>) is always the
    /// default and is replaced only on full, verified success. Every failure mode (kill switch, the
    /// PR115d stop-threshold guard, no/unresolvable plan, source id mismatch, the Dolby Vision
    /// exclusion, an adapter exception) returns <paramref name="legacyStreamInfo"/> unchanged,
    /// logged and retained as a typed <c>PlaybackLiveFallbackReason</c> in
    /// <see cref="IPlaybackLiveWiringDiagnosticsStore"/> - never a silent substitution, never an
    /// exception escaping to the caller.
    /// </summary>
    /// <param name="sessionId">The live session this decision is for.</param>
    /// <param name="legacyStreamInfo">The legacy-planned <c>StreamInfo</c>, the default outcome.</param>
    /// <param name="mediaSource">The media source actually being served.</param>
    /// <param name="profile">The legacy device profile the resulting stream is built for.</param>
    /// <param name="itemId">The library item id the stream belongs to.</param>
    /// <param name="deviceId">The requesting device id, if known.</param>
    /// <param name="playSessionId">The play session id this stream is tied to.</param>
    /// <param name="startTimeTicks">The position, in ticks, playback should start from.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">The client's own subtitle burn-in preference.</param>
    /// <returns>
    /// The <c>StreamInfo</c> that should actually be served - either the v2-resolved one, or
    /// <paramref name="legacyStreamInfo"/> unchanged on any fallback.
    /// </returns>
    StreamInfo Resolve(
        PlaybackSessionId sessionId,
        StreamInfo legacyStreamInfo,
        MediaSourceInfo mediaSource,
        DeviceProfile profile,
        Guid itemId,
        string? deviceId,
        string playSessionId,
        long startTimeTicks,
        bool alwaysBurnInSubtitleWhenTranscoding);
}
