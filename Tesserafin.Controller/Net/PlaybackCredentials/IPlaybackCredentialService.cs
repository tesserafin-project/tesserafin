using System;
using System.Collections.Generic;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// Mints, validates and revokes the two short-lived credentials that replace the durable session
/// token in playback and WebSocket URLs (#153).
/// </summary>
/// <remarks>
/// WHY THIS INTERFACE LIVES IN <c>Tesserafin.Controller</c>. Exactly where
/// <see cref="IAuthorizationContext"/> lives, and for the same reason: the consumers are MVC
/// filters and the WebSocket manager, which sit in <c>Tesserafin.Api</c> and
/// <c>Tesserafin.Server.Core</c>, while the implementation sits in
/// <c>Tesserafin.Server.Implementations</c>. Declaring it here is what keeps the reference
/// direction one-way.
///
/// WHAT THIS IS NOT. It is not a second authentication framework. Nothing here produces a
/// <see cref="System.Security.Claims.ClaimsPrincipal"/>, nothing here is an
/// <c>AuthenticationHandler</c>, and no value it mints is ever read by
/// <see cref="IAuthorizationContext"/>. A capability presented to <c>/Items</c> is not a weak
/// credential — it is not a credential at all, because the only two query keys that path reads are
/// <c>ApiKey</c> and <c>api_key</c>, and it must never be taught a third.
/// </remarks>
public interface IPlaybackCredentialService
{
    /// <summary>
    /// Mints a playback capability. The caller must already be authenticated by the durable
    /// session token in a header; this method does not authenticate anything.
    /// </summary>
    /// <param name="request">The binding the capability is issued against.</param>
    /// <returns>The grant, whose secret value is readable exactly once — here.</returns>
    PlaybackCapabilityGrant MintCapability(PlaybackCapabilityRequest request);

    /// <summary>
    /// Extends a capability's expiry in place, without rotating its secret.
    /// </summary>
    /// <param name="capabilityId">The public identifier returned at mint. Never the secret.</param>
    /// <param name="sessionId">The renewing caller's session, which must own the capability.</param>
    /// <returns>The outcome; on success, the new expiry.</returns>
    /// <remarks>
    /// The secret is neither accepted nor returned here. Renewal by public id means the value never
    /// travels a second time, and extending in place rather than rotating means in-flight segment
    /// requests holding the old value do not fail mid-playback.
    /// </remarks>
    PlaybackCapabilityRenewal RenewCapability(Guid capabilityId, string sessionId);

    /// <summary>
    /// Resolves a presented capability without asking anything about the route.
    /// </summary>
    /// <param name="presentedValue">The raw value from the request, or null.</param>
    /// <returns>Existence, expiry and revocation only.</returns>
    /// <remarks>
    /// This is what the authentication layer can honestly decide. Scope, item and media source
    /// depend on the route being called, which authentication cannot see, so they belong to the
    /// action filter and not here. Splitting the two is what stops the authentication layer from
    /// having to guess a demand and accidentally accepting a mismatch.
    /// </remarks>
    PlaybackCapabilityValidation ResolveCapability(string? presentedValue);

    /// <summary>
    /// Validates a presented capability against what a route demands.
    /// </summary>
    /// <param name="presentedValue">The raw value from the request, or null.</param>
    /// <param name="demand">The scope, item and media source the route requires.</param>
    /// <returns>The validation outcome.</returns>
    PlaybackCapabilityValidation ValidateCapability(string? presentedValue, PlaybackCapabilityDemand demand);

    /// <summary>
    /// Mints a single-use WebSocket ticket.
    /// </summary>
    /// <param name="request">The session and device the ticket is bound to.</param>
    /// <returns>The grant, whose secret value is readable exactly once — here.</returns>
    WebSocketTicketGrant MintWebSocketTicket(WebSocketTicketRequest request);

    /// <summary>
    /// Consumes a presented WebSocket ticket. A successful consumption removes it atomically, so a
    /// second presentation can never succeed.
    /// </summary>
    /// <param name="presentedValue">The raw value from the upgrade request, or null.</param>
    /// <returns>The validation outcome; on success, the bound session and device.</returns>
    WebSocketTicketValidation ConsumeWebSocketTicket(string? presentedValue);

    /// <summary>
    /// Revokes every capability and ticket bound to a session. Called from the existing
    /// <c>SessionManager</c> lifecycle, not from a new one.
    /// </summary>
    /// <param name="sessionId">The session that ended.</param>
    /// <returns>How many credentials were removed.</returns>
    int RevokeSession(string sessionId);

    /// <summary>
    /// Revokes every capability and ticket belonging to a user, optionally sparing one session.
    /// </summary>
    /// <param name="userId">The user whose credentials are revoked.</param>
    /// <param name="exceptSessionId">A session to spare, for the password-change path that keeps
    /// the caller signed in. Null revokes all of them.</param>
    /// <returns>How many credentials were removed.</returns>
    int RevokeUser(Guid userId, string? exceptSessionId);

    /// <summary>
    /// Revokes every capability and ticket issued to a device.
    /// </summary>
    /// <param name="deviceId">The device being removed.</param>
    /// <returns>How many credentials were removed.</returns>
    int RevokeDevice(string deviceId);

    /// <summary>
    /// Revokes only the capabilities of one play session. Other concurrent play sessions of the
    /// same user and the same device are untouched, which is the whole point of binding to a play
    /// session rather than to a user.
    /// </summary>
    /// <param name="playSessionId">The play session that ended.</param>
    /// <returns>How many capabilities were removed.</returns>
    int RevokePlaySession(string playSessionId);

    /// <summary>
    /// Every live capability id bound to a session. Diagnostic only; carries no secret.
    /// </summary>
    /// <param name="sessionId">The session to enumerate.</param>
    /// <returns>The capability identifiers.</returns>
    IReadOnlyList<Guid> GetCapabilityIds(string sessionId);
}
