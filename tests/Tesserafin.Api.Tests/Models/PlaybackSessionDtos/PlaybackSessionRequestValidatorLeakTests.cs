using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Api.Middleware;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Issue #79. A single request carries high-entropy sentinel strings in every free-form field the
/// client controls (codec names, codec profiles, video range types, container names, output codec
/// lists) and simultaneously trips <b>every</b> validator branch that used to interpolate one of
/// them. The sentinels must then appear nowhere: not in the HTTP response body (Development
/// <b>and</b> Production), not in the formatted log message, not in the structured log state, not in
/// a log scope, not in <see cref="Exception.Message"/>, and not in
/// <see cref="Exception.ToString()"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the leak actually lived.</b> <see cref="ExceptionMiddleware"/> logs an
/// <see cref="ArgumentException"/> through the <c>LogError(ex, ...)</c> overload, so the formatted
/// message and the structured state only ever carry <c>Method</c>/<c>Url</c>. The client string
/// travelled inside the <b>exception object</b> handed to the logging provider, and - in
/// Development - inside the response body. Those two sinks are what a sentinel test has to look at
/// for it to be able to fail at all; the remaining sinks are asserted because the acceptance
/// criteria list them and because a future change could start routing the message through them.
/// </para>
/// <para>
/// <b>Anti-vacuity.</b> A sentinel test passes trivially if nothing ran. Every case here therefore
/// also asserts the positive: the seven server-side contract paths that the seven exercised
/// branches must produce are all present in the sink that carries the message, and the sentinel is
/// genuinely embedded in the request (proved against the DTO's own <c>ToString()</c>). If any
/// branch stops firing, or the body/exception stops carrying the validator's message, the control
/// assertions go red before the absence assertions can pass for the wrong reason.
/// </para>
/// <para>
/// <b>Out of scope.</b> <c>PlaybackAttemptId</c> and <c>PlaybackDiagnosticDetail.Capabilities</c>
/// are deliberate, catalogued surfaces tracked by issue #80; this test never plants a sentinel in
/// them and asserts nothing about them.
/// </para>
/// </remarks>
public sealed class PlaybackSessionRequestValidatorLeakTests
{
    private const string VideoCodecSentinel = "SNTL-VCODEC-9F3A7C2E1B84D605";
    private const string AudioCodecSentinel = "SNTL-ACODEC-4D81E6B092CF735A";
    private const string ContainerSentinel = "SNTL-CONTAINER-77B0C4A9E2513D8F";
    private const string ProfileSentinel = "SNTL-PROFILE-2E9A05C7B6134FD8";
    private const string RangeTypeSentinel = "SNTL-RANGE-B31D8F60A45C927E";
    private const string OutputVideoCodecSentinel = "SNTL-OUTVCODEC-5C7E20A8D9146B3F";
    private const string OutputAudioCodecSentinel = "SNTL-OUTACODEC-A806F13B7E4D25C9";

    private static readonly string[] Sentinels =
    [
        VideoCodecSentinel,
        AudioCodecSentinel,
        ContainerSentinel,
        ProfileSentinel,
        RangeTypeSentinel,
        OutputVideoCodecSentinel,
        OutputAudioCodecSentinel,
    ];

    /// <summary>
    /// The seven server-side contract paths the seven leaking branches must now produce. Used as
    /// the anti-vacuity control: their presence proves each branch really fired.
    /// </summary>
    private static readonly string[] ExpectedContractPaths =
    [
        "capabilities.decode.videoCodecs[0].maxBitrate",
        "capabilities.decode.audioCodecs[0].maxBitrate",
        "capabilities.decode.videoCodecs declares a duplicate codec at index 1.",
        "capabilities.decode.audioCodecs declares a duplicate codec at index 1.",
        "capabilities.outputProfiles[0].maxVideoBitrate",
        "capabilities.outputProfiles[0].maxAudioBitrate",
        "capabilities.outputProfiles[0].maxAudioChannels",
    ];

