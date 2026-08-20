using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.LiveTvProbe;

/// <summary>
/// Probing a live tuner stream must never send ffprobe to fetch the server's own
/// <c>[Authorize]</c>d <c>/LiveTv/LiveStreamFiles/**</c> route (#153-LTV-R1, LTV-R0 finding 1).
/// </summary>
/// <remarks>
/// WHAT LTV-R0 MEASURED, REPRODUCIBLY, ON TWO SEPARATE RIG RUNS:
/// <code>
/// [INF] MediaSourceManager: Waiting 3000ms before probing the live stream
/// http://&lt;host&gt;/LiveTv/LiveStreamFiles/&lt;id&gt;/stream.ts: Server returned 401 Unauthorized
/// [ERR] MediaSourceManager: Error probing live tv stream
///       FfmpegException: ffprobe failed - streams and format are both null
/// </code>
/// #153-LTV-S0 routed the <b>transcode</b> through <c>pipe:0</c>. It did not route the
/// <b>probe</b>: the probe still opens <c>MediaSourceInfo.Path</c>, which for a live tuner source
/// is that authorized URL, and it carries no credential, so it is refused. The measured cost is
/// that the media source is published with <c>Codec null</c> / <c>Index -1</c> on every stream.
///
/// WHAT THIS TEST ASSERTS. Not "ffprobe returned good metadata" — that would need a real tuner.
/// It asserts the narrower, decisive thing: the request the encoder is handed cannot be satisfied
/// without an HTTP fetch of that route. A request that names the route and carries no reader over
/// the media is exactly one refused GET waiting to happen.
/// </remarks>
public sealed class LiveStreamProbeInputTests
{
    private const string AuthorizedRoute = "/LiveTv/LiveStreamFiles/";

    [Fact]
    public async Task ProbingALiveStream_NeverSendsFfprobeToTheAuthorizedLiveStreamFilesRoute()
    {
        var recorded = new List<MediaInfoRequest>();
        var encoder = RecordingEncoder(recorded);
        var mediaSource = LiveTunerSource();

        await new LiveStreamHelper(encoder, NullLogger.Instance, Paths())
            .AddMediaInfoWithProbe(mediaSource, isAudio: false, cacheKey: null, addProbeDelay: false, CancellationToken.None)
            .ConfigureAwait(true);

        var probe = Assert.Single(recorded);
        Assert.False(
            RequiresAnHttpFetchOfAnAuthorizedRoute(probe),
            "the live stream probe handed ffprobe the server's own [Authorize]d LiveStreamFiles url and no reader over the media, "
            + "so it can only be satisfied by one uncredentialed GET, which the server refuses 401.");
    }

    /// <summary>
    /// The control. A source that is a plain file on disk is not a fetch of anything, so the
    /// assertion above cannot be passing merely because it never looks.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ProbingAPlainFile_IsNotAnHttpFetch()
    {
        var recorded = new List<MediaInfoRequest>();
        var encoder = RecordingEncoder(recorded);
        var mediaSource = new MediaSourceInfo
        {
            Path = "/var/lib/tesserafin/media/movie.mkv",
            Protocol = MediaProtocol.File,
            SupportsProbing = true,
            MediaStreams = new List<MediaStream>()
        };

        await new LiveStreamHelper(encoder, NullLogger.Instance, Paths())
            .AddMediaInfoWithProbe(mediaSource, isAudio: false, cacheKey: null, addProbeDelay: false, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.False(RequiresAnHttpFetchOfAnAuthorizedRoute(Assert.Single(recorded)));
    }

    private static bool RequiresAnHttpFetchOfAnAuthorizedRoute(MediaInfoRequest request)
        => request.MediaSource.Path is { } path
           && path.Contains(AuthorizedRoute, StringComparison.OrdinalIgnoreCase)
           && request.DirectStreamReader is null;

    private static MediaSourceInfo LiveTunerSource() => new()
    {
        Id = "6d5da76e3955fd1005f75c496c371521",
        LiveStreamId = "6d5da76e3955fd1005f75c496c371521",
        Path = "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/6d5da76e3955fd1005f75c496c371521/stream.ts",
        Protocol = MediaProtocol.Http,
        SupportsProbing = true,
        IsInfiniteStream = true,
        RequiresOpening = true,
        MediaStreams = new List<MediaStream>()
    };

    private static IMediaEncoder RecordingEncoder(List<MediaInfoRequest> recorded)
    {
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Loose);
        encoder
            .Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .Returns((MediaInfoRequest request, CancellationToken _) =>
            {
                recorded.Add(request);
                return Task.FromResult(new MediaInfo
                {
                    MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Codec = "h264", Index = 0 },
                        new() { Type = MediaStreamType.Audio, Codec = "aac", Index = 1 }
                    }
                });
            });
        return encoder.Object;
    }

    private static IApplicationPaths Paths()
    {
        var paths = new Mock<IApplicationPaths>(MockBehavior.Loose);
        paths.SetupGet(p => p.CachePath).Returns(System.IO.Path.GetTempPath());
        return paths.Object;
    }
}
