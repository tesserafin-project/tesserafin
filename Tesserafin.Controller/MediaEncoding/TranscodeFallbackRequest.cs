using System.Collections.Generic;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Everything <see cref="TranscodeFallbackPlanner"/> is allowed to decide on: what the failed
/// attempt actually ran, and what its stderr positively showed.
/// </summary>
/// <param name="Categories">Every distinct category found in the attempt's stderr. Empty means nothing was recognised.</param>
/// <param name="Backend">The hardware backend the attempt's command was built for.</param>
/// <param name="UsedHardwarePipeline">Whether the command that ran actually used a hardware device.</param>
/// <param name="SoftwareAlternativeAvailable">Whether this ffmpeg has the software encoder for the same output codec.</param>
/// <param name="StopRequested">Whether the server itself stopped the attempt (user stop, seek, navigation, cancellation).</param>
/// <param name="UnsupportedCodecName">The codec an "unknown encoder/decoder" line named, when one did.</param>
public sealed record TranscodeFallbackRequest(
    IReadOnlyCollection<FfmpegErrorCategory> Categories,
    HardwareAccelerationType Backend,
    bool UsedHardwarePipeline,
    bool SoftwareAlternativeAvailable,
    bool StopRequested,
    string? UnsupportedCodecName);
