namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// A coarse classification of why an ffmpeg invocation failed, derived from its stderr output.
/// It is the evidence <see cref="TranscodeFallbackPlanner"/> decides a software fallback on, so a
/// category must only ever be assigned from a line that positively identifies it.
/// </summary>
public enum FfmpegErrorCategory
{
    /// <summary>No recognized failure pattern was found (including a clean exit).</summary>
    Unknown,

    /// <summary>The input could not be opened (missing file, unreadable stream, bad protocol).</summary>
    InvalidInput,

    /// <summary>ffmpeg could not write to the destination path.</summary>
    PermissionDenied,

    /// <summary>The requested encoder/decoder is not present in this ffmpeg build.</summary>
    UnsupportedCodec,

    /// <summary>A hardware device (VAAPI/CUDA/QSV/...) failed to initialize.</summary>
    DeviceInitializationFailed,

    /// <summary>
    /// A hardware device that had been working stopped accepting work while the process was
    /// running: the driver reported a lost or rejected context.
    /// </summary>
    HardwareDeviceLost,
}
