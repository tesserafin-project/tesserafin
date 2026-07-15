using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.MediaEncoding;
using Reefin.Extensions.Json;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// PR111d: the shared oracle fixture data (device profile / media source pairs, the
/// <see cref="ApprovedDivergences"/> allow-list, and the case-loading helpers) originally inlined in
/// <see cref="OracleParityTests"/>, extracted so a second test - the PR111d hot-path performance gate
/// (<c>ShadowPerformanceGateTests</c>) - can reuse the exact same cases and the exact same allow-list
/// instead of forking a second copy that could silently drift from the classification gate. There is
/// ONLY one <see cref="ApprovedDivergences"/> in the codebase; both tests reference this one.
/// </summary>
internal static class OracleCaseFixtures
{
    /// <summary>
    /// Every (DeviceProfile, Source) pair whose divergence is *expected* to classify as
    /// <see cref="DivergenceClass.PotentialRegression"/> or <see cref="DivergenceClass.Unexplained"/>,
    /// together with the reason that makes the divergence acceptable rather than a real regression.
    /// PR104: closing this allow-list (as opposed to loosening the test's assertion) is deliberate -
    /// any NEW case that lands here without being added to this dictionary fails the test, forcing a
    /// conscious decision instead of a silently-passing gate. PR111d: moved verbatim from
    /// <see cref="OracleParityTests"/> - contents unchanged, only its home moved.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string DeviceProfile, string Source), string> ApprovedDivergences =
        new Dictionary<(string, string), string>
        {
            [("Chrome", "mp4-h264-ac3-aac-srt-2600k")] =
                "Subtitle text-format conversion (srt->vtt) is now MODELED (PR111c), so the historical " +
                "subtitle gap on this case is CLOSED - both engines convert and copy video. The residual " +
                "divergence (v2-only Remux transform + Container reason) is a SEPARATE, pre-existing bug " +
                "this PR merely exposed once video stopped being force-transcoded by burn-in: legacy's " +
                "StreamBuilder MUTATES the shared MediaSourceInfo.Container into a single value " +
                "(NormalizeMediaSourceFormatIntoSingleContainer, StreamBuilder.cs:836) and the shadow " +
                "flow runs legacy BEFORE snapshotting for v2 (ShadowPlaybackSessionPlanner.RunShadow, " +
                "and the oracle here), so v2 sees the degraded container 'mov' instead of this source's " +
                "real ffprobe set 'mov,mp4,m4a,3gp,3g2,mj2'; v2 then whole-string-compares it against " +
                "target 'mp4' and falsely flags a container remux. Legacy emits no container reason " +
                "(mp4 is a member of the real set). This masks whether v2 handles raw ffprobe container " +
                "strings at all - a CANARY-READINESS question (when v2 is the sole planner no legacy " +
                "normalizes first). FIX tracked as its own PR: (a) v2 containerChanged uses " +
                "ContainerHelper.ContainsContainer; (b) the shadow harness snapshots the source BEFORE " +
                "legacy mutates it - expected to move more than this one case, hence out of PR111c scope.",
            [("Chrome", "mp4-dvhe.08-eac3-15200k")] =
                "Dolby Vision (Profile 8.1) fallback on a non-DV client: legacy and v2 AGREE the DV " +
                "range must be transcoded away on Chrome; they only differ on the transcode target. " +
                "The gating axis is videoRange (legacy=HLG, v2=SDR). Legacy's 'HLG' is not a " +
                "deliberate HDR-preservation policy - it is an artifact of Enum.TryParse bitwise-ORing " +
                "the multi-value 'SDR|HDR10|HLG' EqualsAny VideoRangeType condition (ordinals " +
                "1|2|3 = 3, which coincidentally equals HLG's ordinal; VideoRangeType is not [Flags]). " +
                "v2 deliberately tonemaps any unsupported HDR source down to plain SDR " +
                "(PlaybackEngine, tonemap path) rather than trying to preserve an HDR10/HLG fallback " +
                "the target codec profile would accept - an acknowledged v2 simplification, not a " +
                "regression to match legacy's accidental number for. FOLLOW-UPS (tracked, not blocking " +
                "this gate): (1) v2 could recover HDR10 here since the source carries an HDR10 base " +
                "layer and Chrome's hevc/av1 profiles accept HDR10; (2) v2 picks av1 vs legacy's hevc " +
                "target because it takes OutputProfile.VideoCodecs[0] instead of preferring the " +
                "source's own codec when present (same 'prefer source codec' gap as legacy's " +
                "StreamInfo.TargetVideoCodec) - a separate PlaybackEngine target-selection fix.",
        };

