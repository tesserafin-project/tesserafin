using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Controller.Configuration;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;
using Reefin.Extensions.Json;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Configuration;
using Reefin.Model.Entities;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: the end-to-end proof <c>ci/smoke.sh</c>'s own header names as a real, deliberate gap left
/// by PR117 - a real, booted Reefin server, driven only through its public HTTP surface, planning a
/// session (<c>POST Playback/Sessions</c>), resolving the PR117 URL contract
/// (<c>GET Playback/Sessions/{id}/Stream</c>), and then a real HTTP client fetching the URL the
/// descriptor names, asserting it actually serves bytes - not just that the descriptor LOOKS right.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope, honestly stated (mirrors <c>ci/smoke.sh</c>'s own header discipline).</b> One real fixture
/// (<see cref="EndToEndMediaFixtures.CreateH264AacMp4Async"/>, a genuine ffmpeg-synthesized H.264/AAC
/// MP4) backs every scenario; what changes per scenario is the request's own
/// <see cref="PlaybackConstraints"/>/<see cref="ClientCapabilities"/> (see
/// <see cref="EndToEndCapabilityPresets"/>'s remarks for the exact <c>StreamBuilder</c> mechanics this
/// relies on, verified against <c>Reefin.Model/Dlna/StreamBuilder.cs</c>) - a deliberate simplification
/// over maintaining five differently-encoded fixtures, since the play METHOD is what these tests need
/// to control, not the codec. The library item itself is seeded directly
/// (<see cref="LibraryItemSeeder"/>) rather than through a full virtual-folder scan - see that class's
/// remarks for why that is a faithful shortcut of the exact same real persistence calls a scan would
/// end up making, not a fake one.
/// </para>
/// <para>
/// Tagged <see cref="TraitAttribute"/> <c>Category=Smoke</c>: <c>ci/run.sh</c> (the mandatory daily
/// gate) excludes <c>Category=Smoke</c> by filter already, and <c>ci/smoke.sh</c>'s own
/// <c>SMOKE_FILTER</c> is a <c>Category=Smoke</c> OR-clause, so this class is picked up there
/// automatically - see <c>ci/smoke-e2e.sh</c> for the dedicated invocation this PR adds (this suite is
/// slower than the rest of <c>ci/smoke.sh</c>: a real boot plus, for the transcode scenario, a real
/// ffmpeg encode).
/// </para>
/// </remarks>
[Trait("Category", "Smoke")]
public sealed class PlaybackUrlContractEndToEndTests : IClassFixture<E2eApplicationFactory>, IAsyncLifetime
{
    private static readonly MediaStream[] _baseStreams =
    [
        new MediaStream
        {
            Index = 0,
            Type = MediaStreamType.Video,
            Codec = EndToEndCapabilityPresets.FixtureVideoCodec,
            Width = EndToEndMediaFixtures.Width,
            Height = EndToEndMediaFixtures.Height,
            IsDefault = true,
        },
        new MediaStream
        {
            Index = 1,
            Type = MediaStreamType.Audio,
            Codec = EndToEndCapabilityPresets.FixtureAudioCodec,
            Channels = 2,
            IsDefault = true,
        },
    ];

    private readonly E2eApplicationFactory _factory;
    private HttpClient _client = null!;
    private Guid _userId;
    private string _workDir = null!;
    private string _fixturePath = null!;
    private ILibraryManager _libraryManager = null!;
    private IMediaStreamRepository _mediaStreamRepository = null!;
    private IServerConfigurationManager _configManager = null!;

