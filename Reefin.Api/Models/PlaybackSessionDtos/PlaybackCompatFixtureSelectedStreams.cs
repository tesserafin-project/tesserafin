using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The fixture schema's <c>expected.selectedStreams</c> object.
/// </summary>
/// <param name="Video">The selected video stream index, or <see langword="null"/> if none.</param>
/// <param name="Audio">The selected audio stream index, or <see langword="null"/> if none.</param>
/// <param name="Subtitle">
/// The selected subtitle stream index (never the delivery method - the schema's
/// <c>selectedStreams.subtitle</c> is an index, matching <see cref="SelectedSubtitle.Index"/>, not
/// the full <see cref="SelectedSubtitle"/>), or <see langword="null"/> if none.
/// </param>
public sealed record PlaybackCompatFixtureSelectedStreams(int? Video, int? Audio, int? Subtitle);
