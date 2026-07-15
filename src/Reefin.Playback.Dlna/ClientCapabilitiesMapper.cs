using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Reefin.Model.Dlna;
using Reefin.Playback.Decision;
using DlnaSubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;
using DomainSubtitleDeliveryMethod = Reefin.Playback.Decision.SubtitleDeliveryMethod;

namespace Reefin.Playback.Dlna;

/// <summary>
/// Maps a legacy <see cref="DeviceProfile"/> to the domain <see cref="ClientCapabilities"/>.
/// </summary>
/// <remarks>
/// This is a faithful projection of the capabilities a <see cref="DeviceProfile"/>
/// <em>declares</em>, not a re-implementation of the DLNA <c>StreamBuilder</c> evaluation logic
/// (which combines declared capabilities with a specific source to pick a play method). The
/// resulting <see cref="ClientCapabilities"/> describes both what the client can decode
/// (<see cref="DecodeCapabilities"/>, from <c>DirectPlayProfiles</c>/<c>CodecProfiles</c>) and,
/// separately, what the server should produce when it must transcode
/// (<see cref="PlaybackOutputProfile"/>, from <c>TranscodingProfiles</c>, order preserved - PR102).
/// It is the v2 engine's job (PR96-PR97, PR102, PR102b) to combine this with a
/// <see cref="MediaSourceSnapshot"/> and <see cref="PlaybackConstraints"/> to produce a decision.
/// </remarks>
/// <remarks>
/// <c>CodecProfile.ApplyConditions</c> (conditions to apply <em>if</em> the profile's own
/// <c>Conditions</c> match, i.e. a conditional/derived limit) is not read anywhere in this mapper -
/// only <c>CodecProfile.Conditions</c> is projected. This was already true before PR102b; PR102b
/// does not change it, it only relocates where the (unconditional) <c>Conditions</c> values land
/// (per-codec instead of device-global). Modeling <c>ApplyConditions</c> would require the mapper
/// to evaluate condition matching against a hypothetical stream, which the declared-capability
/// snapshot this type produces has no room for - it is out of scope here, same as before.
/// </remarks>
public static class ClientCapabilitiesMapper
{
    private static readonly char[] TokenSeparators = ['|', ','];

    /// <summary>
    /// Projects a legacy <see cref="DeviceProfile"/> into domain <see cref="ClientCapabilities"/>.
    /// </summary>
    /// <param name="profile">The legacy device profile to project.</param>
    /// <returns>The equivalent domain capabilities.</returns>
    public static ClientCapabilities ToCapabilities(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new ClientCapabilities(
            Decode: BuildDecodeCapabilities(profile),
            OutputProfiles: BuildOutputProfiles(profile));
    }

    private static DecodeCapabilities BuildDecodeCapabilities(DeviceProfile profile)
    {
        var directPlayProfiles = BuildDirectPlayProfiles(profile);
        var videoCodecs = BuildVideoCodecCapabilities(profile);
        var audioCodecs = BuildAudioCodecCapabilities(profile);
        var subtitleDelivery = BuildSubtitleCapabilities(profile);

        var supportsHls = profile.TranscodingProfiles.Any(static t => t.Protocol == Reefin.Data.Enums.MediaStreamProtocol.hls);

        // Reefin.Data.Enums.MediaStreamProtocol has no "dash" member (only http/hls), so a
        // DLNA-declared DeviceProfile can never express DASH support. Always false.
        const bool supportsDash = false;

        return new DecodeCapabilities(
            DirectPlayProfiles: directPlayProfiles,
            VideoCodecs: videoCodecs,
            AudioCodecs: audioCodecs,
            SubtitleDelivery: subtitleDelivery,
            SupportsHls: supportsHls,
            SupportsDash: supportsDash);
    }

