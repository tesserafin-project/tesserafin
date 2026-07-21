using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using Reefin.Api.Playback;
using Reefin.Controller.Configuration;
using Reefin.Model.Configuration;
using Reefin.Playback.Contract.Diagnostics;
using Xunit;

namespace Reefin.Api.Tests.Playback;

/// <summary>
/// Issue #75 slice 75b: the resource filter's three guarantees, exercised end to end - shadow OFF
/// never touches the body, sampling gates the scan, and whatever the scan does the body reaches
/// "binding" byte-for-byte from position 0. A distinctive sentinel in the body never reaches the
/// filter's logs.
/// </summary>
public sealed class PlaybackContractScanFilterTests
{
    private const string Sentinel = "S3nt1nel_c0ffee_LEAK_D0_N0T_ECH0_a11ab1e";

    private static readonly byte[] _bodyBytes = Encoding.UTF8.GetBytes(
        "{\"" + Sentinel + "\":1,\"ItemId\":\"x\",\"Capabilities\":{\"Decode\":{\"Bogus\":2}}}");

    private static IServerConfigurationManager Config(PlaybackShadowOptions shadow)
    {
        var mock = new Mock<IServerConfigurationManager>();
        mock.Setup(c => c.Configuration).Returns(new ServerConfiguration { PlaybackShadow = shadow });
        return mock.Object;
    }

    private static ResourceExecutingContext BuildContext(string method, byte[] body, long? contentLength)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = contentLength;
        httpContext.Request.Body = new MemoryStream(body, writable: false);

        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());

        return new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    private static async Task<(IDictionary<object, object?> Items, byte[] BodySeenByBinding, ListLogger Logger)> RunAsync(
        PlaybackShadowOptions shadow,
        string method = "POST",
        byte[]? body = null,
        long? contentLength = null)
    {
        var provider = new PlaybackContractScanModelProvider();
        var logger = new ListLogger();
        var filter = new PlaybackContractScanFilter(Config(shadow), provider, logger);

        var actualBody = body ?? _bodyBytes;
        var context = BuildContext(method, actualBody, contentLength ?? actualBody.Length);

        byte[] bodySeen = Array.Empty<byte>();
        async Task<ResourceExecutedContext> Next()
        {
            // Stand in for model binding: read the whole body from wherever the filter left it.
            using var ms = new MemoryStream();
            await context.HttpContext.Request.Body.CopyToAsync(ms);
            bodySeen = ms.ToArray();
            return new ResourceExecutedContext(context, context.Filters);
        }

        await filter.OnResourceExecutionAsync(context, Next);
        return (context.HttpContext.Items, bodySeen, logger);
    }

    [Fact]
    public async Task ShadowOff_DoesNotScan_AndBodyIsUntouched()
    {
        var (items, bodySeen, _) = await RunAsync(new PlaybackShadowOptions { Enabled = false });

        Assert.False(items.ContainsKey(PlaybackContractScanFilter.ScanResultKey));
        // Binding still sees the exact original bytes.
        Assert.Equal(_bodyBytes, bodySeen);
    }

    [Fact]
    public async Task SampleRateZero_DoesNotScan_AndBodyIsUntouched()
    {
        var (items, bodySeen, _) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 0.0 });

        Assert.False(items.ContainsKey(PlaybackContractScanFilter.ScanResultKey));
        Assert.Equal(_bodyBytes, bodySeen);
    }

    [Fact]
    public async Task ShadowOnFullSample_Scans_StashesCounts_AndRewindsBodyByteForByte()
    {
        var (items, bodySeen, _) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        // Anti-vacuity: the scan ran and produced the expected counts (unknown at Request + at Decode).
        Assert.True(items.TryGetValue(PlaybackContractScanFilter.ScanResultKey, out var value));
        var scan = Assert.IsType<ContractStructuralScan>(value);
        Assert.Equal(2, scan.UnknownMemberTotal);
        Assert.False(scan.BodyLimitExceeded);

        // Model binding still receives every original byte, from position 0.
        Assert.Equal(_bodyBytes, bodySeen);
    }

    [Fact]
    public async Task Put_UsesReplaceRoot_AndStillScans()
    {
        var (items, bodySeen, _) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 }, method: "PUT");

        Assert.True(items.ContainsKey(PlaybackContractScanFilter.ScanResultKey));
        Assert.Equal(_bodyBytes, bodySeen);
    }

    [Fact]
    public async Task ChunkedRequest_NoContentLength_ReportsMeasuredSize()
    {
        // A chunked/streamed request carries no Content-Length. The scan reads the body and reports
        // the actually-read size - the "size when Content-Length is absent" the spec asks for.
        var body = Encoding.UTF8.GetBytes("{\"ItemId\":\"x\",\"Bogus\":1}");
        var (items, bodySeen, _) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 }, body: body, contentLength: null);

        var scan = Assert.IsType<ContractStructuralScan>(items[PlaybackContractScanFilter.ScanResultKey]);
        Assert.False(scan.BodyLimitExceeded);
        Assert.Equal(body.Length, scan.ScannedBodyByteCount);
        Assert.Equal(1, scan.UnknownMemberTotal);
        Assert.Equal(body, bodySeen);
    }

    [Fact]
    public async Task BodyExactlyAtLimit_ScannedNormally()
    {
        // Exactly the limit: read fully, scanned, not flagged over-limit.
        var pad = new string('A', PlaybackContractScanFilter.BodySizeLimitBytes - "{\"Pad\":\"\"}".Length);
        var body = Encoding.UTF8.GetBytes("{\"Pad\":\"" + pad + "\"}");
        Assert.Equal(PlaybackContractScanFilter.BodySizeLimitBytes, body.Length);

        var (items, _, _) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 }, body: body);

        var scan = Assert.IsType<ContractStructuralScan>(items[PlaybackContractScanFilter.ScanResultKey]);
        Assert.False(scan.BodyLimitExceeded);
        Assert.Equal(1, scan.UnknownMemberTotal);
    }

    [Fact]
    public async Task BodyJustOverLimit_NotParsed_ButBindingStillSeesWholeBody()
    {
        // Just over the limit: the scan stops and only flags it; model binding still receives every
        // original byte, byte-for-byte, from position 0.
        var pad = new string('A', PlaybackContractScanFilter.BodySizeLimitBytes); // pushes total well past the limit
        var body = Encoding.UTF8.GetBytes("{\"Pad\":\"" + pad + "\"}");
        Assert.True(body.Length > PlaybackContractScanFilter.BodySizeLimitBytes);

        var (items, bodySeen, _) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 }, body: body);

        var scan = Assert.IsType<ContractStructuralScan>(items[PlaybackContractScanFilter.ScanResultKey]);
        Assert.True(scan.BodyLimitExceeded);
        Assert.Equal(0, scan.UnknownMemberTotal);
        Assert.Equal(body, bodySeen);
    }

    [Fact]
    public async Task Scan_NeverWritesTheSentinelToTheLog()
    {
        var (_, _, logger) = await RunAsync(new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 });

        Assert.DoesNotContain(logger.Messages, m => m.Contains(Sentinel, StringComparison.Ordinal));
    }

    private sealed class ListLogger : ILogger<PlaybackContractScanFilter>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
