using System;
using System.Collections.Generic;
using System.Globalization;
using Reefin.Model.Dlna;
using Reefin.Playback.Decision;
using DomainSubtitleDeliveryMethod = Reefin.Playback.Decision.SubtitleDeliveryMethod;
using LegacySubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;
using MediaStreamProtocol = Reefin.Data.Enums.MediaStreamProtocol;

namespace Reefin.Playback.Dlna;

/// <summary>
/// Maps domain <see cref="ClientCapabilities"/> back to a legacy <see cref="DeviceProfile"/>.
/// </summary>
/// <remarks>
/// TEMPORARY (PR112b): exists only because the legacy <c>StreamBuilder</c> pipeline, not the v2
/// engine, is still the canary-blocking source of truth (see
/// <c>docs/major-rewrite-plan-v13.md</c>'s "Canary v2" row). The v2 client contract
/// (<c>Reefin.Api.Models.PlaybackSessionDtos.CreatePlaybackSessionRequest</c>) accepts
/// <see cref="ClientCapabilities"/> directly, but the request still has to be translated back into
/// a <see cref="DeviceProfile"/> so the existing legacy pipeline can plan it. Delete this type (and
/// <see cref="ReverseConstraintsMapper"/>/<see cref="ReverseDlnaAdapter"/>) once the v2 execution
/// layer lands and legacy is no longer consulted for live decisions (PR114a).
/// </remarks>
/// <remarks>
/// This is the structural inverse of <see cref="ClientCapabilitiesMapper"/>: it reconstructs a
/// <see cref="DeviceProfile"/> that, projected forward through <see cref="ClientCapabilitiesMapper"/>
/// again, yields equivalent <see cref="ClientCapabilities"/> - but it is NOT a byte-for-byte reverse
/// of any specific original profile. <see cref="ClientCapabilitiesMapper"/> is a lossy projection
/// (per-entry <c>CodecProfile.Container</c> scoping, <c>ContainerProfiles</c>,
/// <c>CodecProfile.ApplyConditions</c>, non-captured axis conditions such as
/// <c>IsSecondaryAudio</c>/<c>IsAnamorphic</c>/<c>IsInterlaced</c>, and the multi-entry-per-codec
/// split are all discarded on the way into <see cref="ClientCapabilities"/>), so nothing can recover
/// them on the way back - this mapper reconstructs one merged <see cref="CodecProfile"/> per codec,
/// exactly matching what <see cref="ClientCapabilitiesMapper"/> itself would read from either a
/// single or a multi-entry legacy declaration.
/// </remarks>
public static class ReverseClientCapabilitiesMapper
{
    private const char TokenSeparator = '|';

    /// <summary>
    /// Reconstructs a legacy <see cref="DeviceProfile"/> from domain <see cref="ClientCapabilities"/>.
    /// </summary>
    /// <param name="capabilities">The domain capabilities to reconstruct a device profile from.</param>
    /// <returns>An equivalent legacy device profile.</returns>
    public static DeviceProfile ToDeviceProfile(ClientCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var decode = capabilities.Decode;

        return new DeviceProfile
        {
            // ClientCapabilitiesMapper only ever reads MaxStreamingBitrate as a per-codec fallback
            // (BuildVideoCodecCapabilities) when no explicit per-codec VideoBitrate condition
            // exists - there is no dedicated domain field carrying it. Recovering the largest
            // declared VideoCodecCapability.MaxBitrate is the only available signal: when the
            // original profile's codecs all inherited the same device-wide ceiling (the common
            // case), this recovers that exact value; where a codec had an explicit narrower limit,
            // that limit is preserved on its own CodecProfile below regardless of this value.
            MaxStreamingBitrate = ResolveMaxStreamingBitrate(decode.VideoCodecs),
            DirectPlayProfiles = BuildDirectPlayProfiles(decode.DirectPlayProfiles),
            TranscodingProfiles = BuildTranscodingProfiles(capabilities.OutputProfiles),
            CodecProfiles = BuildCodecProfiles(decode.VideoCodecs, decode.AudioCodecs),
            SubtitleProfiles = BuildSubtitleProfiles(decode.SubtitleDelivery),
        };
    }

