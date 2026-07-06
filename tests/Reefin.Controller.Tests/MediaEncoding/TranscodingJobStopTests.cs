using System;
using System.Diagnostics;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Reefin.Controller.Tests.MediaEncoding;

/// <summary>
/// Characterization tests for <see cref="TranscodingJob.Stop"/> against real subprocesses,
/// written before any TranscodingJob/TranscodeAttempt decomposition so the split can be verified
/// against these instead of "it builds". <see cref="TranscodingJob"/> had zero test coverage
/// before this file.
/// </summary>
public class TranscodingJobStopTests
{
    [Fact]
    public void Stop_ProcessReadsQFromStdin_ExitsGracefullyWithoutBeingKilled()
    {
        // A process that behaves like ffmpeg's own "press q to stop" handling: reading "q" from
        // stdin and exiting cleanly well within Stop()'s 5-second grace period.
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        using var process = StartRedirectedProcess(ShellCommand(
            unix: "read line; if [ x$line = xq ]; then exit 0; fi; exit 1",
            windows: "set /p line= & if x%line%==xq (exit /b 0) else (exit /b 1)"));
        job.Process = process;

        var stopwatch = Stopwatch.StartNew();
        job.Stop();
        stopwatch.Stop();

        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Expected the graceful 'q' path, not the 5s kill fallback, took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Stop_ProcessIgnoresStdin_IsKilledAfterFiveSecondTimeout()
    {
        // A process that never reads stdin at all (writing "q" to it is a no-op) - Stop() must
        // fall through to Process.Kill() after its 5-second WaitForExit grace period.
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        using var process = StartRedirectedProcess(ShellCommand(
            unix: "sleep 30",
            windows: "ping -n 31 127.0.0.1 >NUL"));
        job.Process = process;

        var stopwatch = Stopwatch.StartNew();
        job.Stop();
        stopwatch.Stop();

        // Stop() sends SIGKILL via Process.Kill() but does not itself wait for the OS to reap
        // the process afterwards - in production that's observed asynchronously via the
        // Process.Exited event instead. Give it a moment here rather than asserting the instant
        // Stop() returns.
        process.WaitForExit(TimeSpan.FromSeconds(2));

        Assert.True(process.HasExited);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(5), $"Expected Stop() to wait out the full 5s grace period before killing, took {stopwatch.Elapsed}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Expected the kill to follow soon after the 5s timeout, took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Stop_ProcessAlreadyExited_DoesNotThrow()
    {
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { HasExited = true };
        using var process = StartRedirectedProcess(ShellCommand(unix: "exit 0", windows: "exit /b 0"));
        process.WaitForExit();
        job.Process = process;

        var exception = Record.Exception(job.Stop);

        Assert.Null(exception);
    }

    private static Process StartRedirectedProcess((string FileName, string Arguments) command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.FileName, command.Arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        process.Start();
        return process;
    }

    /// <summary>
    /// See FfmpegProcessRunnerTests for why the script body must avoid embedded double quotes:
    /// .NET parses ProcessStartInfo.Arguments with the same quoting rules on every OS, and only
    /// double quotes group an argument - the whole script is wrapped in them here for that reason.
    /// </summary>
    private static (string FileName, string Arguments) ShellCommand(string unix, string windows)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c \"{windows}\"")
            : ("/bin/sh", $"-c \"{unix}\"");
}