    /// <summary>
    /// The validator's own output, before any middleware gets involved: seven contract paths in,
    /// zero sentinels out.
    /// </summary>
    [Fact]
    public void Validate_SentinelLadenRequest_MessageCarriesOnlyContractPathsAndIndexes()
    {
        var request = SentinelRequest();

        // ANTI-VACUITY: the sentinels are genuinely in the request under test. Serialized rather
        // than ToString()'d because a record's own ToString() renders its collections as type
        // names, which would make this control pass without ever seeing a sentinel.
        AssertSentinelsArePresentIn(JsonSerializer.Serialize(request));

        var exception = Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));

        AssertNoSentinelIn(exception.Message, "ArgumentException.Message");
        AssertNoSentinelIn(exception.ToString(), "ArgumentException.ToString()");

        // ANTI-VACUITY: all seven branches fired.
        AssertEveryBranchFired(exception.Message);
    }

    /// <summary>
    /// Development is the environment that puts the exception message into the response body, so
    /// the body is the anti-vacuity anchor here: it must carry all seven contract paths, and none
    /// of the sentinels.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task Invoke_Development_LeaksNoSentinelIntoAnySink()
    {
        var captured = await RunThroughExceptionMiddlewareAsync("Development");

        Assert.Equal(StatusCodes.Status400BadRequest, captured.StatusCode);

        AssertNoSentinelAnywhere(captured);

        // ANTI-VACUITY: the body really is the validator's message, with every branch fired.
        AssertEveryBranchFired(captured.Body);
    }

    /// <summary>
    /// Production returns a fixed body, so the exception handed to the logging provider is the only
    /// sink that can carry the message - and therefore the anti-vacuity anchor.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task Invoke_Production_LeaksNoSentinelIntoAnySink()
    {
        var captured = await RunThroughExceptionMiddlewareAsync("Production");

        Assert.Equal(StatusCodes.Status400BadRequest, captured.StatusCode);

        AssertNoSentinelAnywhere(captured);

        // ANTI-VACUITY: Production must not be passing merely because the body is empty.
        Assert.Equal("Error processing request.", captured.Body);
        Assert.NotNull(captured.LoggedException);
        AssertEveryBranchFired(captured.LoggedException!.Message);
    }

    /// <summary>
    /// The control for the control: if the sentinel really is unobservable, a value that IS emitted
    /// by design must still be found by the very same assertion machinery. Otherwise
    /// <see cref="AssertNoSentinelAnywhere"/> could be passing because it inspects nothing.
    /// </summary>
    /// <param name="environmentName">Hosting environment the middleware runs under.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AbsenceAssertion_ActuallyInspectsTheSinks(string environmentName)
    {
        var captured = await RunThroughExceptionMiddlewareAsync(environmentName);

        // The request path is server-observed, not client free-form text, and the middleware
        // deliberately publishes it. Every sink the sentinel assertions walk is walked again here,
        // and this needle IS found - so those assertions are inspecting real content.
        var haystack = AllSinks(captured);
        Assert.Contains(haystack, s => s.Value.Contains("/Playback/Sessions", StringComparison.Ordinal));
        Assert.NotEmpty(captured.Scopes);
        Assert.NotEmpty(captured.StateEntries);
        Assert.Contains(LogLevel.Error, captured.Levels);
    }

    private static CreatePlaybackSessionRequest SentinelRequest()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [],
                VideoCodecs:
                [
                    // index 0: non-positive cap -> videoCodecs[0].maxBitrate
                    new VideoCodecCapability(VideoCodecSentinel, [ProfileSentinel], null, null, [RangeTypeSentinel], null, MaxBitrate: 0),

                    // index 1: repeats index 0 -> duplicate codec at index 1
                    new VideoCodecCapability(VideoCodecSentinel, [], null, null, [], null, null),
                ],
                AudioCodecs:
                [
                    new AudioCodecCapability(AudioCodecSentinel, null, null, null, MaxBitrate: 0),
                    new AudioCodecCapability(AudioCodecSentinel, null, null, null, null),
                ],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles:
            [
                // index 0: all three numeric constraints non-positive at once.
                new PlaybackOutputProfile(
                    MediaKind.Video,
                    StreamingProtocol.Http,
                    ContainerSentinel,
                    [OutputVideoCodecSentinel],
                    [OutputAudioCodecSentinel],
                    MaxVideoBitrate: 0,
                    MaxAudioBitrate: 0,
                    MaxAudioChannels: 0),
            ]);

        var constraints = new PlaybackConstraints(
            AllowDirectPlay: true,
            AllowDirectStream: true,
            AllowTranscoding: true,
            AllowVideoStreamCopy: true,
            AllowAudioStreamCopy: true,
            MaxBitrate: null,
            MaxAudioChannels: null,
            PreferredAudioStreamIndex: null,
            PreferredSubtitleStreamIndex: null,
            SubtitleMode: SubtitlePlaybackMode.Default,
            PreferredSubtitleLanguages: [],
            AlwaysBurnInSubtitleWhenTranscoding: false,
            StartTimeTicks: 0);

        // PlaybackAttemptId is deliberately left null: it is issue #80's surface, not this one's.
        return new CreatePlaybackSessionRequest(Guid.NewGuid(), Guid.NewGuid(), capabilities, constraints);
    }

    private static async Task<CapturedSinks> RunThroughExceptionMiddlewareAsync(string environmentName)
    {
        var logger = new CapturingLogger();

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ProgramSystemPath).Returns("/opt/reefin/system");
        paths.SetupGet(p => p.ProgramDataPath).Returns("/var/lib/reefin");

        var configuration = new Mock<IServerConfigurationManager>();
        configuration.SetupGet(c => c.ApplicationPaths).Returns(paths.Object);

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        // The real pipeline reaches the validator from the controller action; standing in for it
        // keeps this test on the exact exception the controller would raise.
        var exceptionMiddleware = new ExceptionMiddleware(
            _ =>
            {
                PlaybackSessionRequestValidator.Validate(SentinelRequest());
                return Task.CompletedTask;
            },
            logger,
            configuration.Object,
            environment.Object);

        // Issue #42's correlation middleware sits in front and opens the ambient log scope, so the
        // scope sink this test asserts on is a real one rather than a fabricated stand-in.
        var pipeline = new RequestCorrelationMiddleware(exceptionMiddleware.Invoke, logger);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/Playback/Sessions";
        using var body = new MemoryStream();
        context.Response.Body = body;

        await pipeline.Invoke(context);

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        var bodyText = await reader.ReadToEndAsync();

        return new CapturedSinks(
            context.Response.StatusCode,
            bodyText,
            logger.FormattedMessages,
            logger.StateEntries,
            logger.Scopes,
            logger.Exceptions.FirstOrDefault(),
            logger.Levels);
    }

    private static List<(string Name, string Value)> AllSinks(CapturedSinks captured)
    {
        var sinks = new List<(string Name, string Value)>
        {
            ("HTTP response body", captured.Body),
        };

        sinks.AddRange(captured.FormattedMessages.Select((m, i) => ($"formatted log message[{i}]", m)));
        sinks.AddRange(captured.StateEntries.Select((e, i) => ($"structured log state[{i}]", e)));
        sinks.AddRange(captured.Scopes.Select((s, i) => ($"log scope[{i}]", s)));

        if (captured.LoggedException is { } ex)
        {
            sinks.Add(("logged Exception.Message", ex.Message));
            sinks.Add(("logged Exception.ToString()", ex.ToString()));
        }

        return sinks;
    }

    private static void AssertNoSentinelAnywhere(CapturedSinks captured)
    {
        foreach (var (name, value) in AllSinks(captured))
        {
            AssertNoSentinelIn(value, name);
        }
    }

    private static void AssertNoSentinelIn(string value, string sinkName)
    {
        foreach (var sentinel in Sentinels)
        {
            Assert.False(
                value.Contains(sentinel, StringComparison.OrdinalIgnoreCase),
                string.Create(CultureInfo.InvariantCulture, $"Client-supplied value leaked into {sinkName}: {sentinel}"));
        }
    }

    private static void AssertSentinelsArePresentIn(string value)
    {
        foreach (var sentinel in Sentinels)
        {
            Assert.Contains(sentinel, value, StringComparison.Ordinal);
        }
    }

    private static void AssertEveryBranchFired(string message)
    {
        foreach (var path in ExpectedContractPaths)
        {
            Assert.Contains(path, message, StringComparison.Ordinal);
        }
    }

    private sealed record CapturedSinks(
        int StatusCode,
        string Body,
        IReadOnlyList<string> FormattedMessages,
        IReadOnlyList<string> StateEntries,
        IReadOnlyList<string> Scopes,
        Exception? LoggedException,
        IReadOnlyList<LogLevel> Levels);

    /// <summary>
    /// Stands in for a real <c>ILoggerProvider</c>: keeps the formatted message, every structured
    /// state key/value, every scope, the exception object, and the level.
    /// </summary>
    private sealed class CapturingLogger : ILogger<ExceptionMiddleware>, ILogger<RequestCorrelationMiddleware>
    {
        public List<string> FormattedMessages { get; } = [];

        public List<string> StateEntries { get; } = [];

        public List<string> Scopes { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            Scopes.Add(Render(state));
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            Levels.Add(logLevel);
            FormattedMessages.Add(formatter(state, exception));
            StateEntries.Add(Render(state));

            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    StateEntries.Add(string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}"));
                }
            }

            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }

        private static string Render<TState>(TState state)
        {
            if (state is IEnumerable enumerable and not string)
            {
                return string.Join(
                    "|",
                    enumerable.Cast<object?>().Select(o => o?.ToString() ?? string.Empty));
            }

            return state?.ToString() ?? string.Empty;
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
