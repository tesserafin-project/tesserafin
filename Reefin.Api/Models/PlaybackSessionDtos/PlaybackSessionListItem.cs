using System;

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
/// <param name="ItemId">
/// PR114: the requested item's identifier, or <see langword="null"/> when the session was tracked
/// directly (<c>IPlaybackSessionManager.Track</c>) rather than planned from a request, so no
/// <c>MediaOptions</c> was ever attached. Deliberately the raw identifier only - never the resolved
/// item name/library metadata, which would require this admin-only list to depend on
/// <c>ILibraryManager</c> for what is otherwise a cheap, dependency-free projection.
/// </param>
/// <param name="DeviceId">
/// PR114: the requesting client's device identifier, or <see langword="null"/> under the same
/// condition as <see cref="ItemId"/>. Likewise the raw identifier only - never a resolved device/app
/// display name.
/// </param>
public sealed record PlaybackSessionListItem(PlaybackSessionResponse Session, bool HasDiagnostic, Guid? ItemId, string? DeviceId);