    /// <summary>
    /// Projects the device's <c>DirectPlayProfiles</c> into <see cref="DecodeProfile"/> entries,
    /// one per legacy profile, order preserved and <em>not</em> flattened (PR102b problem #1): a
    /// legacy <c>DirectPlayProfile</c> associates its container and codec(s) as a single declared
    /// combination, so a client that declares MP4/H.264 and, separately, WebM/VP9 must not be read
    /// as also accepting MP4/VP9.
    /// </summary>
    private static IReadOnlyList<DecodeProfile> BuildDirectPlayProfiles(DeviceProfile profile)
    {
        var result = new List<DecodeProfile>(profile.DirectPlayProfiles.Length);
        foreach (var directPlayProfile in profile.DirectPlayProfiles)
        {
            MediaKind type;
            if (directPlayProfile.Type == DlnaProfileType.Video)
            {
                type = MediaKind.Video;
            }
            else if (directPlayProfile.Type == DlnaProfileType.Audio)
            {
                type = MediaKind.Audio;
            }
            else
            {
                // Photo/Subtitle/Lyric direct-play profiles have no v2 MediaKind equivalent; skip
                // rather than misclassify.
                continue;
            }

            var containers = SplitCsv(directPlayProfile.Container).Select(static c => c.ToLowerInvariant()).ToList();
            var videoCodecs = type == MediaKind.Video
                ? SplitCsv(directPlayProfile.VideoCodec).Select(static c => c.ToLowerInvariant()).ToList()
                : [];
            var audioCodecs = SplitCsv(directPlayProfile.AudioCodec).Select(static c => c.ToLowerInvariant()).ToList();

            result.Add(new DecodeProfile(type, containers, videoCodecs, audioCodecs));
        }

        return result;
    }

