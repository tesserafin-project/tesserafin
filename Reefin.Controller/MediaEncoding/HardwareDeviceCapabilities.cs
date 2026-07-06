namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// Capabilities specific to one hardware device path, as opposed to <see cref="FfmpegBuildCapabilities"/>
/// which describes what the ffmpeg build supports in general. Today <c>MediaEncoder</c> only ever
/// probes the single VAAPI device configured in <c>EncodingOptions.VaapiDevice</c>, so a
/// <see cref="HardwareCapabilitySnapshot"/> holds at most one of these - this type exists so that
/// "one device" is a real object rather than five separate flat bools, and so a future
/// multi-device probe has somewhere to put additional entries without another rewrite.
/// </summary>
/// <param name="DevicePath">The render node or device path this capability set was probed against, for example <c>/dev/dri/renderD128</c>.</param>
/// <param name="Vendor">The GPU driver identified for this device, or <see cref="HardwareDeviceVendor.Unknown"/> if not determined.</param>
/// <param name="SupportsVulkanDrmModifier">Whether this device supports the Vulkan DRM format modifier extension.</param>
/// <param name="SupportsVulkanDrmInterop">Whether this device supports Vulkan DRM interop via dma-buf.</param>
public sealed record HardwareDeviceCapabilities(
    string DevicePath,
    HardwareDeviceVendor Vendor,
    bool SupportsVulkanDrmModifier,
    bool SupportsVulkanDrmInterop);
