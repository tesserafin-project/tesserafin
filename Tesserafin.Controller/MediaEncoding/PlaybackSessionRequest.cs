using Tesserafin.Model.Dlna;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Input to a playback session create or patch operation.
/// </summary>
/// <param name="Kind">Whether to plan an audio or video stream.</param>
/// <param name="Options">The media options to plan against.</param>
public sealed record PlaybackSessionRequest(PlaybackMediaKind Kind, MediaOptions Options);
