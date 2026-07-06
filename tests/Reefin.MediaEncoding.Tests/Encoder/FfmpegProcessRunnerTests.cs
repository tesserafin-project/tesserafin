using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Encoder;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Exercises <see cref="FfmpegProcessRunner"/> against trivial shell subprocesses instead of
/// ffmpeg itself, so these run without any media tooling installed. The behaviors covered here -
/// concurrent stdout/stderr drain past the OS pipe buffer, timeout, cancellation, exit code, and
/// per-process environment variables - are exactly what a behavior-preserving swap of
/// <see cref="EncoderValidator"/>'s process handling must get right.
/// </summary>
public class FfmpegProcessRunnerTests
{
    private static readonly TimeSpan _ample = TimeSpan.FromSeconds(30);

    private readonly FfmpegProcessRunner _runner = new();

    [Fact]
    public async Task RunProbeAsync_FloodsBothStreamsPastPipeBuffer_NoDeadlock()
    {
        // ~20000 lines per stream is comfortably past the typical 64KB OS pipe buffer that a
        // single-stream-at-a-time reader (the bug in the old EncoderValidator.GetProcessOutput)
        // would deadlock on.
        var command = ShellCommand(
            unix: "for i in $(seq 1 20000); do echo out-line-$i; echo err-line-$i 1>&2; done",
            windows: "for /L %i in (1,1,20000) do @(echo out-line-%i & echo err-line-%i 1>&2)");

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out-line-1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("out-line-20000", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("err-line-1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("err-line-20000", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProbeAsync_ProcessExceedsTimeout_IsKilledAndReportsTimedOut()
    {
        var command = ShellCommand(
            unix: "sleep 30",
            windows: "ping -n 31 127.0.0.1 >NUL");

        var stopwatch = Stopwatch.StartNew();
        var result = await _runner.RunProbeAsync(command, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected the process to be killed well before its own sleep finished, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunProbeAsync_ExternalCancellation_KillsProcessAndThrows()
    {
        var command = ShellCommand(
            unix: "sleep 30",
            windows: "ping -n 31 127.0.0.1 >NUL");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunProbeAsync(command, _ample, cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected the process to be killed well before its own sleep finished, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunProbeAsync_ExitCodeIsCaptured()
    {
        var command = ShellCommand(
            unix: "exit 7",
            windows: "exit /b 7");

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task RunProbeAsync_EnvironmentVariables_AreDeliveredToChildProcessOnly()
    {
        var command = ShellCommand(
            unix: "echo value=$REEFIN_TEST_VAR",
            windows: "echo value=%REEFIN_TEST_VAR%") with
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("REEFIN_TEST_VAR", "hello-from-command"),
        };

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None);

        Assert.Contains("value=hello-from-command", result.StandardOutput, StringComparison.Ordinal);
        Assert.Null(Environment.GetEnvironmentVariable("REEFIN_TEST_VAR"));
    }

    [Fact]
    public async Task RunProbeAsync_StandardInput_IsWrittenToChildProcess()
    {
        var command = ShellCommand(
            unix: "read line; echo got:-$line",
            windows: "set /p line= & echo got:-%line%");

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None, standardInput: "hello-stdin\n");

        Assert.Contains("got:-hello-stdin", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// .NET parses <see cref="ProcessStartInfo.Arguments"/> with the same quoting rules on every
    /// OS (double-quotes group, no special meaning for single-quotes) - so the script body must
    /// never contain a literal '"', and the whole script is wrapped in double quotes here purely
    /// to keep it as one argument to the shell's -c/"/c".
    /// </summary>
    private static FfmpegCommand ShellCommand(string unix, string windows)
        => OperatingSystem.IsWindows()
            ? FfmpegCommand.FromArgumentLine("cmd.exe", $"/c \"{windows}\"")
            : FfmpegCommand.FromArgumentLine("/bin/sh", $"-c \"{unix}\"");
}
