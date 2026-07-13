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
public sealed class PlaybackEngine : IPlaybackEngine
{
    /// <summary>
    /// The version of the decision engine implemented by this type.
    /// </summary>
    public const int EngineVersion = 2;

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

        var candidates = new List<SourceCandidate>(sources.Count);
        foreach (var source in sources)
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
        var selectedSubtitle = SelectSubtitle(source, constraints);

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
        var videoReasons = new List<ReasonNode>();

        if (selectedVideo is not null)
        {
            var videoCap = capabilities.VideoCodecs.FirstOrDefault(c => EqualsIgnoreCase(c.Codec, selectedVideo.Codec));
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
            }

            if (capabilities.MaxResolution is not null && selectedVideo.Width is not null && selectedVideo.Height is not null
                && (selectedVideo.Width > capabilities.MaxResolution.Width || selectedVideo.Height > capabilities.MaxResolution.Height))
            {
                needVideoTranscode = true;
                needDownscale = true;
                videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoResolutionNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
            }

            if (capabilities.MaxVideoBitrate is not null && selectedVideo.Bitrate is not null && selectedVideo.Bitrate > capabilities.MaxVideoBitrate)
            {
                needVideoTranscode = true;
                videoReasons.Add(ReasonNode.Leaf(ReasonCode.VideoBitrateNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
            }
        }

        // --- AUDIO ---
        var needAudioTranscode = false;
        var needDownmix = false;
        int? effMaxChannels = null;
        var audioReasons = new List<ReasonNode>();

        if (selectedAudio is not null)
        {
            var audioCap = capabilities.AudioCodecs.FirstOrDefault(c => EqualsIgnoreCase(c.Codec, selectedAudio.Codec));
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
            }
        }

        // --- SUBTITLE ---
        var needBurnIn = false;
        SubtitleDeliveryMethod? subtitleDelivery = null;
        ReasonNode? subtitleCodecNotSupportedReason = null;

        if (selectedSubtitle is not null)
        {
            var subtitleCap = capabilities.SubtitleDelivery.FirstOrDefault(c => EqualsIgnoreCase(c.Format, selectedSubtitle.Format));
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
        var containerOk = capabilities.Containers.Contains(source.Container, StringComparer.OrdinalIgnoreCase);
        var neededMethod = wantsTranscode
            ? PlaybackMethod.Transcode
            : !containerOk
                ? PlaybackMethod.Remux
                : PlaybackMethod.DirectPlay;

        var allowed = neededMethod switch
        {
            PlaybackMethod.Transcode => constraints.AllowTranscoding && source.SupportsTranscoding,
            PlaybackMethod.Remux => constraints.AllowDirectStream
                && source.SupportsDirectStream
                && (selectedVideo is null || constraints.AllowVideoStreamCopy)
                && constraints.AllowAudioStreamCopy
                && capabilities.Containers.Count > 0,
            PlaybackMethod.DirectPlay => constraints.AllowDirectPlay && source.SupportsDirectPlay,
            _ => false,
        };

        if (!allowed)
        {
            var blockingReasons = new List<ReasonNode>();
            blockingReasons.AddRange(videoReasons);
            blockingReasons.AddRange(audioReasons);
            if (subtitleCodecNotSupportedReason is not null)
            {
                blockingReasons.Add(subtitleCodecNotSupportedReason);
            }

            return SourceCandidate.ForNotViable(blockingReasons);
        }

        // --- CONTAINER ---
        var targetContainer = neededMethod == PlaybackMethod.DirectPlay
            ? source.Container
            : SelectTargetContainer(capabilities.Containers, source.Container);
        var containerChanged = !string.Equals(targetContainer, source.Container, StringComparison.OrdinalIgnoreCase);

        // --- TARGET CODECS ---
        string? targetVideoCodec = null;
        if (needVideoTranscode && selectedVideo is not null)
        {
            targetVideoCodec = SelectTargetCodec(capabilities.VideoCodecs.Select(c => c.Codec).ToList(), "h264", selectedVideo.Codec);
        }

        string? targetAudioCodec = null;
        if (needAudioTranscode && selectedAudio is not null)
        {
            targetAudioCodec = SelectTargetCodec(capabilities.AudioCodecs.Select(c => c.Codec).ToList(), "aac", selectedAudio.Codec);
        }

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
        var output = new OutputSpec(
            Container: targetContainer,
            VideoCodec: selectedVideo is not null ? (needVideoTranscode ? targetVideoCodec : selectedVideo.Codec) : null,
            AudioCodec: selectedAudio is not null ? (needAudioTranscode ? targetAudioCodec : selectedAudio.Codec) : null,
            Resolution: needVideoTranscode && needDownscale ? capabilities.MaxResolution : null,
            VideoRange: needVideoTranscode && needTonemap ? "SDR" : null,
            AudioChannels: needAudioTranscode ? (needDownmix ? effMaxChannels : selectedAudio?.Channels) : null,
            Bitrate: null);

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

        if (subtitleCodecNotSupportedReason is not null)
        {
            children.Add(subtitleCodecNotSupportedReason);
        }

        if (transforms.Contains(TransformKind.BurnInSubtitle))
        {
            children.Add(ReasonNode.Leaf(ReasonCode.SubtitleBurnInRequired, ReasonOutcome.Chosen, ReasonSubject.Subtitle(selectedSubtitle!.Index)));
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

    private static SubtitleStreamSnapshot? SelectSubtitle(MediaSourceSnapshot source, PlaybackConstraints constraints) =>
        constraints.PreferredSubtitleStreamIndex is int preferredIndex
            ? source.SubtitleStreams.FirstOrDefault(s => s.Index == preferredIndex)
            : null;

    private static bool ContainsCodec(IEnumerable<string> codecs, string? codec) =>
        codec is not null && codecs.Any(c => EqualsIgnoreCase(c, codec));

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

    private static string SelectTargetCodec(IReadOnlyList<string> available, string preferred, string sourceCodec)
    {
        if (ContainsCodec(available, preferred))
        {
            return preferred;
        }

        if (ContainsCodec(available, sourceCodec))
        {
            return sourceCodec;
        }

        return available.Count > 0 ? available[0] : sourceCodec;
    }

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
