using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Locks <see cref="MediaEncoder.DetermineAutoSelectedHardwareAccelerationType"/>: the gate that
/// decides whether a successful VAAPI startup probe (transcoding-pipeline plan PR8) is allowed to
/// change the effective hardware acceleration backend. Deliberately pure/testable in isolation
/// from the real probe (<see cref="VaapiHardwareProbeTests"/>) and from <c>SetFFmpegPath</c>'s
/// wider I/O, since this gate is the part that must never be wrong: it must never override an
/// explicit user choice.
/// </summary>
public class MediaEncoderAutoSelectionTests
{
    [Fact]
    public void UnconfiguredWithHardwareEncodingEnabled_ProbeSucceeded_AutoSelectsVaapi()
    {
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = HardwareAccelerationType.none };

        var result = MediaEncoder.DetermineAutoSelectedHardwareAccelerationType(options, vaapiDeviceVerified: true);

        Assert.Equal(HardwareAccelerationType.vaapi, result);
    }

    [Fact]
    public void UnconfiguredWithHardwareEncodingEnabled_ProbeFailed_DoesNotAutoSelect()
    {
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = HardwareAccelerationType.none };

        var result = MediaEncoder.DetermineAutoSelectedHardwareAccelerationType(options, vaapiDeviceVerified: false);

        Assert.Null(result);
    }

    [Fact]
    public void HardwareEncodingDisabled_ProbeSucceeded_DoesNotAutoSelect()
    {
        var options = new EncodingOptions { EnableHardwareEncoding = false, HardwareAccelerationType = HardwareAccelerationType.none };

        var result = MediaEncoder.DetermineAutoSelectedHardwareAccelerationType(options, vaapiDeviceVerified: true);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.qsv)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.amf)]
    public void BackendAlreadyExplicitlyChosen_ProbeSucceeded_DoesNotOverride(HardwareAccelerationType alreadyChosen)
    {
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = alreadyChosen };

        var result = MediaEncoder.DetermineAutoSelectedHardwareAccelerationType(options, vaapiDeviceVerified: true);

        Assert.Null(result);
    }
}
