using System;
using System.Linq;
using System.Threading.Tasks;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
using Reefin.Playback.Decision;
using Reefin.Playback.Dlna;
using Reefin.Playback.Engine;
using Reefin.Playback.Execution;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// PR114a execution-parity gate: for each of the 9 mandatory oracle cases
/// (<see cref="OracleCaseFixtures.Cases"/>), builds a <see cref="PlaybackExecutionPlan"/> from the
/// real v2 decision and converts it to a legacy <see cref="StreamInfo"/> via
/// <see cref="PlaybackExecutionPlanAdapter"/>, then compares it against the REAL legacy
/// <c>StreamBuilder</c> output on the executable fields the execution layer actually needs:
/// <see cref="StreamInfo.PlayMethod"/>, <see cref="StreamInfo.Container"/>,
/// <see cref="StreamInfo.SubProtocol"/>, target video/audio codec, target video bitrate, target
/// resolution, audio/subtitle stream indexes, and subtitle delivery method. Cases whose
/// <see cref="ShadowDivergence.Class"/> is <see cref="DivergenceClass.Equivalent"/> are held to exact
/// equality on every one of those fields, EXCEPT target video bitrate when the video stream is
/// copied rather than transcoded (see <see cref="AssertParity"/>'s remarks - a real, documented v2
/// policy difference, not a gap in this gate). The two cases that are NOT Equivalent
/// (<see cref="OracleCaseFixtures.ApprovedDivergences"/>'s allow-listed Dolby Vision fallback, and the
/// ungated Firefox HEVC 10-bit tonemap case - see <see cref="OracleParityTests"/> remarks) additionally
/// skip the specific axis their divergence is already known and documented to affect, so this gate
/// cannot silently regress on a field it is not actually about.
/// </summary>
public sealed class ExecutionPlanParityTests
{
    [Fact]
    public async Task ExecutionPlan_ToStreamInfo_MatchesLegacyOnExecutableFields()
    {
        foreach (var (deviceProfile, source) in OracleCaseFixtures.Cases)
        {
            var options = await OracleCaseFixtures.GetMediaOptions(deviceProfile, source);

            // PR111e/PR114a: v2 inputs captured BEFORE legacy runs - legacy's StreamBuilder mutates
            // MediaSourceInfo.Container in place, so mapping v2's inputs afterward would silently feed
            // it legacy's already-degraded view of the source (see ShadowPlaybackSessionPlanner/
            // OracleParityTests for the same ordering and why it matters).
            var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
            var constraints = DlnaPlaybackAdapter.ToConstraints(options);
            var sourceSnapshots = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
            var context = DlnaPlaybackAdapter.ToContext(options.ItemId, Guid.Empty, options.MediaSourceId, MediaKind.Video, PlaybackEngine.EngineVersion);

            var legacyStreamInfo = OracleCaseFixtures.GetStreamBuilder().GetOptimalVideoStream(options);
            Assert.True(legacyStreamInfo is not null, $"({deviceProfile}, {source}): legacy must produce a stream for this gate's fixtures.");

            var decision = new PlaybackEngine().Decide(context, capabilities, sourceSnapshots, constraints);
            Assert.True(decision.IsViable, $"({deviceProfile}, {source}): v2 decision must be viable for this gate's fixtures.");

            var legacyVector = LegacyDecisionProjector.Project(new PlaybackPlan(legacyStreamInfo!.PlayMethod, legacyStreamInfo.TranscodeReasons, legacyStreamInfo));
            var v2Vector = V2DecisionProjector.Project(decision);
            var divergence = ShadowComparer.Compare(legacyVector, v2Vector);

            var plan = PlaybackExecutionPlanBuilder.Build(decision);
            var mediaSource = options.MediaSources.First(m => string.Equals(m.Id, decision.SelectedSource, StringComparison.Ordinal));
            var v2StreamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, options.Profile, options.ItemId);

            var caseLabel = $"({deviceProfile}, {source})";

            // Video bitrate is only a meaningful axis to compare when the video is actually being
            // transcoded: OutputSpec.VideoBitrate (and therefore PlaybackExecutionPlan.VideoBitrate) is
            // documented to be "populated only when transcoding" - null, by design, whenever the video
            // stream is copied. Legacy's own StreamInfo.VideoBitrate is not held to that same
            // discipline (StreamBuilder can populate a ceiling-echo value even on a copy), which
            // ShadowComparer's tri-state KnownAndDiffer already treats as "not a real divergence" for
            // classification (a known value on one side and null on the other is never "known and
            // different") - this gate applies the identical exception, explicitly, rather than
            // asserting an equality neither side's own design promises.
            var videoBitrateComparable = decision.Transforms.Contains(TransformKind.TranscodeVideo);

            if (divergence.Class == DivergenceClass.Equivalent)
            {
                AssertParity(caseLabel, legacyStreamInfo, v2StreamInfo, skipVideoBitrate: !videoBitrateComparable);
                continue;
            }

            if (deviceProfile == "Firefox" && source == "mp4-hevc-aac-srt-15200k")
            {
                // KnownV2Limitation (ungated, see OracleParityTests remarks): the only executable-field
                // divergence is the target video bitrate (legacy's precisely-scaled ceiling vs v2's
                // round output-profile ceiling, both legitimate, differently-computed caps) - every
                // other executable field must still match.
                AssertParity(caseLabel, legacyStreamInfo, v2StreamInfo, skipVideoBitrate: true);
                continue;
            }

            if (deviceProfile == "Chrome" && source == "mp4-dvhe.08-eac3-15200k")
            {
                // Allow-listed PotentialRegression (OracleCaseFixtures.ApprovedDivergences, root-caused
                // Dolby Vision fallback): v2 intentionally targets av1/HDR10 where legacy's HLG/hevc
                // target is a documented Enum.TryParse bitwise-OR artifact, not a real policy - skip
                // the video codec/bitrate axis this divergence is actually about, assert every other
                // executable field still matches.
                AssertParity(caseLabel, legacyStreamInfo, v2StreamInfo, skipVideoCodec: true, skipVideoBitrate: true);
                continue;
            }

            Assert.Fail($"{caseLabel} classified as {divergence.Class} ({divergence.Summary}), which this gate does not recognize. " +
                "Either it belongs in one of the two named exceptions above, or it is a real, new divergence to investigate.");
        }
    }

    private static void AssertParity(
        string caseLabel,
        StreamInfo legacy,
        StreamInfo v2,
        bool skipVideoCodec = false,
        bool skipVideoBitrate = false)
    {
        Assert.True(legacy.PlayMethod == v2.PlayMethod, $"{caseLabel}: PlayMethod legacy={legacy.PlayMethod} v2={v2.PlayMethod}");
        Assert.True(string.Equals(legacy.Container, v2.Container, StringComparison.OrdinalIgnoreCase), $"{caseLabel}: Container legacy={legacy.Container} v2={v2.Container}");
        Assert.True(legacy.SubProtocol == v2.SubProtocol, $"{caseLabel}: SubProtocol legacy={legacy.SubProtocol} v2={v2.SubProtocol}");

        if (!skipVideoCodec)
        {
            Assert.True(
                string.Equals(EffectiveVideoCodec(legacy), EffectiveVideoCodec(v2), StringComparison.OrdinalIgnoreCase),
                $"{caseLabel}: video codec legacy={EffectiveVideoCodec(legacy)} v2={EffectiveVideoCodec(v2)}");
        }

        Assert.True(
            string.Equals(EffectiveAudioCodec(legacy), EffectiveAudioCodec(v2), StringComparison.OrdinalIgnoreCase),
            $"{caseLabel}: audio codec legacy={EffectiveAudioCodec(legacy)} v2={EffectiveAudioCodec(v2)}");

        if (!skipVideoBitrate)
        {
            Assert.True(legacy.TargetVideoBitrate == v2.TargetVideoBitrate, $"{caseLabel}: TargetVideoBitrate legacy={legacy.TargetVideoBitrate} v2={v2.TargetVideoBitrate}");
        }

        Assert.True(legacy.TargetWidth == v2.TargetWidth, $"{caseLabel}: TargetWidth legacy={legacy.TargetWidth} v2={v2.TargetWidth}");
        Assert.True(legacy.TargetHeight == v2.TargetHeight, $"{caseLabel}: TargetHeight legacy={legacy.TargetHeight} v2={v2.TargetHeight}");
        Assert.True(legacy.AudioStreamIndex == v2.AudioStreamIndex, $"{caseLabel}: AudioStreamIndex legacy={legacy.AudioStreamIndex} v2={v2.AudioStreamIndex}");

        // Legacy uses -1 (not null) for "no subtitle selected" (see LegacyDecisionProjector's own
        // handling of the same quirk); normalized here so the comparison isn't a false mismatch
        // against v2's null.
        var legacySubtitleIndex = legacy.SubtitleStreamIndex is int idx && idx >= 0 ? (int?)idx : null;
        Assert.True(legacySubtitleIndex == v2.SubtitleStreamIndex, $"{caseLabel}: SubtitleStreamIndex legacy={legacySubtitleIndex} v2={v2.SubtitleStreamIndex}");

        if (legacySubtitleIndex is not null)
        {
            Assert.True(legacy.SubtitleDeliveryMethod == v2.SubtitleDeliveryMethod, $"{caseLabel}: SubtitleDeliveryMethod legacy={legacy.SubtitleDeliveryMethod} v2={v2.SubtitleDeliveryMethod}");
        }
    }

    /// <summary>
    /// The codec actually driving execution. <c>Target*Codec</c> (<see cref="StreamInfo.TargetVideoCodec"/>/
    /// <see cref="StreamInfo.TargetAudioCodec"/>) narrows to a single value whenever it can determine
    /// one: for Direct Play/Stream, always the source stream's own codec; for Transcode, the source
    /// codec too WHEN it happens to also be a declared candidate (the stream is effectively copied
    /// even though the overall <see cref="StreamInfo.PlayMethod"/> is Transcode because some other
    /// stream needs re-encoding - see the video axis of the "(Chrome, mkv-h264-ac3-srt-2600k)" case,
    /// where video is copied but audio is transcoded). Only when NEITHER holds - a real re-encode with
    /// no single obvious target - does the getter fall through to returning the WHOLE declared
    /// candidate list (<see cref="StreamInfo.TargetAudioCodec"/>'s own documented behavior), because
    /// legacy defers the actual single-codec pick to <c>StreamingHelpers.GetStreamingState</c>/
    /// <c>EncodingHelper</c>, machinery outside <c>StreamBuilder</c> and outside this PR's scope. In
    /// that fallback case, the declared candidate list's FIRST entry is legacy's own client-preference
    /// order (<c>TranscodingProfile.AudioCodec</c> CSV, mirrored by
    /// <see cref="DlnaPlaybackAdapter.ToCapabilities"/> into <c>PlaybackOutputProfile.AudioCodecs</c>
    /// in the same order) - the same value v2's engine picks
    /// (<c>PlaybackEngine.BuildForSource</c>'s <c>matchingOutputProfile.AudioCodecs[0]</c>), so it is
    /// the faithful "what would actually be produced" value to compare.
    /// </summary>
    private static string? EffectiveVideoCodec(StreamInfo info) =>
        info.TargetVideoCodec.Count == 1 ? info.TargetVideoCodec[0] : info.VideoCodecs.FirstOrDefault();

    /// <summary>See <see cref="EffectiveVideoCodec"/>; the audio counterpart.</summary>
    private static string? EffectiveAudioCodec(StreamInfo info) =>
        info.TargetAudioCodec.Count == 1 ? info.TargetAudioCodec[0] : info.AudioCodecs.FirstOrDefault();
}