    /// <summary>
    /// Projects the device's <c>TranscodingProfiles</c> into ordered <see cref="PlaybackOutputProfile"/>
    /// entries (PR102): unlike <see cref="BuildDecodeCapabilities"/>, list order is preserved
    /// exactly as declared, because that order <em>is</em> the client's transcoding preference (for
    /// example a browser listing <c>"av1,h264,vp9"</c> on its HLS/MP4 profile prefers AV1 output).
    /// Only <see cref="EncodingContext.Streaming"/> profiles are projected: the v2 domain has no
    /// concept of a <see cref="EncodingContext.Static"/> (whole-file conversion) request, so a
    /// device's static-context transcoding profiles - duplicates of its streaming ones, aimed at a
    /// different use case entirely - would only add noise to the preference order the engine reads.
    /// </summary>
    private static IReadOnlyList<PlaybackOutputProfile> BuildOutputProfiles(DeviceProfile profile)
    {
        var result = new List<PlaybackOutputProfile>(profile.TranscodingProfiles.Length);
        foreach (var transcodingProfile in profile.TranscodingProfiles)
        {
            if (transcodingProfile.Context != EncodingContext.Streaming)
            {
                continue;
            }

            MediaKind type;
            if (transcodingProfile.Type == DlnaProfileType.Video)
            {
                type = MediaKind.Video;
            }
            else if (transcodingProfile.Type == DlnaProfileType.Audio)
            {
                type = MediaKind.Audio;
            }
            else
            {
                // Photo/Subtitle/Lyric transcoding profiles have no v2 MediaKind equivalent and
                // never occur in practice; skip rather than misclassify.
                continue;
            }

            var protocol = transcodingProfile.Protocol == Reefin.Data.Enums.MediaStreamProtocol.hls
                ? StreamingProtocol.Hls
                : StreamingProtocol.Http;

            var container = SplitCsv(transcodingProfile.Container).Select(static c => c.ToLowerInvariant()).FirstOrDefault() ?? string.Empty;

            var videoCodecs = type == MediaKind.Video
                ? SplitCsv(transcodingProfile.VideoCodec).Select(static c => c.ToLowerInvariant()).ToList()
                : [];
            var audioCodecs = SplitCsv(transcodingProfile.AudioCodec).Select(static c => c.ToLowerInvariant()).ToList();

            var conditions = transcodingProfile.Conditions ?? [];
            var maxVideoBitrate = MinInt(conditions, ProfileConditionValue.VideoBitrate, ProfileConditionType.LessThanEqual);
            var maxAudioBitrate = MinInt(conditions, ProfileConditionValue.AudioBitrate, ProfileConditionType.LessThanEqual);
            var maxAudioChannels = ParseNullableInt(transcodingProfile.MaxAudioChannels);

            result.Add(new PlaybackOutputProfile(
                Type: type,
                Protocol: protocol,
                Container: container,
                VideoCodecs: videoCodecs,
                AudioCodecs: audioCodecs,
                MaxVideoBitrate: maxVideoBitrate,
                MaxAudioBitrate: maxAudioBitrate,
                MaxAudioChannels: maxAudioChannels));
        }

        return result;
    }

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static IReadOnlyList<VideoCodecCapability> BuildVideoCodecCapabilities(DeviceProfile profile)
    {
        var codecNames = new List<string>();
        codecNames.AddRange(
            profile.DirectPlayProfiles
                .Where(static p => p.Type == DlnaProfileType.Video)
                .SelectMany(static p => SplitCsv(p.VideoCodec)));
        codecNames.AddRange(
            profile.CodecProfiles
                .Where(static cp => (cp.Type == CodecType.Video || cp.Type == CodecType.VideoAudio) && cp.Codec is not null)
                .SelectMany(static cp => SplitCsv(cp.Codec)));

        var distinctCodecs = codecNames
            .Select(static c => c.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new List<VideoCodecCapability>(distinctCodecs.Count);
        foreach (var codec in distinctCodecs)
        {
            var conditions = profile.CodecProfiles
                .Where(cp => (cp.Type == CodecType.Video || cp.Type == CodecType.VideoAudio) && CodecProfileAppliesTo(cp, codec))
                .SelectMany(static cp => cp.Conditions ?? [])
                .ToList();

            var profiles = ParseTokens(conditions, ProfileConditionValue.VideoProfile)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var maxLevel = MinDouble(conditions, ProfileConditionValue.VideoLevel, ProfileConditionType.LessThanEqual);
            var maxBitDepth = MinInt(conditions, ProfileConditionValue.VideoBitDepth, ProfileConditionType.LessThanEqual);

            var videoRangeTypes = ParseTokens(conditions, ProfileConditionValue.VideoRangeType)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (videoRangeTypes.Count == 0)
            {
                videoRangeTypes = ["SDR"];
            }

            // PR102b: resolution/bitrate limits are per-codec (a CodecProfile's Width/Height/
            // VideoBitrate conditions are scoped to the codec(s) it applies to), not a single
            // device-wide ceiling - see VideoCodecCapability remarks for why the old global
            // DecodeCapabilities.MaxResolution/MaxVideoBitrate collapsed distinct per-codec limits
            // into one artificial minimum.
            var minWidth = MinInt(conditions, ProfileConditionValue.Width, ProfileConditionType.LessThanEqual);
            var minHeight = MinInt(conditions, ProfileConditionValue.Height, ProfileConditionType.LessThanEqual);

            // Same "both axes or neither" rule as the old global computation: a single-axis limit
            // is not a real resolution ceiling, so only emit MaxResolution when both were declared.
            Resolution? maxResolution = minWidth.HasValue && minHeight.HasValue
                ? new Resolution(minWidth.Value, minHeight.Value)
                : null;

            // No per-codec VideoBitrate condition falls back to the device-wide
            // MaxStreamingBitrate ceiling, same fallback the old global computation used - it is a
            // genuine device-level bound (the pipe the device streams over), not a per-codec one,
            // so every codec inherits it absent a more specific declared limit.
            var maxBitrate = MinInt(conditions, ProfileConditionValue.VideoBitrate, ProfileConditionType.LessThanEqual)
                ?? profile.MaxStreamingBitrate;

            result.Add(new VideoCodecCapability(codec, profiles, maxLevel, maxBitDepth, videoRangeTypes, maxResolution, maxBitrate));
        }

        return result;
    }

    private static IReadOnlyList<AudioCodecCapability> BuildAudioCodecCapabilities(DeviceProfile profile)
    {
        var codecNames = new List<string>();
        codecNames.AddRange(profile.DirectPlayProfiles.SelectMany(static p => SplitCsv(p.AudioCodec)));
        codecNames.AddRange(profile.TranscodingProfiles.SelectMany(static p => SplitCsv(p.AudioCodec)));
        codecNames.AddRange(
            profile.CodecProfiles
                .Where(static cp => (cp.Type == CodecType.Audio || cp.Type == CodecType.VideoAudio) && cp.Codec is not null)
                .SelectMany(static cp => SplitCsv(cp.Codec)));

        var distinctCodecs = codecNames
            .Select(static c => c.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new List<AudioCodecCapability>(distinctCodecs.Count);
        foreach (var codec in distinctCodecs)
        {
            var conditions = profile.CodecProfiles
                .Where(cp => (cp.Type == CodecType.Audio || cp.Type == CodecType.VideoAudio) && CodecProfileAppliesTo(cp, codec))
                .SelectMany(static cp => cp.Conditions ?? [])
                .ToList();

            var maxChannels = MinInt(conditions, ProfileConditionValue.AudioChannels, ProfileConditionType.LessThanEqual);
            var maxSampleRate = MinInt(conditions, ProfileConditionValue.AudioSampleRate, ProfileConditionType.LessThanEqual);
            var maxBitDepth = MinInt(conditions, ProfileConditionValue.AudioBitDepth, ProfileConditionType.LessThanEqual);

            // Per-codec audio *decode* ceiling: only an explicit per-codec AudioBitrate condition
            // constrains it. Unlike video (which legitimately falls back to the device-wide
            // MaxStreamingBitrate stream ceiling), audio must NOT fall back to
            // MusicStreamingTranscodingBitrate: that value is a music-file transcode *output* default,
            // not a decode capability. Legacy only ever uses it as a transcode target inside
            // GetOptimalAudioStream for pure-audio items (StreamBuilder), never to gate a video's
            // embedded-audio direct play. Reusing it here (PR102b) spuriously rejected any
            // video-embedded audio track above that default (e.g. 640kbps eac3 vs a 384kbps default),
            // forcing a needless transcode where legacy direct-plays. Leave null absent an explicit
            // condition, matching legacy.
            var maxBitrate = MinInt(conditions, ProfileConditionValue.AudioBitrate, ProfileConditionType.LessThanEqual);

            result.Add(new AudioCodecCapability(codec, maxChannels, maxSampleRate, maxBitDepth, maxBitrate));
        }

        return result;
    }

    private static IReadOnlyList<SubtitleCapability> BuildSubtitleCapabilities(DeviceProfile profile)
    {
        var result = new List<SubtitleCapability>();
        foreach (var subtitleProfile in profile.SubtitleProfiles)
        {
            if (string.IsNullOrEmpty(subtitleProfile.Format))
            {
                continue;
            }

            var mapped = MapDeliveryMethod(subtitleProfile.Method);
            if (mapped is null)
            {
                continue;
            }

            result.Add(new SubtitleCapability(subtitleProfile.Format, mapped.Value));
        }

        return result;
    }

    private static DomainSubtitleDeliveryMethod? MapDeliveryMethod(DlnaSubtitleDeliveryMethod method) => method switch
    {
        DlnaSubtitleDeliveryMethod.Encode => DomainSubtitleDeliveryMethod.Burn,
        DlnaSubtitleDeliveryMethod.Embed => DomainSubtitleDeliveryMethod.Embed,
        DlnaSubtitleDeliveryMethod.External => DomainSubtitleDeliveryMethod.External,
        DlnaSubtitleDeliveryMethod.Hls => DomainSubtitleDeliveryMethod.Hls,
        DlnaSubtitleDeliveryMethod.Drop => null,
        _ => null,
    };

    private static bool CodecProfileAppliesTo(CodecProfile codecProfile, string codecName)
    {
        if (string.IsNullOrEmpty(codecProfile.Codec))
        {
            return true;
        }

        return SplitCsv(codecProfile.Codec).Any(c => string.Equals(c, codecName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<string> ParseTokens(IEnumerable<ProfileCondition> conditions, ProfileConditionValue property)
    {
        foreach (var condition in conditions)
        {
            if (condition.Property != property)
            {
                continue;
            }

            if (condition.Condition != ProfileConditionType.Equals && condition.Condition != ProfileConditionType.EqualsAny)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(condition.Value))
            {
                continue;
            }

            foreach (var token in condition.Value.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// Extracts the numeric values of conditions matching <paramref name="property"/> and
    /// <paramref name="conditionType"/>, skipping values that do not parse as a
    /// culture-invariant integer rather than throwing.
    /// </summary>
    private static IEnumerable<int> ParseIntValues(IEnumerable<ProfileCondition> conditions, ProfileConditionValue property, ProfileConditionType conditionType)
    {
        foreach (var condition in conditions)
        {
            if (condition.Property != property || condition.Condition != conditionType)
            {
                continue;
            }

            if (int.TryParse(condition.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Extracts the numeric values of conditions matching <paramref name="property"/> and
    /// <paramref name="conditionType"/>, skipping values that do not parse as a
    /// culture-invariant floating point number rather than throwing.
    /// </summary>
    private static IEnumerable<double> ParseDoubleValues(IEnumerable<ProfileCondition> conditions, ProfileConditionValue property, ProfileConditionType conditionType)
    {
        foreach (var condition in conditions)
        {
            if (condition.Property != property || condition.Condition != conditionType)
            {
                continue;
            }

            if (double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return value;
            }
        }
    }

    private static int? MinInt(IEnumerable<ProfileCondition> conditions, ProfileConditionValue property, ProfileConditionType conditionType)
    {
        var values = ParseIntValues(conditions, property, conditionType).ToList();
        return values.Count == 0 ? null : values.Min();
    }

    private static double? MinDouble(IEnumerable<ProfileCondition> conditions, ProfileConditionValue property, ProfileConditionType conditionType)
    {
        var values = ParseDoubleValues(conditions, property, conditionType).ToList();
        return values.Count == 0 ? null : values.Min();
    }
}
