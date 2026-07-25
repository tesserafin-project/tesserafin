using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Encoder;

/// <summary>
/// Runs a real ffmpeg trial encode to confirm a hardware acceleration backend candidate actually
/// works - not just that its build support is compiled in and its device path exists (which is all
/// <see cref="HardwareBackendCandidate.IsApplicable"/> checks). This generalizes PR8's VAAPI-only
/// startup probe (transcoding-pipeline plan PR10) to any backend's trial-encode argument line, for
/// use by <see cref="MediaEncoder"/> together with <see cref="HardwareBackendSelector"/>.
/// </summary>
public sealed class HardwareBackendProbe
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(10);

    private readonly IFfmpegProcessRunner _processRunner;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HardwareBackendProbe"/> class.
    /// </summary>
    /// <param name="processRunner">Runner used to launch the trial-encode process.</param>
    /// <param name="logger">Logger for the probe outcome.</param>
    public HardwareBackendProbe(IFfmpegProcessRunner processRunner, ILogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// Runs <paramref name="trialEncodeArguments"/> and reports whether it exited successfully.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg executable.</param>
    /// <param name="trialEncodeArguments">The full ffmpeg argument line for the trial encode.</param>
    /// <param name="cancellationToken">Cancellation token for the probe process.</param>
    /// <returns><c>true</c> if the trial encode completed successfully; <c>false</c> otherwise.</returns>
    public async Task<bool> ProbeAsync(string ffmpegPath, string trialEncodeArguments, CancellationToken cancellationToken)
        => (await ProbeDetailedAsync(ffmpegPath, trialEncodeArguments, cancellationToken).ConfigureAwait(false)).Succeeded;

    /// <summary>
    /// Runs <paramref name="trialEncodeArguments"/> and reports the full outcome, including why it
    /// failed. This is what <see cref="HardwareSelectionPlanner"/> consumes: the startup decision
    /// records the failure category per candidate, so "no accelerator was selected" can be told
    /// apart from "the accelerator is there but its device would not initialize".
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg executable.</param>
    /// <param name="trialEncodeArguments">The full ffmpeg argument line for the trial encode.</param>
    /// <param name="cancellationToken">Cancellation token for the probe process.</param>
    /// <returns>The trial encode's outcome.</returns>
    public async Task<HardwareProbeOutcome> ProbeDetailedAsync(string ffmpegPath, string trialEncodeArguments, CancellationToken cancellationToken)
    {
        var command = FfmpegCommand.FromArgumentLine(ffmpegPath, trialEncodeArguments);

        var result = await _processRunner.RunProbeAsync(command, _probeTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.TimedOut && result.ExitCode == 0)
        {
            _logger.LogInformation("Hardware backend probe succeeded: {Arguments}", trialEncodeArguments);
            return HardwareProbeOutcome.Success;
        }

        var category = ClassifyFailure(result.StandardError);
        _logger.LogInformation(
            "Hardware backend probe failed: timedOut={TimedOut} exitCode={ExitCode} category={Category} arguments={Arguments}",
            result.TimedOut,
            result.ExitCode,
            category,
            trialEncodeArguments);
        return result.TimedOut ? HardwareProbeOutcome.Timeout() : HardwareProbeOutcome.Failure(category);
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
