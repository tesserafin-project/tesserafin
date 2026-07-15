namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// One row in the admin playback-diagnostics session list (docs/pr92-design-playback-api-and-diagnostics.md
/// §4.3, PR113): the same stable projection <see cref="PlaybackSessionResponse"/> already exposes,
/// plus whether a richer diagnostic is available for this session's <c>{id}</c> detail route. Never
/// the internal <c>Reefin.Controller.MediaEncoding.PlaybackSession</c> record - same rule as the
/// detail endpoint.
/// </summary>
/// <param name="Session">The session's stable projection.</param>
/// <param name="HasDiagnostic">
/// Whether a retained shadow diagnostic exists for this session - i.e. whether its detail route
/// would return the v2-sourced fields populated rather than null/empty.
/// </param>
public sealed record PlaybackSessionListItem(PlaybackSessionResponse Session, bool HasDiagnostic);
