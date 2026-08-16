using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tesserafin.Api.Constants;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Api.Auth.PlaybackCapabilityPolicy;

/// <summary>
/// Authenticates a media request from a short-lived playback capability presented in the query
/// string (#153).
/// </summary>
/// <remarks>
/// WHY A SCHEME AND NOT ANOTHER KEY IN <c>AuthorizationContext</c>. A scheme is only ever selected
/// by an endpoint that names it. <c>Policies.MediaDelivery</c> is the only policy
/// that names this one, and only media routes carry that policy, so a capability presented to
/// <c>/Items</c> is never even offered to this handler. The rejection is structural: it needs no
/// per-endpoint denylist, and adding a controller cannot accidentally widen it.
///
/// WHAT THIS HANDLER DOES NOT DECIDE. Scope, item and media source. Those depend on the route being
/// called, which authentication cannot see, so they are checked by
/// <see cref="Attributes.RequiresPlaybackCapabilityAttribute"/> on the action itself. This handler
/// establishes only that the presented value is a live capability belonging to a live session.
/// </remarks>
public class PlaybackCapabilityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// The query key a capability travels in. Deliberately neither <c>ApiKey</c> nor <c>api_key</c>:
    /// those two are what the general authorization path reads, and this value must never be
    /// mistaken for one.
    /// </summary>
    public const string QueryKey = "playbackCapability";

    private readonly IPlaybackCredentialService _credentialService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCapabilityAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="credentialService">The credential store and validator.</param>
    /// <param name="options">Options monitor.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The url encoder.</param>
    public PlaybackCapabilityAuthenticationHandler(
        IPlaybackCredentialService credentialService,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _credentialService = credentialService;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Query[QueryKey].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            // NoResult, never Fail: a request with no capability may still be authenticated by the
            // durable session token through the other scheme this policy accepts. Failing here
            // would break every legacy client on its first media request.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Existence, expiry and revocation only. Scope, item and media source depend on the route,
        // which authentication cannot see; guessing a demand here would either reject a valid
        // font capability or accept a mismatch, and both are worse than deferring.
        var validation = _credentialService.ResolveCapability(presented);

        if (!validation.IsValid)
        {
            // Unknown, expired, revoked and mismatched are one undifferentiated refusal on the
            // wire. A caller that could tell them apart could probe which capabilities exist.
            //
            // NoResult rather than Fail, again on purpose: a presented-but-dead capability must not
            // short-circuit into a 500-shaped failure, and it must NOT silently fall through to the
            // durable token either — it cannot, because the durable token is not in this request
            // unless the client also sent it, and A0 does not add any path that puts it there.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, string.Empty),
            new(ClaimTypes.Role, UserRoles.User),
            new(InternalClaimTypes.UserId, validation.UserId.ToString("N", CultureInfo.InvariantCulture)),
            new(InternalClaimTypes.PlaybackCapabilityId, validation.CapabilityId.ToString("N", CultureInfo.InvariantCulture)),
            new(InternalClaimTypes.PlaybackCapabilityPlaySessionId, validation.PlaySessionId ?? string.Empty),
            new(InternalClaimTypes.IsApiKey, false.ToString(CultureInfo.InvariantCulture))
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
