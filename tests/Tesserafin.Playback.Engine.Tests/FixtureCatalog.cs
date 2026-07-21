using System.Collections.Generic;

namespace Tesserafin.Playback.Engine.Tests;

/// <summary>
/// The single list of fixture file names shared by <see cref="FixtureParityTests"/> (behavioral
/// gate: does the engine produce what's expected) and <see cref="FixtureSchemaValidationTests"/>
/// (structural gate: does the fixture conform to fixture.schema.json) - PR104 introduces the second
/// gate, and a fixture added to only one of the two would silently skip the other, so both theories
/// enumerate this one list instead of maintaining independent <c>[InlineData]</c> sets.
/// </summary>
internal static class FixtureCatalog
{
    public static readonly IReadOnlyList<string> AllFixtureNames =
    [
        "video-h264-aac-mp4-directplay.json",
        "video-mkv-remux-mp4.json",
        "video-mkv-dts-to-aac.json",
        "audio-downmix-51-to-stereo.json",
        "video-no-viable-plan.json",
        "video-codec-incompatible.json",
        "video-resolution-limit.json",
        "video-hdr-tonemap.json",
        "video-hdr-tonemap-hdr10-recovery.json",
        "subtitle-pgs-burn-in.json",
        "subtitle-srt-external.json",
        "video-live-tv-infinite-source.json",
        "video-alternate-versions-best-selected.json",
        "video-requested-source-served.json",
        "video-requested-source-not-found.json",
        "video-direct-play-cross-container-codec-invalid.json",
        "video-per-codec-resolution-limit-hevc-4k-directplay.json",
        "video-output-profile-order-first-viable.json",
        "video-output-profile-first-not-viable-second-viable.json",
        "video-protocol-http-vs-hls.json",
        "subtitle-default-auto-selected.json",
        "subtitle-forced-auto-selected.json",
        "video-invalid-audio-index-fallback-default.json",
        "video-invalid-subtitle-index-no-selection.json",
        "video-maxbitrate-global-caps-transcode.json",
        "audio-codec-bitrate-limit-applied.json",
        "video-av1-preferred-output-profile.json",
    ];

    public static IEnumerable<object[]> AllFixtures()
    {
        foreach (var name in AllFixtureNames)
        {
            yield return [name];
        }
    }
}
