using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
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
        var result = Invoke(_itemA, playlistId: JobA, segmentId: JobA + "0", job: null);

        Assert.False(
            IsFileResultFor(result, JobA + "0.ts"),
            "the file was opened although no server-side job owns it.");
    }

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

    private ActionResult Invoke(Guid itemId, string playlistId, string segmentId, TranscodingJob? job = null)
    {
        var configuration = new Mock<IServerConfigurationManager>(MockBehavior.Loose);
        configuration
            .Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        configuration
            .SetupGet(c => c.CommonApplicationPaths)
            .Returns(Mock.Of<IApplicationPaths>());

        var fileSystem = new Mock<IFileSystem>(MockBehavior.Loose);
        fileSystem
            .Setup(f => f.GetFilePaths(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string directory, bool _) => Directory.GetFiles(directory));

        var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
        transcodeManager
            .Setup(t => t.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()))
            .Returns(job);

        var controller = new HlsSegmentController(fileSystem.Object, configuration.Object, transcodeManager.Object)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Path = new PathString($"/Videos/{itemId:N}/hls/{playlistId}/{segmentId}.ts")
                    }
                }
            }
        };

        return controller.GetHlsVideoSegmentLegacy(itemId.ToString("N"), playlistId, segmentId, "ts");
    }
}
