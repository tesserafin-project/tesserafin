using Reefin.Data.Enums;
using Reefin.Model.Dlna;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Builds a realistic web-client-style <see cref="DeviceProfile"/> fixture, shared by the
/// <see cref="ClientCapabilitiesMapper"/> tests.
/// </summary>
internal static class DeviceProfileFixture
{
    /// <summary>
    /// Builds a device profile roughly modeled on a typical browser web client: mp4/mkv/webm
    /// containers, h264/hevc video with a constrained h264 codec profile, aac/ac3/mp3 audio with
    /// a constrained aac codec profile, srt (external) and ass (burn-in) subtitles, and hls
    /// transcoding support.
    /// </summary>
    /// <returns>The fixture device profile.</returns>
    public static DeviceProfile BuildWebClientProfile()
    {
        return new DeviceProfile
        {
            MaxStreamingBitrate = 20_000_000,
            MusicStreamingTranscodingBitrate = 384_000,
            DirectPlayProfiles =
            [
                new DirectPlayProfile
                {
                    Container = "mp4,mkv,webm",
                    VideoCodec = "h264,hevc",
                    Type = DlnaProfileType.Video,
                },
                new DirectPlayProfile
                {
                    Container = "mp4,mkv,webm",
                    AudioCodec = "aac,ac3,mp3",
                    Type = DlnaProfileType.Audio,
                },
            ],
            TranscodingProfiles =
            [
                new TranscodingProfile
                {
                    Container = "ts",
                    Protocol = MediaStreamProtocol.hls,
                    Type = DlnaProfileType.Video,
                    VideoCodec = "h264",
                    AudioCodec = "aac",
                },
            ],
            CodecProfiles =
            [
                new CodecProfile
                {
                    Type = CodecType.Video,
                    Codec = "h264",
                    Conditions =
                    [
                        new ProfileCondition(ProfileConditionType.Equals, ProfileConditionValue.VideoProfile, "high|main"),
                        new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.VideoLevel, "51"),
                        new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.Width, "1920"),
                        new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.Height, "1080"),
                        new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.VideoBitDepth, "8"),
                    ],
                },
                new CodecProfile
                {
                    Type = CodecType.Audio,
                    Codec = "aac",
                    Conditions =
                    [
                        new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.AudioChannels, "2"),
                    ],
                },
            ],
            SubtitleProfiles =
            [
                new SubtitleProfile { Format = "srt", Method = SubtitleDeliveryMethod.External },
                new SubtitleProfile { Format = "ass", Method = SubtitleDeliveryMethod.Encode },
            ],
        };
    }
}
