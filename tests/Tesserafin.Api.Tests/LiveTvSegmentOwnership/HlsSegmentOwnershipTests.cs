using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// The legacy HLS video segment route must serve the bytes of the job the caller was authorized
/// for, and no other job's bytes (#153-LTV-R1, LTV-R0 finding 2).
/// </summary>
/// <remarks>
/// WHAT LTV-R0 MEASURED. <c>GetHlsVideoSegmentLegacy</c> resolves the file it serves as
/// <c>Path.Combine(transcodeFolderPath, segmentId + extension)</c> — from the caller-supplied
/// <c>segmentId</c> ALONE. <c>itemId</c> is decorated <c>[SuppressMessage CA1801]</c> and is not
/// read at all, and <c>playlistId</c> only selects a transcoding job to keep alive. Path traversal
/// is blocked, but the transcode folder is flat, so every live job's segments sit side by side in
/// it. A capability that satisfies job A therefore reaches job B's segment files.
///
/// These tests are the permanent reproduction. They are deliberately built on the controller
/// directly rather than on a booted server: the property is about which file the action resolves,
/// which is decided before any I/O and does not need a tuner, a transcode or ffmpeg to observe.
/// </remarks>
public sealed class HlsSegmentOwnershipTests : IDisposable
{
    private const string JobA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string JobB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string JobMediaSource = "6d5da76e3955fd1005f75c496c371521";
    private const string JobPlaySession = "306f71094f36456f9d0dc6e7b12b8a6b";

    private static readonly Guid _itemA = new("11111111111111111111111111111111");
    private static readonly Guid _itemB = new("22222222222222222222222222222222");

    private readonly string _transcodePath;

