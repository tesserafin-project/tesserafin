using System;
using System.Linq;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.PlaybackCredentials;

/// <summary>
/// Executable evidence for the #153-A0 credential primitives.
/// </summary>
/// <remarks>
/// NOTHING HERE SLEEPS. Expiry, the renewal window and ticket lifetime are all driven by
/// <see cref="ControllableTimeProvider"/>, and the service reads the clock at VALIDATION rather
/// than only at minting — which is the property that makes these assertions mean anything. A test
/// that advanced a clock the validator never read would pass while proving nothing, so
/// <c>Expired_capability_is_refused_and_the_clock_is_what_decides</c> below also asserts the
/// negative direction: the same capability is accepted one tick before its expiry.
/// </remarks>
public class PlaybackCredentialServiceTests
{
    private static readonly Guid UserA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ItemA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ItemB = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly ControllableTimeProvider _clock = new();
    private readonly CountingRandomSecretSource _random = new();
    private readonly PlaybackCredentialService _service;

    public PlaybackCredentialServiceTests()
    {
        _service = new PlaybackCredentialService(_clock, _random);
    }

    private static PlaybackCapabilityRequest Request(
        Guid? userId = null,
        string sessionId = "session-a",
        string deviceId = "device-a",
        string playSessionId = "play-a",
        Guid? itemId = null,
        string? mediaSourceId = "source-a",
        params PlaybackCapabilityScope[] scopes)
        => new(
            userId ?? UserA,
            sessionId,
            deviceId,
            playSessionId,
            itemId ?? ItemA,
            mediaSourceId,
            scopes.Length == 0 ? new[] { PlaybackCapabilityScope.Media } : scopes);

    private static PlaybackCapabilityDemand Demand(
        PlaybackCapabilityScope scope = PlaybackCapabilityScope.Media,
        Guid? itemId = null,
        string? mediaSourceId = "source-a")
        => new(scope, itemId ?? ItemA, mediaSourceId);

    /// <summary>
    /// A genuinely item-less, source-less request. <see cref="Request"/> cannot express one: its
    /// <c>itemId ?? ItemA</c> quietly substitutes an item for a caller that asked for none, which
    /// is how the font tests came to be minting item-BOUND font capabilities and asserting they
    /// were accepted. Exact binding surfaced that; the helper is what made it invisible.
    /// </summary>
    /// <param name="playSessionId">The play session to bind to.</param>
    /// <returns>An item-less font request.</returns>
    private static PlaybackCapabilityRequest FontRequest(string playSessionId = "play-a")
        => new(UserA, "session-a", "device-a", playSessionId, null, null, new[] { PlaybackCapabilityScope.Fonts });

    // ---------------------------------------------------------------- minting and entropy

    [Fact]
    public void Minting_draws_256_bits_from_the_injected_randomness_boundary()
    {
        _service.MintCapability(Request());

        Assert.Equal(1, _random.CallCount);
        Assert.Equal(32, _random.LargestRequestedByteCount);
        Assert.Equal(32, PlaybackCredentialService.SecretByteCount);
    }

    [Fact]
    public void Minting_without_a_scope_is_refused_because_it_would_grant_nothing()
    {
        var request = new PlaybackCapabilityRequest(
            UserA, "session-a", "device-a", "play-a", ItemA, "source-a", Array.Empty<PlaybackCapabilityScope>());

        Assert.Throws<ArgumentException>(() => _service.MintCapability(request));
    }

    [Fact]
    public void Two_capabilities_never_share_a_value_or_an_identifier()
    {
        var first = _service.MintCapability(Request());
        var second = _service.MintCapability(Request(playSessionId: "play-b"));

        Assert.NotEqual(first.Value, second.Value);
        Assert.NotEqual(first.CapabilityId, second.CapabilityId);
    }

    // ---------------------------------------------------------------- the accepted paths

    [Theory]
    [InlineData(PlaybackCapabilityScope.Media)]
    [InlineData(PlaybackCapabilityScope.Subtitles)]
    [InlineData(PlaybackCapabilityScope.Attachments)]
    [InlineData(PlaybackCapabilityScope.Trickplay)]
    public void An_item_bound_scope_is_accepted_for_its_own_item_and_media_source(PlaybackCapabilityScope scope)
    {
        var grant = _service.MintCapability(Request(scopes: scope));

        var validation = _service.ValidateCapability(grant.Value, Demand(scope));

        Assert.True(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.None, validation.Failure);
        Assert.Equal(UserA, validation.UserId);
        Assert.Equal("play-a", validation.PlaySessionId);
    }

