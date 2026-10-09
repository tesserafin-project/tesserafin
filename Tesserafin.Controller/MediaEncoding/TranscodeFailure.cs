using System;
using System.Collections.Generic;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// What is known about a transcode that ended on its own with a failure. Kept on the
/// <see cref="TranscodingJob"/> so that a request for output the job never produced can be
/// answered with the truth instead of a wait.
/// </summary>
/// <param name="ExitCode">The process exit code. Recorded for diagnostics; never the basis of a decision.</param>
/// <param name="Categories">Every category found in the attempt's stderr.</param>
/// <param name="Decision">Whether a software fallback was granted, and how far it reaches.</param>
/// <param name="OccurredAt">When the process was seen to exit.</param>
public sealed record TranscodeFailure(
    int ExitCode,
    IReadOnlyCollection<FfmpegErrorCategory> Categories,
    TranscodeFallbackDecision Decision,
    DateTime OccurredAt);
