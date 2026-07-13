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
/// resulting <see cref="ClientCapabilities"/> only describes what the client can decode; it is the
/// v2 engine's job (PR96-PR97) to combine it with a <see cref="MediaSourceSnapshot"/> and
/// <see cref="PlaybackConstraints"/> to produce a decision.
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

        var containers = profile.DirectPlayProfiles
            .Where(static p => p.Type == DlnaProfileType.Video || p.Type == DlnaProfileType.Audio)
            .SelectMany(static p => SplitCsv(p.Container))
            .Select(static c => c.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var videoCodecs = BuildVideoCodecCapabilities(profile);
        var audioCodecs = BuildAudioCodecCapabilities(profile);
        var subtitleDelivery = BuildSubtitleCapabilities(profile);

        var allCodecConditions = profile.CodecProfiles
            .SelectMany(static cp => cp.Conditions ?? [])
            .ToList();

        var minWidth = MinInt(allCodecConditions, ProfileConditionValue.Width, ProfileConditionType.LessThanEqual);
        var minHeight = MinInt(allCodecConditions, ProfileConditionValue.Height, ProfileConditionType.LessThanEqual);

        // Resolution(Width, Height) has no "unknown dimension" representation, and a single-axis
        // limit (e.g. only Width <= 1920 declared, no Height condition at all) is not a real
        // resolution ceiling. Rather than substitute a sentinel for the missing axis, only emit
        // MaxResolution when both a width and a height limit were actually declared; otherwise
        // leave it null (unbounded/unknown), same as every other optional limit in this mapper.
        Resolution? maxResolution = minWidth.HasValue && minHeight.HasValue
            ? new Resolution(minWidth.Value, minHeight.Value)
            : null;

        var maxVideoBitrate = MinInt(allCodecConditions, ProfileConditionValue.VideoBitrate, ProfileConditionType.LessThanEqual)
            ?? profile.MaxStreamingBitrate;
        var maxAudioBitrate = MinInt(allCodecConditions, ProfileConditionValue.AudioBitrate, ProfileConditionType.LessThanEqual)
            ?? profile.MusicStreamingTranscodingBitrate;

        var supportsHls = profile.TranscodingProfiles.Any(static t => t.Protocol == Reefin.Data.Enums.MediaStreamProtocol.hls);

        // Reefin.Data.Enums.MediaStreamProtocol has no "dash" member (only http/hls), so a
        // DLNA-declared DeviceProfile can never express DASH support. Always false.
        const bool supportsDash = false;

        return new ClientCapabilities(
            Containers: containers,
            VideoCodecs: videoCodecs,
            AudioCodecs: audioCodecs,
            SubtitleDelivery: subtitleDelivery,
            MaxResolution: maxResolution,
            MaxVideoBitrate: maxVideoBitrate,
            MaxAudioBitrate: maxAudioBitrate,
            SupportsHls: supportsHls,
            SupportsDash: supportsDash);
    }

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

            result.Add(new VideoCodecCapability(codec, profiles, maxLevel, maxBitDepth, videoRangeTypes));
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

            result.Add(new AudioCodecCapability(codec, maxChannels, maxSampleRate, maxBitDepth));
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
