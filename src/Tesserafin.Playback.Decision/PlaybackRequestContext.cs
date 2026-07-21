using System;

namespace Tesserafin.Playback.Decision;

/// <summary>
/// The who/what/when of a playback request, independent of transport.
/// </summary>
/// <param name="RequestId">A unique identifier for this decision, for log/diagnostic correlation.</param>
/// <param name="ItemId">The requested media item.</param>
/// <param name="MediaSourceId">The specific alternate source version requested, or <see langword="null"/> to let the engine choose.</param>
/// <param name="UserId">The requesting user, used for policy/quota purposes rather than technical selection.</param>
/// <param name="MediaKind">Whether audio or video playback is being requested.</param>
/// <param name="RequestedAt">The timestamp the request was made.</param>
/// <param name="EngineVersion">The version of the decision engine that will produce (or produced) the decision for this request.</param>
public sealed record PlaybackRequestContext(
    Guid RequestId,
    Guid ItemId,
    string? MediaSourceId,
    Guid UserId,
    MediaKind MediaKind,
    DateTimeOffset RequestedAt,
    int EngineVersion);
