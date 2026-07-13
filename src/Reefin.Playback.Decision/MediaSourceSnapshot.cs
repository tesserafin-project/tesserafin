using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// A frozen snapshot of a media source, decoupled from any probing/model type. Carries no file
/// paths, URLs, or tokens: the domain performs no I/O (RFC PR91 §8, question 4).
/// </summary>
/// <param name="MediaSourceId">The identifier of the source within its item.</param>
/// <param name="Container">The normalized container name (for example <c>"mkv"</c>).</param>
/// <param name="Protocol">The normalized delivery protocol (for example <c>"http"</c>, <c>"hls"</c>).</param>
/// <param name="Bitrate">The overall source bitrate, or <see langword="null"/> if unknown.</param>
/// <param name="RunTimeTicks">The runtime in ticks, or <see langword="null"/> if unknown.</param>
/// <param name="VideoStreams">The video streams on the source.</param>
/// <param name="AudioStreams">The audio streams on the source.</param>
/// <param name="SubtitleStreams">The subtitle streams on the source.</param>
/// <param name="SupportsDirectPlay">Whether the source itself can be direct played, before client capabilities are considered.</param>
/// <param name="SupportsDirectStream">Whether the source itself can be direct streamed (remuxed), before client capabilities are considered.</param>
/// <param name="SupportsTranscoding">Whether the source itself can be transcoded.</param>
public sealed record MediaSourceSnapshot(
    string MediaSourceId,
    string Container,
    string Protocol,
    int? Bitrate,
    long? RunTimeTicks,
    IReadOnlyList<VideoStreamSnapshot> VideoStreams,
    IReadOnlyList<AudioStreamSnapshot> AudioStreams,
    IReadOnlyList<SubtitleStreamSnapshot> SubtitleStreams,
    bool SupportsDirectPlay,
    bool SupportsDirectStream,
    bool SupportsTranscoding);
