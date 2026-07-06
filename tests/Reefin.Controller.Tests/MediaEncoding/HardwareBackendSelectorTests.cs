using System.Collections.Generic;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Configuration;
using Reefin.Model.Entities;
using Xunit;

namespace Reefin.Controller.Tests.MediaEncoding;

/// <summary>
/// Locks <see cref="HardwareBackendSelector.SelectFirstVerified"/>'s selection logic in isolation
/// from any real ffmpeg process, using synthetic candidates and a stubbed probe delegate
/// (transcoding-pipeline plan PR10). Nothing here spawns a process - see
/// <see cref="Reefin.MediaEncoding.Tests.Encoder.HardwareBackendProbeTests"/> for the real-hardware
/// verification of the actual probe mechanism.
/// </summary>
public class HardwareBackendSelectorTests
{
    private static readonly EncodingOptions _options = new();
    private static readonly FfmpegBuildCapabilities _capabilities = FfmpegBuildCapabilities.Empty;

    [Fact]
    public void FirstApplicableCandidateThatProbesTrue_IsSelected()
    {
        var candidates = new List<HardwareBackendCandidate>
        {
            new(HardwareAccelerationType.nvenc, (o, c) => true, o => "nvenc-args"),
            new(HardwareAccelerationType.vaapi, (o, c) => true, o => "vaapi-args"),
        };

        var result = HardwareBackendSelector.SelectFirstVerified(candidates, _options, _capabilities, (candidate, args) => candidate.Type == HardwareAccelerationType.vaapi);

        Assert.Equal(HardwareAccelerationType.vaapi, result);
    }

    [Fact]
    public void EarlierPriorityCandidateWins_WhenBothWouldProbeTrue()
    {
        var candidates = new List<HardwareBackendCandidate>
        {
            new(HardwareAccelerationType.nvenc, (o, c) => true, o => "nvenc-args"),
            new(HardwareAccelerationType.vaapi, (o, c) => true, o => "vaapi-args"),
        };

        var result = HardwareBackendSelector.SelectFirstVerified(candidates, _options, _capabilities, (candidate, args) => true);

        Assert.Equal(HardwareAccelerationType.nvenc, result);
    }

    [Fact]
    public void NotApplicableCandidate_IsNeverProbed()
    {
        var probedTypes = new List<HardwareAccelerationType>();
        var candidates = new List<HardwareBackendCandidate>
        {
            new(HardwareAccelerationType.nvenc, (o, c) => false, o => "nvenc-args"),
            new(HardwareAccelerationType.vaapi, (o, c) => true, o => "vaapi-args"),
        };

        var result = HardwareBackendSelector.SelectFirstVerified(
            candidates,
            _options,
            _capabilities,
            (candidate, args) =>
            {
                probedTypes.Add(candidate.Type);
                return true;
            });

        Assert.Equal(HardwareAccelerationType.vaapi, result);
        Assert.DoesNotContain(HardwareAccelerationType.nvenc, probedTypes);
    }

    [Fact]
    public void ApplicableButUnbuildableCandidate_IsSkippedWithoutProbing()
    {
        var probeWasCalled = false;
        var candidates = new List<HardwareBackendCandidate>
        {
            new(HardwareAccelerationType.qsv, (o, c) => true, o => null),
        };

        var result = HardwareBackendSelector.SelectFirstVerified(
            candidates,
            _options,
            _capabilities,
            (candidate, args) =>
            {
                probeWasCalled = true;
                return true;
            });

        Assert.Null(result);
        Assert.False(probeWasCalled);
    }

    [Fact]
    public void NoCandidateProbesTrue_ReturnsNull()
    {
        var candidates = new List<HardwareBackendCandidate>
        {
            new(HardwareAccelerationType.nvenc, (o, c) => true, o => "nvenc-args"),
            new(HardwareAccelerationType.vaapi, (o, c) => true, o => "vaapi-args"),
        };

        var result = HardwareBackendSelector.SelectFirstVerified(candidates, _options, _capabilities, (candidate, args) => false);

        Assert.Null(result);
    }

    [Fact]
    public void EmptyCandidateList_ReturnsNull()
    {
        var result = HardwareBackendSelector.SelectFirstVerified([], _options, _capabilities, (candidate, args) => true);

        Assert.Null(result);
    }
}
