using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Api.Tests.PlaybackCredentials;

/// <summary>
/// #153-LTV-S1. The record in <c>HttpContext.Items</c> is the proof that a capability was
/// validated, and a response that propagates one depends on it. These pin down when it appears —
/// and, more importantly, when it must not.
/// </summary>
public class RequiresPlaybackCapabilityStashTests
{
    private const string ItemId = "994eed8437ae83552288b7a773ed00ab";
    private const string MediaSourceId = "6d5da76e3955fd1005f75c496c371521";

    [Fact]
    public async Task AValidatedCapability_IsStashedWithTheBindingsTheRouteNamed()
    {
        var context = Context("presented-value", valid: true);
        var attribute = new RequiresPlaybackCapabilityAttribute(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId");

        await attribute.OnAuthorizationAsync(context);

        var stashed = ValidatedPlaybackCapability.From(context.HttpContext);
        Assert.NotNull(stashed);
        Assert.Equal("presented-value", stashed!.Value);
        Assert.Equal(Guid.ParseExact(ItemId, "N"), stashed.ItemId);
        Assert.Equal(MediaSourceId, stashed.MediaSourceId);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task ARefusedCapability_IsNeverStashed()
    {
        // The whole point of the ordering. A stash written before the check, or on both branches,
        // would hand a refused value to the propagation path.
        var context = Context("presented-value", valid: false);
        var attribute = new RequiresPlaybackCapabilityAttribute(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId");

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(ValidatedPlaybackCapability.From(context.HttpContext));
        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    [Fact]
    public async Task ARequestPresentingNoCapability_StashesNothingAndIsNotRefused()
    {
        // A durable-token request. Nothing to propagate, and nothing minted to make one up.
        var context = Context(presented: null, valid: true);
        var attribute = new RequiresPlaybackCapabilityAttribute(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId");

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(ValidatedPlaybackCapability.From(context.HttpContext));
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task ARouteThatNamesNoMediaSource_StashesANullMediaSource()
    {
        var context = Context("presented-value", valid: true);
        var attribute = new RequiresPlaybackCapabilityAttribute(PlaybackCapabilityScope.Media, "itemId", null);

        await attribute.OnAuthorizationAsync(context);

        var stashed = ValidatedPlaybackCapability.From(context.HttpContext);
        Assert.NotNull(stashed);
        Assert.Null(stashed!.MediaSourceId);
    }

    private static AuthorizationFilterContext Context(string? presented, bool valid)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPlaybackCredentialService>(new StubCredentialService(valid));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        var query = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["mediaSourceId"] = MediaSourceId
        };

        if (presented is not null)
        {
            query[PlaybackCapabilityAuthenticationHandler.QueryKey] = presented;
        }

        httpContext.Request.Query = new QueryCollection(query);

        var routeData = new RouteData();
        routeData.Values["itemId"] = ItemId;

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, routeData, new ActionDescriptor()),
            []);
    }

    private sealed class StubCredentialService : IPlaybackCredentialService
    {
        private readonly bool _valid;

        public StubCredentialService(bool valid) => _valid = valid;

        public PlaybackCapabilityValidation ValidateCapability(string? presentedValue, PlaybackCapabilityDemand demand)
            => _valid
                ? new PlaybackCapabilityValidation(true, PlaybackCapabilityFailure.None, Guid.NewGuid(), Guid.NewGuid(), "session", "play-session")
                : new PlaybackCapabilityValidation(false, PlaybackCapabilityFailure.MediaSourceMismatch, Guid.Empty, Guid.Empty, null, null);

        public PlaybackCapabilityValidation ResolveCapability(string? presentedValue)
            => ValidateCapability(presentedValue, default);

        public PlaybackCapabilityGrant MintCapability(PlaybackCapabilityRequest request)
            => throw new InvalidOperationException("No test here may mint. An implicit mint is the defect, not the fixture.");

        public PlaybackCapabilityRenewal RenewCapability(Guid capabilityId, string sessionId)
            => throw new NotSupportedException();

        public WebSocketTicketGrant MintWebSocketTicket(WebSocketTicketRequest request)
            => throw new NotSupportedException();

        public WebSocketTicketValidation ConsumeWebSocketTicket(string? presentedValue)
            => throw new NotSupportedException();

        public int RevokeSession(string sessionId) => throw new NotSupportedException();

        public int RevokeUser(Guid userId, string? exceptSessionId) => throw new NotSupportedException();

        public int RevokeDevice(string deviceId) => throw new NotSupportedException();

        public int RevokePlaySession(string playSessionId) => throw new NotSupportedException();

        public int RevokeItem(Guid itemId) => throw new NotSupportedException();

        public IReadOnlyList<Guid> GetCapabilityIds(string sessionId) => throw new NotSupportedException();
    }
}
