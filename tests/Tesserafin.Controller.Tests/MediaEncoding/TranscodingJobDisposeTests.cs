using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Regression tests for the PR119 fix in <see cref="TranscodingJob.Dispose"/>: it no longer nulls out
/// <see cref="TranscodingJob.CurrentAttempt"/>, so the job's exit state survives disposal.
/// </summary>
/// <remarks>
/// <para>
/// These are the fast, deterministic counterpart to the end-to-end scenario that originally surfaced
/// the bug (<c>PlaybackUrlContractEndToEndTests.Transcode_Hls_PostThenGetStream_ServesManifestAndSegment</c>,
/// tagged <c>Category=Smoke</c> and therefore excluded from <c>ci/run.sh</c>'s mandatory gate). This
/// file carries no <c>Category</c> trait on purpose: it runs in <c>ci/run.sh</c>, so a re-introduction
/// of <c>CurrentAttempt = null;</c> is caught by the merge gate itself, in milliseconds, without a
/// real ffmpeg encode.
/// </para>
/// <para>
/// The production sequence being pinned: <c>Process.Exited</c> fires
/// <c>TranscodeManager.OnFfMpegProcessExited</c>, which sets <c>job.HasExited = true</c>/
/// <c>job.ExitCode</c> and then calls <c>job.Dispose()</c> before returning. Because
/// <see cref="TranscodingJob.HasExited"/> reads <c>CurrentAttempt?.HasExited ?? false</c> and
/// <see cref="TranscodingJob.ExitCode"/> reads <c>CurrentAttempt?.ExitCode ?? 0</c>, nulling the
/// attempt silently reverted both to their fallbacks - hanging
/// <c>DynamicHlsController.GetSegmentResult</c>'s <c>while (!transcodingJob.HasExited)</c> readiness
/// loop forever. Every assertion below deliberately uses values DISTINCT from those fallbacks
/// (<c>HasExited = true</c>, a NON-zero <c>ExitCode</c>), so re-adding <c>CurrentAttempt = null;</c>
/// turns them red instead of leaving them accidentally green.
/// </para>
/// </remarks>
public class TranscodingJobDisposeTests
{
    private const int NonZeroExitCode = 3;

    [Fact]
    public void Dispose_ReleasesUnderlyingProcess_ButPreservesHasExitedAndExitCode()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        using var process = StartProcessThatExitsWith(NonZeroExitCode);
        process.WaitForExit();

        // Exactly what TranscodeManager.OnFfMpegProcessExited does before calling Dispose().
        job.Process = process;
        job.HasExited = true;
        job.ExitCode = process.ExitCode;

        Assert.Equal(NonZeroExitCode, job.ExitCode);

        job.Dispose();

        // The Process handle IS released (TranscodeAttempt.Dispose sets Process = null)...
        Assert.Null(job.Process);
        // ...while the plain exit state it reported survives for any caller still holding this job.
        Assert.True(job.HasExited, "Dispose() must not revert HasExited to its 'no attempt' fallback (false).");
        Assert.Equal(NonZeroExitCode, job.ExitCode);
        Assert.NotNull(job.CurrentAttempt);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrowAndStillPreservesExitState()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        using var process = StartProcessThatExitsWith(NonZeroExitCode);
        process.WaitForExit();
        job.Process = process;
        job.HasExited = true;
        job.ExitCode = process.ExitCode;

        job.Dispose();
        var secondDispose = Record.Exception(job.Dispose);

        Assert.Null(secondDispose);
        Assert.Null(job.Process);
        Assert.True(job.HasExited);
        Assert.Equal(NonZeroExitCode, job.ExitCode);
    }

    [Fact]
    public void Dispose_WithNoAttemptEverCreated_DoesNotThrow()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);

        var exception = Record.Exception(job.Dispose);

        Assert.Null(exception);
        Assert.False(job.HasExited);
        Assert.Equal(0, job.ExitCode);
    }

    /// <summary>
    /// The kill-timer consequence of the same fix, asserted deterministically (no sleeping, no
    /// "the callback did not fire" race): <see cref="TranscodingJob.StartKillTimer(Action{object?})"/>
    /// early-returns when <see cref="TranscodingJob.HasExited"/> is true, so with the exit state
    /// preserved a disposed job can no longer be handed a fresh <see cref="Timer"/> that would later
    /// fire a kill callback against an already-exited process.
    /// </summary>
    [Fact]
    public void StartKillTimer_AfterDisposeOfAnExitedJob_IsANoOp()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            HasExited = true,
            PingTimeout = 1,
        };

        job.Dispose();

        var callbackFired = false;
        job.StartKillTimer(_ => callbackFired = true, intervalMs: 0);

        // Interval 0 means "fire immediately" had a timer actually been created; none was, because
        // HasExited is still true after Dispose().
        Assert.False(callbackFired);
        Assert.True(job.HasExited);
    }

    /// <summary>
    /// No regression in the timer lifecycle the fix touches adjacently: a job that DID start a kill
    /// timer can still be stopped, explicitly timer-disposed, and fully disposed in any order.
    /// </summary>
    [Fact]
    public void KillTimerLifecycle_StopThenDisposeTimerThenDispose_DoesNotThrow()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PingTimeout = 60_000 };
        job.StartKillTimer(_ => { });

        var exception = Record.Exception(() =>
        {
            job.ChangeKillTimerIfStarted();
            job.StopKillTimer();
            job.DisposeKillTimer();
            job.DisposeKillTimer();
            job.StopKillTimer();
            job.Dispose();
        });

        Assert.Null(exception);
    }

    /// <summary>
    /// <see cref="TranscodingJob.Stop"/> remains safe after disposal: the attempt object is still
    /// there (that is the fix), but its Process handle is gone, so Stop() must short-circuit rather
    /// than touch a released handle.
    /// </summary>
    [Fact]
    public void Stop_AfterDispose_DoesNotThrow()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        using var process = StartProcessThatExitsWith(NonZeroExitCode);
        process.WaitForExit();
        job.Process = process;
        job.HasExited = true;
        job.ExitCode = process.ExitCode;
        job.Dispose();

        var exception = Record.Exception(job.Stop);

        Assert.Null(exception);
    }

    /// <summary>
    /// See TranscodingJobStopTests for why the script body must avoid embedded double quotes: .NET
    /// parses ProcessStartInfo.Arguments with the same quoting rules on every OS, and only double
    /// quotes group an argument - the whole script is wrapped in them here for that reason.
    /// </summary>
    private static Process StartProcessThatExitsWith(int exitCode)
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c \"exit /b {exitCode}\"")
            : ("/bin/sh", $"-c \"exit {exitCode}\"");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
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
}
