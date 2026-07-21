using System;
using System.Linq;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Dlna;

/// <summary>
/// Maps the legacy <see cref="MediaSourceInfo"/> DTO to the domain
/// <see cref="MediaSourceSnapshot"/>. Mechanical, field-for-field projection: no decision logic.
/// </summary>
public static class MediaSourceSnapshotMapper
{
    /// <summary>
    /// Projects a legacy <see cref="MediaSourceInfo"/> into a frozen <see cref="MediaSourceSnapshot"/>.
    /// </summary>
    /// <param name="source">The legacy media source to project.</param>
    /// <returns>The equivalent domain snapshot.</returns>
    public static MediaSourceSnapshot ToSnapshot(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var streams = source.MediaStreams ?? [];

        var videoStreams = streams
            .Where(static s => s.Type == MediaStreamType.Video)
            .Select(ToVideoStreamSnapshot)
            .ToList();

        var audioStreams = streams
            .Where(static s => s.Type == MediaStreamType.Audio)
            .Select(ToAudioStreamSnapshot)
            .ToList();

        var subtitleStreams = streams
            .Where(static s => s.Type == MediaStreamType.Subtitle)
            .Select(ToSubtitleStreamSnapshot)
            .ToList();

        return new MediaSourceSnapshot(
            MediaSourceId: source.Id ?? string.Empty,
            Container: source.Container ?? string.Empty,
            Protocol: source.Protocol.ToString().ToLowerInvariant(),
            Bitrate: source.Bitrate,
            RunTimeTicks: source.RunTimeTicks,
            VideoStreams: videoStreams,
            AudioStreams: audioStreams,
            SubtitleStreams: subtitleStreams,
            SupportsDirectPlay: source.SupportsDirectPlay,
            SupportsDirectStream: source.SupportsDirectStream,
            SupportsTranscoding: source.SupportsTranscoding);
    }

    private static VideoStreamSnapshot ToVideoStreamSnapshot(MediaStream stream)
    {
        var videoRangeType = stream.VideoRangeType;
        var videoRange = videoRangeType == VideoRangeType.Unknown
            ? null
            : videoRangeType.ToString();

        double? framerate = stream.RealFrameRate ?? stream.AverageFrameRate;

        return new VideoStreamSnapshot(
            Index: stream.Index,
            Codec: stream.Codec ?? string.Empty,
            Profile: stream.Profile,
            Level: stream.Level,
            Width: stream.Width,
            Height: stream.Height,
            BitDepth: stream.BitDepth,
            VideoRange: videoRange,
            Framerate: framerate,
            Bitrate: stream.BitRate,
            IsAnamorphic: stream.IsAnamorphic ?? false,
            IsInterlaced: stream.IsInterlaced);
    }

    private static AudioStreamSnapshot ToAudioStreamSnapshot(MediaStream stream)
    {
        return new AudioStreamSnapshot(
            Index: stream.Index,
            Codec: stream.Codec ?? string.Empty,
            Channels: stream.Channels,
            SampleRate: stream.SampleRate,
            BitDepth: stream.BitDepth,
            Bitrate: stream.BitRate,
            Language: stream.Language,
            IsDefault: stream.IsDefault);
    }

    private static SubtitleStreamSnapshot ToSubtitleStreamSnapshot(MediaStream stream)
    {
        return new SubtitleStreamSnapshot(
            Index: stream.Index,
            Format: NormalizeSubtitleFormat(stream.Codec),
            IsExternal: stream.IsExternal,
            IsForced: stream.IsForced,
            IsDefault: stream.IsDefault,
            Language: stream.Language);
    }

    /// <summary>
    /// Normalizes an identity alias between ffmpeg's probed codec name and the vocabulary legacy
    /// <c>SubtitleProfile.Format</c> declares (PR103): <c>"webvtt"</c> (probed codec) and
    /// <c>"vtt"</c> (the format/extension legacy device profiles declare, see e.g.
    /// <c>DeviceProfile-Chrome.json</c>'s <c>SubtitleProfiles</c>) name the same format - legacy's
    /// <c>StreamBuilder.GetExternalSubtitleProfile</c> (StreamBuilder.cs:1594-1624) resolves this via
    /// <c>MediaStream.SupportsSubtitleConversionTo</c>, which is a no-op "conversion" for this pair.
    /// Not a general text-subtitle-format-conversion model (srt-to-vtt real re-encoding stays
    /// unmodeled, a documented PR103 gap) - just this one same-format spelling difference, so v2's
    /// strict <c>EqualsIgnoreCase</c> capability match (<c>PlaybackEngine.BuildForSource</c>) sees
    /// them as the same format, same as legacy effectively does.
    /// </summary>
    private static string NormalizeSubtitleFormat(string? codec) =>
        string.Equals(codec, "webvtt", StringComparison.OrdinalIgnoreCase) ? "vtt" : codec ?? string.Empty;
}
