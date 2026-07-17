using System.Linq;
using Reefin.Data.Enums;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Maps the <c>StreamInfo</c> <see cref="IPlaybackLiveStreamResolver.Resolve"/> actually resolved
/// (PR117, docs/pr116d-url-contract-design.md §2.2) into the client-facing
/// <see cref="PlaybackSessionStreamDescriptor"/>. Reads only what is already on the resolved
/// <see cref="StreamInfo"/> - never re-decides, never re-serializes via a new URL-building path
/// (design doc §1.3: the executable parity invariant requires reusing <c>StreamInfo.ToUrl</c>
/// verbatim).
/// </summary>
public static class PlaybackSessionStreamDescriptorMapper
{
    /// <summary>
    /// Builds the descriptor for a resolved stream.
    /// </summary>
    /// <param name="streamInfo">The <c>StreamInfo</c> that will actually be served, already re-stamped with the caller's own <c>PlaySessionId</c>/<c>StartPositionTicks</c>.</param>
    /// <param name="servedBy">The real engine version that produced <paramref name="streamInfo"/>, or <see cref="PlaybackSessionResponse.LegacyDecisionVersion"/> when it fell back to legacy.</param>
    /// <param name="fallbackReason">Why legacy was served instead of v2, or <see langword="null"/> when <paramref name="servedBy"/> is a real v2 version.</param>
    /// <param name="transcoderSupport">Used to resolve the external subtitle delivery URL, mirroring <c>MediaInfoHelper.SetDeviceSpecificSubtitleInfo</c>'s own resolution - never mutates <paramref name="streamInfo"/>'s media source.</param>
    /// <param name="accessToken">The caller's own access token - the same value the legacy path already serializes into <c>&amp;ApiKey=</c>.</param>
    /// <returns>The mapped descriptor.</returns>
    public static PlaybackSessionStreamDescriptor Map(
        StreamInfo streamInfo,
        int servedBy,
        PlaybackLiveFallbackReason? fallbackReason,
        ITranscoderSupport transcoderSupport,
        string? accessToken)
    {
        return new PlaybackSessionStreamDescriptor(
            streamInfo.ToUrl(null, accessToken, null),
            streamInfo.SubProtocol == MediaStreamProtocol.hls ? Reefin.Playback.Decision.StreamingProtocol.Hls : Reefin.Playback.Decision.StreamingProtocol.Http,
            servedBy,
            fallbackReason,
            ResolveSubtitleUrl(streamInfo, transcoderSupport, accessToken));
    }

    /// <summary>
    /// Mirrors <c>MediaInfoHelper.SetDeviceSpecificSubtitleInfo</c>'s own condition for an external
    /// subtitle delivery URL (design doc §2.2: "présent uniquement quand ... Method == External"),
    /// read-only - the legacy method mutates a shared <c>MediaSourceInfo</c>, this one only reads
    /// <see cref="StreamInfo.GetSubtitleProfiles(ITranscoderSupport, bool, string, string)"/>'s own
    /// projection, since the descriptor never needs to mutate the caller's media source.
    /// </summary>
    private static string? ResolveSubtitleUrl(StreamInfo streamInfo, ITranscoderSupport transcoderSupport, string? accessToken)
    {
        if (streamInfo.SubtitleStreamIndex is not int index || index < 0
            || streamInfo.SubtitleDeliveryMethod != SubtitleDeliveryMethod.External)
        {
            return null;
        }

        var profile = streamInfo
            .GetSubtitleProfiles(transcoderSupport, includeSelectedTrackOnly: true, baseUrl: "-", accessToken)
            .FirstOrDefault(p => p.Index == index && p.DeliveryMethod == SubtitleDeliveryMethod.External);

        return profile?.Url?.TrimStart('-');
    }
}
