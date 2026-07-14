using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Engine;

/// <summary>
/// The v2 playback decision engine, phase 2: everything phase 1 had (simple audio, video direct
/// play, remux, source/stream selection), plus audio/video transcoding, subtitle handling
/// (embed/external/burn-in), bitrate/resolution limits, codec profile/level/bit-depth checks, HDR
/// tonemapping (<see cref="VideoStreamSnapshot.VideoRange"/>), and audio channel downmix. All name-only,
/// case-insensitive codec/format matching, same as phase 1.
/// </summary>
/// <remarks>
/// PR102: when transcoding, the target container/codecs now come from the first
/// <see cref="PlaybackOutputProfile"/> in <see cref="ClientCapabilities.OutputProfiles"/> matching
/// the request's <see cref="MediaKind"/> (client preference order, not a hardcoded server
/// preference) - see <see cref="LegacyFallbackVideoCodec"/> for the named fallback used when the
/// client declares none.
/// </remarks>
/// <remarks>
/// PR102b: direct play now requires a single <see cref="DecodeProfile"/> to accept the exact
/// container + video codec + audio codec (+ <see cref="MediaKind"/>) combination
/// (<see cref="DirectPlayProfileMatches"/>), rather than independent membership in aggregate
/// per-axis sets. Codec-level limits (resolution/level/bit depth/bitrate/etc.) are read from the
/// matched codec's own <see cref="VideoCodecCapability"/>/<see cref="AudioCodecCapability"/>
/// instead of a device-wide ceiling. <see cref="OutputSpec.Protocol"/> is populated from the
/// <see cref="PlaybackOutputProfile"/> actually used, defaulting to
/// <see cref="StreamingProtocol.Http"/> otherwise.
/// </remarks>
/// <remarks>
/// PR103: an explicit <see cref="PlaybackRequestContext.MediaSourceId"/> now scopes source
/// selection to that one source - never falling back to a different one - and produces
/// <see cref="ReasonCode.RequestedSourceNotFound"/> when no source has that id (see
/// <see cref="Decide"/>). Subtitle auto-selection (no explicit
/// <see cref="PlaybackConstraints.PreferredSubtitleStreamIndex"/>) now reproduces legacy's
/// default/forced selection (see <see cref="SelectDefaultSubtitle"/>). Transcode target output
/// profiles are now chosen as the first client-declared <see cref="PlaybackOutputProfile"/> that is
/// actually viable (see <see cref="IsOutputProfileViable"/>), not just the first of the right
/// <see cref="MediaKind"/>. <see cref="OutputSpec.TotalBitrate"/>/<see cref="OutputSpec.VideoBitrate"/>/
/// <see cref="OutputSpec.AudioBitrate"/> replace the previously-always-null <c>OutputSpec.Bitrate</c>,
/// cascading <see cref="PlaybackConstraints.MaxBitrate"/>, the output profile's per-axis ceilings, and
/// the target codec's own per-codec ceiling; the same ceilings now also gate Direct Play, forcing a
/// transcode when exceeded.
/// </remarks>
public sealed class PlaybackEngine : IPlaybackEngine
{
    /// <summary>
    /// The version of the decision engine implemented by this type.
    /// </summary>
    public const int EngineVersion = 5;

    /// <summary>
    /// The transcoding target video codec used when the client declares no
    /// <see cref="PlaybackOutputProfile"/> matching the requested media kind. Mirrors the
    /// pre-PR102 engine's hardcoded default, kept as a named fallback (not a magic literal) rather
    /// than removed, so a client that predates output-profile declarations keeps behaving exactly
    /// as it did in engine v2.0/v2.1.
    /// </summary>
    private const string LegacyFallbackVideoCodec = "h264";

    /// <summary>
    /// The transcoding target audio codec used when the client declares no
    /// <see cref="PlaybackOutputProfile"/> matching the requested media kind. See
    /// <see cref="LegacyFallbackVideoCodec"/>.
    /// </summary>
    private const string LegacyFallbackAudioCodec = "aac";

