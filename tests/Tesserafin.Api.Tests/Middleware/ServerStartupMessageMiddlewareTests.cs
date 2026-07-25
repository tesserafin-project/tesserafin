using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Model.Globalization;
using Xunit;

namespace Tesserafin.Api.Middleware.Tests;

/// <summary>
/// The startup-message middleware short-circuits every request with a plain-text 503 until the
/// host finishes core startup. #91 / [A5] made that a contract problem for one route: <c>/health</c>
/// promises a single stable JSON shape to container runtimes and reverse proxies, and swallowing it
/// here answered an HTML body instead — during exactly the window a probe is most likely to hit.
/// These tests pin the exemption so it cannot be removed by accident.
/// </summary>
public sealed class ServerStartupMessageMiddlewareTests
{
    private const string LoadingMessage = "Server is loading. Please try again shortly.";

    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH")]
    [InlineData("/system/ping")]
    public async Task Invoke_BeforeCoreStartup_LetsProbeRoutesThrough(string path)
    {
        var (middleware, context, nextCalled) = Build(path, coreStartupHasCompleted: false);

        await middleware.Invoke(context.Context, context.Host, context.Localization);

        Assert.True(nextCalled(), $"{path} must reach the endpoint so it can answer its own contract.");
        Assert.Equal(StatusCodes.Status200OK, context.Context.Response.StatusCode);
        Assert.Equal(string.Empty, ReadBody(context.Context));
    }

    [Fact]
    public async Task Invoke_BeforeCoreStartup_StillHoldsEveryOtherRoute()
    {
        var (middleware, context, nextCalled) = Build("/web/index.html", coreStartupHasCompleted: false);

        await middleware.Invoke(context.Context, context.Host, context.Localization);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Context.Response.StatusCode);
        Assert.Equal(LoadingMessage, ReadBody(context.Context));
    }

    [Fact]
    public async Task Invoke_AfterCoreStartup_LetsEverythingThrough()
    {
        var (middleware, context, nextCalled) = Build("/web/index.html", coreStartupHasCompleted: true);

        await middleware.Invoke(context.Context, context.Host, context.Localization);

        Assert.True(nextCalled());
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private static (ServerStartupMessageMiddleware Middleware, Fixture Context, System.Func<bool> NextCalled) Build(
        string path,
        bool coreStartupHasCompleted)
    {
        var called = false;
        var middleware = new ServerStartupMessageMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(coreStartupHasCompleted);

        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetLocalizedString("StartupEmbyServerIsLoading")).Returns(LoadingMessage);

        return (middleware, new Fixture(httpContext, host.Object, localization.Object), () => called);
    }

    private sealed record Fixture(HttpContext Context, IServerApplicationHost Host, ILocalizationManager Localization);
}