    /// <summary>
    /// The 9 (DeviceProfile, Source) pairs the oracle harness runs both engines over: 4 original
    /// (PR98), 2 more (PR104), and 3 mandatory HDR/10-bit cases (PR111b). Moved verbatim from
    /// <see cref="OracleParityTests"/> - same cases, same order, same comments.
    /// </summary>
    public static readonly (string DeviceProfile, string Source)[] Cases =
    {
        ("Chrome", "mp4-h264-aac-vtt-2600k"), // direct play
        ("Chrome", "mkv-h264-ac3-srt-2600k"), // transcode (container + audio codec)
        ("Chrome", "mp4-h264-ac3-aac-srt-2600k"), // transcode (secondary audio track)
        ("Firefox", "mp4-hevc-aac-srt-15200k"), // transcode (video codec not supported)

        // PR104: 2 additional cases on a different device profile (Firefox instead of Chrome),
        // reusing the already-understood, already-vtt-normalized (PR103) source plus a second
        // vp9/vorbis/vtt source Firefox decodes natively - both direct play cleanly on both
        // engines, extending oracle coverage without touching the documented srt->vtt gap.
        ("Firefox", "mp4-h264-aac-vtt-2600k"), // direct play, second device profile
        ("Firefox", "mkv-vp9-vorbis-vtt-2600k"), // direct play, vp9/vorbis/mkv

        // PR111b: 3 new mandatory HDR/10-bit cases, promoted from "documented but untested" to
        // real oracle cases that actually run both engines and get classified.
        ("Chrome", "mp4-h264-hi10p-aac-5000k"), // H.264 10-bit (High 10 profile) - Chrome's h264
                                                 // CodecProfile explicitly allows "high 10"
        ("WebOS-23", "mp4-dvhe.08-eac3-15200k"), // Dolby Vision natively supported - WebOS-23's
                                                  // hevc CodecProfile allows DOVIWithHDR10
        ("Chrome", "mp4-dvhe.08-eac3-15200k"), // Dolby Vision NOT supported - Chrome's hevc
                                                // CodecProfile only allows SDR|HDR10|HLG, forcing
                                                // legacy to fall back/transcode the DOVI range
    };

    /// <summary>
    /// Builds a real (non-mocked, other than <see cref="ITranscoderSupport"/>) legacy
    /// <see cref="StreamBuilder"/>, same as the original oracle test used.
    /// </summary>
    public static StreamBuilder GetStreamBuilder()
    {
        var transcodeSupport = new Mock<ITranscoderSupport>();
        var logger = NullLogger.Instance;
        return new StreamBuilder(transcodeSupport.Object, logger);
    }

    /// <summary>
    /// Loads the named device profile and media source(s) fixtures and assembles them into a
    /// <see cref="MediaOptions"/>, identical to the original oracle test's assembly.
    /// </summary>
    public static async ValueTask<MediaOptions> GetMediaOptions(string deviceProfile, params string[] sources)
    {
        var mediaSources = sources.Select(src => TestData<MediaSourceInfo>(src))
            .Select(val => val.Result)
            .ToArray();
        var mediaSourceId = mediaSources[0]?.Id;

        var dp = await TestData<DeviceProfile>(deviceProfile);

        return new MediaOptions()
        {
            ItemId = new System.Guid("11D229B7-2D48-4B95-9F9B-49F6AB75E613"),
            MediaSourceId = mediaSourceId,
            MediaSources = mediaSources,
            DeviceId = "test-deviceId",
            Profile = dp,
            AllowAudioStreamCopy = true,
            AllowVideoStreamCopy = true,
            EnableDirectStream = false,
        };
    }

    /// <summary>
    /// Deserializes a Test Data fixture JSON file named "{typeof(T).Name}-{name}.json".
    /// </summary>
    public static async ValueTask<T> TestData<T>(string name)
    {
        var path = Path.Join("Test Data", typeof(T).Name + "-" + name + ".json");

        using var stream = File.OpenRead(path);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options);
        if (value is not null)
        {
            return value;
        }

        throw new SerializationException("Invalid test data: " + name);
    }
}
