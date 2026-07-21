using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Locks <see cref="MediaEncoder.ShouldAutoSelectHardwareAcceleration"/>: the gate that decides
/// whether auto-selection (transcoding-pipeline plan PR8/PR10) is allowed to run at all. This gate
/// is the part that must never be wrong: it must never let auto-selection override an explicit
/// user choice. Which backend gets selected once the gate passes is
/// <see cref="HardwareBackendSelectorTests"/>'s concern, not this one.
/// </summary>
public class MediaEncoderAutoSelectionTests
{
    [Fact]
    public void UnconfiguredWithHardwareEncodingEnabled_ShouldAutoSelect()
    {
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = HardwareAccelerationType.none };

        Assert.True(MediaEncoder.ShouldAutoSelectHardwareAcceleration(options));
    }

    [Fact]
    public void HardwareEncodingDisabled_DoesNotAutoSelect()
    {
        var options = new EncodingOptions { EnableHardwareEncoding = false, HardwareAccelerationType = HardwareAccelerationType.none };

        Assert.False(MediaEncoder.ShouldAutoSelectHardwareAcceleration(options));
    }

    [Theory]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.qsv)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.amf)]
    public void BackendAlreadyExplicitlyChosen_DoesNotAutoSelect(HardwareAccelerationType alreadyChosen)
    {
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = alreadyChosen };

        Assert.False(MediaEncoder.ShouldAutoSelectHardwareAcceleration(options));
    }
}
