namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// The GPU driver identified on a probed hardware device, as determined by
/// <c>EncoderValidator.CheckVaapiDeviceByDriverName</c>. Today only VAAPI devices are identified
/// this way; QSV/NVENC/AMF/videotoolbox/rkmpp devices are not probed for vendor, so they always
/// surface as <see cref="Unknown"/> here.
/// </summary>
public enum HardwareDeviceVendor
{
    /// <summary>
    /// No vendor-specific driver was identified for this device.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// AMD GPU via the Mesa Gallium driver.
    /// </summary>
    Amd,

    /// <summary>
    /// Intel GPU via the modern iHD driver.
    /// </summary>
    IntelIHD,

    /// <summary>
    /// Intel GPU via the legacy i965 driver.
    /// </summary>
    IntelI965,
}
