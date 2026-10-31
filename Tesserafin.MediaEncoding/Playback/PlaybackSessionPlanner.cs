using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;

namespace Tesserafin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackSessionPlanner"/>
public class PlaybackSessionPlanner : IPlaybackSessionPlanner
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<PlaybackSessionPlanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionPlanner"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PlaybackSessionPlanner}"/> interface.</param>
    public PlaybackSessionPlanner(IMediaEncoder mediaEncoder, ILogger<PlaybackSessionPlanner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <inheritdoc/>
    public PlaybackPlan? PlanAudio(MediaOptions options)
    {
        var streamBuilder = new StreamBuilder(_mediaEncoder, _logger);
        var streamInfo = streamBuilder.GetOptimalAudioStream(options);
        return streamInfo is null ? null : new PlaybackPlan(streamInfo.PlayMethod, streamInfo.TranscodeReasons, streamInfo);
    }

    /// <inheritdoc/>
    public PlaybackPlan? PlanVideo(MediaOptions options)
    {
        var streamBuilder = new StreamBuilder(_mediaEncoder, _logger);
        var streamInfo = streamBuilder.GetOptimalVideoStream(options);
        if (streamInfo is null)
        {
            return null;
        }

        // #374: StreamBuilder's DirectStream is the source file, served verbatim (Static=true). That
        // only answers the client when no direct-stream reason was recorded; with one, it hands over
        // a container or audio codec the client just declared unplayable.
        if (streamInfo.PlayMethod == PlayMethod.DirectStream && streamInfo.TranscodeReasons != 0)
        {
            return PlanRealOutput(streamBuilder, options);
        }

        // StreamBuilder leaves PlayMethod at its Transcode default when no profile applied, with
        // nothing to produce: a descriptor for that would point at an output nobody declared.
        if (streamInfo.PlayMethod == PlayMethod.Transcode && !HasOutput(streamInfo))
        {
            return null;
        }

        return new PlaybackPlan(streamInfo.PlayMethod, streamInfo.TranscodeReasons, streamInfo);
    }

    /// <summary>
    /// Plans a delivery that really produces a compatible output: through the output profiles the
    /// client declared, otherwise as a copy into the mp4 it declared it can direct play. A job that
    /// copies every stream is the remux that was decided, so it stays reported as one. Nothing
    /// permitted leaves no plan, and the session is refused.
    /// </summary>
    private static PlaybackPlan? PlanRealOutput(StreamBuilder streamBuilder, MediaOptions options)
    {
        var enableDirectStream = options.EnableDirectStream;
        var declaredOutputs = options.Profile.TranscodingProfiles;
        options.EnableDirectStream = false;
        try
        {
            var streamInfo = streamBuilder.GetOptimalVideoStream(options);
            if (!HasOutput(streamInfo) && DeclaredMp4Output(options.Profile) is { } mp4Output)
            {
                options.Profile.TranscodingProfiles = [mp4Output];
                streamInfo = streamBuilder.GetOptimalVideoStream(options);
                if (streamInfo is { CopiesEveryStream: false })
                {
                    // The direct-play declaration vouches for a copy, not for anything re-encoded.
                    return null;
                }
            }

            return HasOutput(streamInfo)
                ? new PlaybackPlan(streamInfo!.CopiesEveryStream ? PlayMethod.DirectStream : PlayMethod.Transcode, streamInfo.TranscodeReasons, streamInfo)
                : null;
        }
        finally
        {
            options.EnableDirectStream = enableDirectStream;
            options.Profile.TranscodingProfiles = declaredOutputs;
        }
    }

    private static bool HasOutput(StreamInfo? streamInfo)
        => streamInfo is { PlayMethod: PlayMethod.Transcode, VideoCodecs.Count: > 0 };

    private static TranscodingProfile? DeclaredMp4Output(DeviceProfile profile)
    {
        var directPlay = profile.DirectPlayProfiles.FirstOrDefault(p => p.Type == DlnaProfileType.Video && p.SupportsContainer("mp4"));
        return directPlay is null
            ? null
            : new TranscodingProfile
            {
                Type = DlnaProfileType.Video,
                Context = EncodingContext.Streaming,
                Protocol = MediaStreamProtocol.http,
                Container = "mp4",
                VideoCodec = directPlay.VideoCodec ?? string.Empty,
                AudioCodec = directPlay.AudioCodec ?? string.Empty,
            };
    }
}
