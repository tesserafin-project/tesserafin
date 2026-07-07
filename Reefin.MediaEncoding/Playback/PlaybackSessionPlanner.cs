using Microsoft.Extensions.Logging;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;

namespace Reefin.MediaEncoding.Playback;

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
        return streamInfo is null ? null : new PlaybackPlan(streamInfo.PlayMethod, streamInfo.TranscodeReasons, streamInfo);
    }
}
