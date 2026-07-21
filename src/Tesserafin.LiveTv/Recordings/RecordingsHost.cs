using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Tesserafin.Controller.LiveTv;
using Tesserafin.LiveTv.Timers;

namespace Tesserafin.LiveTv.Recordings;

/// <summary>
/// <see cref="IHostedService"/> responsible for Live TV recordings.
/// </summary>
public sealed class RecordingsHost : IHostedService
{
    private readonly IRecordingsManager _recordingsManager;
    private readonly TimerManager _timerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingsHost"/> class.
    /// </summary>
    /// <param name="recordingsManager">The <see cref="IRecordingsManager"/>.</param>
    /// <param name="timerManager">The <see cref="TimerManager"/>.</param>
    public RecordingsHost(IRecordingsManager recordingsManager, TimerManager timerManager)
    {
        _recordingsManager = recordingsManager;
        _timerManager = timerManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timerManager.RestartTimers();
        return _recordingsManager.CreateRecordingFolders();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