    [Fact]
    public void One_capability_can_carry_several_scopes_at_once()
    {
        // A player fetching a stream, its subtitles and its trickplay tiles holds one capability,
        // not three: three would mean three minting round trips before the first frame.
        var grant = _service.MintCapability(Request(
            scopes: new[] { PlaybackCapabilityScope.Media, PlaybackCapabilityScope.Subtitles, PlaybackCapabilityScope.Trickplay }));

        Assert.True(_service.ValidateCapability(grant.Value, Demand(PlaybackCapabilityScope.Media)).IsValid);
        Assert.True(_service.ValidateCapability(grant.Value, Demand(PlaybackCapabilityScope.Subtitles)).IsValid);
        Assert.True(_service.ValidateCapability(grant.Value, Demand(PlaybackCapabilityScope.Trickplay)).IsValid);
        Assert.False(_service.ValidateCapability(grant.Value, Demand(PlaybackCapabilityScope.Attachments)).IsValid);
    }

    [Fact]
    public void A_font_capability_carries_no_item_and_is_still_accepted()
    {
        // The "narrowed further" case. A fallback font belongs to no item, so a font capability
        // that had to name one would either be impossible to mint or would name an unrelated item.
        var grant = _service.MintCapability(FontRequest());

        var validation = _service.ValidateCapability(
            grant.Value,
            new PlaybackCapabilityDemand(PlaybackCapabilityScope.Fonts, null, null));

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void A_font_capability_grants_no_media()
    {
        var grant = _service.MintCapability(FontRequest());

        var validation = _service.ValidateCapability(grant.Value, Demand());

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.ScopeMismatch, validation.Failure);
    }

    [Fact]
    public void A_capability_that_names_no_media_source_reaches_a_route_that_names_none()
    {
        // The legacy HLS segment routes name an item and no media source. A capability minted the
        // same way agrees with them, and that is the ONLY capability shape they accept.
        var grant = _service.MintCapability(Request(mediaSourceId: null));

        Assert.True(_service.ValidateCapability(grant.Value, Demand(mediaSourceId: null)).IsValid);
    }

    // ------------------------------------------------- exact binding, all four permutations
    //
    // Two of these cannot be reached over HTTP any more, because the minting checks refuse to
    // issue a capability whose media source does not belong to its item. They are asserted here
    // instead, against the validator directly, so the rule has evidence at the level it lives at.
    // See MediaAuthorizationBoundaryTests for the two that do survive at request level.

