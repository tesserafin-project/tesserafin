using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;

namespace MediaBrowser.MediaEncoding.Encoder;

/// <inheritdoc cref="IFfmpegProcessRunner" />
public sealed class FfmpegProcessRunner : IFfmpegProcessRunner
{
    /// <inheritdoc />
    public async Task<FfmpegProcessResult> RunProbeAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.Executable, command.ToArgumentLine())
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                WorkingDirectory = string.IsNullOrEmpty(command.WorkingDirectory) ? string.Empty : command.WorkingDirectory,
            }
        };

        foreach (var (key, value) in command.EnvironmentVariables)
        {
            process.StartInfo.Environment[key] = value;
        }

        process.Start();

        if (standardInput is not null)
        {
            using var writer = process.StandardInput;
            await writer.WriteAsync(standardInput).ConfigureAwait(false);
        }

        // Both streams must be drained concurrently: reading one to completion before starting
        // the other can deadlock if the unread stream fills its OS pipe buffer first. They are
        // read with CancellationToken.None deliberately - killing the process below closes its
        // pipes, which is what ends these reads (with whatever was captured so far), rather than
        // having them throw and lose that partial output.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new FfmpegProcessResult(
            timedOut ? null : process.ExitCode,
            stdOut,
            stdErr,
            timedOut);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill.
        }
    }
}
