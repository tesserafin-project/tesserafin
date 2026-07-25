using System.Collections.Generic;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Locks the startup gate <c>MediaEncoder.SetFFmpegPath</c> relies on (#90 / [A4]).
/// </summary>
/// <remarks>
/// This replaces the earlier <c>MediaEncoderAutoSelectionTests</c>, which locked the opposite rule:
/// that auto-selection ran only when the configured backend was <c>none</c>, and that an explicitly
/// chosen backend was therefore never re-examined. That gate is what made a persisted selection
/// unsafe to carry between hosts — it let a config directory from a GPU machine drive an unprobed
/// hardware command on a machine with no device. A4 reverses it deliberately: a configured backend
/// is a preference that is re-verified on every start, and the only way to suppress probing
/// entirely is <c>EnableHardwareEncoding=false</c>. These tests pin that reversal so it cannot be
/// silently undone.
/// </remarks>
public class MediaEncoderHardwareSelectionTests
{
    private static readonly FfmpegBuildCapabilities _capabilities = FfmpegBuildCapabilities.Empty;

    private static HardwareSelectionDecision Decide(EncodingOptions options, ICollection<HardwareAccelerationType> probed, bool probeSucceeds)
        => HardwareSelectionPlanner.Decide(
            [
                new(HardwareAccelerationType.nvenc, (o, c) => true, o => "nvenc-args"),
                new(HardwareAccelerationType.vaapi, (o, c) => true, o => "vaapi-args"),
            ],
            options,
            _capabilities,
            (candidate, args) =>
            {
                probed.Add(candidate.Type);
                return probeSucceeds ? HardwareProbeOutcome.Success : HardwareProbeOutcome.Failure(FfmpegErrorCategory.DeviceInitializationFailed);
            });

    [Fact]
    public void HardwareEncodingDisabled_SuppressesProbingEntirely()
    {
        var probed = new List<HardwareAccelerationType>();
        var options = new EncodingOptions { EnableHardwareEncoding = false, HardwareAccelerationType = HardwareAccelerationType.vaapi };

        var decision = Decide(options, probed, probeSucceeds: true);

        Assert.Empty(probed);
        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareSelectionReason.HardwareDisabled, decision.Reason);
    }

    [Fact]
    public void UnconfiguredWithHardwareEncodingEnabled_Probes()
    {
        var probed = new List<HardwareAccelerationType>();
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = HardwareAccelerationType.none };

        var decision = Decide(options, probed, probeSucceeds: true);

        Assert.NotEmpty(probed);
        Assert.Equal(HardwareSelectionMode.Hardware, decision.Mode);
        Assert.Equal(HardwareSelectionReason.AutoSelectedBackendVerified, decision.Reason);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.qsv)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.amf)]
    public void AnAlreadyChosenBackend_IsStillProbedOnThisStart(HardwareAccelerationType alreadyChosen)
    {
        var probed = new List<HardwareAccelerationType>();
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = alreadyChosen };

        Decide(options, probed, probeSucceeds: true);

        Assert.NotEmpty(probed);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.nvenc)]
    public void AnAlreadyChosenBackendThatNoLongerWorks_FallsBackToSoftware(HardwareAccelerationType alreadyChosen)
    {
        var probed = new List<HardwareAccelerationType>();
        var options = new EncodingOptions { EnableHardwareEncoding = true, HardwareAccelerationType = alreadyChosen };

        var decision = Decide(options, probed, probeSucceeds: false);

        Assert.Contains(alreadyChosen, probed);
        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.Equal(HardwareSelectionReason.AllProbesFailed, decision.Reason);
    }
}
