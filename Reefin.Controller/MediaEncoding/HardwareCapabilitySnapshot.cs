using System.Collections.Immutable;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// The full picture of what this server's ffmpeg install can do: <see cref="Ffmpeg"/> for what the
/// build supports in general, plus <see cref="Devices"/> for what has been probed about specific
/// hardware. This is the typed replacement for <c>MediaEncoder</c>'s previously-separate
/// <c>_encoders</c>/<c>_decoders</c>/<c>_hwaccels</c>/<c>_filters</c> lists and
/// <c>_isVaapiDevice*</c>/<c>_isVaapiDeviceSupportVulkan*</c> flags - one snapshot object instead
/// of eleven loose fields, with <see cref="Devices"/> deliberately plural even though only one
/// entry is ever populated today.
/// </summary>
public sealed record HardwareCapabilitySnapshot
{
    /// <summary>
    /// A snapshot with no build capabilities and no probed devices. Used before ffmpeg has been
    /// probed at all.
    /// </summary>
    public static readonly HardwareCapabilitySnapshot Empty = new();

    /// <summary>
    /// Gets what the ffmpeg build supports in general (encoders/decoders/hwaccels/filters).
    /// </summary>
    public FfmpegBuildCapabilities Ffmpeg { get; init; } = FfmpegBuildCapabilities.Empty;

    /// <summary>
    /// Gets the capabilities probed for specific hardware devices. At most one entry today.
    /// </summary>
    public ImmutableArray<HardwareDeviceCapabilities> Devices { get; init; } = ImmutableArray<HardwareDeviceCapabilities>.Empty;

    /// <summary>
    /// Gets the probed vendor of the configured device, or <see cref="HardwareDeviceVendor.Unknown"/>
    /// if no device has been probed. There is at most one device today; this is the single-device
    /// convenience accessor pending real multi-device probing.
    /// </summary>
    public HardwareDeviceVendor PrimaryDeviceVendor => Devices.IsDefaultOrEmpty ? HardwareDeviceVendor.Unknown : Devices[0].Vendor;

    /// <summary>
    /// Gets a value indicating whether the configured device supports the Vulkan DRM format
    /// modifier extension. <c>false</c> if no device has been probed.
    /// </summary>
    public bool PrimaryDeviceSupportsVulkanDrmModifier => !Devices.IsDefaultOrEmpty && Devices[0].SupportsVulkanDrmModifier;

    /// <summary>
    /// Gets a value indicating whether the configured device supports Vulkan DRM interop via
    /// dma-buf. <c>false</c> if no device has been probed.
    /// </summary>
    public bool PrimaryDeviceSupportsVulkanDrmInterop => !Devices.IsDefaultOrEmpty && Devices[0].SupportsVulkanDrmInterop;

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="Ffmpeg"/> replaced.
    /// </summary>
    /// <param name="ffmpeg">The new ffmpeg build capabilities.</param>
    /// <returns>The updated snapshot.</returns>
    public HardwareCapabilitySnapshot WithFfmpeg(FfmpegBuildCapabilities ffmpeg) => this with { Ffmpeg = ffmpeg };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="Devices"/> replaced by a single entry.
    /// </summary>
    /// <param name="device">The probed device capabilities.</param>
    /// <returns>The updated snapshot.</returns>
    public HardwareCapabilitySnapshot WithSingleDevice(HardwareDeviceCapabilities device) => this with { Devices = [device] };
}
