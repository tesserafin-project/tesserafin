namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// A purely informational round-to-round comparison for one phase. Reported, never asserted.
/// </summary>
/// <param name="RoundAP95Ms">Round A's hot p95 for the phase, in milliseconds.</param>
/// <param name="RoundBP95Ms">Round B's hot p95 for the phase, in milliseconds.</param>
/// <param name="FlooredRatio">Max/min of both p95 values after the floor is added to each.</param>
internal readonly record struct RoundStabilityMeasurement(double RoundAP95Ms, double RoundBP95Ms, double FlooredRatio);
