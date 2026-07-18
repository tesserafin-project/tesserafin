using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Reefin.Api.Middleware.Tests;

/// <summary>
/// Issue #42. These tests pin the ONE property that makes this identifier what it is: it is
/// per-HTTP-request, and therefore <b>changes between two requests even when those requests concern
/// the same playback session</b>. That is the exact opposite of what <c>PlaybackAttemptId</c>
/// (issue #43) must do, which is why issue #34 was split rather than implemented as one field.
/// </summary>
public sealed class RequestCorrelationMiddlewareTests
{
    /// <summary>
    /// The acceptance criterion of issue #42, stated directly: a <c>POST Playback/Sessions</c>
    /// followed by a <c>PUT Playback/Sessions/{id}</c> for the SAME session produce two DIFFERENT
    /// request ids, while the session-scoped identifier they carry stays identical.
    /// </summary>
    [Fact]
    public async Task TwoRequestsOnSameSession_ProduceDifferentRequestIds_ButSamePlaySessionId()
    {
        const string PlaySessionId = "play-session-fixed-for-the-whole-session";
        var observed = new List<(string RequestId, string PlaySessionId)>();

        // Stands in for the controller action: whatever handler runs, it sees the request id the
        // middleware just assigned, and the play session id the client sent.
        var middleware = new RequestCorrelationMiddleware(
            ctx =>
            {
                observed.Add((RequestCorrelation.Get(ctx)!, ctx.Request.Headers["X-PlaySessionId"].ToString()));
                return Task.CompletedTask;
            },
            NullLogger<RequestCorrelationMiddleware>.Instance);

        await middleware.Invoke(BuildContext("POST", "/Playback/Sessions", PlaySessionId));
        await middleware.Invoke(BuildContext("PUT", "/Playback/Sessions/abc", PlaySessionId));

        Assert.Equal(2, observed.Count);
        Assert.All(observed, o => Assert.False(string.IsNullOrEmpty(o.RequestId)));

        // The whole point: request scope is strictly narrower than session scope.
        Assert.NotEqual(observed[0].RequestId, observed[1].RequestId);
        Assert.Equal(observed[0].PlaySessionId, observed[1].PlaySessionId);
        Assert.Equal(PlaySessionId, observed[0].PlaySessionId);
    }

    [Fact]
    public async Task Invoke_EchoesRequestIdOnResponseHeader()
    {
        var context = BuildContext("GET", "/Playback/Sessions/abc/Stream", playSessionId: null);
        var responseFeature = new CallbackCapturingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);

        string? assigned = null;
        var middleware = new RequestCorrelationMiddleware(
            ctx =>
            {
                assigned = RequestCorrelation.Get(ctx);
                return Task.CompletedTask;
            },
            NullLogger<RequestCorrelationMiddleware>.Instance);

        await middleware.Invoke(context);

        // The real server fires the OnStarting callbacks when the response begins; drive them here.
        await responseFeature.FireOnStartingAsync();

        Assert.False(string.IsNullOrEmpty(assigned));
        Assert.Equal(assigned, context.Response.Headers[RequestCorrelation.ResponseHeaderName].ToString());
    }

    [Fact]
    public async Task Invoke_PublishesRequestIdIntoTheLogScope()
    {
        var logger = new ScopeCapturingLogger();
        var middleware = new RequestCorrelationMiddleware(_ => Task.CompletedTask, logger);
        var context = BuildContext("POST", "/Playback/Sessions", playSessionId: null);

        await middleware.Invoke(context);

        var scope = Assert.Single(logger.Scopes);
        var state = Assert.IsType<Dictionary<string, object>>(scope);
        Assert.Equal(RequestCorrelation.Get(context), Assert.IsType<string>(state[RequestCorrelation.LogPropertyName]));
    }

    /// <summary>
    /// The identifier adopts the ambient W3C trace, which is what makes an inbound
    /// <c>traceparent</c> work end to end without this middleware parsing headers itself: the
    /// hosting layer has already turned that header into the ambient <see cref="Activity"/>.
    /// </summary>
    [Fact]
    public void Derive_PrefersTheAmbientW3CTraceId()
    {
        var previous = Activity.Current;
        try
        {
            using var activity = new Activity("issue42");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.SetParentId("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
            activity.Start();

            var derived = RequestCorrelation.Derive(BuildContext("POST", "/Playback/Sessions", playSessionId: null));

            // Same trace as the caller's traceparent: the trace is joined, not restarted.
            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", derived);
            Assert.Equal(activity.TraceId.ToHexString(), derived);
            activity.Stop();
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void Derive_FallsBackToTraceIdentifier_WhenNoActivityIsInFlight()
    {
        var previous = Activity.Current;
        try
        {
            Activity.Current = null;

            var context = BuildContext("POST", "/Playback/Sessions", playSessionId: null);
            context.TraceIdentifier = "trace-identifier-fallback";

            Assert.Equal("trace-identifier-fallback", RequestCorrelation.Derive(context));
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void Get_ReturnsNull_WhenTheMiddlewareNeverRan()
    {
        Assert.Null(RequestCorrelation.Get(new DefaultHttpContext()));
        Assert.Null(RequestCorrelation.Get(null));
    }

    private static HttpContext BuildContext(string method, string path, string? playSessionId)
    {
        // DefaultHttpContext hands out a fresh, unique TraceIdentifier per instance, which is
        // precisely the per-request uniqueness under test.
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        if (playSessionId is not null)
        {
            context.Request.Headers["X-PlaySessionId"] = playSessionId;
        }

        return context;
    }

    /// <summary>
    /// The stock <see cref="HttpResponseFeature"/> accepts <c>OnStarting</c> callbacks and drops
    /// them on the floor, so a test could never observe the header being set. This one retains them.
    /// </summary>
    private sealed class CallbackCapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public async Task FireOnStartingAsync()
        {
            foreach (var (callback, state) in _onStarting)
            {
                await callback(state).ConfigureAwait(false);
            }
        }
    }

    private sealed class ScopeCapturingLogger : ILogger<RequestCorrelationMiddleware>
    {
        public List<object?> Scopes { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            Scopes.Add(state);
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
