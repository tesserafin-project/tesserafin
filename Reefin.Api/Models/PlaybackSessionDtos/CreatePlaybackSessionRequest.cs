using System;
using Reefin.Model.Dlna;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Request body for creating (or replacing, when <see cref="PlaySessionId"/> matches an existing
/// session) a playback session via the point-1 v2 protocol. Deliberately narrower than the
/// internal <see cref="MediaOptions"/> it gets translated into: server-resolved fields (media
/// sources, requesting IP) are left out, and there is no way to pass DLNA capability data beyond
/// the device profile the client is already required to supply for playback today.
/// </summary>
/// <param name="ItemId">The item to plan playback for.</param>
/// <param name="UserId">The requesting user.</param>
/// <param name="DeviceProfile">The requesting device's capability profile.</param>
/// <param name="MediaSourceId">Optional. A specific media source id, if playing an alternate version.</param>
/// <param name="PlaySessionId">
/// Optional. The client-facing play session id. At most one session is kept per play session id:
/// creating with the same id again replaces that session's plan and request.
/// </param>
/// <param name="MaxBitrate">Optional. The client's maximum bitrate.</param>
/// <param name="StartTimeTicks">Optional. The starting offset, in ticks.</param>
/// <param name="AudioStreamIndex">Optional. The audio stream index to use.</param>
/// <param name="SubtitleStreamIndex">Optional. The subtitle stream index to use.</param>
/// <param name="MaxAudioChannels">Optional. An override for the number of audio channels.</param>
/// <param name="EnableDirectPlay">Whether direct play is allowed.</param>
/// <param name="EnableDirectStream">Whether direct stream is allowed.</param>
/// <param name="EnableTranscoding">Whether transcoding is allowed.</param>
/// <param name="AllowVideoStreamCopy">Whether copying the video stream is allowed.</param>
/// <param name="AllowAudioStreamCopy">Whether copying the audio stream is allowed.</param>
/// <param name="AlwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
public sealed record CreatePlaybackSessionRequest(
    Guid ItemId,
    Guid UserId,
    DeviceProfile DeviceProfile,
    string? MediaSourceId = null,
    string? PlaySessionId = null,
    int? MaxBitrate = null,
    long StartTimeTicks = 0,
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null,
    int? MaxAudioChannels = null,
    bool EnableDirectPlay = true,
    bool EnableDirectStream = true,
    bool EnableTranscoding = true,
    bool AllowVideoStreamCopy = true,
    bool AllowAudioStreamCopy = true,
    bool AlwaysBurnInSubtitleWhenTranscoding = false);
