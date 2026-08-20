using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// A real ffmpeg process fed from <see cref="DirectStreamPump"/> over <c>-i pipe:0</c>, which is
/// what a Live TV transcode does once the tuner stream is handed over internally instead of being
/// fetched from the <c>[Authorize]</c>d <c>/LiveTv/LiveStreamFiles/**</c> endpoint.
/// </summary>
/// <remarks>
/// The unit tests prove the pump moves bytes. This proves the bytes it moves are the ones ffmpeg
/// needs: that <c>pipe:0</c> really is a working input for the HLS argument shape the server builds,
/// and that the resulting segments decode. Both scenarios have an explicit process timeout.
/// </remarks>
public class DirectStreamPipeFfmpegTests
{
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Ffmpeg_FedFromTheDirectStreamPump_ProducesDecodableHlsSegments()
    {
        using var workDirectory = new TempWorkDirectory();
        var source = await SynthesizeTransportStream(workDirectory.Path);
        var outputDirectory = Path.Combine(workDirectory.Path, "piped");
        Directory.CreateDirectory(outputDirectory);

        using var process = StartHlsTranscode("-i pipe:0", outputDirectory);
        var pump = DirectStreamPump.Start(
            new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
            process.StandardInput.BaseStream,
            NullLogger.Instance,
            CancellationToken.None);

        await AwaitExit(process, "reading from pipe:0");
        Assert.Equal(0, process.ExitCode);

        await pump.StopAsync();
        Assert.Null(pump.Fault);
        Assert.Equal(new FileInfo(source).Length, pump.BytesCopied);

        var segments = Directory.GetFiles(outputDirectory, "*.ts");
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.True(new FileInfo(segment).Length > 0, $"{segment} is empty."));

        // Non-empty is not the same as playable.
        foreach (var segment in segments)
        {
            Assert.True(await IsDecodable(segment), $"{segment} did not decode.");
        }
    }

    [Fact]
    public async Task Ffmpeg_FedFromAPlainFilePath_StillWorks()
    {
        // The control for "paths without a provider are unchanged": the same argument shape with a
        // file input rather than pipe:0.
        using var workDirectory = new TempWorkDirectory();
        var source = await SynthesizeTransportStream(workDirectory.Path);
        var outputDirectory = Path.Combine(workDirectory.Path, "file");
        Directory.CreateDirectory(outputDirectory);

        using var process = StartHlsTranscode($"-i \"{source}\"", outputDirectory);

        await AwaitExit(process, "reading from a file");
        Assert.Equal(0, process.ExitCode);

        var segments = Directory.GetFiles(outputDirectory, "*.ts");
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.True(new FileInfo(segment).Length > 0, $"{segment} is empty."));
    }

    private static async Task<string> SynthesizeTransportStream(string directory)
    {
        var path = Path.Combine(directory, "tuner.ts");
        var arguments = string.Join(
            ' ',
            "-hide_banner",
            "-loglevel error",
            "-y",
            "-f lavfi -i testsrc=size=320x240:rate=15:duration=4",
            "-f lavfi -i sine=frequency=440:duration=4",
            "-c:v libx264 -preset ultrafast -c:a aac -f mpegts",
            $"\"{path}\"");

        var exitCode = await RunToCompletion("ffmpeg", arguments);
        Assert.Equal(0, exitCode);
        Assert.True(new FileInfo(path).Length > 0);

        return path;
    }

    private static Process StartHlsTranscode(string input, string outputDirectory)
    {
        // The Live TV argument shape the server builds, minus the hardware-specific parts: the
        // point is the input selection, not the encoder.
        var arguments = string.Join(
            ' ',
            "-hide_banner",
            "-loglevel error",
            "-analyzeduration 3000000 -probesize 1G",
            "-fflags +igndts",
            input,
            "-map_metadata -1 -map_chapters -1 -sn",
            "-c:v libx264 -preset ultrafast -c:a aac",
            "-copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048",
            "-f hls -hls_time 2 -hls_segment_type mpegts -start_number 0",
            $"-hls_segment_filename \"{Path.Combine(outputDirectory, "segment%d.ts")}\"",
            "-hls_playlist_type event -hls_list_size 0 -y",
            $"\"{Path.Combine(outputDirectory, "live.m3u8")}\"");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();

        // Must drain stderr or a chatty ffmpeg can deadlock on a full pipe.
        _ = process.StandardError.ReadToEndAsync();

        return process;
    }

    private static async Task AwaitExit(Process process, string what)
    {
        using var timeout = new CancellationTokenSource(_processTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            Assert.Fail($"ffmpeg did not exit within {_processTimeout} while {what}.");
        }
    }

    private static async Task<bool> IsDecodable(string path)
        => await RunToCompletion("ffmpeg", $"-hide_banner -loglevel error -xerror -i \"{path}\" -f null -") == 0;

    private static async Task<int> RunToCompletion(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            }
        };

        process.Start();
        _ = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(_processTimeout);
        await process.WaitForExitAsync(timeout.Token);

        return process.ExitCode;
    }

    private sealed class TempWorkDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ltv-s0-ffmpeg-");

        public string Path => _directory.FullName;

        public void Dispose() => _directory.Delete(true);
    }
}
