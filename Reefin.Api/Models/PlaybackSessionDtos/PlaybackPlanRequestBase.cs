using System;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The fields shared by <see cref="CreatePlaybackSessionRequest"/> and
/// <see cref="ReplacePlaybackSessionRequest"/>: what to plan (<see cref="ItemId"/>/
/// <see cref="MediaSourceId"/>) and for whom, under what capabilities and constraints. Deliberately
/// narrower than the internal <c>MediaOptions</c> these get translated into (PR112b, via the
/// TEMPORARY <c>Reefin.Playback.Dlna.ReverseDlnaAdapter</c>): server-resolved fields (media sources,
/// requesting IP) are left out.
/// </summary>
/// <param name="ItemId">The item to plan playback for.</param>
/// <param name="UserId">The requesting user.</param>
/// <param name="Capabilities">
/// What the requesting client can decode, and what it wants produced when the server must
/// transcode. Replaces the point-1 protocol's raw DLNA <c>DeviceProfile</c> (PR112b): the same
/// PR91 decision vocabulary <see cref="Reefin.Api.Models.PlaybackSessionDtos.PlaybackSessionResponse"/>
/// already uses for the response, so the contract is symmetric in both directions.
/// </param>
/// <param name="Constraints">
/// The playback method/bitrate/subtitle preferences and limits for this request. Replaces the
/// point-1 protocol's scattered <c>enableDirectPlay</c>/<c>enableDirectStream</c>/
/// <c>enableTranscoding</c>/<c>maxBitrate</c>/... top-level booleans and fields (PR112b) with a
/// single grouped object, mirroring how <see cref="Capabilities"/> replaces <c>DeviceProfile</c>.
/// </param>
/// <param name="MediaSourceId">Optional. A specific media source id, if playing an alternate version.</param>
public abstract record PlaybackPlanRequestBase(
    Guid ItemId,
    Guid UserId,
    ClientCapabilities Capabilities,
    PlaybackConstraints Constraints,
    string? MediaSourceId = null);
