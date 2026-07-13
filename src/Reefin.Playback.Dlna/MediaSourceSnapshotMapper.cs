using System;
using System.Linq;
using Reefin.Data.Enums;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Dlna;

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
            Format: stream.Codec ?? string.Empty,
            IsExternal: stream.IsExternal,
            IsForced: stream.IsForced,
            IsDefault: stream.IsDefault,
            Language: stream.Language);
    }
}
