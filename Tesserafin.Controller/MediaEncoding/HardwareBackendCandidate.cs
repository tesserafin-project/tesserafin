using System;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// One hardware acceleration backend <see cref="HardwareBackendSelector"/> can consider for
/// auto-selection: whether it is even worth probing (<see cref="IsApplicable"/>, cheap - platform
/// check plus build capability plus configured device), and if so, the real ffmpeg trial-encode
/// command to probe it with (<see cref="BuildTrialEncodeArguments"/>, expensive - spawns ffmpeg).
/// </summary>
/// <param name="Type">The backend this candidate represents.</param>
/// <param name="IsApplicable">
/// Cheap precondition checked before ever running a probe: is this backend's ffmpeg support
/// compiled in, and does the OS/configured device make attempting it plausible at all. Takes the
/// ffmpeg build capabilities (<see cref="MediaEncoding.FfmpegBuildCapabilities"/>) rather than
/// requiring a live <c>MediaEncoder</c>, so it stays pure and testable with synthetic data.
/// </param>
/// <param name="BuildTrialEncodeArguments">
/// Builds the ffmpeg argument line for a real trial encode against this backend, or <c>null</c> if
/// the candidate turns out not to be buildable for the given options (for example, no usable
/// device path). A wrong or overly optimistic argument string here is safe by construction: it can
/// only make the trial encode fail, which means the backend is never selected - never that a
/// broken backend gets selected. See <see cref="HardwareBackendCatalog"/> for which of these are
/// real ffmpeg syntax the codebase already builds elsewhere versus unverified-in-this-environment
/// best effort.
/// </param>
public sealed record HardwareBackendCandidate(
    HardwareAccelerationType Type,
    Func<EncodingOptions, FfmpegBuildCapabilities, bool> IsApplicable,
    Func<EncodingOptions, string?> BuildTrialEncodeArguments);
