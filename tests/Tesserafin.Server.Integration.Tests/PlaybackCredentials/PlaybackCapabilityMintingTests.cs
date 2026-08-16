using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// What the minting endpoint must refuse (#153-A0-R1 item 3).
/// </summary>
/// <remarks>
/// WHY MINTING IS THE ONLY PLACE THESE CAN BE CHECKED. <c>StreamingHelpers.GetStreamingState</c>
/// reads the user id off the principal and then never asks whether that user may see the item: no
/// library restriction, no blocked tag, no media-source ownership check runs anywhere on the
/// delivery path. Whatever a capability is allowed to name at minting is therefore what it can
/// fetch, forever, for its whole lifetime. Remote access and the parental schedule are the
/// exception — <c>MediaDeliveryRequirement</c> subclasses <c>DefaultAuthorizationRequirement</c>,
/// so <c>DefaultAuthorizationHandler</c> re-evaluates both on every delivery request, for a
/// capability principal as much as for a durable token.
/// </remarks>
[Collection(MintingCollection.Name)]
public sealed class PlaybackCapabilityMintingTests
{
    private readonly MintingFixture _fixture;

    public PlaybackCapabilityMintingTests(MintingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Minting_for_an_item_that_does_not_exist_is_refused()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Media],
            Guid.NewGuid(),
            mediaSourceId: null);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    /// <summary>
    /// A capability minted for an item the caller may not see would be a permanent bypass: the
    /// delivery path never re-checks visibility, so the restriction would survive exactly as long
    /// as it took the restricted user to ask for a credential.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Minting_for_an_item_the_caller_may_not_see_is_refused()
    {
        using var restricted = _fixture.ClientFor(_fixture.RestrictedDeviceId, _fixture.RestrictedToken);

        var (status, _) = await MintingFixture.MintAsync(
            restricted,
            [PlaybackCapabilityScope.Media],
            _fixture.BlockedItemId,
            mediaSourceId: null);

        Assert.Equal(HttpStatusCode.NotFound, status);

        // The negative direction: the same user, the same request shape, an item they MAY see.
        // Without this the assertion above would also pass against an endpoint that refused
        // everything.
        var (allowed, _) = await MintingFixture.MintAsync(
            restricted,
            [PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            mediaSourceId: null);

        Assert.Equal(HttpStatusCode.OK, allowed);
    }

    /// <summary>
    /// "Not found" and "not yours" answer identically, or the response becomes an oracle for which
    /// items exist on a server the caller cannot browse.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task A_hidden_item_and_a_missing_item_are_indistinguishable()
    {
        using var restricted = _fixture.ClientFor(_fixture.RestrictedDeviceId, _fixture.RestrictedToken);

        var (hiddenStatus, hiddenBody) = await MintingFixture.MintAsync(
            restricted,
            [PlaybackCapabilityScope.Media],
            _fixture.BlockedItemId,
            mediaSourceId: null);

        var (missingStatus, missingBody) = await MintingFixture.MintAsync(
            restricted,
            [PlaybackCapabilityScope.Media],
            Guid.NewGuid(),
            mediaSourceId: null);

        Assert.Equal(missingStatus, hiddenStatus);

        // The framework stamps a per-request traceId into its ProblemDetails body. It differs
        // between any two requests and says nothing about the item, so it is normalised out
        // rather than asserted around.
        Assert.Equal(WithoutTraceId(missingBody), WithoutTraceId(hiddenBody));
    }

    [Fact]
    public async Task Minting_with_a_media_source_that_belongs_to_another_item_is_refused()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            _fixture.OtherMediaSourceId);

        Assert.Equal(HttpStatusCode.NotFound, status);

        var (own, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            _fixture.VisibleMediaSourceId);

        Assert.Equal(HttpStatusCode.OK, own);
    }

    [Fact]
    public async Task Minting_with_a_media_source_that_belongs_to_nothing_is_refused()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            "not-a-media-source");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    /// <summary>
    /// A play session the server already knows belongs to a device. Naming someone else's is not a
    /// binding, it is a claim about a playback that is not yours.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Minting_for_a_play_session_owned_by_another_device_is_refused()
    {
        _fixture.Factory.TranscodingJobs = playSessionId => playSessionId switch
        {
            "r1-foreign-play-session" => Job(playSessionId, "someone-elses-device"),
            "r1-own-play-session" => Job(playSessionId, _fixture.OwnerDeviceId),
            _ => null
        };

        try
        {
            using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

            var (foreign, _) = await MintingFixture.MintAsync(
                client,
                [PlaybackCapabilityScope.Media],
                _fixture.VisibleItemId,
                _fixture.VisibleMediaSourceId,
                "r1-foreign-play-session");

            Assert.Equal(HttpStatusCode.NotFound, foreign);

            // The same shape, this device's own play session, so the refusal above is attributable
            // to ownership and not to the check existing at all.
            var (own, _) = await MintingFixture.MintAsync(
                client,
                [PlaybackCapabilityScope.Media],
                _fixture.VisibleItemId,
                _fixture.VisibleMediaSourceId,
                "r1-own-play-session");

            Assert.Equal(HttpStatusCode.OK, own);
        }
        finally
        {
            _fixture.Factory.TranscodingJobs = _ => null;
        }
    }

    /// <summary>
    /// A play session the server has never heard of is the ordinary direct-play case: the client
    /// chose the identifier and no transcoding job carries it. There is nothing to compare it
    /// against, so minting accepts it and the binding is enforced at delivery instead.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task An_unknown_play_session_is_accepted_because_nothing_owns_it()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            _fixture.VisibleMediaSourceId,
            "r1-never-seen-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Minting_with_a_scope_that_is_not_defined_is_refused()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintRawAsync(
            client,
            new
            {
                PlaySessionId = "r1-undefined-scope",
                ItemId = _fixture.VisibleItemId,
                MediaSourceId = _fixture.VisibleMediaSourceId,
                Scopes = new[] { 99 }
            });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// Fonts is item-less by contract, and now that binding is compared exactly a font capability
    /// carrying an item can never satisfy a font route — the route names none. Minting it would
    /// hand back a credential that is silently unusable, which is worse than an error.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Minting_Fonts_with_an_item_is_refused()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Fonts],
            _fixture.VisibleItemId,
            mediaSourceId: null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// The same incoherence in its other form. A set containing Fonts and an item-bound scope
    /// cannot satisfy both: the font routes name no item and every other route names one, so
    /// whichever way the capability is bound, one half of its scope set is dead.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Minting_Fonts_together_with_an_item_bound_scope_is_refused()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Fonts, PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            _fixture.VisibleMediaSourceId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Minting_Fonts_alone_and_item_less_is_accepted()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, _) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Fonts],
            itemId: null,
            mediaSourceId: null);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// The grant reports what it is bound to, and it is bound to the caller — not to anything the
    /// caller asked to be bound to.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task A_grant_is_bound_to_the_callers_own_item_and_media_source()
    {
        using var client = _fixture.ClientFor(_fixture.OwnerDeviceId, _fixture.OwnerToken);

        var (status, body) = await MintingFixture.MintAsync(
            client,
            [PlaybackCapabilityScope.Media],
            _fixture.VisibleItemId,
            _fixture.VisibleMediaSourceId);

        Assert.Equal(HttpStatusCode.OK, status);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(_fixture.VisibleItemId, Guid.Parse(document.RootElement.GetProperty("ItemId").GetString()!));
        Assert.Equal(_fixture.VisibleMediaSourceId, document.RootElement.GetProperty("MediaSourceId").GetString());
    }

    private static string WithoutTraceId(string body)
        => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"\"");

    private static TranscodingJob Job(string playSessionId, string deviceId)
        => new(NullLogger<TranscodingJob>.Instance) { PlaySessionId = playSessionId, DeviceId = deviceId };
}
