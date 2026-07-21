using System.Collections.Immutable;
using System.Linq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Sanity checks for <see cref="HardwareBackendCatalog"/>'s shape and the VAAPI candidate's
/// applicability/argument-building logic - the one candidate this environment can exercise for
/// real. See <see cref="HardwareBackendCatalog"/>'s remarks for which other candidates are
/// unverified-here by design.
/// </summary>
public class HardwareBackendCatalogTests
{
    [Fact]
    public void CandidatesInPriorityOrder_HasNoDuplicateBackendTypes()
    {
        var types = HardwareBackendCatalog.CandidatesInPriorityOrder.Select(c => c.Type).ToImmutableArray();

        Assert.Equal(types.Distinct().Count(), types.Length);
    }

    [Fact]
    public void CandidatesInPriorityOrder_CoversAllNonNoneBackendTypes()
    {
        var coveredTypes = HardwareBackendCatalog.CandidatesInPriorityOrder.Select(c => c.Type).ToImmutableArray();
        var allNonNoneTypes = System.Enum.GetValues<HardwareAccelerationType>().Where(t => t != HardwareAccelerationType.none);

        foreach (var type in allNonNoneTypes)
        {
            Assert.Contains(type, coveredTypes);
        }
    }

    [Fact]
    public void VaapiCandidate_ApplicableOnLinuxWithSupportedHwaccelAndExistingDevice()
    {
        if (!System.OperatingSystem.IsLinux())
        {
            return;
        }

        var candidate = HardwareBackendCatalog.CandidatesInPriorityOrder.Single(c => c.Type == HardwareAccelerationType.vaapi);
        var capabilities = FfmpegBuildCapabilities.Empty.WithHwaccels(["vaapi"]);
        var options = new EncodingOptions { VaapiDevice = "/dev/dri/renderD128" };

        // This asserts the *shape* of the applicability check, not real device presence -
        // File.Exists is part of the check, so this only proves true when run on a machine
        // that actually has this render node, same as the sandbox this was written against.
        if (System.IO.File.Exists(options.VaapiDevice))
        {
            Assert.True(candidate.IsApplicable(options, capabilities));
            Assert.Contains("h264_vaapi", candidate.BuildTrialEncodeArguments(options), System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VaapiCandidate_NotApplicableWhenBuildDoesNotSupportHwaccel()
    {
        var candidate = HardwareBackendCatalog.CandidatesInPriorityOrder.Single(c => c.Type == HardwareAccelerationType.vaapi);
        var options = new EncodingOptions { VaapiDevice = "/dev/dri/renderD128" };

        Assert.False(candidate.IsApplicable(options, FfmpegBuildCapabilities.Empty));
    }
}