    private static int? ResolveMaxStreamingBitrate(IReadOnlyList<VideoCodecCapability> videoCodecs)
    {
        int? max = null;
        foreach (var codec in videoCodecs)
        {
            if (codec.MaxBitrate.HasValue && (!max.HasValue || codec.MaxBitrate.Value > max.Value))
            {
                max = codec.MaxBitrate.Value;
            }
        }

        return max;
    }

    /// <summary>
    /// Reconstructs one <see cref="DirectPlayProfile"/> per <see cref="DecodeProfile"/>, order
    /// preserved - the exact inverse of <c>ClientCapabilitiesMapper.BuildDirectPlayProfiles</c>,
    /// which never flattens declared combinations (PR102b).
    /// </summary>
    private static DirectPlayProfile[] BuildDirectPlayProfiles(IReadOnlyList<DecodeProfile> profiles)
    {
        var result = new DirectPlayProfile[profiles.Count];
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            result[i] = new DirectPlayProfile
            {
                Type = profile.Type == MediaKind.Video ? DlnaProfileType.Video : DlnaProfileType.Audio,
                Container = JoinCsv(profile.Containers),
                VideoCodec = profile.Type == MediaKind.Video ? JoinCsv(profile.VideoCodecs) : null,
                AudioCodec = JoinCsv(profile.AudioCodecs),
            };
        }

