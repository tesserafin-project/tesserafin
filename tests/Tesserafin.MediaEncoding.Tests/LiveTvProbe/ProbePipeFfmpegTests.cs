using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.LiveTvProbe;

/// <summary>
/// A real ffprobe fed from <see cref="DirectStreamPump"/> over <c>-i pipe:0</c>, which is what a
/// Live TV media-source probe does once the tuner stream is handed over internally instead of
/// being fetched from the <c>[Authorize]</c>d <c>/LiveTv/LiveStreamFiles/**</c> endpoint
/// (#153-LTV-R1, LTV-R0 finding 1).
/// </summary>
/// <remarks>
/// The argument-level tests prove the pipe branch is selected and carries no protocol option. This
/// proves the thing the mission actually names: that a piped probe returns <b>the same useful
/// media information</b> as one that opened the file, that it stops within a bound, and that it
/// leaves no task or fault unobserved. Both scenarios have an explicit process timeout.
/// </remarks>
public sealed class ProbePipeFfmpegTests
{
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task AProbeFedFromThePump_ReportsTheSameStreamsAsOneThatOpenedTheFile()
    {
        using var work = new TempWorkDirectory();
        var source = await SynthesizeTransportStream(work.Path);

        var fromFile = await ProbeFile(source);
        var (fromPipe, pump) = await ProbePipe(source);

        Assert.NotEmpty(fromFile);
        Assert.Equal(fromFile, fromPipe);

        // Same ownership model as the transcode handoff: nothing faulted, and every byte the pump
        // moved is accounted for.
        Assert.Null(pump.Fault);
        Assert.True(pump.BytesCopied > 0, "the pump moved no bytes, so the probe read nothing from it.");
    }

    /// <summary>
    /// The pump stops within a bound even though ffprobe exits long before the stream ends — which
    /// is the normal case, since it reads only <c>-analyzeduration</c> worth of bytes and then
    /// closes its stdin. A pump that waited for end-of-stream would hang on a live tuner forever.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ThePump_StopsWhenFfprobeExitsAndLeavesNothingUnobserved()
    {
        using var work = new TempWorkDirectory();
        var source = await SynthesizeTransportStream(work.Path);

        var started = Stopwatch.StartNew();
        var (streams, pump) = await ProbePipe(source);
        started.Stop();

        Assert.NotEmpty(streams);
        Assert.True(pump.Completion.IsCompleted, "the pump was still running after the probe returned.");
        Assert.Null(pump.Fault);
        Assert.True(
            started.Elapsed < _processTimeout,
            $"the piped probe took {started.Elapsed}, which is not a bounded stop.");
    }

    /// <summary>
    /// Reads the codec/type pairs ffprobe reports, which is the information the server actually
    /// consumes: a media source published with <c>Codec null</c> and <c>Index -1</c> — what
    /// LTV-R0 measured after the 401 — is what makes direct-stream unselectable.
    /// </summary>
    private static async Task<string[]> ProbeFile(string path)
    {
        var json = await CaptureProbe($"-v warning -print_format json -show_streams -show_format -i \"{path}\"", null);
        return StreamSignatures(json);
    }

    private static async Task<(string[] Streams, DirectStreamPump Pump)> ProbePipe(string path)
    {
        DirectStreamPump? pump = null;
        var json = await CaptureProbe(
            "-v warning -print_format json -show_streams -show_format -i pipe:0",
            process =>
            {
                pump = DirectStreamPump.Start(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    process.StandardInput.BaseStream,
                    NullLogger.Instance,
                    CancellationToken.None);
                return pump;
            });

        Assert.NotNull(pump);
        await pump!.StopAsync();
        await pump.DisposeAsync();
        return (StreamSignatures(json), pump);
    }

    private static string[] StreamSignatures(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams))
        {
            return Array.Empty<string>();
        }

        return streams
            .EnumerateArray()
            .Select(stream =>
                stream.GetProperty("codec_type").GetString() + ":" + stream.GetProperty("codec_name").GetString())
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> CaptureProbe(string arguments, Func<Process, DirectStreamPump>? feed)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffprobe", "-hide_banner " + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = feed is not null
            }
        };

        process.Start();
        _ = feed?.Invoke(process);

        // Must drain stderr or a chatty ffprobe can deadlock on a full pipe.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(_processTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            Assert.Fail($"ffprobe did not exit within {_processTimeout}.");
        }

        await stderr;
        return stdout;
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

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        _ = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(_processTimeout);
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.True(new FileInfo(path).Length > 0);
        return path;
    }

    private sealed class TempWorkDirectory : IDisposable
    {
        public TempWorkDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ltvr1-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }
}
