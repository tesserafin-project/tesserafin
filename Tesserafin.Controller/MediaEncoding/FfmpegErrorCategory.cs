namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// A coarse classification of why an ffmpeg invocation failed, derived from its stderr output.
/// Diagnostic only for now - nothing branches on this yet. Intended to become the signal a
/// future hardware-encoding fallback (trying the next backend after a hardware failure) decides
/// on, once that exists.
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
}
