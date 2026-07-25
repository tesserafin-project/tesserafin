using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Locks the startup hardware-selection contract (#90 / [A4]) against synthetic probe outcomes.
/// Nothing here spawns ffmpeg: the decision table is what is under test, and keeping it pure is
/// what makes every branch — including the ones that need absent hardware — reachable. The real
/// trial-encode mechanism is covered separately by the probe tests, and the end-to-end proof that a
/// container actually transcodes in software and in VAAPI lives in <c>docker/hwa-smoke.sh</c> and
/// <c>docker/hwa-vaapi.sh</c>.
/// </summary>
public class HardwareSelectionPlannerTests
{
    private static readonly FfmpegBuildCapabilities _capabilities = FfmpegBuildCapabilities.Empty;

    private static HardwareBackendCandidate Candidate(HardwareAccelerationType type, bool applicable = true)
        => new(type, (o, c) => applicable, o => $"{type}-args");

    private static EncodingOptions Options(bool enabled = true, HardwareAccelerationType configured = HardwareAccelerationType.none)
        => new() { EnableHardwareEncoding = enabled, HardwareAccelerationType = configured };

    private static Func<HardwareBackendCandidate, string, HardwareProbeOutcome> ProbeSucceedsFor(params HardwareAccelerationType[] verified)
        => (candidate, args) => verified.Contains(candidate.Type)
            ? HardwareProbeOutcome.Success
            : HardwareProbeOutcome.Failure(FfmpegErrorCategory.DeviceInitializationFailed);

    [Fact]
    public void HardwareDisabled_SelectsSoftwareAndRunsNoProbe()
    {
        var probed = new List<HardwareAccelerationType>();

        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi)],
            Options(enabled: false, configured: HardwareAccelerationType.vaapi),
            _capabilities,
            (candidate, args) =>
            {
                probed.Add(candidate.Type);
                return HardwareProbeOutcome.Success;
            });

        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.Equal(HardwareSelectionReason.HardwareDisabled, decision.Reason);
        Assert.Empty(probed);
        Assert.Empty(decision.CandidatesProbed);
        // The operator's preference survives being switched off, so turning hardware encoding back
        // on reconsiders it first rather than starting from scratch.
        Assert.Equal(HardwareAccelerationType.vaapi, decision.ConfiguredBackend);
    }

    [Fact]
    public void NoApplicableCandidates_SelectsSoftwareWithNoApplicableBackend()
    {
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi, applicable: false), Candidate(HardwareAccelerationType.qsv, applicable: false)],
            Options(),
            _capabilities,
            ProbeSucceedsFor(HardwareAccelerationType.vaapi));

        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.Equal(HardwareSelectionReason.NoApplicableBackend, decision.Reason);
        Assert.Empty(decision.CandidatesConsidered);
        Assert.Empty(decision.CandidatesProbed);
    }

    [Fact]
    public void EveryApplicableProbeFails_SelectsSoftwareWithAllProbesFailed()
    {
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.nvenc), Candidate(HardwareAccelerationType.vaapi)],
            Options(),
            _capabilities,
            ProbeSucceedsFor());

        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.Equal(HardwareSelectionReason.AllProbesFailed, decision.Reason);
        Assert.Equal([HardwareAccelerationType.nvenc, HardwareAccelerationType.vaapi], decision.CandidatesProbed);
        Assert.Equal([FfmpegErrorCategory.DeviceInitializationFailed], decision.ProbeFailureCategories);
    }

    [Fact]
    public void PreferredBackendVerified_IsSelectedAheadOfHigherPriorityCandidates()
    {
        // vaapi sits after nvenc in the catalog order, but it is the configured preference and it
        // verifies, so it must win — and be reported as the preference having been verified.
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.nvenc), Candidate(HardwareAccelerationType.vaapi)],
            Options(configured: HardwareAccelerationType.vaapi),
            _capabilities,
            ProbeSucceedsFor(HardwareAccelerationType.nvenc, HardwareAccelerationType.vaapi));

        Assert.Equal(HardwareSelectionMode.Hardware, decision.Mode);
        Assert.Equal(HardwareAccelerationType.vaapi, decision.Backend);
        Assert.Equal(HardwareSelectionReason.PreferredBackendVerified, decision.Reason);
        Assert.Equal([HardwareAccelerationType.vaapi], decision.CandidatesProbed);
    }

    [Fact]
    public void PreferredBackendFails_LaterCandidateThatVerifiesIsSelected()
    {
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.nvenc), Candidate(HardwareAccelerationType.vaapi)],
            Options(configured: HardwareAccelerationType.vaapi),
            _capabilities,
            ProbeSucceedsFor(HardwareAccelerationType.nvenc));

        Assert.Equal(HardwareSelectionMode.Hardware, decision.Mode);
        Assert.Equal(HardwareAccelerationType.nvenc, decision.Backend);
        Assert.Equal(HardwareSelectionReason.AutoSelectedBackendVerified, decision.Reason);
        // The preference was tried first and rejected, which the audit trail must show.
        Assert.Equal([HardwareAccelerationType.vaapi, HardwareAccelerationType.nvenc], decision.CandidatesProbed);
    }

    [Fact]
    public void MultipleCandidatesVerify_FirstInPriorityOrderWins()
    {
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.nvenc), Candidate(HardwareAccelerationType.qsv), Candidate(HardwareAccelerationType.vaapi)],
            Options(),
            _capabilities,
            ProbeSucceedsFor(HardwareAccelerationType.nvenc, HardwareAccelerationType.qsv, HardwareAccelerationType.vaapi));

        Assert.Equal(HardwareAccelerationType.nvenc, decision.Backend);
        Assert.Equal(HardwareSelectionReason.AutoSelectedBackendVerified, decision.Reason);
        // Nothing after the winner is probed: probing costs a real encode per candidate.
        Assert.Equal([HardwareAccelerationType.nvenc], decision.CandidatesProbed);
    }

    [Fact]
    public void PersistedVaapiPreference_OnAHostWithoutARenderNode_SelectsSoftware()
    {
        // The migration case that makes re-probing load-bearing: a config directory carried from a
        // GPU host still names vaapi, but this host has no render node, so the real catalog's
        // applicability check rejects it and nothing hardware-backed can be selected.
        var options = Options(configured: HardwareAccelerationType.vaapi);
        options.VaapiDevice = Path.Combine(Path.GetTempPath(), $"tesserafin-absent-render-node-{Guid.NewGuid():N}");
        Assert.False(File.Exists(options.VaapiDevice));

        var decision = HardwareSelectionPlanner.Decide(
            HardwareBackendCatalog.CandidatesInPriorityOrder,
            options,
            _capabilities,
            (candidate, args) => HardwareProbeOutcome.Success);

        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.DoesNotContain(HardwareAccelerationType.vaapi, decision.CandidatesProbed);
        Assert.Equal(HardwareAccelerationType.vaapi, decision.ConfiguredBackend);
    }

    [Fact]
    public void APreviouslyAutoSelectedBackend_IsProbedAgainOnTheNextStart()
    {
        // Start 1: nothing configured, vaapi verifies and becomes effective.
        var first = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi)],
            Options(),
            _capabilities,
            ProbeSucceedsFor(HardwareAccelerationType.vaapi));

        Assert.Equal(HardwareAccelerationType.vaapi, first.Backend);

        // Start 2: that selection is now the persisted configuration. It must still be probed —
        // and when the device has gone, it must not survive into the effective decision.
        var probed = new List<HardwareAccelerationType>();
        var second = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi)],
            Options(configured: first.Backend),
            _capabilities,
            (candidate, args) =>
            {
                probed.Add(candidate.Type);
                return HardwareProbeOutcome.Failure(FfmpegErrorCategory.DeviceInitializationFailed);
            });

        Assert.Equal([HardwareAccelerationType.vaapi], probed);
        Assert.Equal(HardwareSelectionMode.Software, second.Mode);
        Assert.Equal(HardwareAccelerationType.none, second.Backend);
        Assert.Equal(HardwareSelectionReason.AllProbesFailed, second.Reason);
    }

    [Fact]
    public void ProbeTimeout_IsAFailureAndSelectsSoftware()
    {
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi)],
            Options(),
            _capabilities,
            (candidate, args) => HardwareProbeOutcome.Timeout());

        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.Equal(HardwareSelectionReason.AllProbesFailed, decision.Reason);
        Assert.True(decision.ProbeAttempts.Single().Outcome.TimedOut);
    }

    [Fact]
    public void ProbeThatThrows_IsContainedAndSelectsSoftware()
    {
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi)],
            Options(),
            _capabilities,
            (candidate, args) => throw new InvalidOperationException("probe blew up"));

        Assert.Equal(HardwareSelectionMode.Software, decision.Mode);
        Assert.Equal(HardwareAccelerationType.none, decision.Backend);
        Assert.Equal(HardwareSelectionReason.AllProbesFailed, decision.Reason);
        Assert.False(decision.ProbeAttempts.Single().Outcome.Succeeded);
    }

    [Theory]
    [InlineData(false, HardwareAccelerationType.vaapi)]
    [InlineData(true, HardwareAccelerationType.none)]
    [InlineData(true, HardwareAccelerationType.vaapi)]
    public void ModeAndBackendAlwaysAgree(bool enabled, HardwareAccelerationType configured)
    {
        // The structured log renders Mode and Backend as separate fields; a decision where they
        // disagreed would make the log lie about what ffmpeg is going to be told to do.
        foreach (var verifies in new[] { true, false })
        {
            var decision = HardwareSelectionPlanner.Decide(
                [Candidate(HardwareAccelerationType.vaapi)],
                Options(enabled, configured),
                _capabilities,
                (candidate, args) => verifies ? HardwareProbeOutcome.Success : HardwareProbeOutcome.Failure(FfmpegErrorCategory.Unknown));

            if (decision.Mode == HardwareSelectionMode.Software)
            {
                Assert.Equal(HardwareAccelerationType.none, decision.Backend);
            }
            else
            {
                Assert.NotEqual(HardwareAccelerationType.none, decision.Backend);
                Assert.Contains(decision.Backend, decision.CandidatesProbed);
                Assert.True(decision.ProbeAttempts.Single(a => a.Backend == decision.Backend).Outcome.Succeeded);
            }
        }
    }

    [Theory]
    [InlineData(true, "hardware")]
    [InlineData(false, "software")]
    public void ModeNameIsTheLowerCaseTokenTheStartupLogEmits(bool verifies, string expected)
    {
        // The container acceptance gates match Mode=hardware / Mode=software literally, and the
        // operator documentation tells people to grep for exactly that. Enum ToString() would
        // render "Hardware"/"Software" and quietly break both.
        var decision = HardwareSelectionPlanner.Decide(
            [Candidate(HardwareAccelerationType.vaapi)],
            Options(),
            _capabilities,
            (candidate, args) => verifies ? HardwareProbeOutcome.Success : HardwareProbeOutcome.Failure(FfmpegErrorCategory.Unknown));

        Assert.Equal(expected, decision.ModeName);
    }

    [Fact]
    public void ASelectedBackendAlwaysHasASuccessfulProbeOnThisStart()
    {
        // The core safety invariant: no path exists that returns a hardware mode without a
        // current-start verification behind it.
        foreach (var configured in Enum.GetValues<HardwareAccelerationType>())
        {
            var decision = HardwareSelectionPlanner.Decide(
                HardwareBackendCatalog.CandidatesInPriorityOrder,
                Options(configured: configured),
                _capabilities,
                (candidate, args) => HardwareProbeOutcome.Success);

            if (decision.Mode == HardwareSelectionMode.Hardware)
            {
                Assert.True(decision.ProbeAttempts.Single(a => a.Backend == decision.Backend).Outcome.Succeeded);
            }
            else
            {
                Assert.Equal(HardwareAccelerationType.none, decision.Backend);
            }
        }
    }
}
