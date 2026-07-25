namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The effective transcoding mode chosen at startup: whether the server will assemble hardware or
/// software ffmpeg commands for this run.
/// </summary>
public enum HardwareSelectionMode
{
    /// <summary>
    /// Software encoding (for example <c>libx264</c>). This is the safe default and the outcome
    /// whenever no hardware backend was verified on this start.
    /// </summary>
    Software,

    /// <summary>
    /// A hardware backend passed a real trial encode on this start and will be used.
    /// </summary>
    Hardware,
}
