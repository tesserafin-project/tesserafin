using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Reefin.Controller.Tests.MediaEncoding;

/// <summary>
/// Locks <see cref="TranscodeFallbackPlanner.Evaluate"/>'s decision rules (transcoding-pipeline
/// plan PR9). Nothing here is wired into a live retry path - see the class remarks on
/// <see cref="TranscodeFallbackPlanner"/> for why that is out of scope in this environment.
/// </summary>
public class TranscodeFallbackPlannerTests
{
    [Theory]
    [InlineData(FfmpegErrorCategory.DeviceInitializationFailed)]
    [InlineData(FfmpegErrorCategory.UnsupportedCodec)]
    public void HardwareSpecificFailure_UsingHardware_RecommendsSoftwareFallback(FfmpegErrorCategory category)
    {
        var decision = TranscodeFallbackPlanner.Evaluate(category, HardwareAccelerationType.vaapi);

        Assert.True(decision.ShouldFallback);
        Assert.Equal(HardwareAccelerationType.none, decision.FallbackHardwareAccelerationType);
    }

    [Theory]
    [InlineData(FfmpegErrorCategory.InvalidInput)]
    [InlineData(FfmpegErrorCategory.PermissionDenied)]
    [InlineData(FfmpegErrorCategory.Unknown)]
    public void InputOrUnclassifiedFailure_UsingHardware_DoesNotRecommendFallback(FfmpegErrorCategory category)
    {
        var decision = TranscodeFallbackPlanner.Evaluate(category, HardwareAccelerationType.vaapi);

        Assert.False(decision.ShouldFallback);
    }

    [Theory]
    [InlineData(FfmpegErrorCategory.DeviceInitializationFailed)]
    [InlineData(FfmpegErrorCategory.UnsupportedCodec)]
    [InlineData(FfmpegErrorCategory.InvalidInput)]
    [InlineData(FfmpegErrorCategory.PermissionDenied)]
    [InlineData(FfmpegErrorCategory.Unknown)]
    public void AlreadySoftware_NeverRecommendsFallback(FfmpegErrorCategory category)
    {
        var decision = TranscodeFallbackPlanner.Evaluate(category, HardwareAccelerationType.none);

        Assert.False(decision.ShouldFallback);
    }
}
