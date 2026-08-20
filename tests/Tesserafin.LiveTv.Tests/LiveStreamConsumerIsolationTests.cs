using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.LiveTv.TunerHosts;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.LiveTv.Tests;

/// <summary>
/// Two ffmpeg processes reading the same tuner must not read through one <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <c>TranscodeManager.StartFfMpeg</c> calls <c>GetStream()</c> once per process, so this is the
/// property that call relies on: a shared instance would interleave two consumers' reads and
/// corrupt both. Sharing the tuner is the point; sharing the reader is the bug.
/// </remarks>
public class LiveStreamConsumerIsolationTests : IDisposable
{
    private readonly DirectoryInfo _transcodeDirectory = Directory.CreateTempSubdirectory("ltv-s0-livestream-");

    [Fact]
    public async Task GetStream_CalledTwice_GivesTwoIndependentReaders()
    {
        var liveStream = CreateLiveStream();
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(TempFilePathOf(liveStream), payload, TestContext.Current.CancellationToken);

        using var first = liveStream.GetStream();
        using var second = liveStream.GetStream();

        Assert.False(ReferenceEquals(first, second), "Two consumers were handed the same Stream instance.");

        // Independent positions: draining one must leave the other whole.
        var firstBuffer = new byte[payload.Length];
        var readByFirst = await ReadExactlyAsync(first, firstBuffer);
        var secondBuffer = new byte[payload.Length];
        var readBySecond = await ReadExactlyAsync(second, secondBuffer);

        Assert.Equal(payload.Length, readByFirst);
        Assert.Equal(payload.Length, readBySecond);
        Assert.Equal(payload, firstBuffer);
        Assert.Equal(payload, secondBuffer);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the temp transcode directory.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _transcodeDirectory.Delete(true);
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), TestContext.Current.CancellationToken);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private string TempFilePathOf(LiveStream liveStream)
        => Path.Combine(_transcodeDirectory.FullName, liveStream.UniqueId + ".ts");

    private LiveStream CreateLiveStream()
    {
        var applicationPaths = new Mock<IApplicationPaths>();
        var configurationManager = new Mock<IConfigurationManager>();
        configurationManager
            .Setup(x => x.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodeDirectory.FullName });
        configurationManager
            .SetupGet(x => x.CommonApplicationPaths)
            .Returns(applicationPaths.Object);

        return new LiveStream(
            new MediaSourceInfo(),
            null,
            Mock.Of<IFileSystem>(),
            NullLogger.Instance,
            configurationManager.Object,
            Mock.Of<IStreamHelper>());
    }
}
