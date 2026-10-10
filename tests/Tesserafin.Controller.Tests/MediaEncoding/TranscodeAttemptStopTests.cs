using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// A process the server stops must be recognisable as stopped before anything can make it exit
/// (tesserafin#119): the exit handler tells a stop from a failure by this flag alone.
/// </summary>
public class TranscodeAttemptStopTests
{
    [Fact]
    public void NewAttempt_IsNotStopRequested()
    {
        using var attempt = new TranscodeAttempt();

        Assert.False(attempt.StopRequested);
    }

    [Fact]
    public void JobStop_MarksTheAttempt_EvenWithNoProcessToStop()
    {
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { CurrentAttempt = new TranscodeAttempt() };

        job.Stop();

        Assert.True(job.CurrentAttempt!.StopRequested);
    }

    [Fact]
    public void AttemptStop_MarksItself_EvenWhenTheProcessHasAlreadyExited()
    {
        using var attempt = new TranscodeAttempt { HasExited = true };

        attempt.Stop(NullLogger.Instance, null);

        Assert.True(attempt.StopRequested);
    }

    [Fact]
    public void AttemptStop_DoesNotThrow_WhenTheProcessExitedAndTheAttemptDoesNotSayYet()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs a POSIX `true`.");

        // tesserafin#289. A process that is gone while its exit is still being handled: HasExited
        // is false, and the "q" written to its standard input meets a closed pipe.
        using var process = Process.Start(new ProcessStartInfo("true") { RedirectStandardInput = true, UseShellExecute = false })!;
        process.WaitForExit();
        using var attempt = new TranscodeAttempt { Process = process };

        attempt.Stop(NullLogger.Instance, "/nowhere/x.m3u8");

        Assert.True(attempt.StopRequested);
    }
}