    public PlaybackUrlContractEndToEndTests(E2eApplicationFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        // Bounded so a genuine server-side hang (e.g. a stuck real ffmpeg process) fails the test with
        // a diagnosable timeout instead of hanging the whole test run indefinitely.
        _client.Timeout = TimeSpan.FromSeconds(90);
        // The startup wizard can only be completed ONCE against this class's single shared, real
        // booted server (IClassFixture<E2eApplicationFactory>) - EnsureAuthenticatedAsync races-and-
        // caches across this class's concurrently-run [Fact]s so only the first caller actually runs
        // it; every caller (including this one) still adds the resulting token to its OWN client.
        var (accessToken, userId) = await _factory.EnsureAuthenticatedAsync();
        _client.DefaultRequestHeaders.AddAuthHeader(accessToken);
        _userId = userId;

        _libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();
        _mediaStreamRepository = _factory.Services.GetRequiredService<IMediaStreamRepository>();
        _configManager = _factory.Services.GetRequiredService<IServerConfigurationManager>();

        // Explicit, known starting state regardless of framework defaults - scenarios that care about
        // the kill switch set this themselves.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;

        _workDir = Directory.CreateTempSubdirectory("reefin-pr119-e2e-").FullName;
        _fixturePath = await EndToEndMediaFixtures.CreateH264AacMp4Async(_workDir);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup - mirrors HlsSmokeTests' own TempDirectory discipline.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Scenario 1: DirectPlay.</summary>
    [Fact]
    public async Task DirectPlay_PostThenGetStream_ServesRealBytes()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlay();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-direct-play");
        Assert.Equal(PlaybackMethod.DirectPlay, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal(StreamingProtocol.Http, descriptor.Protocol);

        await AssertUrlServes200Async(descriptor.Url, "DirectPlay");
    }

    /// <summary>Scenario 2: Remux / DirectStream.</summary>
    [Fact]
    public async Task Remux_PostThenGetStream_ServesRealBytes()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.Remux();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-remux");
        Assert.Equal(PlaybackMethod.Remux, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);

        await AssertUrlServes200Async(descriptor.Url, "Remux/DirectStream");
    }

