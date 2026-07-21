using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The priority-ordered list of hardware acceleration backends <see cref="HardwareBackendSelector"/>
/// considers for auto-selection.
/// </summary>
/// <remarks>
/// Priority order (dedicated encode silicon before generic/software-adjacent paths): NVENC, QSV,
/// AMF, VAAPI, VideoToolbox, RKMPP, V4L2M2M. This is a reasonable default, not a measured ranking - no
/// throughput/quality comparison was run, since only one of these backends has hardware available
/// to measure in this environment. It can only ever select a candidate ahead of a better one if
/// both pass a real trial encode, which bounds how wrong a bad ordering could be.
///
/// Verification status of each candidate's trial-encode arguments:
/// - VAAPI: real syntax, confirmed working against actual AMD hardware (see
///   <c>VaapiHardwareProbeTests</c>), including the 320x240 minimum-size fix found by running it.
/// - QSV (Linux): modeled on <c>EncodingHelper.GetQsvDeviceArgs</c>'s real "derive qsv from vaapi
///   device" pattern (iHD driver, i915 kernel driver, Intel vendor ID 0x8086) - same shape as
///   production code, not a guess, but not run against real Intel hardware here.
/// - NVENC, AMF, VideoToolbox, RKMPP, V4L2M2M: modeled on the corresponding real
///   <c>EncodingHelper.GetXxxDeviceArgs</c> init syntax where one exists (RKMPP, VideoToolbox), or
///   ffmpeg's documented device-less usage (NVENC's own internal CUDA init, AMF's and V4L2M2M's
///   device-less paths - the codebase has no dedicated device-args builder for either) where no
///   existing builder exists to model from. None of these has been run against real hardware. Per
///   <see cref="HardwareBackendSelector"/>'s invariant, a wrong guess here just means that backend
///   is never selected - it cannot select a broken one.
/// </remarks>
public static class HardwareBackendCatalog
{
    /// <summary>
    /// Gets the candidates in priority order, most preferred first.
    /// </summary>
    public static ImmutableArray<HardwareBackendCandidate> CandidatesInPriorityOrder { get; } =
    [
        new(
            HardwareAccelerationType.nvenc,
            (options, caps) => !OperatingSystem.IsMacOS() && caps.SupportsEncoder("h264_nvenc"),
            options => "-hide_banner -init_hw_device cuda=cu:0 -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v h264_nvenc -f null -"),
        new(
            HardwareAccelerationType.qsv,
            (options, caps) => OperatingSystem.IsLinux() && caps.SupportsEncoder("h264_qsv") && File.Exists(string.IsNullOrEmpty(options.QsvDevice) ? options.VaapiDevice : options.QsvDevice),
            options =>
            {
                var devicePath = string.IsNullOrEmpty(options.QsvDevice) ? options.VaapiDevice : options.QsvDevice;
                return string.IsNullOrEmpty(devicePath)
                    ? null
                    : $"-hide_banner -init_hw_device vaapi=va:{devicePath},driver=iHD,kernel_driver=i915 -init_hw_device qsv=qs@va -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -vf format=nv12,hwupload=derive_device=qsv -c:v h264_qsv -f null -";
            }),
        new(
            HardwareAccelerationType.amf,
            (options, caps) => OperatingSystem.IsWindows() && caps.SupportsEncoder("h264_amf"),
            options => "-hide_banner -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v h264_amf -f null -"),
        new(
            HardwareAccelerationType.vaapi,
            (options, caps) => OperatingSystem.IsLinux() && caps.SupportsHwaccel("vaapi") && !string.IsNullOrEmpty(options.VaapiDevice) && File.Exists(options.VaapiDevice),
            options => $"-hide_banner -init_hw_device vaapi=va:{options.VaapiDevice} -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -vf format=nv12,hwupload -c:v h264_vaapi -f null -"),
        new(
            HardwareAccelerationType.videotoolbox,
            (options, caps) => OperatingSystem.IsMacOS() && caps.SupportsEncoder("h264_videotoolbox"),
            options => "-hide_banner -init_hw_device videotoolbox=vt -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v h264_videotoolbox -f null -"),
        new(
            HardwareAccelerationType.rkmpp,
            (options, caps) => OperatingSystem.IsLinux()
                && (RuntimeInformation.ProcessArchitecture == Architecture.Arm || RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                && caps.SupportsEncoder("h264_rkmpp"),
            options => "-hide_banner -init_hw_device rkmpp=rk -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v h264_rkmpp -f null -"),
        new(
            HardwareAccelerationType.v4l2m2m,
            (options, caps) => OperatingSystem.IsLinux() && caps.SupportsEncoder("h264_v4l2m2m"),
            options => "-hide_banner -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v h264_v4l2m2m -f null -"),
    ];
}
