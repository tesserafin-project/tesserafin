using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Request-level evidence for the media authorization boundary (#153-A0-R1), one real HTTP request
/// at a time, over every media class #153 names.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. A0 proved the credential primitives in isolation and the route table by
/// reflection, and neither can answer the question an operator actually has: what does the server
/// return to this request. The structural test keeps passing while a route serves media to nobody
/// in particular, because a route table says nothing about what the action does. This suite makes
/// every claim against a booted server, a real library item, and real bytes on disk.
///
/// WHAT A REFUSAL HAS TO LOOK LIKE. 401 or 403, and a body that is not the fixture's media. Both
/// halves matter: a 200 carrying an error page would be a refusal by status alone, and a 401 that
/// still streamed the file would be a refusal by nothing at all.
/// </remarks>
[Collection(MediaBoundaryCollection.Name)]
public sealed class MediaAuthorizationBoundaryTests
{
    private readonly MediaBoundaryFixture _fixture;

    public MediaAuthorizationBoundaryTests(MediaBoundaryFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> AllRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex)
            .Select(route => new object[] { route.Name });

    public static IEnumerable<object[]> ItemBoundRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex)
            .Where(route => route.ItemBound)
            .Select(route => new object[] { route.Name });

    public static IEnumerable<object[]> MediaSourceBoundRoutes()
        => MediaRouteCatalog
            .For(Guid.Empty, string.Empty, MediaBoundaryFixture.SubtitleStreamIndex)
            .Where(route => route.MediaSourceBound)
            .Select(route => new object[] { route.Name });

    // ---------------------------------------------------------------------------------------
    // 1. No credential at all.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The headline claim of R1. Before the repair, five of these routes answered 200 with the
    /// fixture's own bytes and two more answered 200 with the fixture's subtitle text, to a client
    /// that presented nothing.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task No_credential_is_refused_and_never_returns_media(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.AnonymousClient();

        var (status, body) = await MediaBoundaryFixture.SendAsync(client, route.Method, route.Path);

        AssertRefused(status, body, route, "a request that presented no credential at all");
    }

    // ---------------------------------------------------------------------------------------
    // 2. The durable token still works, in a header and in the query string.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A0's compatibility promise, kept: the legacy request still reaches its action.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task The_durable_token_in_a_header_still_reaches_the_action(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.DurableHeaderClient();

        var (status, _) = await MediaBoundaryFixture.SendAsync(client, route.Method, route.Path);

        AssertReachedTheAction(status, route, "the durable token in the Authorization header");
    }

    /// <summary>
    /// The transport #153 exists to replace is still accepted, because A0-R1 does not remove it —
    /// it stops it being the only thing standing between an anonymous caller and the file.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task The_durable_token_in_the_query_string_still_reaches_the_action(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.AnonymousClient();

        var (status, _) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("ApiKey", _fixture.DurableToken));

        AssertReachedTheAction(status, route, "the durable token in the ApiKey query parameter");
    }

    // ---------------------------------------------------------------------------------------
    // 3. A correctly scoped capability.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The capability reaches the real action, and where the fixture can produce bytes it returns
    /// exactly the fixture's bytes.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task A_correctly_scoped_capability_reaches_the_action(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintForAsync(route);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertReachedTheAction(status, route, "a correctly scoped capability");

        if (route.Evidence == MediaRouteEvidence.Bytes && route.Scope == PlaybackCapabilityScope.Media)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(_fixture.MediaBytes, body);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 4. Expiry and revocation.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Expiry is decided by the clock the validator reads, not by the clock the minter read.
    /// Nothing here sleeps.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task An_expired_capability_is_refused(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintForAsync(route);
        var path = route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value));

        // The negative direction first: one tick before expiry it still works. Without this, a
        // validator that refused everything would pass the assertion below.
        using (var early = _fixture.AnonymousClient())
        {
            var (acceptedStatus, _) = await MediaBoundaryFixture.SendAsync(early, route.Method, path);
            AssertReachedTheAction(acceptedStatus, route, "a capability one tick inside its lifetime");
        }

        _fixture.Factory.Clock.Advance(PlaybackCredentialService.CapabilityLifetime + TimeSpan.FromSeconds(1));

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(client, route.Method, path);

        AssertRefused(status, body, route, "an expired capability");
    }

    /// <summary>
    /// Revocation reaches the capability through the session that owns it. The session revoked here
    /// is a second device, so the rest of the suite keeps its own token.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task A_revoked_capability_is_refused(string routeName)
    {
        var route = Route(routeName);
        var deviceId = "r1-revocation-" + Guid.NewGuid().ToString("N");
        var token = await _fixture.AuthenticateAsync(deviceId);

        using var owner = _fixture.ClientFor(deviceId, token);
        var capability = await MediaBoundaryFixture.MintWithAsync(
            owner,
            [route.Scope],
            route.ItemBound ? _fixture.ItemId : null,
            route.MediaSourceBound ? _fixture.MediaSourceId : null);

        using (var logout = await owner.PostAsync("/Sessions/Logout", content: null, TestContext.Current.CancellationToken))
        {
            Assert.True(logout.IsSuccessStatusCode, $"Logout answered {(int)logout.StatusCode}.");
        }

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability whose owning session was logged out");
    }

    // ---------------------------------------------------------------------------------------
    // 5. No fallback.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A bad capability is not repaired by a good durable token. If it were, a client could present
    /// both and the capability's scope, item, expiry and revocation would all be decoration.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task An_invalid_capability_is_not_rescued_by_a_valid_durable_token(string routeName)
    {
        var route = Route(routeName);
        using var client = _fixture.DurableHeaderClient();

        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", "not-a-capability"));

        AssertRefused(status, body, route, "an invalid capability presented alongside a valid durable token");
    }

    // ---------------------------------------------------------------------------------------
    // 6. Binding.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A capability minted for another item does not reach this one.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(ItemBoundRoutes))]
    public async Task A_capability_bound_to_another_item_is_refused(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintAsync(
            [route.Scope],
            _fixture.OtherItemId,
            route.MediaSourceBound ? _fixture.OtherMediaSourceId : null);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability bound to a different item");
    }

    /// <summary>
    /// A capability minted for another media source does not reach this one.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(MediaSourceBoundRoutes))]
    public async Task A_capability_bound_to_another_media_source_is_refused(string routeName)
    {
        var route = Route(routeName);
        var capability = await _fixture.MintAsync([route.Scope], _fixture.ItemId, _fixture.OtherMediaSourceId);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability bound to a different media source");
    }

    /// <summary>
    /// The defect item 4 names first: a capability BOUND to a media source, presented on a route
    /// that names none, was accepted because the demand was null and a null demand matched
    /// anything. A binding that only holds when the route bothers to state it is not a binding.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task A_media_source_bound_capability_is_refused_where_the_route_names_no_media_source()
    {
        var route = Route("hls legacy segment");
        Assert.False(route.MediaSourceBound, "This test needs a route that names no media source.");

        var capability = await _fixture.MintAsync([PlaybackCapabilityScope.Media], _fixture.ItemId, _fixture.MediaSourceId);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a media-source-bound capability on a route that names no media source");
    }

    /// <summary>
    /// The mirror of the above: a capability bound to NO media source, presented on a route that
    /// does name one. The route's demand is specific; a capability that cannot answer it is not
    /// entitled to be treated as if it had.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(MediaSourceBoundRoutes))]
    public async Task An_unbound_capability_is_refused_where_the_route_names_a_media_source(string routeName)
    {
        var route = Route(routeName);
        if (route.Scope == PlaybackCapabilityScope.Fonts)
        {
            return;
        }

        var capability = await _fixture.MintAsync([route.Scope], _fixture.ItemId, mediaSourceId: null);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, "a capability bound to no media source on a route that names one");
    }

    /// <summary>
    /// Scope is per route, not per credential. A Fonts capability is the sharpest case: it is
    /// item-less by contract, so if scope were not compared it would reach every media route
    /// without ever naming an item.
    /// </summary>
    /// <param name="routeName">The route under test.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task A_capability_for_the_wrong_scope_is_refused(string routeName)
    {
        var route = Route(routeName);
        var wrongScope = route.Scope == PlaybackCapabilityScope.Fonts
            ? PlaybackCapabilityScope.Trickplay
            : PlaybackCapabilityScope.Fonts;

        // Bound exactly as the route is, so scope is the ONLY thing that differs — except on the
        // font routes, where the substitute scope is item-bound by contract and cannot be minted
        // without an item. Scope is compared before item and media source, so the refusal there is
        // still attributable to scope.
        var capability = await _fixture.MintAsync(
            [wrongScope],
            route.ItemBound || wrongScope != PlaybackCapabilityScope.Fonts ? _fixture.ItemId : null,
            route.MediaSourceBound ? _fixture.MediaSourceId : null);

        using var client = _fixture.AnonymousClient();
        var (status, body) = await MediaBoundaryFixture.SendAsync(
            client,
            route.Method,
            route.WithQuery("playbackCapability", Uri.EscapeDataString(capability.Value)));

        AssertRefused(status, body, route, $"a {wrongScope} capability on a {route.Scope} route");
    }

    // ---------------------------------------------------------------------------------------
    // 7. Not a general-API credential.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A capability is not a credential outside media delivery, on any general endpoint, under any
    /// query key the general authorization path reads.
    /// </summary>
    /// <param name="path">The general endpoint to try.</param>
    /// <param name="key">The query key to smuggle the capability into.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("/Items", "playbackCapability")]
    [InlineData("/Items", "ApiKey")]
    [InlineData("/Items", "api_key")]
    [InlineData("/Users/Me", "playbackCapability")]
    [InlineData("/Users/Me", "ApiKey")]
    [InlineData("/System/Info", "playbackCapability")]
    [InlineData("/System/Info", "ApiKey")]
    [InlineData("/Sessions", "playbackCapability")]
    [InlineData("/Sessions", "ApiKey")]
    [InlineData("/Library/VirtualFolders", "playbackCapability")]
    public async Task A_capability_is_not_a_credential_on_a_general_endpoint(string path, string key)
    {
        var capability = await _fixture.MintAsync([PlaybackCapabilityScope.Fonts], null, null);

        using var client = _fixture.AnonymousClient();
        var (status, _) = await MediaBoundaryFixture.SendAsync(
            client,
            "GET",
            $"{path}?{key}={Uri.EscapeDataString(capability.Value)}");

        Assert.True(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"GET {path} with the capability in '{key}' answered {(int)status}. A capability must never authenticate a general endpoint.");
    }

    private MediaRoute Route(string name)
        => _fixture.Routes.Single(route => string.Equals(route.Name, name, StringComparison.Ordinal));

    private void AssertRefused(HttpStatusCode status, byte[] body, MediaRoute route, string what)
    {
        Assert.True(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"[{route.MediaClass}] {route.Method} {route.Path} answered {(int)status} to {what}. Expected 401 or 403.");

        Assert.False(
            body.Length == _fixture.MediaBytes.Length && body.SequenceEqual(_fixture.MediaBytes),
            $"[{route.MediaClass}] {route.Method} {route.Path} returned the fixture's media bytes to {what}.");
    }

    private static void AssertReachedTheAction(HttpStatusCode status, MediaRoute route, string what)
    {
        Assert.False(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"[{route.MediaClass}] {route.Method} {route.Path} answered {(int)status} to {what}. Authorization refused a request it had to admit.");
    }
}
