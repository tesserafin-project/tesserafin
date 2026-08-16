using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// What a protected route requires of a presented capability.
/// </summary>
/// <param name="Scope">The single scope this route demands.</param>
/// <param name="ItemId">The item this request is for, or null for a route that names none.</param>
/// <param name="MediaSourceId">The media source this request is for, or null for a route that names none.</param>
/// <param name="PlaySessionId">The play session this request names, if it named one.</param>
/// <remarks>
/// A null <paramref name="ItemId"/> or <paramref name="MediaSourceId"/> means "this route names
/// none", and that is a REFUSAL for a capability that is bound to one, not a wildcard. #153-A0
/// compared a binding only when both sides stated it, so a capability bound to an item satisfied
/// every route that did not name one — which is every route an attacker would choose.
///
/// <paramref name="PlaySessionId"/> is different, deliberately. Only a mismatch refuses: a route
/// that exposes the parameter does not require the client to send it, and refusing its absence
/// would make the play-session binding a mandatory query parameter on routes that have always
/// treated it as optional. That asymmetry is recorded in
/// <c>docs/playback-credential-server-contract.md</c> rather than left to be rediscovered.
/// </remarks>
public readonly record struct PlaybackCapabilityDemand(
    PlaybackCapabilityScope Scope,
    Guid? ItemId,
    string? MediaSourceId,
    string? PlaySessionId = null);
