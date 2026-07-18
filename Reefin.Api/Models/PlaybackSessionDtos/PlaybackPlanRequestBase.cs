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
/// <param name="PlaybackAttemptId">
/// Issue #43. Optional, opaque, client-generated. The identifier of the playback ATTEMPT this
/// request belongs to — generated once by the client when it starts trying to play something, and
/// resent unchanged on every request of that attempt: <c>PlaybackInfo</c>, this <c>POST</c>, the
/// <c>PUT</c>, and any retry. A new attempt gets a new value; a retry inside the same attempt keeps
/// the old one.
/// <para>
/// Distinct from both neighbouring scopes and substitutable for neither. It is NOT the
/// <c>RequestId</c>/<c>TraceId</c> of issue #42, which is server-derived and changes on every
/// request. It is NOT <see cref="CreatePlaybackSessionRequest.PlaySessionId"/>, which only exists
/// once a session does — i.e. after <c>PlaybackInfo</c> has already happened — and which survives
/// across several attempts. See <c>docs/observabilite-identifiants-correlation.md</c>.
/// </para>
/// <para>
/// Diagnostics only: never an authorization key, no access decision is derived from it, and it
/// replaces no existing access control. Validated for length and printability only (see
/// <see cref="PlaybackAttemptIdValidator"/>); no structure is imposed and no meaning is read out of it.
/// </para>
/// </param>
public abstract record PlaybackPlanRequestBase(
    Guid ItemId,
    Guid UserId,
    ClientCapabilities Capabilities,
    PlaybackConstraints Constraints,
    string? MediaSourceId = null,
    string? PlaybackAttemptId = null);
