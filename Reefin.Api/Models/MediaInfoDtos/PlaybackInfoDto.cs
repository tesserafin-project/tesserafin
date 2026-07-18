using System;
using Reefin.Model.Dlna;

namespace Reefin.Api.Models.MediaInfoDtos;

/// <summary>
/// Playback info dto.
/// </summary>
public class PlaybackInfoDto
{
    /// <summary>
    /// Gets or sets the playback userId.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Gets or sets the max streaming bitrate.
    /// </summary>
    public int? MaxStreamingBitrate { get; set; }

    /// <summary>
    /// Gets or sets the start time in ticks.
    /// </summary>
    public long? StartTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the audio stream index.
    /// </summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the subtitle stream index.
    /// </summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the max audio channels.
    /// </summary>
    public int? MaxAudioChannels { get; set; }

    /// <summary>
    /// Gets or sets the media source id.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the opaque, client-generated playback attempt id (issue #43).
    /// </summary>
    /// <remarks>
    /// Optional and additive. This is the FIRST request of a playback attempt, which is precisely
    /// why the attempt id has to exist as its own field: no session exists yet, so
    /// <c>PlaySessionId</c> cannot cover this call, and the <c>RequestId</c> of issue #42 will be
    /// different by the time the client reaches <c>POST Playback/Sessions</c>. The client generates
    /// the value once here and resends it unchanged for the rest of the attempt, retries included.
    /// Echoed back on <c>PlaybackInfoResponse</c>. Opaque, length-capped, never an authorization key.
    /// </remarks>
    public string? PlaybackAttemptId { get; set; }

    /// <summary>
    /// Gets or sets the live stream id.
    /// </summary>
    public string? LiveStreamId { get; set; }

    /// <summary>
    /// Gets or sets the device profile.
    /// </summary>
    public DeviceProfile? DeviceProfile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable direct play.
    /// </summary>
    public bool? EnableDirectPlay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable direct stream.
    /// </summary>
    public bool? EnableDirectStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable transcoding.
    /// </summary>
    public bool? EnableTranscoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable video stream copy.
    /// </summary>
    public bool? AllowVideoStreamCopy { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to allow audio stream copy.
    /// </summary>
    public bool? AllowAudioStreamCopy { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto open the live stream.
    /// </summary>
    public bool? AutoOpenLiveStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether always burn in subtitles when transcoding.
    /// </summary>
    public bool? AlwaysBurnInSubtitleWhenTranscoding { get; set; }
}
