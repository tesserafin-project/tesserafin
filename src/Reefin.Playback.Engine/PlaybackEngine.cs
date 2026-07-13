using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Engine;

/// <summary>
/// The v2 playback decision engine, phase 1: simple audio, video direct play, remux (container
/// change with stream copy), and source/stream selection, all with name-only, case-insensitive
/// codec matching.
/// </summary>
/// <remarks>
/// Phase 1 does not implement video/audio transcoding, subtitle handling or burn-in, bitrate or
/// resolution limits, codec profile/level/bit-depth checks, HDR tonemapping, or channel downmix
/// (all PR97). A source that would need any of those to play returns
/// <see cref="PlaybackDecision.NotViable(PlaybackMethod, ReasonNode, int)"/> instead of a half-built
/// transcode plan. That is correct only because nothing consumes this engine's output yet: no
/// application code switches on <see cref="PlaybackDecision"/> before PR97/PR98 wire it in, so a
/// phase-1 engine that cannot transcode cannot regress anyone's current playback.
/// </remarks>
public sealed class PlaybackEngine : IPlaybackEngine
{
    /// <summary>
    /// The version of the decision engine implemented by this type.
    /// </summary>
    public const int EngineVersion = 2;

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
        var selectedVideo = context.MediaKind == MediaKind.Video ? source.VideoStreams.FirstOrDefault() : null;
        var selectedAudio = SelectAudio(source, constraints);

        var videoOk = context.MediaKind == MediaKind.Audio
            || (selectedVideo is not null && ContainsCodec(capabilities.VideoCodecs.Select(c => c.Codec), selectedVideo.Codec));
        var audioOk = selectedAudio is not null && ContainsCodec(capabilities.AudioCodecs.Select(c => c.Codec), selectedAudio.Codec);
        var containerOk = capabilities.Containers.Contains(source.Container, StringComparer.OrdinalIgnoreCase);

        if (constraints.AllowDirectPlay && source.SupportsDirectPlay && containerOk && videoOk && audioOk)
        {
            return SourceCandidate.ForDirectPlay(BuildDirectPlay(source, selectedVideo, selectedAudio));
        }

        if (!containerOk
            && videoOk
            && audioOk
            && constraints.AllowDirectStream
            && source.SupportsDirectStream
            && (context.MediaKind == MediaKind.Audio || constraints.AllowVideoStreamCopy)
            && constraints.AllowAudioStreamCopy
            && capabilities.Containers.Count > 0)
        {
            return SourceCandidate.ForRemux(BuildRemux(capabilities, source, selectedVideo, selectedAudio));
        }

        var blockingReasons = new List<ReasonNode>();
        if (!videoOk && selectedVideo is not null)
        {
            blockingReasons.Add(ReasonNode.Leaf(ReasonCode.VideoCodecNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(selectedVideo.Index)));
        }

        if (!audioOk && selectedAudio is not null)
        {
            blockingReasons.Add(ReasonNode.Leaf(ReasonCode.AudioCodecNotSupported, ReasonOutcome.Rejected, ReasonSubject.AudioStream(selectedAudio.Index)));
        }

        return SourceCandidate.ForNotViable(blockingReasons);
    }

    private static PlaybackDecision BuildDirectPlay(MediaSourceSnapshot source, VideoStreamSnapshot? selectedVideo, AudioStreamSnapshot? selectedAudio)
    {
        var output = new OutputSpec(source.Container, selectedVideo?.Codec, selectedAudio?.Codec, null, null, null, null);
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());
        var streams = new SelectedStreams(selectedVideo?.Index, selectedAudio?.Index, null);

        return PlaybackDecision.DirectPlay(source.MediaSourceId, streams, output, reasoning, EngineVersion);
    }

    private static PlaybackDecision BuildRemux(
        ClientCapabilities capabilities,
        MediaSourceSnapshot source,
        VideoStreamSnapshot? selectedVideo,
        AudioStreamSnapshot? selectedAudio)
    {
        var targetContainer = SelectRemuxContainer(capabilities.Containers);

        var transforms = new List<TransformKind> { TransformKind.RemuxContainer };
        if (selectedVideo is not null)
        {
            transforms.Add(TransformKind.CopyVideo);
        }

        transforms.Add(TransformKind.CopyAudio);

        var output = new OutputSpec(targetContainer, selectedVideo?.Codec, selectedAudio?.Codec, null, null, null, null);

        ReasonSubject streamCopyableSubject;
        if (selectedVideo is not null)
        {
            streamCopyableSubject = ReasonSubject.VideoStream(selectedVideo.Index);
        }
        else if (selectedAudio is not null)
        {
            streamCopyableSubject = ReasonSubject.AudioStream(selectedAudio.Index);
        }
        else
        {
            throw new InvalidOperationException("A remux candidate requires a selected audio stream when no video stream was selected.");
        }

        var reasoning = new ReasonNode(
            ReasonCode.MethodChosen,
            ReasonOutcome.Chosen,
            ReasonSubject.Method(),
            null,
            new List<ReasonNode>
            {
                ReasonNode.Leaf(ReasonCode.ContainerNotSupported, ReasonOutcome.Rejected, ReasonSubject.Container()),
                ReasonNode.Leaf(ReasonCode.StreamCopyable, ReasonOutcome.Accepted, streamCopyableSubject),
            });

        var streams = new SelectedStreams(selectedVideo?.Index, selectedAudio?.Index, null);

        return PlaybackDecision.Remux(source.MediaSourceId, streams, output, transforms, reasoning, EngineVersion);
    }

    private static AudioStreamSnapshot? SelectAudio(MediaSourceSnapshot source, PlaybackConstraints constraints)
    {
        AudioStreamSnapshot? selected = null;
        if (constraints.PreferredAudioStreamIndex is int preferredIndex)
        {
            selected = source.AudioStreams.FirstOrDefault(a => a.Index == preferredIndex);
        }

        selected ??= source.AudioStreams.FirstOrDefault(a => a.IsDefault);
        selected ??= source.AudioStreams.FirstOrDefault();

        return selected;
    }

    private static bool ContainsCodec(IEnumerable<string> codecs, string? codec) =>
        codec is not null && codecs.Any(c => string.Equals(c, codec, StringComparison.OrdinalIgnoreCase));

    private static string SelectRemuxContainer(IReadOnlyList<string> containers)
    {
        var mp4 = containers.FirstOrDefault(c => string.Equals(c, "mp4", StringComparison.OrdinalIgnoreCase));
        if (mp4 is not null)
        {
            return mp4;
        }

        var ts = containers.FirstOrDefault(c => string.Equals(c, "ts", StringComparison.OrdinalIgnoreCase));
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

        public static SourceCandidate ForNotViable(IReadOnlyList<ReasonNode> blockingReasons) => new(null, null, blockingReasons);
    }
}
