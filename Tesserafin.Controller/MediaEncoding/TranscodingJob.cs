using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Model.Dto;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Class TranscodingJob.
/// </summary>
public sealed class TranscodingJob : IDisposable
{
    private readonly ILogger<TranscodingJob> _logger;
    private readonly Lock _processLock = new();
    private readonly Lock _timerLock = new();

    private Timer? _killTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodingJob"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TranscodingJobDto}"/> interface.</param>
    public TranscodingJob(ILogger<TranscodingJob> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the play session identifier.
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets the item this job was started for (#153-LTV-R1).
    /// </summary>
    /// <remarks>
    /// The job already carried the play session and the media source. It did not carry the item,
    /// which is why the legacy HLS segment route had nothing to compare its <c>itemId</c> against
    /// and simply did not read it.
    /// </remarks>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the media source identifier this job was started for (#153-LTV-R1).
    /// </summary>
    /// <remarks>
    /// Taken from the request that started the job, not from <see cref="MediaSource"/>: the value
    /// a capability is bound to is the one the client named, and for a live tuner source the two
    /// can differ.
    /// </remarks>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets a number that increases with every job this process starts (#153-LTV-R1). It
    /// distinguishes two jobs that happen to reuse one playlist identifier.
    /// </summary>
    public long Generation { get; set; }

    /// <summary>
    /// Gets or sets the live stream identifier.
    /// </summary>
    public string? LiveStreamId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether is live output.
    /// </summary>
    public bool IsLiveOutput { get; set; }

    /// <summary>
    /// Gets or sets the path.
    /// </summary>
    public MediaSourceInfo? MediaSource { get; set; }

    /// <summary>
    /// Gets or sets path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the type.
    /// </summary>
    public TranscodingJobType Type { get; set; }

    /// <summary>
    /// Gets or sets the current attempt. A job survives across attempts; today there is always
    /// exactly one, but this is the seam a future multi-attempt fallback would swap.
    /// </summary>
    public TranscodeAttempt? CurrentAttempt { get; set; }

    /// <summary>
    /// Gets or sets the process of the current attempt.
    /// </summary>
    public Process? Process
    {
        get => CurrentAttempt?.Process;
        set => (CurrentAttempt ??= new TranscodeAttempt()).Process = value;
    }

    /// <summary>
    /// Gets or sets the active request count.
    /// </summary>
    public int ActiveRequestCount { get; set; }

    /// <summary>
    /// Gets or sets device id.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets cancellation token source.
    /// </summary>
    public CancellationTokenSource? CancellationTokenSource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the current attempt's process has exited.
    /// </summary>
    public bool HasExited
    {
        get => CurrentAttempt?.HasExited ?? false;
        set => (CurrentAttempt ??= new TranscodeAttempt()).HasExited = value;
    }

    /// <summary>
    /// Gets or sets the current attempt's process exit code.
    /// </summary>
    public int ExitCode
    {
        get => CurrentAttempt?.ExitCode ?? 0;
        set => (CurrentAttempt ??= new TranscodeAttempt()).ExitCode = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether is user paused.
    /// </summary>
    public bool IsUserPaused { get; set; }

    /// <summary>
    /// Gets or sets id.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets framerate.
    /// </summary>
    public float? Framerate { get; set; }

    /// <summary>
    /// Gets or sets completion percentage.
    /// </summary>
    public double? CompletionPercentage { get; set; }

    /// <summary>
    /// Gets or sets bytes downloaded.
    /// </summary>
    public long BytesDownloaded { get; set; }

    /// <summary>
    /// Gets or sets bytes transcoded.
    /// </summary>
    public long? BytesTranscoded { get; set; }

    /// <summary>
    /// Gets or sets bit rate.
    /// </summary>
    public int? BitRate { get; set; }

    /// <summary>
    /// Gets or sets transcoding position ticks.
    /// </summary>
    public long? TranscodingPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets download position ticks.
    /// </summary>
    public long? DownloadPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets transcoding throttler.
    /// </summary>
    public TranscodingThrottler? TranscodingThrottler { get; set; }

    /// <summary>
    /// Gets or sets transcoding segment cleaner.
    /// </summary>
    public TranscodingSegmentCleaner? TranscodingSegmentCleaner { get; set; }

    /// <summary>
    /// Gets or sets the pump feeding this job's ffmpeg process from a live stream provider, when
    /// the input is stdin rather than a path or a URL. Null for every other job.
    /// </summary>
    public DirectStreamPump? DirectStreamPump { get; set; }

    /// <summary>
    /// Gets or sets last ping date.
    /// </summary>
    public DateTime LastPingDate { get; set; }

    /// <summary>
    /// Gets or sets ping timeout.
    /// </summary>
    public int PingTimeout { get; set; }

    /// <summary>
    /// Stop kill timer.
    /// </summary>
    public void StopKillTimer()
    {
        lock (_timerLock)
        {
            _killTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Dispose kill timer.
    /// </summary>
    public void DisposeKillTimer()
    {
        lock (_timerLock)
        {
            if (_killTimer is not null)
            {
                _killTimer.Dispose();
                _killTimer = null;
            }
        }
    }

    /// <summary>
    /// Start kill timer.
    /// </summary>
    /// <param name="callback">Callback action.</param>
    public void StartKillTimer(Action<object?> callback)
    {
        StartKillTimer(callback, PingTimeout);
    }

    /// <summary>
    /// Start kill timer.
    /// </summary>
    /// <param name="callback">Callback action.</param>
    /// <param name="intervalMs">Callback interval.</param>
    public void StartKillTimer(Action<object?> callback, int intervalMs)
    {
        if (HasExited)
        {
            return;
        }

        lock (_timerLock)
        {
            if (_killTimer is null)
            {
                _logger.LogDebug("Starting kill timer at {0}ms. JobId {1} PlaySessionId {2}", intervalMs, Id, PlaySessionId);
                _killTimer = new Timer(new TimerCallback(callback), this, intervalMs, Timeout.Infinite);
            }
            else
            {
                _logger.LogDebug("Changing kill timer to {0}ms. JobId {1} PlaySessionId {2}", intervalMs, Id, PlaySessionId);
                _killTimer.Change(intervalMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Change kill timer if started.
    /// </summary>
    public void ChangeKillTimerIfStarted()
    {
        if (HasExited)
        {
            return;
        }

        lock (_timerLock)
        {
            if (_killTimer is not null)
            {
                var intervalMs = PingTimeout;

                _logger.LogDebug("Changing kill timer to {0}ms. JobId {1} PlaySessionId {2}", intervalMs, Id, PlaySessionId);
                _killTimer.Change(intervalMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Stops the transcoding job: session-scoped throttling/cleanup, then the current attempt's process.
    /// </summary>
    public void Stop()
    {
        lock (_processLock)
        {
#pragma warning disable CA1849 // Can't await in lock block
            TranscodingThrottler?.Stop().GetAwaiter().GetResult();
            TranscodingSegmentCleaner?.Stop();

            // Stop the pump before the process. Closing stdin is the graceful stop for a piped job -
            // ffmpeg sees EOF and finalizes its output - and it guarantees no write races against a
            // process that is about to be killed. StopAsync is cancellation-driven and awaits the
            // pump's own completion, so no pumping task outlives this call.
            DirectStreamPump?.StopAsync().GetAwaiter().GetResult();
#pragma warning restore CA1849

            CurrentAttempt?.Stop(_logger, Path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Deliberately does NOT null out CurrentAttempt after disposing it (contrast with every other
        // disposable field below, which IS nulled). CurrentAttempt.Dispose() already releases the
        // Process handle (CurrentAttempt.Process = null) - keeping the TranscodeAttempt object itself
        // alive preserves its plain HasExited/ExitCode values for whichever caller is still holding
        // this TranscodingJob reference. Real, observed bug this fixes: Process.Exited fires
        // OnFfMpegProcessExited, which sets job.HasExited = true and THEN calls job.Dispose() (this
        // method) before returning - if this nulled CurrentAttempt, HasExited's getter
        // (CurrentAttempt?.HasExited ?? false) would silently revert to false the instant Dispose()
        // ran, even though the process had genuinely already exited. Any concurrent poller relying on
        // HasExited to know the transcode is done (DynamicHlsController.GetSegmentResult's
        // "while (!transcodingJob.HasExited)" readiness loop, in particular) would then loop forever:
        // its exit condition can never become true again, and - for a short-lived encode with only one
        // segment - the "or the next segment appeared" alternative never becomes true either, so the
        // request hangs indefinitely instead of serving the segment that is already correct and
        // complete on disk. See PlaybackUrlContractEndToEndTests' Transcode_Hls_* scenario remarks for
        // how this was originally found.
        CurrentAttempt?.Dispose();
#pragma warning disable CA1849 // Can't await in a synchronous Dispose
        // Backstop for the paths that dispose a job without calling Stop() first - notably
        // OnFfMpegProcessExited, which fires when ffmpeg ends on its own.
        DirectStreamPump?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore CA1849
        DirectStreamPump = null;
        _killTimer?.Dispose();
        _killTimer = null;
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = null;
        TranscodingThrottler?.Dispose();
        TranscodingThrottler = null;
        TranscodingSegmentCleaner?.Dispose();
        TranscodingSegmentCleaner = null;
    }
}
