using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Execution;

/// <summary>
/// Everything the execution layer needs to actually stream a decision the v2 engine
/// (<c>Reefin.Playback.Engine.PlaybackEngine</c>) already made: the method, the selected source and
/// streams, the target container/codecs/bitrates/resolution, and the transforms the pipeline must
/// perform. Frozen (PR114a): every value here is copied verbatim from a <see cref="PlaybackDecision"/>
/// that was already validated as viable - this type carries no logic of its own, only the shape a
/// decision takes once it is ready for execution instead of comparison/diagnostics. Built exclusively
/// by <see cref="PlaybackExecutionPlanBuilder"/>, which refuses (rather than guesses) when the source
/// decision is incomplete; see that type's remarks for the refusal contract.
/// </summary>
/// <param name="Method">The playback method the decision selected.</param>
/// <param name="SourceId">The identifier of the media source selected for playback.</param>
/// <param name="Container">The target output container.</param>
/// <param name="Protocol">The transport protocol this output is delivered over.</param>
/// <param name="VideoStreamIndex">
/// The selected video stream index, or <see langword="null"/> when no video stream was selected
/// (for example, audio-only playback).
/// </param>
/// <param name="VideoCodec">
/// The target video codec (the source codec unchanged for Direct Play/Remux, or the transcode
/// target), or <see langword="null"/> when no video stream was selected.
/// </param>
/// <param name="VideoBitrate">The target video stream's bitrate ceiling, or <see langword="null"/> if not applicable/unbounded.</param>
/// <param name="Resolution">The target output resolution, or <see langword="null"/> if not applicable/unchanged.</param>
/// <param name="VideoRange">The target output video range type, or <see langword="null"/> if not applicable/unchanged.</param>
/// <param name="AudioStreamIndex">
/// The selected audio stream index, or <see langword="null"/> when no audio stream was selected.
/// </param>
/// <param name="AudioCodec">
/// The target audio codec (the source codec unchanged for Direct Play/Remux, or the transcode
/// target), or <see langword="null"/> when no audio stream was selected.
/// </param>
/// <param name="AudioBitrate">The target audio stream's bitrate ceiling, or <see langword="null"/> if not applicable/unbounded.</param>
/// <param name="AudioChannels">The target output audio channel count, or <see langword="null"/> if not applicable/unchanged.</param>
/// <param name="TotalBitrate">The output's overall bitrate ceiling, or <see langword="null"/> if not applicable/unbounded.</param>
/// <param name="SubtitleStreamIndex">The selected subtitle stream index, or <see langword="null"/> if no subtitle was selected.</param>
/// <param name="SubtitleDelivery">How the selected subtitle is delivered to the client, or <see langword="null"/> if no subtitle was selected.</param>
/// <param name="SubtitleFormat">The format the client will actually receive the selected subtitle in, or <see langword="null"/> if no subtitle was selected.</param>
/// <param name="Transforms">The transformations the pipeline must perform to realize this plan.</param>
public sealed record PlaybackExecutionPlan(
    PlaybackMethod Method,
    string SourceId,
    string Container,
    StreamingProtocol Protocol,
    int? VideoStreamIndex,
    string? VideoCodec,
    int? VideoBitrate,
    Resolution? Resolution,
    string? VideoRange,
    int? AudioStreamIndex,
    string? AudioCodec,
    int? AudioBitrate,
    int? AudioChannels,
    int? TotalBitrate,
    int? SubtitleStreamIndex,
    SubtitleDeliveryMethod? SubtitleDelivery,
    string? SubtitleFormat,
    IReadOnlyList<TransformKind> Transforms);
