using System;
using Reefin.Data.Enums;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Session;
using Reefin.Playback.Execution;
using DomainPlaybackMethod = Reefin.Playback.Decision.PlaybackMethod;
using DomainStreamingProtocol = Reefin.Playback.Decision.StreamingProtocol;
using DomainSubtitleDeliveryMethod = Reefin.Playback.Decision.SubtitleDeliveryMethod;
using LegacySubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;

namespace Reefin.Playback.Dlna;

/// <summary>
/// Fills a legacy <see cref="StreamInfo"/> from a v2 <see cref="PlaybackExecutionPlan"/>, so the
/// existing HLS/ffmpeg execution machinery (<c>Reefin.Api.Helpers.StreamingHelpers.GetStreamingState</c>,
/// <c>Reefin.Controller.Streaming.StreamState</c>, <c>DynamicHlsController</c>, <c>VideosController</c>)
/// can run a decision the v2 engine made, without that machinery itself changing at all.
/// </summary>
/// <remarks>
/// TEMPORARY (PR114a): this adapter exists only because the execution machinery above still consumes
/// <see cref="StreamInfo"/>, not <see cref="PlaybackExecutionPlan"/>, directly. It will be replaced by
/// a native pipeline that consumes <see cref="PlaybackExecutionPlan"/> directly once v2 becomes the
/// live source of truth (PR115 canary and beyond) - unlike <see cref="ReverseDlnaAdapter"/> (which
/// maps the client's REQUEST, pre-decision, and stays legacy's input contract until then),
/// this adapter maps the DECISION, post-v2, and is the piece PR115 replaces.
/// </remarks>
/// <remarks>
/// Re-decides NOTHING: every field the source/stream selection determines - <see cref="StreamInfo.MediaSourceId"/>
/// (via <see cref="StreamInfo.MediaSource"/>, validated below to match <see cref="PlaybackExecutionPlan.SourceId"/>),
/// stream indexes, target codecs/container - is copied verbatim from the plan, never recomputed or
/// defaulted from the caller-supplied media source/device profile. Those two arguments (plus the item
/// id and the optional device/session identifiers) are supplied by the caller because
/// <see cref="PlaybackExecutionPlan"/> deliberately carries none of them: it names a source by id only
/// (RFC PR91 §8: the domain performs no I/O and holds no full legacy DTOs), and <see cref="StreamInfo"/>
/// requires the caller's own resolved objects (a <see cref="MediaSourceInfo"/> looked up by that id, a
/// legacy <see cref="DeviceProfile"/>) to do anything useful downstream (URL building, subtitle
/// profile resolution, encoding param derivation).
/// </remarks>
/// <remarks>
/// Fields filled: <see cref="StreamInfo.PlayMethod"/>, <see cref="StreamInfo.Container"/>,
/// <see cref="StreamInfo.SubProtocol"/>, <see cref="StreamInfo.MediaType"/> (inferred from whether a
/// video stream was selected - not re-decided, just read off the plan), <see cref="StreamInfo.AudioStreamIndex"/>,
/// <see cref="StreamInfo.VideoCodecs"/>/<see cref="StreamInfo.AudioCodecs"/> (each a single-element
/// list holding the plan's target codec, so the <c>Target*Codec</c> derived getters resolve to it
/// regardless of <see cref="StreamInfo.IsDirectStream"/>), <see cref="StreamInfo.VideoBitrate"/>/
/// <see cref="StreamInfo.AudioBitrate"/>, <see cref="StreamInfo.MaxWidth"/>/<see cref="StreamInfo.MaxHeight"/>
/// (from <see cref="PlaybackExecutionPlan.Resolution"/>, when set), <see cref="StreamInfo.GlobalMaxAudioChannels"/>
/// (from <see cref="PlaybackExecutionPlan.AudioChannels"/>), <see cref="StreamInfo.SubtitleStreamIndex"/>/
/// <see cref="StreamInfo.SubtitleDeliveryMethod"/>/<see cref="StreamInfo.SubtitleFormat"/> (only when a
/// subtitle was selected), <see cref="StreamInfo.DeviceProfile"/>, <see cref="StreamInfo.MediaSource"/>,
/// <see cref="StreamInfo.ItemId"/>.
/// </remarks>
/// <remarks>
/// Fields deliberately left at their default (no v2 equivalent, or caller-supplied separately, never
/// guessed here): there is no <c>StreamInfo.VideoStreamIndex</c> property to fill at all - legacy
/// StreamInfo has never tracked one explicitly, it implies a single video stream via
/// <see cref="StreamInfo.MediaSource"/>'s own <c>VideoStream</c>. <see cref="PlaybackExecutionPlan.VideoRange"/>
/// is NOT projected onto <see cref="StreamInfo.TargetVideoRangeType"/>: that getter reads a
/// <c>"rangetype"</c> <see cref="StreamInfo.StreamOptions"/> entry legacy's own <c>StreamBuilder</c>
/// populates from <c>CodecProfile.ApplyConditions</c> as a comma-joined *acceptable-values* list, not
/// a single resolved target - the PR111e-documented <c>Enum.TryParse</c> bitwise-OR artifact behind
/// legacy's own HLG-vs-real-target bug (see <c>OracleCaseFixtures.ApprovedDivergences</c>'s Dolby
/// Vision entry) lives exactly there. Reproducing that mechanism would mean deliberately reproducing
/// the bug it causes; the plan's own <see cref="PlaybackExecutionPlan.VideoRange"/> remains the
/// correct, single, already-resolved target until the native pipeline consumes it directly.
/// <see cref="StreamInfo.TranscodeReasons"/>
/// (v2's reasoning is a <c>ReasonNode</c> tree, not a <c>TranscodeReason</c> flag set - reconstructing
/// one would be a re-decision, not a projection; this field is informational/telemetry only, not
/// required by the execution machinery itself). <see cref="StreamInfo.StartPositionTicks"/>,
/// <see cref="StreamInfo.CopyTimestamps"/>, <see cref="StreamInfo.RequireAvc"/>,
/// <see cref="StreamInfo.RequireNonAnamorphic"/>, <see cref="StreamInfo.EnableMpegtsM2TsMode"/>,
/// <see cref="StreamInfo.EnableSubtitlesInManifest"/>, <see cref="StreamInfo.TranscodingMaxAudioChannels"/>,
/// <see cref="StreamInfo.AudioSampleRate"/>, <see cref="StreamInfo.MaxFramerate"/>,
/// <see cref="StreamInfo.RunTimeTicks"/>, <see cref="StreamInfo.TranscodeSeekInfo"/>,
/// <see cref="StreamInfo.EstimateContentLength"/>, <see cref="StreamInfo.EnableAudioVbrEncoding"/>,
/// <see cref="StreamInfo.AlwaysBurnInSubtitleWhenTranscoding"/>, <see cref="StreamInfo.SegmentLength"/>/
/// <see cref="StreamInfo.MinSegments"/> are legacy request/session/tuning knobs
/// <see cref="PlaybackExecutionPlan"/> carries no equivalent of (they come from the original client
/// request or server configuration, not from the v2 decision) - left at their type defaults; a caller
/// that needs one set explicitly does so on the returned <see cref="StreamInfo"/> after this call.
/// </remarks>
public static class PlaybackExecutionPlanAdapter
{
    /// <summary>
    /// Fills a legacy <see cref="StreamInfo"/> from a v2 execution plan.
    /// </summary>
    /// <param name="plan">The execution plan to adapt.</param>
    /// <param name="mediaSource">
    /// The legacy media source matching <see cref="PlaybackExecutionPlan.SourceId"/>. The caller is
    /// responsible for resolving it (for example via <c>IMediaSourceManager</c>) - this adapter never
    /// looks one up, and validates that its <see cref="MediaSourceInfo.Id"/> actually matches, so the
    /// source v2 selected is never silently substituted.
    /// </param>
    /// <param name="deviceProfile">The legacy device profile the resulting stream is built for.</param>
    /// <param name="itemId">The library item id the stream belongs to.</param>
    /// <param name="deviceId">The requesting device id, if known.</param>
    /// <param name="deviceProfileId">The requesting device profile id, if known.</param>
    /// <param name="playSessionId">The play session id this stream is tied to, if known.</param>
    /// <returns>A <see cref="StreamInfo"/> carrying the plan's decision, ready for the existing HLS/ffmpeg execution machinery.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="plan"/>, <paramref name="mediaSource"/>, or <paramref name="deviceProfile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mediaSource"/>'s id does not match <see cref="PlaybackExecutionPlan.SourceId"/>.</exception>
    public static StreamInfo ToStreamInfo(
        PlaybackExecutionPlan plan,
        MediaSourceInfo mediaSource,
        DeviceProfile deviceProfile,
        Guid itemId,
        string? deviceId = null,
        string? deviceProfileId = null,
        string? playSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentNullException.ThrowIfNull(deviceProfile);

        if (!string.Equals(mediaSource.Id, plan.SourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"mediaSource.Id ('{mediaSource.Id}') does not match plan.SourceId ('{plan.SourceId}'): " +
                "the adapter must never substitute a different source than the one v2 selected.",
                nameof(mediaSource));
        }

        var streamInfo = new StreamInfo
        {
            ItemId = itemId,
            PlayMethod = ToPlayMethod(plan.Method),
            MediaType = plan.VideoStreamIndex is not null ? DlnaProfileType.Video : DlnaProfileType.Audio,
            Container = plan.Container,
            SubProtocol = plan.Protocol == DomainStreamingProtocol.Hls ? MediaStreamProtocol.hls : MediaStreamProtocol.http,
            AudioStreamIndex = plan.AudioStreamIndex,
            VideoCodecs = plan.VideoCodec is null ? [] : [plan.VideoCodec],
            AudioCodecs = plan.AudioCodec is null ? [] : [plan.AudioCodec],
            VideoBitrate = plan.VideoBitrate,
            AudioBitrate = plan.AudioBitrate,
            GlobalMaxAudioChannels = plan.AudioChannels,
            DeviceProfile = deviceProfile,
            DeviceId = deviceId,
            DeviceProfileId = deviceProfileId,
            PlaySessionId = playSessionId,
            MediaSource = mediaSource,
        };

        if (plan.Resolution is not null)
        {
            streamInfo.MaxWidth = plan.Resolution.Width;
            streamInfo.MaxHeight = plan.Resolution.Height;
        }

        if (plan.SubtitleStreamIndex is int subtitleIndex)
        {
            streamInfo.SubtitleStreamIndex = subtitleIndex;
            streamInfo.SubtitleDeliveryMethod = ToLegacySubtitleDeliveryMethod(plan.SubtitleDelivery);
            streamInfo.SubtitleFormat = plan.SubtitleFormat;
        }

        return streamInfo;
    }

    private static PlayMethod ToPlayMethod(DomainPlaybackMethod method) => method switch
    {
        DomainPlaybackMethod.DirectPlay => PlayMethod.DirectPlay,
        DomainPlaybackMethod.Remux => PlayMethod.DirectStream,
        DomainPlaybackMethod.Transcode => PlayMethod.Transcode,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unhandled playback method."),
    };

    private static LegacySubtitleDeliveryMethod ToLegacySubtitleDeliveryMethod(DomainSubtitleDeliveryMethod? method) => method switch
    {
        DomainSubtitleDeliveryMethod.Embed => LegacySubtitleDeliveryMethod.Embed,
        DomainSubtitleDeliveryMethod.External => LegacySubtitleDeliveryMethod.External,
        DomainSubtitleDeliveryMethod.Burn => LegacySubtitleDeliveryMethod.Encode,
        DomainSubtitleDeliveryMethod.Hls => LegacySubtitleDeliveryMethod.Hls,
        _ => LegacySubtitleDeliveryMethod.Encode,
    };
}