    [Fact]
    public void A_bound_item_is_refused_where_the_demand_names_none()
    {
        var grant = _service.MintCapability(Request());

        var validation = _service.ValidateCapability(grant.Value, Demand(itemId: Guid.Empty) with { ItemId = null });

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.ItemMismatch, validation.Failure);
    }

    [Fact]
    public void An_unbound_item_is_refused_where_the_demand_names_one()
    {
        var grant = _service.MintCapability(FontRequest());

        var validation = _service.ValidateCapability(
            grant.Value,
            new PlaybackCapabilityDemand(PlaybackCapabilityScope.Fonts, ItemA, null));

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.ItemMismatch, validation.Failure);
    }

    [Fact]
    public void A_bound_media_source_is_refused_where_the_demand_names_none()
    {
        var grant = _service.MintCapability(Request());

        var validation = _service.ValidateCapability(grant.Value, Demand(mediaSourceId: null));

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.MediaSourceMismatch, validation.Failure);
    }

    [Fact]
    public void An_unbound_media_source_is_refused_where_the_demand_names_one()
    {
        var grant = _service.MintCapability(Request(mediaSourceId: null));

        var validation = _service.ValidateCapability(grant.Value, Demand());

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.MediaSourceMismatch, validation.Failure);
    }

    [Fact]
    public void A_mismatched_play_session_is_refused_where_the_route_exposes_one()
    {
        var grant = _service.MintCapability(Request());

        var validation = _service.ValidateCapability(
            grant.Value,
            Demand() with { PlaySessionId = "play-b" });

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.PlaySessionMismatch, validation.Failure);
    }

    [Fact]
    public void A_matching_play_session_is_accepted_and_an_absent_one_is_not_demanded()
    {
        // Asymmetric on purpose, and the asymmetry is the assertion: the route may expose
        // playSessionId without the client sending it, and refusing that absence would make an
        // optional query parameter mandatory on routes that have never required it.
        var grant = _service.MintCapability(Request());

        Assert.True(_service.ValidateCapability(grant.Value, Demand() with { PlaySessionId = "play-a" }).IsValid);
        Assert.True(_service.ValidateCapability(grant.Value, Demand()).IsValid);
    }

    // ---------------------------------------------------------------- the refused paths

    [Fact]
    public void A_capability_for_another_item_is_refused()
    {
        var grant = _service.MintCapability(Request());

        var validation = _service.ValidateCapability(grant.Value, Demand(itemId: ItemB));

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.ItemMismatch, validation.Failure);
    }

    [Fact]
    public void A_capability_for_another_media_source_is_refused()
    {
        var grant = _service.MintCapability(Request());

        var validation = _service.ValidateCapability(grant.Value, Demand(mediaSourceId: "source-b"));

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.MediaSourceMismatch, validation.Failure);
    }

    [Fact]
    public void A_capability_that_was_never_minted_is_refused()
    {
        var validation = _service.ValidateCapability("not-a-capability", Demand());

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.Unknown, validation.Failure);
    }

    [Fact]
    public void An_absent_capability_is_refused_as_missing_rather_than_unknown()
    {
        Assert.Equal(PlaybackCapabilityFailure.Missing, _service.ValidateCapability(null, Demand()).Failure);
        Assert.Equal(PlaybackCapabilityFailure.Missing, _service.ValidateCapability(string.Empty, Demand()).Failure);
    }

    [Fact]
    public void Expired_capability_is_refused_and_the_clock_is_what_decides()
    {
        var grant = _service.MintCapability(Request());

        // One tick before expiry it is still good. Without this half the test would also pass
        // against a validator that ignored the clock and rejected everything.
        _clock.Advance(PlaybackCredentialService.CapabilityLifetime - TimeSpan.FromTicks(1));
        Assert.True(_service.ValidateCapability(grant.Value, Demand()).IsValid);

        _clock.Advance(TimeSpan.FromTicks(1));
        var expired = _service.ValidateCapability(grant.Value, Demand());

        Assert.False(expired.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.Expired, expired.Failure);
    }

    // ---------------------------------------------------------------- renewal

    [Fact]
    public void Renewal_inside_the_window_moves_the_expiry_without_changing_the_value()
    {
        var grant = _service.MintCapability(Request());
        _clock.Advance(PlaybackCredentialService.CapabilityLifetime - PlaybackCredentialService.CapabilityRenewalWindow);

        var renewal = _service.RenewCapability(grant.CapabilityId, "session-a");

        Assert.True(renewal.Succeeded);
        Assert.Equal(grant.IssuedAt, renewal.IssuedAt);
        Assert.True(renewal.ExpiresAt > grant.ExpiresAt);

        // The client keeps using the value it already holds; renewal never reissues a secret.
        Assert.True(_service.ValidateCapability(grant.Value, Demand()).IsValid);
    }

    [Fact]
    public void Renewal_before_the_window_opens_is_refused()
    {
        // Otherwise a client could renew from the moment of issue and chain a short-lived
        // credential into a durable one with extra steps.
        var grant = _service.MintCapability(Request());

        var renewal = _service.RenewCapability(grant.CapabilityId, "session-a");

        Assert.False(renewal.Succeeded);
        Assert.Equal(PlaybackCapabilityFailure.RenewalTooEarly, renewal.Failure);
    }

    [Fact]
    public void Renewal_after_expiry_is_refused_rather_than_resurrecting_the_capability()
    {
        var grant = _service.MintCapability(Request());
        _clock.Advance(PlaybackCredentialService.CapabilityLifetime);

        var renewal = _service.RenewCapability(grant.CapabilityId, "session-a");

        Assert.False(renewal.Succeeded);
        Assert.Equal(PlaybackCapabilityFailure.RenewalAfterExpiry, renewal.Failure);
    }

    [Fact]
    public void Renewal_from_another_session_is_refused()
    {
        var grant = _service.MintCapability(Request());
        _clock.Advance(PlaybackCredentialService.CapabilityLifetime - PlaybackCredentialService.CapabilityRenewalWindow);

        var renewal = _service.RenewCapability(grant.CapabilityId, "session-b");

        Assert.False(renewal.Succeeded);
        Assert.Equal(PlaybackCapabilityFailure.SessionMismatch, renewal.Failure);
    }

    [Fact]
    public void Renewal_after_the_owning_session_was_invalidated_is_refused()
    {
        var grant = _service.MintCapability(Request());
        _clock.Advance(PlaybackCredentialService.CapabilityLifetime - PlaybackCredentialService.CapabilityRenewalWindow);
        _service.RevokeSession("session-a");

        var renewal = _service.RenewCapability(grant.CapabilityId, "session-a");

        Assert.False(renewal.Succeeded);
        Assert.Equal(PlaybackCapabilityFailure.Unknown, renewal.Failure);
    }

    // ---------------------------------------------------------------- revocation

    [Fact]
    public void Logout_style_session_revocation_refuses_the_capability_as_revoked()
    {
        var grant = _service.MintCapability(Request());

        Assert.Equal(1, _service.RevokeSession("session-a"));

        var validation = _service.ValidateCapability(grant.Value, Demand());
        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.Revoked, validation.Failure);
    }

    [Fact]
    public void Device_deletion_revokes_the_capabilities_of_that_device_only()
    {
        var mine = _service.MintCapability(Request(deviceId: "device-a"));
        var theirs = _service.MintCapability(Request(sessionId: "session-b", deviceId: "device-b"));

        _service.RevokeDevice("device-a");

        Assert.False(_service.ValidateCapability(mine.Value, Demand()).IsValid);
        Assert.True(_service.ValidateCapability(theirs.Value, Demand()).IsValid);
    }

    [Fact]
    public void Password_change_revocation_can_spare_the_session_that_asked_for_it()
    {
        var caller = _service.MintCapability(Request(sessionId: "session-a"));
        var elsewhere = _service.MintCapability(Request(sessionId: "session-b"));

        _service.RevokeUser(UserA, exceptSessionId: "session-a");

        Assert.True(_service.ValidateCapability(caller.Value, Demand()).IsValid);
        Assert.False(_service.ValidateCapability(elsewhere.Value, Demand()).IsValid);
    }

    [Fact]
    public void Ending_one_play_session_leaves_the_others_playing()
    {
        // The reason a capability binds to a play session rather than to a user: someone watching
        // two things at once must not lose both because one stopped.
        var first = _service.MintCapability(Request(playSessionId: "play-a"));
        var second = _service.MintCapability(Request(playSessionId: "play-b", itemId: ItemB));

        Assert.Equal(1, _service.RevokePlaySession("play-a"));

        Assert.False(_service.ValidateCapability(first.Value, Demand()).IsValid);
        Assert.True(_service.ValidateCapability(second.Value, Demand(itemId: ItemB)).IsValid);
    }

    [Fact]
    public void Two_accounts_in_one_browser_do_not_share_credentials()
    {
        var a = _service.MintCapability(Request(userId: UserA, sessionId: "session-a"));
        var b = _service.MintCapability(Request(userId: UserB, sessionId: "session-b"));

        _service.RevokeUser(UserA, exceptSessionId: null);

        Assert.False(_service.ValidateCapability(a.Value, Demand()).IsValid);
        Assert.True(_service.ValidateCapability(b.Value, Demand()).IsValid);
    }

    [Fact]
    public void Concurrent_playback_sessions_do_not_collide()
    {
        var first = _service.MintCapability(Request(playSessionId: "play-a", itemId: ItemA));
        var second = _service.MintCapability(Request(playSessionId: "play-b", itemId: ItemB));

        Assert.True(_service.ValidateCapability(first.Value, Demand(itemId: ItemA)).IsValid);
        Assert.True(_service.ValidateCapability(second.Value, Demand(itemId: ItemB)).IsValid);
        Assert.False(_service.ValidateCapability(first.Value, Demand(itemId: ItemB)).IsValid);
        Assert.False(_service.ValidateCapability(second.Value, Demand(itemId: ItemA)).IsValid);
    }

    [Fact]
    public void A_restart_loses_every_credential_which_is_the_frozen_contract()
    {
        // The store is in memory because SessionManager's own session table is. A fresh instance IS
        // a restart, and the contract says the client re-mints with its durable token.
        var grant = _service.MintCapability(Request());
        var afterRestart = new PlaybackCredentialService(_clock, _random);

        var validation = afterRestart.ValidateCapability(grant.Value, Demand());

        Assert.False(validation.IsValid);
        Assert.Equal(PlaybackCapabilityFailure.Unknown, validation.Failure);
    }

    [Fact]
    public void Live_capability_ids_can_be_enumerated_for_a_session_without_exposing_a_secret()
    {
        var grant = _service.MintCapability(Request());

        var ids = _service.GetCapabilityIds("session-a");

        Assert.Equal(new[] { grant.CapabilityId }, ids);
        Assert.Empty(_service.GetCapabilityIds("session-b"));
    }

    // ---------------------------------------------------------------- WebSocket tickets

    [Fact]
    public void A_ticket_is_accepted_once()
    {
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));

        var consumed = _service.ConsumeWebSocketTicket(ticket.Value);

        Assert.True(consumed.IsValid);
        Assert.Equal(UserA, consumed.UserId);
        Assert.Equal("session-a", consumed.SessionId);
        Assert.Equal("device-a", consumed.DeviceId);
    }

    [Fact]
    public void A_replayed_ticket_is_refused()
    {
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));
        Assert.True(_service.ConsumeWebSocketTicket(ticket.Value).IsValid);

        var replay = _service.ConsumeWebSocketTicket(ticket.Value);

        Assert.False(replay.IsValid);
        Assert.Equal(WebSocketTicketFailure.AlreadyUsed, replay.Failure);
    }

    [Fact]
    public void An_expired_ticket_is_refused_and_the_clock_is_what_decides()
    {
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));
        _clock.Advance(PlaybackCredentialService.WebSocketTicketLifetime);

        var consumed = _service.ConsumeWebSocketTicket(ticket.Value);

        Assert.False(consumed.IsValid);
        Assert.Equal(WebSocketTicketFailure.Expired, consumed.Failure);
    }

    [Fact]
    public void A_ticket_that_was_never_minted_is_refused()
    {
        Assert.Equal(WebSocketTicketFailure.Unknown, _service.ConsumeWebSocketTicket("not-a-ticket").Failure);
        Assert.Equal(WebSocketTicketFailure.Missing, _service.ConsumeWebSocketTicket(null).Failure);
    }

    [Fact]
    public void A_ticket_is_revoked_with_its_session()
    {
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));

        _service.RevokeSession("session-a");

        var consumed = _service.ConsumeWebSocketTicket(ticket.Value);
        Assert.False(consumed.IsValid);
        Assert.Equal(WebSocketTicketFailure.Revoked, consumed.Failure);
    }

    [Fact]
    public void A_ticket_is_revoked_with_its_device()
    {
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));

        _service.RevokeDevice("device-a");

        Assert.False(_service.ConsumeWebSocketTicket(ticket.Value).IsValid);
    }

    [Fact]
    public void A_ticket_is_revoked_when_a_password_change_invalidates_its_user()
    {
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));

        _service.RevokeUser(UserA, exceptSessionId: null);

        Assert.False(_service.ConsumeWebSocketTicket(ticket.Value).IsValid);
    }

    // ---------------------------------------------------------------- namespace separation

    [Fact]
    public void A_ticket_is_not_a_capability_and_a_capability_is_not_a_ticket()
    {
        // Two types, two stores, two namespaces. If either value were accepted by the other's
        // validator, "unusable for HTTP media" and "valid only during upgrade" would both be prose.
        var capability = _service.MintCapability(Request());
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));

        Assert.False(_service.ConsumeWebSocketTicket(capability.Value).IsValid);
        Assert.False(_service.ValidateCapability(ticket.Value, Demand()).IsValid);
    }

    [Fact]
    public void Revoking_a_play_session_leaves_the_sessions_websocket_ticket_alone()
    {
        // A play session ending is not the browser session ending; the socket keeps running.
        var ticket = _service.MintWebSocketTicket(new WebSocketTicketRequest(UserA, "session-a", "device-a"));
        _service.MintCapability(Request(playSessionId: "play-a"));

        _service.RevokePlaySession("play-a");

        Assert.True(_service.ConsumeWebSocketTicket(ticket.Value).IsValid);
    }

    // ---------------------------------------------------------------- secret handling

    [Fact]
    public void The_presented_value_is_never_recoverable_from_the_service()
    {
        // Nothing on the public surface returns a secret after minting. This is asserted rather
        // than assumed because "the store keeps only a verifier" is a claim about an API, and an
        // API can grow a getter.
        var grant = _service.MintCapability(Request());

        var surface = typeof(IPlaybackCredentialService)
            .GetMethods()
            .Select(m => m.ReturnType)
            .ToArray();

        Assert.DoesNotContain(typeof(string), surface);
        Assert.NotEqual(string.Empty, grant.Value);
    }
}
