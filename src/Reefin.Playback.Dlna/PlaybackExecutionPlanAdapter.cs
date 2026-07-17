using System;
using System.Globalization;
using System.Linq;
using Reefin.Data.Enums;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.Session;
using Reefin.Playback.Execution;
using DomainPlaybackMethod = Reefin.Playback.Decision.PlaybackMethod;
using DomainStreamingProtocol = Reefin.Playback.Decision.StreamingProtocol;
using DomainSubtitleDeliveryMethod = Reefin.Playback.Decision.SubtitleDeliveryMethod;
using DomainTransformKind = Reefin.Playback.Decision.TransformKind;
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
/// defaulted from the caller-supplied media source/device profile. <see cref="PlaybackExecutionContext"/>
/// (plus the media source/device profile arguments below, see PR115b's design doc
/// <c>docs/pr115-design-canary-execution.md</c> §3 for why those two are resolved by the adapter rather
/// than carried in the context) are supplied by the caller because <see cref="PlaybackExecutionPlan"/>
/// deliberately carries none of them: it names a source by id only (RFC PR91 §8: the domain performs no
/// I/O and holds no full legacy DTOs), and <see cref="StreamInfo"/> requires the caller's own resolved
/// objects (a <see cref="MediaSourceInfo"/> looked up by that id, a legacy <see cref="DeviceProfile"/>)
/// to do anything useful downstream (URL building, subtitle profile resolution, encoding param
/// derivation).
/// </remarks>
/// <remarks>
/// Fields filled - three categories (PR115b's design doc §3), on top of the executable fields PR114a
/// already filled: (§3.A, from <see cref="PlaybackExecutionContext"/>, never derived)
/// <see cref="StreamInfo.PlaySessionId"/>, <see cref="StreamInfo.StartPositionTicks"/>,
/// <see cref="StreamInfo.AlwaysBurnInSubtitleWhenTranscoding"/>, <see cref="StreamInfo.ItemId"/>,
/// <see cref="StreamInfo.DeviceId"/>, <see cref="StreamInfo.DeviceProfileId"/>. (§3.B, read directly off
/// the media source's selected streams at the indexes <see cref="PlaybackExecutionPlan.VideoStreamIndex"/>/
/// <see cref="PlaybackExecutionPlan.AudioStreamIndex"/> already name - never a new stream selection)
/// <see cref="StreamInfo.RunTimeTicks"/>; for <see cref="StreamInfo.PlayMethod"/> other than
/// <see cref="PlayMethod.DirectPlay"/> (legacy's own <c>BuildStreamVideoItem</c> gate):
/// <see cref="StreamInfo.MaxFramerate"/> and the source-video-codec-qualified <c>level</c>/<c>videobitdepth</c>/<c>profile</c>
/// <see cref="StreamInfo.StreamOptions"/> entries; additionally, when the selected audio stream is not
/// itself being transcoded (<see cref="PlaybackExecutionPlan.Transforms"/> excludes
/// <see cref="DomainTransformKind.TranscodeAudio"/>): <see cref="StreamInfo.AudioSampleRate"/> and the
/// source-audio-codec-qualified <c>profile</c>/<c>level</c> options, plus the (legacy quirk, reproduced
/// verbatim) source-VIDEO-codec-qualified <c>audiochannels</c> option. (§3.C, resolved from the device
/// profile, only for actual <see cref="PlayMethod.Transcode"/> - <see cref="StreamInfo.ToUrl"/>
/// serializes this whole block only then) the <see cref="TranscodingProfile"/> matched by
/// <c>(MediaType, EncodingContext.Streaming, Container)</c> populates <see cref="StreamInfo.CopyTimestamps"/>,
/// <see cref="StreamInfo.TranscodingMaxAudioChannels"/>, <see cref="StreamInfo.EstimateContentLength"/>,
/// <see cref="StreamInfo.EnableSubtitlesInManifest"/>, <see cref="StreamInfo.EnableMpegtsM2TsMode"/>,
/// <see cref="StreamInfo.EnableAudioVbrEncoding"/>, <see cref="StreamInfo.MinSegments"/>,
/// <see cref="StreamInfo.SegmentLength"/>, <see cref="StreamInfo.TranscodeSeekInfo"/> (via
/// <c>StreamBuilder.SetStreamInfoOptionsFromTranscodingProfile</c>, made <c>internal</c> for this
/// reuse); separately, <see cref="StreamInfo.RequireAvc"/>/<see cref="StreamInfo.RequireNonAnamorphic"/>
/// are resolved via <c>StreamBuilder.ApplyRequireAvcAndNonAnamorphic</c> - the invariant de parité
/// exécutable (design doc, top section) makes these two mandatory PR115b scope, not a later PR's
/// problem; see that method's remarks for why it deliberately reuses only the narrow IsAvc/IsAnamorphic
/// sub-part of legacy's condition engine rather than the whole thing.
/// </remarks>
/// <remarks>
/// Fields still deliberately left at their default (§3 does not cover them - see PR115b's design doc for
/// the reasoning): there is no <c>StreamInfo.VideoStreamIndex</c> property to fill at all - legacy
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
/// <see cref="StreamInfo.TranscodeReasons"/> (v2's reasoning is a <c>ReasonNode</c> tree, not a
/// <see cref="TranscodeReason"/> flag set - reconstructing one would be a re-decision, not a
/// projection; this field is informational/telemetry only - <c>StreamingHelpers</c>/<c>DynamicHlsController</c>
/// never branch on it, they only surface it back to the client). <see cref="StreamInfo.VideoCodecs"/>/
/// <see cref="StreamInfo.AudioCodecs"/> stay single-element (the plan's one target codec, not legacy's
/// full declared-candidate CSV list; PR114a's own design, unchanged by PR115b) - see the ToUrl parity
/// gate (<c>ExecutionPlanParityTests</c>) for the accepted, execution-neutral consequence on the
/// <c>VideoCodec</c>/<c>AudioCodec</c> query keys' literal value (still a single, correct winner either
/// way) and its documented residual case.
/// </remarks>
public static class PlaybackExecutionPlanAdapter
{
    /// <summary>
    /// Fills a legacy <see cref="StreamInfo"/> from a v2 execution plan.
    /// </summary>
    /// <param name="plan">The execution plan to adapt.</param>
    /// <param name="context">The request-scoped facts the plan itself never carries (PR115b, §3.A).</param>
    /// <param name="mediaSource">
    /// The legacy media source matching <see cref="PlaybackExecutionPlan.SourceId"/>. The caller is
    /// responsible for resolving it (for example via <c>IMediaSourceManager</c>) - this adapter never
    /// looks one up, and validates that its <see cref="MediaSourceInfo.Id"/> actually matches, so the
    /// source v2 selected is never silently substituted. Also the source of the §3.B source-scoped
    /// facts (read directly off its selected streams, never a new stream selection).
    /// </param>
    /// <param name="deviceProfile">
    /// The legacy device profile the resulting stream is built for. Also the source of the §3.C
    /// device-profile-scoped facts (the matched <see cref="TranscodingProfile"/>'s knobs,
    /// <see cref="StreamInfo.RequireAvc"/>/<see cref="StreamInfo.RequireNonAnamorphic"/>).
    /// </param>
    /// <returns>A <see cref="StreamInfo"/> carrying the plan's decision, ready for the existing HLS/ffmpeg execution machinery.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="plan"/>, <paramref name="context"/>, <paramref name="mediaSource"/>, or <paramref name="deviceProfile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mediaSource"/>'s id does not match <see cref="PlaybackExecutionPlan.SourceId"/>.</exception>
    public static StreamInfo ToStreamInfo(
        PlaybackExecutionPlan plan,
        PlaybackExecutionContext context,
        MediaSourceInfo mediaSource,
        DeviceProfile deviceProfile)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
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
            // §3.A: request-scoped facts, carried verbatim from the context, never derived.
            ItemId = context.ItemId,
            DeviceId = context.DeviceId,
            DeviceProfileId = context.DeviceProfileId,
            PlaySessionId = context.PlaySessionId,
            StartPositionTicks = context.StartPositionTicks,
            AlwaysBurnInSubtitleWhenTranscoding = context.AlwaysBurnInSubtitleWhenTranscoding,

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
            MediaSource = mediaSource,

