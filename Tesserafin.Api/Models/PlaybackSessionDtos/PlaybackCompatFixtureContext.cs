using Tesserafin.Playback.Decision;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The fixture schema's <c>input.context</c> object: only the media kind - the requested source id
/// lives on <see cref="PlaybackCompatFixtureInput.RequestedMediaSourceId"/> instead (mirrors
/// <c>PlaybackRequestContext</c>'s PR104 shape).
/// </summary>
/// <param name="MediaKind">Whether audio or video playback was requested.</param>
public sealed record PlaybackCompatFixtureContext(MediaKind MediaKind);
