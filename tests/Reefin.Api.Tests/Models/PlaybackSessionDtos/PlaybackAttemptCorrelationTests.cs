using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Api.Middleware;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Controller.MediaEncoding;
using Reefin.Controller.Session;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Issue #43, and the single test that proves #42 and #43 were <b>not</b> merged into one concept.
/// </summary>
/// <remarks>
/// One playback attempt spans several HTTP requests — <c>PlaybackInfo</c>, <c>POST</c>,
/// <c>GET Stream</c>, <c>PUT</c>, a retry, <c>DELETE</c>. Across that whole span the
/// <c>PlaybackAttemptId</c> must stay <b>identical</b> while the <c>RequestId</c> of issue #42 must
/// be <b>different every time</b>. Those two requirements are mutually exclusive for a single
/// field, which is why issue #34 was split. If both assertions below hold simultaneously, the two
/// identifiers are mechanically distinct: one client-supplied and stable, one server-derived and
/// per-request.
/// </remarks>
public class PlaybackAttemptCorrelationTests
{
    private const string AttemptId = "attempt-7f3a";

    [Fact]
    public async Task OneAttempt_AcrossRequestsAndARetry_KeepsOneAttemptIdAndManyRequestIds()
    {
        var manager = BuildManager();
        var requestIds = new List<string>();
        var attemptIdsSeenByServer = new List<string?>();

        // The five requests of one attempt that actually reach the server carrying the field.
        // Each is a genuinely separate HTTP request, so each gets its own RequestId from #42's
        // middleware — exactly as it would in production.
        var session = await SimulateRequest(
            requestIds,
            () =>
            {
                var created = manager.Create(BuildRequest(), "play-session-1", AttemptId);
                attemptIdsSeenByServer.Add(created!.PlaybackAttemptId);
                return created;
            });

        // A retry inside the SAME attempt: new HTTP request, new RequestId, same attempt id.
        await SimulateRequest(
            requestIds,
            () =>
            {
                var retried = manager.Create(BuildRequest(), "play-session-1", AttemptId);
                attemptIdsSeenByServer.Add(retried!.PlaybackAttemptId);
                return retried;
            });

        // A PUT mid-attempt (track change), again the same attempt id.
        await SimulateRequest(
            requestIds,
            () =>
            {
                var patched = manager.Patch(session.Id, BuildRequest(), AttemptId);
                attemptIdsSeenByServer.Add(patched!.PlaybackAttemptId);
                return patched;
            });

        // ONE attempt id for the whole attempt, retry included.
        Assert.Equal(new string?[] { AttemptId, AttemptId, AttemptId }, attemptIdsSeenByServer);

        // THREE different request ids for the same three requests.
        Assert.Equal(3, requestIds.Count);
        Assert.Equal(3, new HashSet<string>(requestIds, StringComparer.Ordinal).Count);

        // And the attempt id is none of them — it is not derived from any request.
        Assert.DoesNotContain(AttemptId, requestIds);
    }

    [Fact]
    public void TwoDistinctAttempts_CarryDifferentAttemptIds()
    {
        var manager = BuildManager();

        var first = manager.Create(BuildRequest(), "play-session-1", "attempt-one");
        var second = manager.Create(BuildRequest(), "play-session-2", "attempt-two");

        Assert.Equal("attempt-one", first!.PlaybackAttemptId);
        Assert.Equal("attempt-two", second!.PlaybackAttemptId);
        Assert.NotEqual(first.PlaybackAttemptId, second.PlaybackAttemptId);

        // Distinct sessions too - the attempt id never becomes a lookup key.
        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    /// A later request of the same attempt that omits the field must not wipe the correlation the
    /// attempt already established. "Not sent" is not "forget it".
    /// </summary>
    [Fact]
    public void Patch_WithoutAnAttemptId_DoesNotEraseTheOneAlreadyRecorded()
    {
        var manager = BuildManager();
        var session = manager.Create(BuildRequest(), "play-session-1", AttemptId);

        var patched = manager.Patch(session!.Id, BuildRequest(), playbackAttemptId: null);

        Assert.Equal(AttemptId, patched!.PlaybackAttemptId);
    }

    [Fact]
    public void ClientThatNeverSendsTheField_PlaysNormallyWithANullAttemptId()
    {
        var manager = BuildManager();

        var session = manager.Create(BuildRequest(), "play-session-1");

        Assert.NotNull(session);
        Assert.Null(session.PlaybackAttemptId);
    }

    /// <summary>
    /// The attempt id reaches the client-facing response verbatim — echoed, never regenerated.
    /// </summary>
    [Fact]
    public void ResponseMapper_EchoesTheAttemptIdVerbatim()
    {
        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            "play-session-1",
            null,
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            AttemptId);

        Assert.Equal(AttemptId, PlaybackSessionResponseMapper.Map(session).PlaybackAttemptId);
    }

    /// <summary>
    /// Runs <paramref name="serverWork"/> "inside" a fresh HTTP request, capturing the
    /// <c>RequestId</c> issue #42's middleware assigns to it.
    /// </summary>
    private static async Task<T> SimulateRequest<T>(List<string> requestIds, Func<T> serverWork)
    {
        T? result = default;
        var middleware = new RequestCorrelationMiddleware(
            ctx =>
            {
                requestIds.Add(RequestCorrelation.Get(ctx)!);
                result = serverWork();
                return Task.CompletedTask;
            },
            NullLogger<RequestCorrelationMiddleware>.Instance);

        await middleware.Invoke(new DefaultHttpContext()).ConfigureAwait(false);
        return result!;
    }

    private static PlaybackSessionManager BuildManager()
    {
        var planner = new Mock<IPlaybackSessionPlanner>();
        planner.Setup(p => p.PlanVideo(It.IsAny<MediaOptions>())).Returns(new PlaybackPlan(PlayMethod.DirectPlay, default));
        return new PlaybackSessionManager(
            planner.Object,
            new Mock<ITranscodeManager>().Object,
            new Mock<ISessionManager>().Object);
    }

    private static PlaybackSessionRequest BuildRequest() =>
        new(PlaybackMediaKind.Video, new MediaOptions { ItemId = Guid.NewGuid(), Profile = new DeviceProfile() });
}