            // §3.B: source-scoped fact, read directly off mediaSource - not in ToUrl, but kept for
            // StreamInfo value fidelity (design doc §3.B).
            RunTimeTicks = mediaSource.RunTimeTicks,
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

        var videoStream = plan.VideoStreamIndex is int videoIndex ? mediaSource.GetMediaStream(MediaStreamType.Video, videoIndex) : null;
        var audioStream = plan.AudioStreamIndex is int audioIndex ? mediaSource.GetMediaStream(MediaStreamType.Audio, audioIndex) : null;
        var useSubContainer = streamInfo.SubProtocol == MediaStreamProtocol.hls;

        ApplySourceScopedFields(streamInfo, plan, videoStream, audioStream);

        if (streamInfo.PlayMethod == PlayMethod.Transcode)
        {
            ApplyDeviceProfileScopedFields(streamInfo, plan, deviceProfile, mediaSource, videoStream, useSubContainer);
        }

        return streamInfo;
    }

    /// <summary>
    /// §3.B: source-scoped facts <c>StreamBuilder.BuildStreamVideoItem</c> reads off the selected video
    /// (and, when it is not itself being transcoded, audio) stream - never a new stream selection, the
    /// plan already names the indexes. Legacy only reaches this code for <see cref="PlayMethod.DirectStream"/>/
    /// <see cref="PlayMethod.Transcode"/> (never pure <see cref="PlayMethod.DirectPlay"/>, which never
    /// calls <c>BuildStreamVideoItem</c> at all), mirrored here by the same gate.
    /// </summary>
    private static void ApplySourceScopedFields(StreamInfo streamInfo, PlaybackExecutionPlan plan, MediaStream? videoStream, MediaStream? audioStream)
    {
        if (videoStream is null || streamInfo.PlayMethod == PlayMethod.DirectPlay)
        {
            return;
        }

        streamInfo.MaxFramerate = videoStream.ReferenceFrameRate;
        var videoQualifier = videoStream.Codec;

        if (videoStream.Level is not null)
        {
            streamInfo.SetOption(videoQualifier, "level", videoStream.Level.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (videoStream.BitDepth is not null)
        {
            streamInfo.SetOption(videoQualifier, "videobitdepth", videoStream.BitDepth.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(videoStream.Profile))
        {
            streamInfo.SetOption(videoQualifier, "profile", videoStream.Profile.ToLowerInvariant());
        }

        if (audioStream is null || plan.Transforms.Contains(DomainTransformKind.TranscodeAudio))
        {
            return;
        }

        // Legacy's own "directAudioStream" branch (StreamBuilder.BuildStreamVideoItem): only reached
        // when the audio stream is actually copied, not re-encoded.
        streamInfo.AudioSampleRate = audioStream.SampleRate;

        // Legacy quirk, reproduced verbatim: this "audiochannels" option is keyed by the VIDEO codec
        // (the "qualifier" local variable is never reassigned between the video and audio blocks in
        // StreamBuilder.BuildStreamVideoItem), not the audio codec.
        streamInfo.SetOption(videoQualifier, "audiochannels", audioStream.Channels?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        if (!string.IsNullOrEmpty(audioStream.Profile))
        {
            streamInfo.SetOption(audioStream.Codec, "profile", audioStream.Profile.ToLowerInvariant());
        }

        if (audioStream.Level.HasValue && audioStream.Level.Value != 0)
        {
            streamInfo.SetOption(audioStream.Codec, "level", audioStream.Level.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// §3.C: device-profile-scoped facts, resolved only for actual <see cref="PlayMethod.Transcode"/> -
    /// <see cref="StreamInfo.ToUrl"/> serializes this whole block (the matched <see cref="TranscodingProfile"/>'s
    /// nine knobs, plus <see cref="StreamInfo.RequireAvc"/>/<see cref="StreamInfo.RequireNonAnamorphic"/>)
    /// only when <c>!IsDirectStream</c>, i.e. only for Transcode.
    /// </summary>
    private static void ApplyDeviceProfileScopedFields(
        StreamInfo streamInfo,
        PlaybackExecutionPlan plan,
        DeviceProfile deviceProfile,
        MediaSourceInfo mediaSource,
        MediaStream? videoStream,
        bool useSubContainer)
    {
        // Matching key kept deliberately simple (design doc §3.C): (MediaType, Streaming, Container).
        // Legacy's own GetVideoTranscodeProfile ranks candidates by codec-copy-compatibility, but any
        // profile legacy could have picked necessarily has this same Container (StreamBuilder sets
        // playlistItem.Container from the matched profile's own Container) - and plan.Container is
        // already proven equal to legacy's for every Equivalent oracle case (ExecutionPlanParityTests).
        var transcodingProfile = deviceProfile.TranscodingProfiles.FirstOrDefault(p =>
            p.Type == streamInfo.MediaType &&
            p.Context == EncodingContext.Streaming &&
            string.Equals(p.Container, plan.Container, StringComparison.OrdinalIgnoreCase));

        if (transcodingProfile is not null)
        {
            StreamBuilder.SetStreamInfoOptionsFromTranscodingProfile(mediaSource, streamInfo, transcodingProfile);
        }

        StreamBuilder.ApplyRequireAvcAndNonAnamorphic(deviceProfile, streamInfo, mediaSource, videoStream, streamInfo.VideoCodecs, plan.Container, useSubContainer);
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