    /// <summary>
    /// Scenario 3: Transcode to HLS - manifest AND at least one real segment.
    /// </summary>
    /// <remarks>
    /// <b>Root-caused and fixed, not papered over.</b> This scenario used to hang past both its own
    /// <see cref="FactAttribute.Timeout"/> and <see cref="CreateBoundedCancellation"/>'s per-call
    /// bound: the real ffmpeg process completes in well under a second and writes a complete, valid
    /// VOD <c>.m3u8</c> (<c>#EXT-X-ENDLIST</c>) plus its <c>.ts</c> segment to disk, but the segment
    /// HTTP request never returned. Root cause, confirmed by instrumenting
    /// <c>DynamicHlsController.GetSegmentResult</c>'s readiness-wait loop directly: <c>Process.Exited</c>
    /// fires <c>TranscodeManager.OnFfMpegProcessExited</c>, which sets <c>job.HasExited = true</c> and
    /// then immediately calls <c>job.Dispose()</c> - and <see cref="TranscodingJob.Dispose"/> used to
    /// null out <see cref="TranscodingJob.CurrentAttempt"/> as part of that cleanup, which silently
    /// reverted <c>job.HasExited</c>'s getter (<c>CurrentAttempt?.HasExited ?? false</c>) back to
    /// <c>false</c> the instant <c>Dispose()</c> ran - even though the process had genuinely already
    /// exited. <c>GetSegmentResult</c>'s <c>while (!transcodingJob.HasExited)</c> readiness loop can
    /// then never exit on its own condition again, and - for this fixture, which produces exactly one
    /// segment - its "or the next segment appeared" alternative never becomes true either, so the
    /// request hung forever even though the file it needed to serve was already complete and correct
    /// on disk. Fixed at the source in <see cref="TranscodingJob.Dispose"/> (stopped nulling
    /// <c>CurrentAttempt</c> - nothing else in the codebase reads it, so the seam for a future
    /// multi-attempt fallback wasn't lost). <see cref="CreateBoundedCancellation"/>'s per-call bound is
    /// kept as defense in depth (a genuinely stuck request - e.g. a real transcode failure - should
    /// still fail this test fast rather than hang the run), not because it is still needed to survive
    /// this particular bug.
    /// </remarks>
    [Fact(Timeout = 90_000)]
    public async Task Transcode_Hls_PostThenGetStream_ServesManifestAndSegment()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.TranscodeHls();

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-transcode-hls");
        Assert.Equal(PlaybackMethod.Transcode, session.Method);

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);

        var masterManifest = await GetTextAsync(descriptor.Url, "HLS master playlist");
        Assert.Contains("#EXTM3U", masterManifest, StringComparison.Ordinal);

        var variantUrl = FirstUriLine(masterManifest, descriptor.Url)
            ?? throw new InvalidOperationException($"HLS master playlist named no variant. Content:\n{masterManifest}");

        var mediaPlaylist = await GetTextWithRetryAsync(variantUrl, "HLS media playlist", TimeSpan.FromSeconds(60));
        Assert.Contains("#EXTINF", mediaPlaylist, StringComparison.Ordinal);

        var segmentUrl = FirstUriLine(mediaPlaylist, variantUrl)
            ?? throw new InvalidOperationException($"HLS media playlist named no segment. Content:\n{mediaPlaylist}");

        await AssertUrlServes200Async(segmentUrl, "HLS segment", minBytes: 1);
    }

    /// <summary>Scenario 4: an external subtitle sidecar is named on the descriptor and itself servable.</summary>
    [Fact]
    public async Task ExternalSubtitle_IsNamedOnDescriptorAndServable()
    {
        var subtitlePath = EndToEndMediaFixtures.CreateExternalSrt(_workDir);
        var streams = _baseStreams.Concat(
        [
            new MediaStream
            {
                Index = 2,
                Type = MediaStreamType.Subtitle,
                Codec = "srt",
                IsExternal = true,
                Path = subtitlePath,
                Language = "eng",
            },
        ]).ToArray();

        var itemId = LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, _fixturePath, "mp4", streams, $"PR119 subtitle fixture {Guid.NewGuid()}");
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlayWithExternalSubtitle(subtitleStreamIndex: 2);

        // A specific subtitle stream request requires naming the media source explicitly
        // (PlaybackSessionRequestValidator) - the item's single MediaSource id, itself the item id.
        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-external-subtitle", mediaSourceId: itemId.ToString("N"));

        var descriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.True(!string.IsNullOrEmpty(descriptor.SubtitleUrl), $"Descriptor named no SubtitleUrl for an external-subtitle session (FallbackReason={descriptor.FallbackReason}).");

        await AssertUrlServes200Async(descriptor.Url, "DirectPlay (subtitle scenario)");

        using var subtitleResponse = await _client.GetAsync(descriptor.SubtitleUrl, TestContext.Current.CancellationToken);
        var subtitleBody = await subtitleResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(subtitleResponse.StatusCode == HttpStatusCode.OK, $"External subtitle URL '{descriptor.SubtitleUrl}' did not serve 200 - got {(int)subtitleResponse.StatusCode}. Body: {Truncate(subtitleBody)}");
        Assert.Contains("PR119 end-to-end external subtitle fixture", subtitleBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario 5: the PR115c kill switch (<see cref="PlaybackShadowOptions.Mode"/> flipped away from
    /// <see cref="PlaybackEngineMode.V2"/>/<see cref="PlaybackEngineMode.Canary"/>) forces legacy for
    /// the very next request, with no restart - and the URL that request resolves to must still be
    /// servable.
    /// </summary>
    [Fact]
    public async Task KillSwitch_ForcesLegacyOnNextRequest_UrlStillServable()
    {
        var itemId = SeedItem();
        var (capabilities, constraints) = EndToEndCapabilityPresets.DirectPlay();

        // V2 mode (not merely Canary): every session is v2-authoritative, no cohort-hash dependency -
        // the simplest way to get a REAL "before" state where v2 actually served this session, so the
        // "after" transition below is a genuine transition and not just re-observing the default.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.V2;

        var session = await CreateSessionAsync(itemId, capabilities, constraints, "e2e-kill-switch");

        var beforeDescriptor = await GetStreamDescriptorAsync(session.Id);
        await AssertUrlServes200Async(beforeDescriptor.Url, "Before kill switch");
        Assert.True(
            beforeDescriptor.FallbackReason is null && beforeDescriptor.ServedBy != PlaybackSessionResponse.LegacyDecisionVersion,
            $"Expected the session to be genuinely v2-served before the kill switch - got ServedBy={beforeDescriptor.ServedBy}, FallbackReason={beforeDescriptor.FallbackReason}. " +
            "The kill-switch transition below is only meaningful against a real v2-served 'before' state.");

        // The kill switch itself: an operator flips PlaybackShadow.Mode back to Legacy/Shadow. Per
        // PlaybackLiveStreamResolver's own remarks, this takes effect on the very next request - no
        // restart, no session recreation.
        _configManager.Configuration.PlaybackShadow.Mode = PlaybackEngineMode.Legacy;

        var afterDescriptor = await GetStreamDescriptorAsync(session.Id);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, afterDescriptor.FallbackReason);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, afterDescriptor.ServedBy);

        await AssertUrlServes200Async(afterDescriptor.Url, "After kill switch (legacy fallback)");
    }

    private Guid SeedItem() =>
        LibraryItemSeeder.SeedVideo(_libraryManager, _mediaStreamRepository, _fixturePath, "mp4", _baseStreams, $"PR119 fixture {Guid.NewGuid()}");

    private async Task<PlaybackSessionResponse> CreateSessionAsync(Guid itemId, ClientCapabilities capabilities, PlaybackConstraints constraints, string playSessionId, string? mediaSourceId = null)
    {
        var request = new CreatePlaybackSessionRequest(itemId, _userId, capabilities, constraints, MediaSourceId: mediaSourceId, PlaySessionId: playSessionId);
        using var response = await _client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"POST Playback/Sessions failed for PlaySessionId='{playSessionId}': {(int)response.StatusCode} {Truncate(body)}");

        return JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)
            ?? throw new InvalidOperationException("POST Playback/Sessions returned an empty body.");
    }

    private async Task<PlaybackSessionStreamDescriptor> GetStreamDescriptorAsync(Guid sessionId)
    {
        using var response = await _client.GetAsync($"Playback/Sessions/{sessionId}/Stream", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET Playback/Sessions/{sessionId}/Stream failed: {(int)response.StatusCode} {Truncate(body)}");

        return JsonSerializer.Deserialize<PlaybackSessionStreamDescriptor>(body, JsonDefaults.Options)
            ?? throw new InvalidOperationException("GET .../Stream returned an empty body.");
    }

    private async Task AssertUrlServes200Async(string relativeUrl, string diagnosticContext, int minBytes = 1)
    {
        using var boundedCts = CreateBoundedCancellation();
        using var response = await _client.GetAsync(relativeUrl, boundedCts.Token);
        var content = await response.Content.ReadAsByteArrayAsync(boundedCts.Token);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{diagnosticContext}: expected HTTP 200 fetching '{relativeUrl}', got {(int)response.StatusCode}. Body head: {Truncate(Encoding.UTF8.GetString(content))}");
        Assert.True(content.Length >= minBytes, $"{diagnosticContext}: '{relativeUrl}' returned 200 but only {content.Length} byte(s).");
    }

    private async Task<string> GetTextAsync(string relativeUrl, string diagnosticContext)
    {
        using var boundedCts = CreateBoundedCancellation();
        using var response = await _client.GetAsync(relativeUrl, boundedCts.Token);
        var body = await response.Content.ReadAsStringAsync(boundedCts.Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{diagnosticContext}: expected HTTP 200 fetching '{relativeUrl}', got {(int)response.StatusCode}. Body: {Truncate(body)}");
        return body;
    }

    /// <summary>
    /// A per-call bounded cancellation, independent of (and a deliberate belt-and-suspenders on top
    /// of) <see cref="HttpClient.Timeout"/>: a real, reproducible hang was found in this suite's own
    /// development (the real HLS transcode/serve path never returning even after ffmpeg itself had
    /// already produced a complete, valid VOD manifest+segment on disk - root-caused and fixed, see
    /// <see cref="Transcode_Hls_PostThenGetStream_ServesManifestAndSegment"/>'s remarks) where
    /// <see cref="HttpClient.Timeout"/> alone did not observably abort the stuck request within
    /// several minutes. Linking an explicit token to each individual call, rather than trusting the
    /// client-wide timeout, is what actually turns a hang into a clean, fast test failure - kept as
    /// defense in depth for any other genuinely stuck request, not only the one root-caused above.
    /// </summary>
    private static CancellationTokenSource CreateBoundedCancellation(int seconds = 30) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token);

    /// <summary>
    /// A real ffmpeg transcode is not instantaneous - the first HLS media playlist/segment is not
    /// guaranteed ready the instant the master playlist returns. Polls with a clear diagnostic on
    /// timeout, mirroring <c>ci/smoke.sh</c>'s own "attente readiness" discipline.
    /// </summary>
    private async Task<string> GetTextWithRetryAsync(string relativeUrl, string diagnosticContext, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await GetTextAsync(relativeUrl, diagnosticContext).ConfigureAwait(false);
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                lastFailure = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"{diagnosticContext}: '{relativeUrl}' never became ready within {timeout}.", lastFailure);
    }

    /// <summary>
    /// Minimal, deliberately naive M3U8 parsing: the first non-empty, non-comment line is the next
    /// URI to follow - true for both a master playlist's variant reference and a media playlist's
    /// segment reference. Resolves a relative URI against <paramref name="baseUrl"/>'s own path, the
    /// same resolution a real HLS client performs.
    /// </summary>
    private static string? FirstUriLine(string manifest, string baseUrl)
    {
        var line = manifest
            .Split('\n')
            .Select(l => l.Trim('\r', ' '))
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

        if (line is null)
        {
            return null;
        }

        if (Uri.TryCreate(line, UriKind.Absolute, out _))
        {
            return line;
        }

        var basePath = baseUrl[..(baseUrl.LastIndexOf('/') + 1)];
        return basePath + line;
    }

    private static string Truncate(string text, int maxChars = 2000) => text.Length <= maxChars ? text : text[..maxChars];
}
