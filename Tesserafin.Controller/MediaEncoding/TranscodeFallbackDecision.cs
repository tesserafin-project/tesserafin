using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The outcome of <see cref="TranscodeFallbackPlanner.Evaluate"/>: whether a failed transcode
/// attempt should be retried with a different hardware acceleration backend, and which one.
/// </summary>
/// <param name="ShouldFallback">Whether a retry is recommended for this failure.</param>
/// <param name="FallbackHardwareAccelerationType">The backend to retry with if <see cref="ShouldFallback"/> is <c>true</c>; meaningless otherwise.</param>
public sealed record TranscodeFallbackDecision(bool ShouldFallback, HardwareAccelerationType FallbackHardwareAccelerationType);