    private static readonly IReadOnlyCollection<string> TextBasedSubtitleFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "srt", "subrip", "ass", "ssa", "vtt", "webvtt", "ttml", "sami", "smi" };

    /// <inheritdoc />
    public PlaybackDecision Decide(
        PlaybackRequestContext context,
        ClientCapabilities capabilities,
        IReadOnlyList<MediaSourceSnapshot> sources,
        PlaybackConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(constraints);

        // PR103: a request that names a specific MediaSourceId is scoped to that source only - the
        // engine never silently substitutes a different one, even if another source on the item
        // would have produced a viable plan. This is a hard filter before candidate-building, not a
        // preference: "requested source absent" (RequestedSourceNotFound) and "requested source
        // present but not viable" (the usual per-axis reasons, scoped to that one source) are kept
        // clearly distinct.
        var candidateSources = sources;
        if (context.MediaSourceId is not null)
        {
            var requested = sources.Where(s => string.Equals(s.MediaSourceId, context.MediaSourceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (requested.Count == 0)
            {
                var notFoundReasoning = new ReasonNode(
                    ReasonCode.NoViablePlan,
                    ReasonOutcome.Rejected,
                    ReasonSubject.Method(),
                    null,
                    [ReasonNode.Leaf(ReasonCode.RequestedSourceNotFound, ReasonOutcome.Rejected, ReasonSubject.Source(context.MediaSourceId))]);
                return PlaybackDecision.NotViable(PlaybackMethod.Transcode, notFoundReasoning, EngineVersion);
            }

            candidateSources = requested;
        }

        var candidates = new List<SourceCandidate>(candidateSources.Count);
        foreach (var source in candidateSources)
        {
            candidates.Add(BuildForSource(context, capabilities, source, constraints));
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Method == PlaybackMethod.DirectPlay && candidate.Decision is not null)
            {
                return candidate.Decision;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Method == PlaybackMethod.Remux && candidate.Decision is not null)
            {
                return candidate.Decision;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Method == PlaybackMethod.Transcode && candidate.Decision is not null)
            {
                return candidate.Decision;
            }
        }

        IReadOnlyList<ReasonNode> firstBlockingReasons = candidates.Count > 0 ? candidates[0].BlockingReasons : [];
        var reasoning = new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, firstBlockingReasons);
        return PlaybackDecision.NotViable(PlaybackMethod.Transcode, reasoning, EngineVersion);
    }

    private static SourceCandidate BuildForSource(
        PlaybackRequestContext context,
        ClientCapabilities capabilities,
        MediaSourceSnapshot source,
        PlaybackConstraints constraints)
    {
        var selectedVideo = context.MediaKind == MediaKind.Video && source.VideoStreams.Count > 0 ? source.VideoStreams[0] : null;
        var selectedAudio = SelectAudio(source, constraints);
        var selectedSubtitle = SelectSubtitle(source, constraints, selectedAudio?.Language);

        // A source with nothing to select at all (no video, no audio - regardless of MediaKind)
        // has no stream for any method to play, copy, or transcode; matches phase 1, which also
        // could never satisfy the videoOk/audioOk gates in this case and fell through to
        // NotViable with no tripped reasons to report.
        if (selectedVideo is null && selectedAudio is null)
        {
            return SourceCandidate.ForNotViable([]);
        }

        // --- VIDEO ---
        var needVideoTranscode = false;
        var needTonemap = false;
        var needDownscale = false;
        Resolution? downscaleTarget = null;
        var videoReasons = new List<ReasonNode>();

        if (selectedVideo is not null)
        {
            var videoCap = capabilities.Decode.VideoCodecs.FirstOrDefault(c => EqualsIgnoreCase(c.Codec, selectedVideo.Codec));
            if (videoCap is null)
            {
                needVideoTranscode = true;
                videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoCodecNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
            }
            else
            {
                if (videoCap.Profiles.Count > 0 && (selectedVideo.Profile is null || !videoCap.Profiles.Contains(selectedVideo.Profile, StringComparer.OrdinalIgnoreCase)))
                {
                    needVideoTranscode = true;
                    videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoProfileNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
                }

                if (videoCap.MaxLevel is not null && selectedVideo.Level is not null && selectedVideo.Level > videoCap.MaxLevel)
                {
                    needVideoTranscode = true;
                    videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoLevelNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
                }

                if (videoCap.MaxBitDepth is not null && selectedVideo.BitDepth is not null && selectedVideo.BitDepth > videoCap.MaxBitDepth)
                {
                    needVideoTranscode = true;
                    videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoBitDepthNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
                }

                if (videoCap.VideoRangeTypes.Count > 0 && selectedVideo.VideoRange is not null && !videoCap.VideoRangeTypes.Contains(selectedVideo.VideoRange, StringComparer.OrdinalIgnoreCase))
                {
                    needVideoTranscode = true;
                    needTonemap = true;
                    videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoRangeTypeNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
                }

                // PR102b: resolution/bitrate limits are this codec's own (VideoCodecCapability),
                // not a device-wide ceiling - an H.264-limited-to-1080p client is not thereby
                // limited to 1080p for HEVC. Checked only when the codec itself is decodable
                // (videoCap found); an undecodable codec already forces a transcode above, and has
                // no per-codec limit to check against.
                if (videoCap.MaxResolution is not null && selectedVideo.Width is not null && selectedVideo.Height is not null
                    && (selectedVideo.Width > videoCap.MaxResolution.Width || selectedVideo.Height > videoCap.MaxResolution.Height))
                {
                    needVideoTranscode = true;
                    needDownscale = true;
                    downscaleTarget = videoCap.MaxResolution;
                    videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoResolutionNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
                }

                if (videoCap.MaxBitrate is not null && selectedVideo.Bitrate is not null && selectedVideo.Bitrate > videoCap.MaxBitrate)
                {
                    needVideoTranscode = true;
                    videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoBitrateNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
                }
            }
        }

        // --- AUDIO ---
        var needAudioTranscode = false;
        var needDownmix = false;
        int? effMaxChannels = null;
        var audioReasons = new List<ReasonNode>();

        if (selectedAudio is not null)
        {
            var audioCap = capabilities.Decode.AudioCodecs.FirstOrDefault(c => EqualsIgnoreCase(c.Codec, selectedAudio.Codec));
            if (audioCap is null)
            {
                needAudioTranscode = true;
                audioReasons.Add(ReasonNode.Leaf(ReasonCode.AudioCodecNotSupported, ReasonOutcome.Rejected, ReasonSubject.AudioStream(selectedAudio.Index)));
            }
            else
            {
                effMaxChannels = MinIgnoringNulls(audioCap.MaxChannels, constraints.MaxAudioChannels);
                if (effMaxChannels is not null && selectedAudio.Channels is not null && selectedAudio.Channels > effMaxChannels)
                {
                    needDownmix = true;
                    needAudioTranscode = true;
                    audioReasons.Add(ReasonNode.Leaf(ReasonCode.AudioChannelsNotSupported, ReasonOutcome.Rejected, ReasonSubject.AudioStream(selectedAudio.Index)));
                }

                if (audioCap.MaxSampleRate is not null && selectedAudio.SampleRate is not null && selectedAudio.SampleRate > audioCap.MaxSampleRate)
                {
                    needAudioTranscode = true;
                    audioReasons.Add(ReasonNode.Leaf(ReasonCode.AudioSampleRateNotSupported, ReasonOutcome.Rejected, ReasonSubject.AudioStream(selectedAudio.Index)));
                }

                // PR103: per-codec audio bitrate ceiling (AudioCodecCapability.MaxBitrate), the
                // audio counterpart of the video check above - previously missing entirely, so an
                // audio stream that decoded fine codec-wise but exceeded the client's declared
                // bitrate ceiling for that codec was never caught.
                if (audioCap.MaxBitrate is not null && selectedAudio.Bitrate is not null && selectedAudio.Bitrate > audioCap.MaxBitrate)
                {
                    needAudioTranscode = true;
                    audioReasons.Add(ReasonNode.Leaf(ReasonCode.AudioBitrateNotSupported, ReasonOutcome.Rejected, ReasonSubject.AudioStream(selectedAudio.Index)));
                }
            }
        }

        // --- BITRATE CEILING (PR103) ---
        // PlaybackConstraints.MaxBitrate is a request-wide cap on the source's overall bitrate,
        // independent of per-codec decode limits: Direct Play and Remux both copy the source's
        // streams verbatim, so neither can honor a cap tighter than the source's own muxed bitrate -
        // only a transcode (re-encoding video, or audio if there is no video) can. Forcing
        // needVideoTranscode/needAudioTranscode here (rather than a separate flag) means the rest of
        // the pipeline - transforms, target codec/container selection, output shape - falls out of
        // the same logic paths already driven by those flags, with no special-casing.
        var needBitrateTranscode = constraints.MaxBitrate is not null && source.Bitrate is not null && source.Bitrate > constraints.MaxBitrate;
        var bitrateReasons = new List<ReasonNode>();
        if (needBitrateTranscode)
        {
            bitrateReasons.Add(ReasonNode.Leaf(ReasonCode.ContainerBitrateExceedsLimit, ReasonOutcome.Rejected, ReasonSubject.Container()));

            if (selectedVideo is not null)
            {
                needVideoTranscode = true;
            }
            else if (selectedAudio is not null)
            {
                needAudioTranscode = true;
            }
        }

        // --- SUBTITLE ---
        var needBurnIn = false;
        SubtitleDeliveryMethod? subtitleDelivery = null;
        ReasonNode? subtitleCodecNotSupportedReason = null;

        if (selectedSubtitle is not null)
        {
            var subtitleCap = capabilities.Decode.SubtitleDelivery.FirstOrDefault(c => EqualsIgnoreCase(c.Format, selectedSubtitle.Format));
            if (subtitleCap is not null)
            {
                subtitleDelivery = subtitleCap.Method;
                if (subtitleDelivery == SubtitleDeliveryMethod.Burn)
                {
                    needBurnIn = true;
                }
            }
            else
            {
                subtitleDelivery = SubtitleDeliveryMethod.Burn;
                needBurnIn = true;
                subtitleCodecNotSupportedReason = ReasonNode.Leaf(ReasonCode.SubtitleCodecNotSupported, ReasonOutcome.Rejected, ReasonSubject.Subtitle(selectedSubtitle.Index));
            }

            if (!needBurnIn && needVideoTranscode && constraints.AlwaysBurnInSubtitleWhenTranscoding && IsTextBasedSubtitle(selectedSubtitle.Format))
            {
                needBurnIn = true;
                subtitleDelivery = SubtitleDeliveryMethod.Burn;
            }

            // Burning in requires a video stream to burn into; without one (audio-only playback),
            // burn-in is meaningless, so don't let it force a nonsensical video transcode need.
            if (needBurnIn && selectedVideo is null)
            {
                needBurnIn = false;
            }
        }

        if (needBurnIn)
        {
            needVideoTranscode = true;
        }

        // --- METHOD ---
        var wantsTranscode = needVideoTranscode || needAudioTranscode || needBurnIn;

        // PR102b problem #1: direct play requires a single DecodeProfile to accept this exact
        // container + video codec + audio codec (+ MediaKind) combination, not independent
        // membership in aggregate per-axis sets - the latter accepts combinations the client never
        // declared (e.g. MP4/H.264 and WebM/VP9 declared separately would wrongly also accept
        // MP4/VP9).
        var hasDirectPlayMatch = capabilities.Decode.DirectPlayProfiles.Any(p =>
            DirectPlayProfileMatches(p, context.MediaKind, source.Container, selectedVideo?.Codec, selectedAudio?.Codec));

        var neededMethod = wantsTranscode
            ? PlaybackMethod.Transcode
            : hasDirectPlayMatch
                ? PlaybackMethod.DirectPlay
                : PlaybackMethod.Remux;

        // The containers a DecodeProfile of this MediaKind declares together with these exact
        // (already-decodable, no-transcode-needed) codecs - used both to gate Remux (there must be
        // somewhere to remux into) and, below, to pick the remux target container.
        var remuxContainers = AcceptableContainers(capabilities, context.MediaKind, selectedVideo?.Codec, selectedAudio?.Codec);

        var allowed = neededMethod switch
        {
            PlaybackMethod.Transcode => constraints.AllowTranscoding && source.SupportsTranscoding,
            PlaybackMethod.Remux => constraints.AllowDirectStream
                && source.SupportsDirectStream
                && (selectedVideo is null || constraints.AllowVideoStreamCopy)
                && constraints.AllowAudioStreamCopy
                && remuxContainers.Count > 0,
            PlaybackMethod.DirectPlay => constraints.AllowDirectPlay && source.SupportsDirectPlay,
            _ => false,
        };

        if (!allowed)
        {
            var blockingReasons = new List<ReasonNode>();
            blockingReasons.AddRange(videoReasons);
            blockingReasons.AddRange(audioReasons);
            blockingReasons.AddRange(bitrateReasons);
            if (subtitleCodecNotSupportedReason is not null)
            {
                blockingReasons.Add(subtitleCodecNotSupportedReason);
            }

            return SourceCandidate.ForNotViable(blockingReasons);
        }

        // --- OUTPUT PROFILE (PR102, viability PR103) ---
        // When transcoding, the target format is the first client-declared PlaybackOutputProfile
        // matching this request's MediaKind AND actually viable (PR103: see IsOutputProfileViable) -
        // the client's own preference order, not a hardcoded server preference, and not merely the
        // first of the right MediaKind regardless of whether it could ever be produced. A client
        // declaring no matching-and-viable profile falls back to the named legacy default
        // (LegacyFallbackVideoCodec/LegacyFallbackAudioCodec, container chosen from decode
        // containers exactly as the pre-PR102 engine did).
        var matchingOutputProfile = neededMethod == PlaybackMethod.Transcode
            ? capabilities.OutputProfiles.FirstOrDefault(p =>
                p.Type == context.MediaKind && IsOutputProfileViable(p, capabilities.Decode, needVideoTranscode, needAudioTranscode))
            : null;
        var usedOutputProfileFallback = false;

        // --- CONTAINER ---
        string targetContainer;
        if (neededMethod == PlaybackMethod.DirectPlay)
        {
            targetContainer = source.Container;
        }
        else if (matchingOutputProfile is not null)
        {
            targetContainer = matchingOutputProfile.Container;
        }
        else if (neededMethod == PlaybackMethod.Remux)
        {
            // Remux never had an OutputProfile concept to begin with (it copies streams into a
            // container the client already decodes without transcoding them), and PR102b scopes
            // the candidate containers to ones actually declared together with these codecs
            // (remuxContainers), not just any container the client accepts for something else.
            targetContainer = SelectTargetContainer(remuxContainers, source.Container);
        }
        else
        {
            // Transcode with no matching client-declared OutputProfile: named legacy default
            // container selection. Scoped to this MediaKind but deliberately not to the fallback
            // target codecs (LegacyFallbackVideoCodec/LegacyFallbackAudioCodec, chosen below) -
            // those codecs may not themselves appear in any declared DecodeProfile at all (e.g. a
            // client whose only direct-play video codec is VP9 still needs *some* container
            // picked here), matching the pre-PR102b engine's laxness for this already-degraded
            // fallback path.
            var fallbackContainers = AcceptableContainers(capabilities, context.MediaKind, videoCodec: null, audioCodec: null);
            targetContainer = SelectTargetContainer(fallbackContainers, source.Container);
            usedOutputProfileFallback = true;
        }

        var containerChanged = !string.Equals(targetContainer, source.Container, StringComparison.OrdinalIgnoreCase);

        // --- TARGET CODECS ---
        string? targetVideoCodec = null;
        if (needVideoTranscode && selectedVideo is not null)
        {
            if (matchingOutputProfile is not null && matchingOutputProfile.VideoCodecs.Count > 0)
            {
                targetVideoCodec = matchingOutputProfile.VideoCodecs[0];
            }
            else
            {
                targetVideoCodec = LegacyFallbackVideoCodec;
                usedOutputProfileFallback = true;
            }
        }

        string? targetAudioCodec = null;
        if (needAudioTranscode && selectedAudio is not null)
        {
            if (matchingOutputProfile is not null && matchingOutputProfile.AudioCodecs.Count > 0)
            {
                targetAudioCodec = matchingOutputProfile.AudioCodecs[0];
            }
            else
            {
                targetAudioCodec = LegacyFallbackAudioCodec;
                usedOutputProfileFallback = true;
            }
        }

        // --- OUTPUT BITRATE CEILINGS (PR103) ---
        // Cascades, per axis, the used output profile's ceiling and the target codec's own per-codec
        // ceiling (the client's declared decode limit for whatever codec the server will actually
        // produce - not the source codec, which is being abandoned). Populated only for an axis
        // actually being transcoded: an unchanged (copied) stream carries no output ceiling of its
        // own, same convention as Resolution/VideoRange/AudioChannels above. TotalBitrate then
        // narrows the request's global PlaybackConstraints.MaxBitrate against the sum of the two
        // per-axis ceilings when both are known - never fabricating a bound out of an unknown one.
        var targetVideoCap = targetVideoCodec is not null
            ? capabilities.Decode.VideoCodecs.FirstOrDefault(c => EqualsIgnoreCase(c.Codec, targetVideoCodec))
            : null;
        var targetAudioCap = targetAudioCodec is not null
            ? capabilities.Decode.AudioCodecs.FirstOrDefault(c => EqualsIgnoreCase(c.Codec, targetAudioCodec))
            : null;

        var outputVideoBitrate = needVideoTranscode
            ? MinIgnoringNulls(matchingOutputProfile?.MaxVideoBitrate, targetVideoCap?.MaxBitrate)
            : null;
        var outputAudioBitrate = needAudioTranscode
            ? MinIgnoringNulls(matchingOutputProfile?.MaxAudioBitrate, targetAudioCap?.MaxBitrate)
            : null;
        var outputTotalBitrate = neededMethod == PlaybackMethod.Transcode
            ? MinIgnoringNulls(constraints.MaxBitrate, SumIgnoringNulls(outputVideoBitrate, outputAudioBitrate))
            : null;

        // --- TRANSFORMS ---
        var transforms = new List<TransformKind>();
        if (neededMethod != PlaybackMethod.DirectPlay)
        {
            if (selectedVideo is not null)
            {
                transforms.Add(needVideoTranscode ? TransformKind.TranscodeVideo : TransformKind.CopyVideo);
                if (needTonemap)
                {
                    transforms.Add(TransformKind.Tonemap);
                }
            }

            if (selectedAudio is not null)
            {
                transforms.Add(needAudioTranscode ? TransformKind.TranscodeAudio : TransformKind.CopyAudio);
                if (needDownmix)
                {
                    transforms.Add(TransformKind.Downmix);
                }
            }

            if (needBurnIn)
            {
                transforms.Add(TransformKind.BurnInSubtitle);
            }

            if (selectedSubtitle is not null && subtitleDelivery == SubtitleDeliveryMethod.External && !selectedSubtitle.IsExternal)
            {
                transforms.Add(TransformKind.ExtractSubtitle);
            }

            var hasCopy = transforms.Contains(TransformKind.CopyVideo) || transforms.Contains(TransformKind.CopyAudio);
            if (containerChanged && hasCopy)
            {
                transforms.Insert(0, TransformKind.RemuxContainer);
            }
        }

        // --- OUTPUT ---
        // PR102b: Protocol comes from the client-declared PlaybackOutputProfile actually used for
        // this decision - only ever non-null when transcoding to a matched profile. Direct Play,
        // Remux, and the no-matching-profile transcode fallback all carry no server-chosen delivery
        // protocol, so they use the neutral value (plain HTTP).
        var outputProtocol = matchingOutputProfile?.Protocol ?? StreamingProtocol.Http;

        var output = new OutputSpec(
            Container: targetContainer,
            VideoCodec: selectedVideo is not null ? (needVideoTranscode ? targetVideoCodec : selectedVideo.Codec) : null,
            AudioCodec: selectedAudio is not null ? (needAudioTranscode ? targetAudioCodec : selectedAudio.Codec) : null,
            Resolution: needVideoTranscode && needDownscale ? downscaleTarget : null,
            VideoRange: needVideoTranscode && needTonemap ? "SDR" : null,
            AudioChannels: needAudioTranscode
                ? MinIgnoringNulls(needDownmix ? effMaxChannels : selectedAudio?.Channels, matchingOutputProfile?.MaxAudioChannels)
                : null,
            TotalBitrate: outputTotalBitrate,
            VideoBitrate: outputVideoBitrate,
            AudioBitrate: outputAudioBitrate,
            Protocol: outputProtocol);

        // --- REASONING ---
        var children = new List<ReasonNode>();
        if (transforms.Contains(TransformKind.RemuxContainer))
        {
            children.Add(ReasonNode.Leaf(ReasonCode.ContainerNotSupported, ReasonOutcome.Rejected, ReasonSubject.Container()));
        }

        if (neededMethod == PlaybackMethod.Remux)
        {
            var streamCopyableSubject = selectedVideo is not null
                ? ReasonSubject.VideoStream(selectedVideo.Index)
                : ReasonSubject.AudioStream(selectedAudio!.Index);
            children.Add(ReasonNode.Leaf(ReasonCode.StreamCopyable, ReasonOutcome.Accepted, streamCopyableSubject));
        }

        children.AddRange(videoReasons);
        if (transforms.Contains(TransformKind.Tonemap))
        {
            children.Add(ReasonNode.Leaf(ReasonCode.TonemapRequired, ReasonOutcome.Chosen, ReasonSubject.VideoStream(selectedVideo!.Index)));
        }

        children.AddRange(audioReasons);
        if (transforms.Contains(TransformKind.Downmix))
        {
            children.Add(ReasonNode.Leaf(ReasonCode.DownmixRequired, ReasonOutcome.Chosen, ReasonSubject.AudioStream(selectedAudio!.Index)));
        }

        children.AddRange(bitrateReasons);

        if (subtitleCodecNotSupportedReason is not null)
        {
            children.Add(subtitleCodecNotSupportedReason);
        }

        if (transforms.Contains(TransformKind.BurnInSubtitle))
        {
            children.Add(ReasonNode.Leaf(ReasonCode.SubtitleBurnInRequired, ReasonOutcome.Chosen, ReasonSubject.Subtitle(selectedSubtitle!.Index)));
        }

        if (usedOutputProfileFallback)
        {
            children.Add(ReasonNode.Leaf(ReasonCode.OutputProfileFallbackUsed, ReasonOutcome.Chosen, ReasonSubject.Method()));
        }

        var reasoning = new ReasonNode(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method(), null, children);

        // --- SELECTED STREAMS ---
        var streams = new SelectedStreams(
            selectedVideo?.Index,
            selectedAudio?.Index,
            selectedSubtitle is not null ? new SelectedSubtitle(selectedSubtitle.Index, subtitleDelivery!.Value) : null);

        // --- DECISION ---
        var decision = neededMethod switch
        {
            PlaybackMethod.DirectPlay => PlaybackDecision.DirectPlay(source.MediaSourceId, streams, output, reasoning, EngineVersion),
            PlaybackMethod.Remux => PlaybackDecision.Remux(source.MediaSourceId, streams, output, transforms, reasoning, EngineVersion),
            PlaybackMethod.Transcode => PlaybackDecision.Transcode(source.MediaSourceId, streams, output, transforms, reasoning, EngineVersion),
            _ => throw new InvalidOperationException($"Unhandled playback method '{neededMethod}'."),
        };

        return neededMethod switch
        {
            PlaybackMethod.DirectPlay => SourceCandidate.ForDirectPlay(decision),
            PlaybackMethod.Remux => SourceCandidate.ForRemux(decision),
            PlaybackMethod.Transcode => SourceCandidate.ForTranscode(decision),
            _ => throw new InvalidOperationException($"Unhandled playback method '{neededMethod}'."),
        };
    }

    /// <summary>
    /// Selects the audio stream to act on. An explicit but invalid <see cref="PlaybackConstraints.PreferredAudioStreamIndex"/>
    /// (no stream on this source has that index) is characterized (PR103) to fall through to the
    /// computed default rather than yield no audio at all: this mirrors legacy's "remembered
    /// selection" validity check
    /// (<c>Reefin.Server.Core.Library.MediaSourceManager.SetDefaultAudioStreamIndex</c>,
    /// MediaSourceManager.cs:541-551 - <c>if (... i.Index == index) { ...return; }</c> else falls
    /// through to <c>MediaStreamSelector.GetDefaultAudioStreamIndex</c>), not a documented legacy
    /// bug: a stale/invalid explicit index degrades to "no preference" rather than "no audio".
    /// </summary>
    private static AudioStreamSnapshot? SelectAudio(MediaSourceSnapshot source, PlaybackConstraints constraints)
    {
        AudioStreamSnapshot? selected = null;
        if (constraints.PreferredAudioStreamIndex is int preferredIndex)
        {
            selected = source.AudioStreams.FirstOrDefault(a => a.Index == preferredIndex);
        }

        selected ??= source.AudioStreams.FirstOrDefault(a => a.IsDefault);
        selected ??= source.AudioStreams.Count > 0 ? source.AudioStreams[0] : null;

        return selected;
    }

    /// <summary>
    /// Selects the subtitle stream to act on, if any. An explicit <see cref="PlaybackConstraints.PreferredSubtitleStreamIndex"/>
    /// is used as-is, including when it names no real stream on this source (PR103 characterization:
    /// unlike <see cref="SelectAudio"/>, there is no "fall through to computed default" for an
    /// invalid subtitle index - this mirrors legacy's <c>StreamBuilder.BuildVideoItem</c>
    /// (StreamBuilder.cs:661), which uses <c>options.SubtitleStreamIndex</c> via <c>??</c> without
    /// revalidating it against the source's actual streams; a stale explicit index there also
    /// resolves to no real subtitle stream object downstream, the same practical outcome as the
    /// <see langword="null"/> this returns). With no explicit index, falls through to
    /// <see cref="SelectDefaultSubtitle"/>.
    /// </summary>
    private static SubtitleStreamSnapshot? SelectSubtitle(MediaSourceSnapshot source, PlaybackConstraints constraints, string? audioLanguage)
    {
        if (constraints.PreferredSubtitleStreamIndex is int preferredIndex)
        {
            return source.SubtitleStreams.FirstOrDefault(s => s.Index == preferredIndex);
        }

        return SelectDefaultSubtitle(source.SubtitleStreams, constraints.PreferredSubtitleLanguages ?? [], constraints.SubtitleMode, audioLanguage);
    }

    /// <summary>
    /// Reproduces <c>Reefin.Server.Core.Library.MediaStreamSelector.GetDefaultSubtitleStreamIndex</c>
    /// (MediaStreamSelector.cs:31-87) over <see cref="SubtitleStreamSnapshot"/> instead of the legacy
    /// <c>MediaStream</c> type (PR103): sorts candidates "Default &gt; No tag &gt; Forced" (external
    /// first as a tie-break), then applies the mode-specific filter. <see cref="SubtitlePlaybackMode.None"/>
    /// or no candidate streams both yield no selection.
    /// </summary>
    private static SubtitleStreamSnapshot? SelectDefaultSubtitle(
        IReadOnlyList<SubtitleStreamSnapshot> streams,
        IReadOnlyList<string> preferredLanguages,
        SubtitlePlaybackMode mode,
        string? audioLanguage)
    {
        if (mode == SubtitlePlaybackMode.None || streams.Count == 0)
        {
            return null;
        }

        var sorted = streams
            .OrderByDescending(s => s.IsExternal)
            .ThenByDescending(s => s.IsDefault)
            .ThenByDescending(s => !s.IsForced && MatchesPreferredLanguage(s.Language, preferredLanguages))
            .ThenByDescending(s => s.IsForced && MatchesPreferredLanguage(s.Language, preferredLanguages))
            .ThenByDescending(s => s.IsForced && IsLanguageUndefined(s.Language))
            .ThenByDescending(s => s.IsForced)
            .ToList();

        return mode switch
        {
            // "Load subtitles according to external, default and forced flags."
            SubtitlePlaybackMode.Default => sorted.FirstOrDefault(s => s.IsExternal || s.IsDefault || s.IsForced),

            SubtitlePlaybackMode.Smart => SelectSmart(sorted, preferredLanguages, audioLanguage),

            // "Always load (full/non-forced) subtitles of the user's preferred subtitle language if
            // possible, otherwise OnlyForced behaviour."
            SubtitlePlaybackMode.Always => sorted.FirstOrDefault(s => !s.IsForced && MatchesPreferredLanguage(s.Language, preferredLanguages))
                ?? OnlyForced(sorted, preferredLanguages).FirstOrDefault(),

            // "Load subtitles that are flagged forced of the user's preferred subtitle language or
            // with an undefined language."
            SubtitlePlaybackMode.OnlyForced => OnlyForced(sorted, preferredLanguages).FirstOrDefault(),

            _ => null,
        };
    }

    /// <summary>
    /// <see cref="SubtitlePlaybackMode.Smart"/>: "Only attempt to load subtitles if the audio
    /// language is not one of the user's preferred subtitle languages. If the audio language is one
    /// of the user's preferred subtitle languages, behave like OnlyForced".
    /// </summary>
    private static SubtitleStreamSnapshot? SelectSmart(IReadOnlyList<SubtitleStreamSnapshot> sorted, IReadOnlyList<string> preferredLanguages, string? audioLanguage)
    {
        var audioLanguageIsPreferred = audioLanguage is not null && preferredLanguages.Contains(audioLanguage, StringComparer.OrdinalIgnoreCase);
        if (!audioLanguageIsPreferred)
        {
            return sorted.FirstOrDefault(s => MatchesPreferredLanguage(s.Language, preferredLanguages));
        }

        return OnlyForced(sorted, preferredLanguages).FirstOrDefault();
    }

    private static List<SubtitleStreamSnapshot> OnlyForced(IEnumerable<SubtitleStreamSnapshot> sortedStreams, IReadOnlyList<string> preferredLanguages) =>
        sortedStreams
            .Where(s => s.IsForced && (MatchesPreferredLanguage(s.Language, preferredLanguages) || IsLanguageUndefined(s.Language)))
            .OrderByDescending(s => MatchesPreferredLanguage(s.Language, preferredLanguages))
            .ThenByDescending(s => IsLanguageUndefined(s.Language))
            .ToList();

    /// <summary>
    /// An empty <paramref name="preferredLanguages"/> is a wildcard - matches any language - mirroring
    /// <c>MediaStreamSelector.MatchesPreferredLanguage</c>.
    /// </summary>
    private static bool MatchesPreferredLanguage(string? language, IReadOnlyList<string> preferredLanguages) =>
        preferredLanguages.Count == 0 || (language is not null && preferredLanguages.Contains(language, StringComparer.OrdinalIgnoreCase));

    /// <summary>Mirrors <c>MediaStreamSelector.IsLanguageUndefined</c>.</summary>
    private static bool IsLanguageUndefined(string? language) =>
        string.IsNullOrEmpty(language) ||
        string.Equals(language, "und", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "undetermined", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "mul", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "zxx", StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsTextBasedSubtitle(string format) => TextBasedSubtitleFormats.Contains(format);

    private static int? MinIgnoringNulls(int? a, int? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return Math.Min(a.Value, b.Value);
    }

    private static int? SumIgnoringNulls(int? a, int? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return a.Value + b.Value;
    }

    /// <summary>
    /// Whether a client-declared <see cref="PlaybackOutputProfile"/> could actually be produced for
    /// this decision (PR103), as opposed to merely matching <see cref="MediaKind"/>: an empty
    /// <see cref="PlaybackOutputProfile.Container"/>, a protocol the client never declared support
    /// for (<see cref="IsProtocolViable"/>), or an empty target codec list on an axis that must
    /// actually be encoded all make the profile unusable. Bitrate/channel ceilings
    /// (<see cref="PlaybackOutputProfile.MaxVideoBitrate"/>/<see cref="PlaybackOutputProfile.MaxAudioBitrate"/>/
    /// <see cref="PlaybackOutputProfile.MaxAudioChannels"/>) are deliberately not checked here: they
    /// shape the output (see the bitrate ceiling cascade in <see cref="BuildForSource"/>), they do
    /// not disqualify the profile from being used at all.
    /// </summary>
    private static bool IsOutputProfileViable(PlaybackOutputProfile profile, DecodeCapabilities decode, bool needVideoTranscode, bool needAudioTranscode)
    {
        if (string.IsNullOrEmpty(profile.Container))
        {
            return false;
        }

        if (!IsProtocolViable(profile.Protocol, decode))
        {
            return false;
        }

        if (needVideoTranscode && profile.VideoCodecs.Count == 0)
        {
            return false;
        }

        if (needAudioTranscode && profile.AudioCodecs.Count == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the client has declared support for the delivery protocol an output profile asks for.
    /// <see cref="StreamingProtocol.Http"/> is always viable (plain progressive delivery, no special
    /// client support needed); <see cref="StreamingProtocol.Hls"/> requires <see cref="DecodeCapabilities.SupportsHls"/>.
    /// </summary>
    private static bool IsProtocolViable(StreamingProtocol protocol, DecodeCapabilities decode) => protocol switch
    {
        StreamingProtocol.Hls => decode.SupportsHls,
        _ => true,
    };

    /// <summary>
    /// Returns whether a single <see cref="DecodeProfile"/> accepts the given
    /// container/video-codec/audio-codec combination for <paramref name="mediaKind"/> (PR102b
    /// problem #1/#3): the profile's <see cref="DecodeProfile.Type"/> must match
    /// <paramref name="mediaKind"/> exactly (an audio-only profile never authorizes a video
    /// combination and vice versa), and each of <see cref="DecodeProfile.Containers"/>/
    /// <see cref="DecodeProfile.VideoCodecs"/>/<see cref="DecodeProfile.AudioCodecs"/> either is
    /// empty (wildcard - matches anything, same semantics as an empty legacy
    /// <c>DirectPlayProfile</c> field) or contains the corresponding value. A <see langword="null"/>
    /// <paramref name="videoCodec"/>/<paramref name="audioCodec"/> (no such stream selected) skips
    /// that axis entirely rather than requiring a wildcard match.
    /// </summary>
    private static bool DirectPlayProfileMatches(DecodeProfile profile, MediaKind mediaKind, string container, string? videoCodec, string? audioCodec)
    {
        if (profile.Type != mediaKind)
        {
            return false;
        }

        if (profile.Containers.Count > 0 && !profile.Containers.Contains(container, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (videoCodec is not null && profile.VideoCodecs.Count > 0 && !profile.VideoCodecs.Contains(videoCodec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (audioCodec is not null && profile.AudioCodecs.Count > 0 && !profile.AudioCodecs.Contains(audioCodec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Collects the distinct containers declared, across all of the client's <paramref name="mediaKind"/>
    /// <see cref="DecodeProfile"/> entries, together with <paramref name="videoCodec"/>/
    /// <paramref name="audioCodec"/> (each axis wildcard-matched the same way as
    /// <see cref="DirectPlayProfileMatches"/>; pass <see langword="null"/> for either codec to skip
    /// that axis and collect containers regardless of it). A profile whose own
    /// <see cref="DecodeProfile.Containers"/> is itself a wildcard (empty) contributes nothing
    /// concrete here - there is no specific container to offer as a repackaging target from it, only
    /// "accepts whatever container it already has," which is a <see cref="PlaybackMethod.DirectPlay"/>
    /// concern (<see cref="DirectPlayProfileMatches"/>), not a <see cref="PlaybackMethod.Remux"/> one.
    /// </summary>
    private static IReadOnlyList<string> AcceptableContainers(ClientCapabilities capabilities, MediaKind mediaKind, string? videoCodec, string? audioCodec)
    {
        var result = new List<string>();
        foreach (var profile in capabilities.Decode.DirectPlayProfiles)
        {
            if (profile.Type != mediaKind)
            {
                continue;
            }

            if (videoCodec is not null && profile.VideoCodecs.Count > 0 && !profile.VideoCodecs.Contains(videoCodec, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (audioCodec is not null && profile.AudioCodecs.Count > 0 && !profile.AudioCodecs.Contains(audioCodec, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.AddRange(profile.Containers);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Picks a container from a candidate container list: used for <see cref="PlaybackMethod.Remux"/>
    /// (which only ever repackages into a container the client already decodes, never an encode
    /// target) and as the transcode container fallback when the client declares no matching
    /// <see cref="PlaybackOutputProfile"/>. Prefers mp4, then ts, then whatever is first, matching
    /// the pre-PR102 engine's container preference exactly.
    /// </summary>
    private static string SelectTargetContainer(IReadOnlyList<string> containers, string sourceContainer)
    {
        if (containers.Count == 0)
        {
            return sourceContainer;
        }

        var mp4 = containers.FirstOrDefault(c => EqualsIgnoreCase(c, "mp4"));
        if (mp4 is not null)
        {
            return mp4;
        }

        var ts = containers.FirstOrDefault(c => EqualsIgnoreCase(c, "ts"));
        if (ts is not null)
        {
            return ts;
        }

        return containers[0];
    }

    /// <summary>
    /// The best candidate found for a single source: a chosen method with its decision, or a
    /// "not viable for this source" marker carrying the blocking reasons that would feed a
    /// <see cref="PlaybackDecision.NotViable(PlaybackMethod, ReasonNode, int)"/> built from it.
    /// </summary>
    private sealed record SourceCandidate(PlaybackMethod? Method, PlaybackDecision? Decision, IReadOnlyList<ReasonNode> BlockingReasons)
    {
        public static SourceCandidate ForDirectPlay(PlaybackDecision decision) => new(PlaybackMethod.DirectPlay, decision, []);

        public static SourceCandidate ForRemux(PlaybackDecision decision) => new(PlaybackMethod.Remux, decision, []);

        public static SourceCandidate ForTranscode(PlaybackDecision decision) => new(PlaybackMethod.Transcode, decision, []);

        public static SourceCandidate ForNotViable(IReadOnlyList<ReasonNode> blockingReasons) => new(null, null, blockingReasons);
    }
}
