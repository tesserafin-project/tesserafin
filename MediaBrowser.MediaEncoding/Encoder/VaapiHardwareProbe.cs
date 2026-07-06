using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Encoder;

/// <summary>
/// Runs a real trial encode against a VAAPI render node at server startup, to confirm the device
/// actually works end-to-end - not just that a render node file exists or a driver name matched
/// (which is all <see cref="MediaEncoder"/>'s existing vendor detection checks). This is the
/// runtime probe the transcoding-pipeline plan's PR6 deliberately deferred: it costs startup
/// latency and spins the GPU once, which is why it only runs when hardware encoding is enabled but
/// no backend has been chosen yet (see <see cref="MediaEncoder.DetermineAutoSelectedHardwareAccelerationType"/>).
/// </summary>
public sealed class VaapiHardwareProbe
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(10);

    private readonly IFfmpegProcessRunner _processRunner;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VaapiHardwareProbe"/> class.
    /// </summary>
    /// <param name="processRunner">Runner used to launch the trial-encode process.</param>
    /// <param name="logger">Logger for the probe outcome.</param>
    public VaapiHardwareProbe(IFfmpegProcessRunner processRunner, ILogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// Attempts a one-second h264_vaapi trial encode against <paramref name="devicePath"/>.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg executable.</param>
    /// <param name="devicePath">The VAAPI render node path, for example <c>/dev/dri/renderD128</c>.</param>
    /// <param name="cancellationToken">Cancellation token for the probe process.</param>
    /// <returns><c>true</c> if the trial encode completed successfully; <c>false</c> otherwise.</returns>
    public async Task<bool> ProbeAsync(string ffmpegPath, string devicePath, CancellationToken cancellationToken)
    {
        // 320x240 is not an arbitrary choice: a 64x64 trial encode was rejected by real hardware
        // here with "Hardware does not support encoding at size 64x64 (constraints: width
        // 128-4096 height 128-4096)" - too small a probe frame produces a false negative, not a
        // faster one. 320x240 is confirmed accepted against the real AMD VAAPI device this was
        // verified on.
        var command = FfmpegCommand.FromArgumentLine(
            ffmpegPath,
            $"-hide_banner -init_hw_device vaapi=va:{devicePath} -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -vf format=nv12,hwupload -c:v h264_vaapi -f null -");

        var result = await _processRunner.RunProbeAsync(command, _probeTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.TimedOut && result.ExitCode == 0)
        {
            _logger.LogInformation("VAAPI startup probe succeeded on {DevicePath}", devicePath);
            return true;
        }

        var category = ClassifyFailure(result.StandardError);
        _logger.LogInformation(
            "VAAPI startup probe failed on {DevicePath}: timedOut={TimedOut} exitCode={ExitCode} category={Category}",
            devicePath,
            result.TimedOut,
            result.ExitCode,
            category);
        return false;
    }

    private static FfmpegErrorCategory ClassifyFailure(string standardError)
    {
        foreach (var line in standardError.Split('\n'))
        {
            var category = FfmpegErrorClassifier.Classify(line);
            if (category != FfmpegErrorCategory.Unknown)
            {
                return category;
            }
        }

        return FfmpegErrorCategory.Unknown;
    }
}