    public HlsSegmentOwnershipTests()
    {
        _transcodePath = Path.Combine(Path.GetTempPath(), "ltvr1-segment-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transcodePath);

        // Two live jobs, side by side in the one flat transcode folder, exactly as the server lays
        // them out: "<hash>.m3u8" for the playlist and "<hash><n>.ts" for its segments.
        File.WriteAllText(Path.Combine(_transcodePath, JobA + ".m3u8"), "#EXTM3U\n");
        File.WriteAllText(Path.Combine(_transcodePath, JobB + ".m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(_transcodePath, JobA + "0.ts"), new byte[] { 0x47, 0x41 });
        File.WriteAllBytes(Path.Combine(_transcodePath, JobB + "0.ts"), new byte[] { 0x47, 0x42 });
    }

    /// <summary>
    /// A caller authorized for job A asks for a segment file belonging to job B. The bytes of job B
    /// must never be served.
    /// </summary>
    [Fact]
    public void ASegmentOfAnotherJob_IsNeverServed()
    {
        var result = Invoke(_itemA, playlistId: JobA, segmentId: JobB + "0");

        Assert.False(
            IsFileResultFor(result, JobB + "0.ts"),
            "job A's playlist reached job B's segment file: the served file is resolved from segmentId alone.");
    }

    /// <summary>
    /// The item the route names is part of the decision. A segment belonging to a job started for
    /// another item must not be served merely because the caller holds a capability for this one.
    /// </summary>
    [Fact]
    public void TheItemInTheRoute_IsActuallyConsumed()
    {
        var forItemA = Invoke(_itemA, playlistId: JobA, segmentId: JobA + "0");
        var forItemB = Invoke(_itemB, playlistId: JobA, segmentId: JobA + "0");

        Assert.True(
            IsFileResultFor(forItemA, JobA + "0.ts"),
            "the route refused its own job's segment; the reproduction would be vacuous.");
        Assert.False(
            IsFileResultFor(forItemB, JobA + "0.ts"),
            "a different itemId in the route changed nothing: itemId is not consumed at all.");
    }

    /// <summary>
    /// With no server-side job for the playlist the route names, the answer must be a closed
    /// refusal — never a fallback to resolving the file from the caller's own parameters.
    /// </summary>
    [Fact]
    public void WithNoServerSideJob_TheAnswerIsAClosedRefusal()
    {
        var result = Invoke(_itemA, playlistId: JobA, segmentId: JobA + "0", jobIsGone: true);

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "the file was opened although no server-side job owns it.");
    }

    /// <summary>
    /// A capability bound to no media source must not stand in for one bound to the job's.
    /// </summary>
    [Fact]
    public void AnItemOnlyCapability_CannotDowngradeAMediaSourceBoundJob()
    {
        var result = Invoke(
            _itemA,
            playlistId: JobA,
            segmentId: JobA + "0",
            jobMediaSourceId: JobMediaSource,
            capability: Capability(mediaSourceId: null, playSessionId: JobPlaySession),
            requestedMediaSourceId: JobMediaSource);

        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>
    /// A capability minted under another play session must not reach this job's segments, even
    /// with the right item and the right media source.
    /// </summary>
    [Fact]
    public void ACapabilityFromAnotherPlaySession_NeverReachesTheJobsSegments()
    {
        var result = Invoke(
            _itemA,
            playlistId: JobA,
            segmentId: JobA + "0",
            jobMediaSourceId: JobMediaSource,
            jobPlaySessionId: JobPlaySession,
            capability: Capability(JobMediaSource, playSessionId: "a-play-session-this-job-does-not-have"),
            requestedMediaSourceId: JobMediaSource);

        Assert.IsType<UnauthorizedResult>(result);
    }

    /// <summary>
    /// The control for the two refusals above: the job's own capability still serves its own bytes.
    /// </summary>
    [Fact]
    public void TheJobsOwnCapability_StillServesItsOwnSegment()
    {
        var result = Invoke(
            _itemA,
            playlistId: JobA,
            segmentId: JobA + "0",
            jobMediaSourceId: JobMediaSource,
            jobPlaySessionId: JobPlaySession,
            capability: Capability(JobMediaSource, JobPlaySession),
            requestedMediaSourceId: JobMediaSource);

        Assert.True(IsFileResultFor(result, JobA + "0.ts"));
    }

    /// <summary>
    /// A caller-named media source that is not the job's is refused, and omitting it is not a way
    /// around that.
    /// </summary>
    [Fact]
    public void AMediaSourceThatIsNotTheJobs_IsRefusedAndOmittingItIsNoBetter()
    {
        Assert.IsType<UnauthorizedResult>(Invoke(
            _itemA,
            playlistId: JobA,
            segmentId: JobA + "0",
            jobMediaSourceId: JobMediaSource,
            jobPlaySessionId: JobPlaySession,
            capability: Capability(JobMediaSource, JobPlaySession),
            requestedMediaSourceId: "a-media-source-this-job-does-not-have"));

        Assert.IsType<UnauthorizedResult>(Invoke(
            _itemA,
            playlistId: JobA,
            segmentId: JobA + "0",
            jobMediaSourceId: JobMediaSource,
            jobPlaySessionId: JobPlaySession,
            capability: Capability(JobMediaSource, JobPlaySession)));
    }

    /// <summary>
    /// A segment name that walks out of the job's directory is refused before any file is opened.
    /// </summary>
    [Fact]
    public void ASegmentNameThatLeavesTheJobsRoot_IsRefused()
    {
        var result = Invoke(_itemA, playlistId: JobA, segmentId: JobA + "/../../escaped");

        Assert.False(result is PhysicalFileResult);
    }

    private static ValidatedPlaybackCapability Capability(string? mediaSourceId, string? playSessionId)
        => new("capability-value", Guid.NewGuid(), _itemA, mediaSourceId, playSessionId);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_transcodePath, true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private static bool IsFileResultFor(ActionResult result, string fileName)
        => result is PhysicalFileResult physical
           && string.Equals(Path.GetFileName(physical.FileName), fileName, StringComparison.Ordinal);

    private ActionResult Invoke(
        Guid itemId,
        string playlistId,
        string segmentId,
        bool jobIsGone = false,
        string? jobMediaSourceId = null,
        string? jobPlaySessionId = null,
        ValidatedPlaybackCapability? capability = null,
        string? requestedMediaSourceId = null)
    {
        var configuration = new Mock<IServerConfigurationManager>(MockBehavior.Loose);
        configuration
            .Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        configuration
            .SetupGet(c => c.CommonApplicationPaths)
            .Returns(Mock.Of<IApplicationPaths>());

        // The one job the server knows about: job A, started for item A. Everything the route is
        // allowed to conclude has to come from here.
        var binding = jobIsGone
            ? null
            : new HlsSegmentBinding(
                JobA,
                _itemA,
                MediaSourceId: jobMediaSourceId,
                PlaySessionId: jobPlaySessionId,
                CanonicalRoot: Path.GetFullPath(_transcodePath),
                CanonicalPlaylistPath: Path.GetFullPath(Path.Combine(_transcodePath, JobA + ".m3u8")),
                Generation: 1);

        var registry = new Mock<IHlsSegmentBindingRegistry>(MockBehavior.Loose);
        registry
            .Setup(r => r.ResolveByPlaylistId(JobA))
            .Returns(binding);

        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns((TranscodingJob?)null);

        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Path = new PathString($"/Videos/{itemId:N}/hls/{playlistId}/{segmentId}.ts")
            }
        };

        if (requestedMediaSourceId is not null)
        {
            httpContext.Request.QueryString = QueryString.Create("mediaSourceId", requestedMediaSourceId);
        }

        if (capability is not null)
        {
            httpContext.Items[ValidatedPlaybackCapability.ItemsKey] = capability;
        }

        var controller = new HlsSegmentController(configuration.Object, transcodeManager.Object, registry.Object)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpContext }
        };

        return controller.GetHlsVideoSegmentLegacy(itemId.ToString("N"), playlistId, segmentId, "ts");
    }
}
