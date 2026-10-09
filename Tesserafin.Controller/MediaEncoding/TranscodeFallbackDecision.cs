using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The outcome of <see cref="TranscodeFallbackPlanner"/>: whether a failed transcode attempt
/// should be retried with a different hardware acceleration backend, and which one.
/// </summary>
/// <param name="ShouldFallback">Whether a retry is recommended for this failure.</param>
/// <param name="FallbackHardwareAccelerationType">The backend to retry with if <see cref="ShouldFallback"/> is <c>true</c>; meaningless otherwise.</param>
public sealed record TranscodeFallbackDecision(bool ShouldFallback, HardwareAccelerationType FallbackHardwareAccelerationType)
{
    /// <summary>
    /// Gets how far the fallback reaches. <see cref="TranscodeFallbackScope.None"/> when
    /// <see cref="ShouldFallback"/> is <see langword="false"/>.
    /// </summary>
    public TranscodeFallbackScope Scope { get; init; }

    /// <summary>
    /// Gets a short, fixed reason for the decision. Diagnostic text; it never contains a path, a
    /// command line or anything read from the failing process.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}
