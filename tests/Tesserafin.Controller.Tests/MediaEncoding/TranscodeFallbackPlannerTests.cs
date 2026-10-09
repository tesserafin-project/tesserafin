using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Locks <see cref="TranscodeFallbackPlanner"/>'s decision rules (tesserafin#119). The planner is
/// pure; what applies its decision is covered by <c>TranscodeManagerHardwareFallbackTests</c> and,
/// end to end, by the playback rig described in <c>docs/hardware-transcode-recovery.md</c>.
/// </summary>
public class TranscodeFallbackPlannerTests
{
    private static TranscodeFallbackRequest Request(
        FfmpegErrorCategory[] categories,
        HardwareAccelerationType backend = HardwareAccelerationType.vaapi,
        bool usedHardware = true,
        bool softwareAvailable = true,
        bool stopRequested = false,
        string? codec = null)
        => new(categories, backend, usedHardware, softwareAvailable, stopRequested, codec);

    [Theory]
    [InlineData(FfmpegErrorCategory.DeviceInitializationFailed)]
    [InlineData(FfmpegErrorCategory.HardwareDeviceLost)]
    public void DeviceFailure_OnHardware_GrantsSoftwareAndWithholdsTheBackend(FfmpegErrorCategory category)
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([category]));

        Assert.True(decision.ShouldFallback);
        Assert.Equal(HardwareAccelerationType.none, decision.FallbackHardwareAccelerationType);
        Assert.Equal(TranscodeFallbackScope.Backend, decision.Scope);
    }

    [Fact]
    public void MissingHardwareCodecOfTheBackendInUse_GrantsSoftwareForThatSourceOnly()
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.UnsupportedCodec], codec: "h264_vaapi"));

        Assert.True(decision.ShouldFallback);
        Assert.Equal(TranscodeFallbackScope.MediaSource, decision.Scope);
    }

    [Theory]
    [InlineData("libx264")] // a software codec this build lacks: software cannot fix that
    [InlineData("h264_qsv")] // another backend's codec, not the one in use
    [InlineData(null)] // "Encoder not found" names nothing
    public void MissingCodecThatIsNotTheBackendsOwn_IsRefused(string? codec)
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.UnsupportedCodec], codec: codec));

        Assert.False(decision.ShouldFallback);
        Assert.Equal(TranscodeFallbackScope.None, decision.Scope);
    }

    [Theory]
    [InlineData(FfmpegErrorCategory.InvalidInput)]
    [InlineData(FfmpegErrorCategory.PermissionDenied)]
    public void InputFailure_IsRefused_EvenBesideADeviceFailure(FfmpegErrorCategory inputCategory)
    {
        Assert.False(TranscodeFallbackPlanner.Evaluate(Request([inputCategory])).ShouldFallback);
        Assert.False(TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.HardwareDeviceLost, inputCategory])).ShouldFallback);
        Assert.False(TranscodeFallbackPlanner.Evaluate(Request([inputCategory, FfmpegErrorCategory.DeviceInitializationFailed])).ShouldFallback);
    }

    [Fact]
    public void NothingRecognised_IsRefused()
    {
        // A crash, a signal, a bare exit code: no line matched, so nothing is known.
        Assert.False(TranscodeFallbackPlanner.Evaluate(Request([])).ShouldFallback);
    }

    [Fact]
    public void StopRequested_IsRefused_WhateverTheEvidence()
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.HardwareDeviceLost], stopRequested: true));

        Assert.False(decision.ShouldFallback);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.none, true)] // software configured
    [InlineData(HardwareAccelerationType.vaapi, false)] // hardware configured, but the command that ran used none
    public void AttemptThatWasAlreadySoftware_IsRefused(HardwareAccelerationType backend, bool usedHardware)
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.DeviceInitializationFailed], backend, usedHardware));

        Assert.False(decision.ShouldFallback);
    }

    [Fact]
    public void NoSoftwareEncoderForTheOutput_IsRefused()
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.HardwareDeviceLost], softwareAvailable: false));

        Assert.False(decision.ShouldFallback);
    }

    [Fact]
    public void Reason_IsFixedText()
    {
        var decision = TranscodeFallbackPlanner.Evaluate(Request([FfmpegErrorCategory.HardwareDeviceLost]));

        Assert.Equal("the hardware device stopped accepting work", decision.Reason);
    }

    [Fact]
    public void CategoryOnlyOverload_GrantsDeviceFailures_AndRefusesWhatItCannotTell()
    {
        Assert.True(TranscodeFallbackPlanner.Evaluate(FfmpegErrorCategory.DeviceInitializationFailed, HardwareAccelerationType.vaapi).ShouldFallback);

        // Without the codec name it cannot tell a hardware codec from a software one.
        Assert.False(TranscodeFallbackPlanner.Evaluate(FfmpegErrorCategory.UnsupportedCodec, HardwareAccelerationType.vaapi).ShouldFallback);
        Assert.False(TranscodeFallbackPlanner.Evaluate(FfmpegErrorCategory.Unknown, HardwareAccelerationType.vaapi).ShouldFallback);
        Assert.False(TranscodeFallbackPlanner.Evaluate(FfmpegErrorCategory.InvalidInput, HardwareAccelerationType.vaapi).ShouldFallback);
        Assert.False(TranscodeFallbackPlanner.Evaluate(FfmpegErrorCategory.DeviceInitializationFailed, HardwareAccelerationType.none).ShouldFallback);
    }
}
