using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;

namespace Tesserafin.Playback.Shadow.Tests;

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
            // PR111e CLOSED the former (Chrome, mp4-h264-ac3-aac-srt-2600k) container-CSV entry that
            // used to live here: legacy's StreamBuilder mutates the shared MediaSourceInfo.Container
            // into a single value (NormalizeMediaSourceFormatIntoSingleContainer, StreamBuilder.cs:836)
            // - both ShadowPlaybackSessionPlanner and this oracle now capture the v2 inputs BEFORE
            // legacy runs, so v2 sees the real ffprobe set (for example
            // "mov,mp4,m4a,3gp,3g2,mj2") instead of legacy's already-degraded view of it, AND
            // PlaybackEngine's containerChanged/DirectPlayProfileMatches/ResolveDirectPlayContainer
            // now compare CSV-membership rather than whole-string equality. That case (and every
            // other CSV-container source in this fixture set) now classifies Equivalent - see
            // ShadowPlaybackSessionPlannerTests for the dedicated regression coverage of both halves
            // of the fix (pre-legacy capture ordering; CSV-aware v2 container comparison).
            [("Chrome", "mp4-dvhe.08-eac3-15200k")] =
                "Dolby Vision (Profile 8.1) fallback on a non-DV client: legacy and v2 AGREE the DV " +
                "range must be transcoded away on Chrome; they only differ on the transcode target. " +
                "The gating axis is videoRange (legacy=HLG, v2=HDR10). Legacy's 'HLG' is not a " +
                "deliberate HDR-preservation policy - it is an artifact of Enum.TryParse bitwise-ORing " +
                "the multi-value 'SDR|HDR10|HLG' EqualsAny VideoRangeType condition (ordinals " +
                "1|2|3 = 3, which coincidentally equals HLG's ordinal; VideoRangeType is not [Flags]). " +
                "v2's named policy (PR111e, PlaybackEngine's tonemap-target VideoRange logic): when a " +
                "source's video range needs tonemapping, output HDR10 if the TARGET codec's declared " +
                "VideoRangeTypes include HDR10, else fall all the way back to SDR - never trying to " +
                "match legacy's HLG number, which is not a real policy to replicate. PR111e RESOLVED " +
                "former follow-up (1) below (v2 now recovers HDR10 here, since the source carries an " +
                "HDR10 base layer and Chrome's hevc/av1 profiles both declare HDR10 support), which is " +
                "exactly why this entry's legacy/v2 values changed from HLG/SDR to HLG/HDR10 - the " +
                "divergence persists (legacy's HLG is still a bug, not a target v2 should chase) so the " +
                "case stays allow-listed, just for a different, now-intentional, v2 value. REMAINING " +
                "FOLLOW-UP (tracked, not blocking this gate): v2 picks av1 vs legacy's hevc target " +
                "because it takes OutputProfile.VideoCodecs[0] instead of preferring the source's own " +
                "codec when present (same 'prefer source codec' gap as legacy's StreamInfo.TargetVideoCodec) " +
                "- a separate PlaybackEngine target-selection fix.",
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
    /// <returns>A legacy <see cref="StreamBuilder"/> backed by a mocked <see cref="ITranscoderSupport"/> and a null logger.</returns>
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
    /// <param name="deviceProfile">Name of the <c>DeviceProfile</c> fixture to load.</param>
    /// <param name="sources">Names of the <c>MediaSourceInfo</c> fixtures to load; the first one supplies the selected media source id.</param>
    /// <returns>The assembled <see cref="MediaOptions"/> for the loaded profile and media sources.</returns>
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
    /// <typeparam name="T">Type the fixture is deserialized into; its name forms the first half of the file name.</typeparam>
    /// <param name="name">Fixture name forming the second half of the file name.</param>
    /// <returns>The deserialized fixture.</returns>
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
