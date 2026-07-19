using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Reefin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: synthesizes real, on-disk media fixtures with a real ffmpeg process - the same
/// no-test-asset-dependency approach <c>HlsSmokeTests</c> already uses (a hand-built
/// <c>testsrc</c>/<c>sine</c> lavfi source), extended here to produce files a real, booted server can
/// register as a library item and actually serve. Every fixture is a real, decodable file: what
/// distinguishes DirectPlay from remux from transcode in the end-to-end tests is the request
/// (<c>PlaybackConstraints.AllowDirectPlay</c>/<c>AllowDirectStream</c>/<c>AllowTranscoding</c>), not
/// the file - see <c>PlaybackUrlContractEndToEndTests</c>' remarks for why that is a faithful,
/// deliberately-chosen simplification.
/// </summary>
public static class EndToEndMediaFixtures
{
    /// <summary>The exact duration, in seconds, every synthesized fixture below encodes.</summary>
    public const int DurationSeconds = 2;

    /// <summary>The exact duration above, expressed in the ticks <c>BaseItem.RunTimeTicks</c> expects.</summary>
    public const long DurationTicks = DurationSeconds * TimeSpan.TicksPerSecond;

    public const int Width = 320;

    public const int Height = 240;

    /// <summary>
    /// Synthesizes a real H.264/AAC MP4 - a container/codec pair any reasonable client declares as a
    /// DirectPlay target, so this single fixture backs the DirectPlay, remux, transcode, and subtitle
    /// scenarios alike (the request's own constraints decide which method actually gets used).
    /// </summary>
    /// <param name="directory">The directory to write the fixture into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <returns>The full path to the synthesized file.</returns>
    public static async Task<string> CreateH264AacMp4Async(string directory, string fileName = "fixture.mp4")
    {
        var path = Path.Combine(directory, fileName);
        string[] arguments =
        [
            "-hide_banner",
            "-y",
            "-f", "lavfi", "-i", $"testsrc=size={Width}x{Height}:rate=15:duration={DurationSeconds}",
            "-f", "lavfi", "-i", $"sine=frequency=1000:duration={DurationSeconds}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-movflags", "+faststart",
            path,
        ];

        await RunFfmpegAsync(arguments).ConfigureAwait(false);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg did not produce a non-empty fixture at {path}.");
        }

        return path;
    }

    /// <summary>
    /// Writes a real, valid external SubRip (.srt) subtitle sidecar - no ffmpeg needed, an external
    /// subtitle is a plain sidecar file by definition (PR117 design doc §2.2's <c>SubtitleUrl</c>).
    /// </summary>
    /// <param name="directory">The directory to write the sidecar into.</param>
    /// <param name="fileName">The file name (including extension) to write.</param>
    /// <returns>The full path to the written subtitle file.</returns>
    public static string CreateExternalSrt(string directory, string fileName = "fixture.srt")
    {
        var path = Path.Combine(directory, fileName);
        var content = new StringBuilder()
            .AppendLine("1")
            .AppendLine("00:00:00,000 --> 00:00:01,000")
            .AppendLine("PR119 end-to-end external subtitle fixture.")
            .AppendLine()
            .AppendLine("2")
            .AppendLine("00:00:01,000 --> 00:00:02,000")
            .AppendLine("Second cue, still real bytes on disk.")
            .ToString();

        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static async Task RunFfmpegAsync(string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = E2eApplicationFactory.FfmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"ffmpeg fixture synthesis timed out. Arguments: {string.Join(' ', arguments)}");
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited {process.ExitCode}. Arguments: {string.Join(' ', arguments)}\nstderr tail: {Tail(stderr)}");
        }
    }

    private static string Tail(string text, int maxChars = 2000) => text.Length <= maxChars ? text : text[^maxChars..];
}