        return result;
    }

    /// <summary>
    /// Reconstructs one <see cref="TranscodingProfile"/> per <see cref="PlaybackOutputProfile"/>,
    /// order preserved (PR102 - list order is the client's transcode-target preference). Always
    /// emitted with <see cref="EncodingContext.Streaming"/>: <c>ClientCapabilitiesMapper</c> only
    /// ever reads streaming-context profiles, so that is the only context this reconstruction needs
    /// to reproduce.
    /// </summary>
    private static TranscodingProfile[] BuildTranscodingProfiles(IReadOnlyList<PlaybackOutputProfile> profiles)
    {
        var result = new TranscodingProfile[profiles.Count];
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var conditions = new List<ProfileCondition>();
            if (profile.MaxVideoBitrate.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.VideoBitrate, profile.MaxVideoBitrate.Value));
            }

            if (profile.MaxAudioBitrate.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.AudioBitrate, profile.MaxAudioBitrate.Value));
            }

            result[i] = new TranscodingProfile
            {
                Type = profile.Type == MediaKind.Video ? DlnaProfileType.Video : DlnaProfileType.Audio,
                Container = profile.Container,
                Protocol = profile.Protocol == StreamingProtocol.Hls ? MediaStreamProtocol.hls : MediaStreamProtocol.http,
                VideoCodec = profile.Type == MediaKind.Video ? JoinCsv(profile.VideoCodecs) : string.Empty,
                AudioCodec = JoinCsv(profile.AudioCodecs),
                Context = EncodingContext.Streaming,
                MaxAudioChannels = profile.MaxAudioChannels?.ToString(CultureInfo.InvariantCulture),
                Conditions = conditions.ToArray(),
            };
        }

        return result;
    }

    /// <summary>
    /// Reconstructs one merged <see cref="CodecProfile"/> per distinct codec - one for each
    /// <see cref="VideoCodecCapability"/>, one for each <see cref="AudioCodecCapability"/> - since
    /// <c>ClientCapabilitiesMapper</c> already merges every legacy <c>CodecProfile</c> entry
    /// matching a given codec into a single aggregate capability (<c>CodecProfileAppliesTo</c> +
    /// <c>SelectMany</c>), a single reconstructed entry per codec is a faithful inverse.
    /// </summary>
    private static CodecProfile[] BuildCodecProfiles(
        IReadOnlyList<VideoCodecCapability> videoCodecs,
        IReadOnlyList<AudioCodecCapability> audioCodecs)
    {
        var result = new List<CodecProfile>(videoCodecs.Count + audioCodecs.Count);

        foreach (var codec in videoCodecs)
        {
            var conditions = new List<ProfileCondition>();
            if (codec.Profiles.Count > 0)
            {
                conditions.Add(new ProfileCondition(ProfileConditionType.EqualsAny, ProfileConditionValue.VideoProfile, JoinTokens(codec.Profiles)));
            }

            if (codec.MaxLevel.HasValue)
            {
                conditions.Add(new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.VideoLevel, codec.MaxLevel.Value.ToString(CultureInfo.InvariantCulture)));
            }

            if (codec.MaxBitDepth.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.VideoBitDepth, codec.MaxBitDepth.Value));
            }

            if (codec.VideoRangeTypes.Count > 0)
            {
                conditions.Add(new ProfileCondition(ProfileConditionType.EqualsAny, ProfileConditionValue.VideoRangeType, JoinTokens(codec.VideoRangeTypes)));
            }

            if (codec.MaxResolution is { } resolution)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.Width, resolution.Width));
                conditions.Add(NumericCondition(ProfileConditionValue.Height, resolution.Height));
            }

            if (codec.MaxBitrate.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.VideoBitrate, codec.MaxBitrate.Value));
            }

            result.Add(new CodecProfile { Type = CodecType.Video, Codec = codec.Codec, Conditions = conditions.ToArray() });
        }

        foreach (var codec in audioCodecs)
        {
            var conditions = new List<ProfileCondition>();
            if (codec.MaxChannels.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.AudioChannels, codec.MaxChannels.Value));
            }

            if (codec.MaxSampleRate.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.AudioSampleRate, codec.MaxSampleRate.Value));
            }

            if (codec.MaxBitDepth.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.AudioBitDepth, codec.MaxBitDepth.Value));
            }

            if (codec.MaxBitrate.HasValue)
            {
                conditions.Add(NumericCondition(ProfileConditionValue.AudioBitrate, codec.MaxBitrate.Value));
            }

            result.Add(new CodecProfile { Type = CodecType.Audio, Codec = codec.Codec, Conditions = conditions.ToArray() });
        }

        return result.ToArray();
    }

    private static SubtitleProfile[] BuildSubtitleProfiles(IReadOnlyList<SubtitleCapability> subtitleDelivery)
    {
        var result = new SubtitleProfile[subtitleDelivery.Count];
        for (var i = 0; i < subtitleDelivery.Count; i++)
        {
            var capability = subtitleDelivery[i];
            result[i] = new SubtitleProfile
            {
                Format = capability.Format,
                Method = MapDeliveryMethod(capability.Method),
            };
        }

        return result;
    }

    private static LegacySubtitleDeliveryMethod MapDeliveryMethod(DomainSubtitleDeliveryMethod method) => method switch
    {
        DomainSubtitleDeliveryMethod.Burn => LegacySubtitleDeliveryMethod.Encode,
        DomainSubtitleDeliveryMethod.Embed => LegacySubtitleDeliveryMethod.Embed,
        DomainSubtitleDeliveryMethod.External => LegacySubtitleDeliveryMethod.External,
        DomainSubtitleDeliveryMethod.Hls => LegacySubtitleDeliveryMethod.Hls,

        // Unreachable given the domain enum's 4 members - Drop has no domain equivalent (it is
        // dropped entirely on the forward trip, never produced here), kept only so the switch stays
        // exhaustive against future domain members.
        _ => LegacySubtitleDeliveryMethod.Drop,
    };

    private static ProfileCondition NumericCondition(ProfileConditionValue property, int value) =>
        new(ProfileConditionType.LessThanEqual, property, value.ToString(CultureInfo.InvariantCulture));

    private static string JoinCsv(IReadOnlyList<string> values) => values.Count == 0 ? string.Empty : string.Join(',', values);

    private static string JoinTokens(IReadOnlyList<string> values) => string.Join(TokenSeparator, values);
}
