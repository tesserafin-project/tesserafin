using System.Text;
using Reefin.Playback.Contract.Diagnostics;
using Reefin.Playback.Contract.Scan;

namespace Reefin.Playback.Contract.Scan.Tests;

/// <summary>
/// The playback-request contract topology, hand-built with the same PascalCase member names the
/// real <c>PlaybackContractScanModelProvider</c> reads from the binder's metadata. Kept here (rather
/// than referencing Reefin.Api) so the scanner is tested in isolation from the web stack; a separate
/// Reefin.Api test proves the provider sources these exact names from the binder's own
/// <c>JsonTypeInfo</c>.
/// </summary>
internal static class ContractScanTestModel
{
    public static ScanContractLevel Root { get; } = Build();

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static ScanContractLevel Build()
    {
        var videoCodec = new ScanContractLevel(ContractPath.DecodeVideoCodecs, new[]
        {
            new ScanMember(Utf8("Codec"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("Profiles"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("MaxLevel"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("MaxBitDepth"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("VideoRangeTypes"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("MaxResolution"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("MaxBitrate"), ScanMemberKind.NumericScalar),
        });

        var audioCodec = new ScanContractLevel(ContractPath.DecodeAudioCodecs, new[]
        {
            new ScanMember(Utf8("Codec"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("MaxChannels"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("MaxSampleRate"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("MaxBitDepth"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("MaxBitrate"), ScanMemberKind.NumericScalar),
        });

        var profile = new ScanContractLevel(ContractPath.OutputProfiles, new[]
        {
            new ScanMember(Utf8("Type"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("Protocol"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("Container"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("VideoCodecs"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("AudioCodecs"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("MaxVideoBitrate"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("MaxAudioBitrate"), ScanMemberKind.NumericScalar),
            new ScanMember(Utf8("MaxAudioChannels"), ScanMemberKind.NumericScalar),
        });

        var decode = new ScanContractLevel(ContractPath.Decode, new[]
        {
            new ScanMember(Utf8("DirectPlayProfiles"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("VideoCodecs"), ScanMemberKind.ObjectArray, videoCodec),
            new ScanMember(Utf8("AudioCodecs"), ScanMemberKind.ObjectArray, audioCodec),
            new ScanMember(Utf8("SubtitleDelivery"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("SupportsHls"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("SupportsDash"), ScanMemberKind.Scalar),
        });

        var capabilities = new ScanContractLevel(ContractPath.Capabilities, new[]
        {
            new ScanMember(Utf8("Decode"), ScanMemberKind.ObjectContainer, decode),
            new ScanMember(Utf8("OutputProfiles"), ScanMemberKind.ObjectArray, profile),
        });

        return new ScanContractLevel(ContractPath.Request, new[]
        {
            new ScanMember(Utf8("ItemId"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("UserId"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("Capabilities"), ScanMemberKind.ObjectContainer, capabilities),
            new ScanMember(Utf8("Constraints"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("MediaSourceId"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("PlaySessionId"), ScanMemberKind.Scalar),
            new ScanMember(Utf8("PlaybackAttemptId"), ScanMemberKind.Scalar),
        });
    }
}
