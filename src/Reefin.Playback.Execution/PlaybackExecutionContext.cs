using System;

namespace Reefin.Playback.Execution;

/// <summary>
/// Request-scoped facts the execution machinery needs alongside a <see cref="PlaybackExecutionPlan"/>,
/// but that the v2 decision engine never sees and never decides - carried verbatim from the calling
/// request, not derived, not re-decided. Deliberately does NOT carry source-scoped facts (resolved by
/// the adapter directly from the caller-supplied media source, e.g. RunTimeTicks/MaxFramerate/
/// AudioSampleRate) or device-profile-scoped facts (resolved by the adapter from the caller-supplied
/// device profile, e.g. the matched TranscodingProfile's knobs, or RequireAvc/RequireNonAnamorphic) -
/// see PR115b's design doc (docs/pr115-design-canary-execution.md) §3 for why those two are not
/// context, and its "Invariant de parité exécutable" section for why RequireAvc/RequireNonAnamorphic
/// are resolved by the adapter rather than left to a later PR.
/// </summary>
/// <param name="ItemId">The library item id the stream belongs to.</param>
/// <param name="PlaySessionId">The play session id this stream is tied to, if known.</param>
/// <param name="DeviceId">The requesting device id, if known.</param>
/// <param name="DeviceProfileId">The requesting device profile id, if known.</param>
/// <param name="StartPositionTicks">The position, in ticks, playback should start from.</param>
/// <param name="AlwaysBurnInSubtitleWhenTranscoding">
/// The client's own preference forcing subtitle burn-in, distinct from the decision's own
/// <see cref="PlaybackExecutionPlan.SubtitleDelivery"/>.
/// </param>
public sealed record PlaybackExecutionContext(
    Guid ItemId,
    string? PlaySessionId,
    string? DeviceId,
    string? DeviceProfileId,
    long StartPositionTicks,
    bool AlwaysBurnInSubtitleWhenTranscoding);
