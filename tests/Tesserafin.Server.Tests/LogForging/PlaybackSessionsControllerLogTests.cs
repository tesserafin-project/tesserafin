using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Playback;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Contract.Diagnostics;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="PlaybackSessionsController"/>'s <c>DELETE</c> statement logs two values: the
    /// route-bound session id and the stored, client-supplied <c>PlaybackAttemptId</c>. Only the
    /// second is a string a caller chose the bytes of — the first is a
    /// <see cref="PlaybackSessionId"/>, a <see cref="Guid"/>-backed record struct whose only
    /// construction path is <c>Guid.Parse</c>, so it cannot carry a separator at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arguments of the statement are covered here rather than only the one CodeQL displays:
    /// the point is a coherent boundary for the whole logging statement.
    /// </para>
    /// <para>
    /// The controller carries <see cref="AuthorizeAttribute"/> and the endpoint additionally
    /// requires the caller to own the session or be an administrator. Neither is relaxed by this
    /// tranche, and the ownership check is exercised below so that stays true.
    /// </para>
    /// </remarks>
    public sealed class PlaybackSessionsControllerLogTests
    {
        private const string OrdinaryAttempt = "attempt-7";

        [Fact]
        public void Controller_StillRequiresAuthorization()
        {
            Assert.NotEmpty(typeof(PlaybackSessionsController)
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .ToArray());
        }

        [Theory]
        [InlineData("attempt\r7")]
        [InlineData("attempt\n7")]
        [InlineData("attempt\r\n7")]
        [InlineData("attempt-7\r\n" + RealFormatterLogProbe.ForgedRecordPrefix)]
        public void Delete_HostileStoredAttemptId_WritesExactlyOnePhysicalRecord(string attemptId)
        {
            using var probe = RealFormatterLogProbe.Text();

            Delete(probe, attemptId);

            Assert.Equal(1, probe.TextRecordCount());
            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Fact]
        public void Delete_ForgedRecordPrefix_StaysInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text();

            Delete(probe, OrdinaryAttempt + "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix);

            var record = Assert.Single(probe.Lines());
            Assert.Contains("[INF] [1] Tesserafin.Api.Controllers.PlaybackSessionsController:", record, StringComparison.Ordinal);
            Assert.Contains("deleted (attempt attempt-7\\r\\n[12:00:00.000] [ERR]", record, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", record, StringComparison.Ordinal);
        }

        [Fact]
        public void Delete_OrdinaryAttemptId_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();

            Delete(probe, OrdinaryAttempt);

            var record = Assert.Single(probe.Lines());
            Assert.EndsWith("deleted (attempt attempt-7).", record.TrimEnd('\r'), StringComparison.Ordinal);
        }

        [Fact]
        public void Delete_NoStoredAttemptId_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();

            Delete(probe, attemptId: null);

            var record = Assert.Single(probe.Lines());
            // Serilog renders an absent scalar as the literal "null"; unchanged by this tranche.
            Assert.EndsWith("deleted (attempt null).", record.TrimEnd('\r'), StringComparison.Ordinal);
        }

        [Fact]
        public void Delete_ByANonOwner_IsStillForbiddenAndLogsNothing()
        {
            using var probe = RealFormatterLogProbe.Text();

            var fixture = new Fixture(probe, OrdinaryAttempt);
            var result = fixture.DeleteAs(Guid.NewGuid(), isAdministrator: false);

            Assert.IsType<ForbidResult>(result);
            Assert.Equal(string.Empty, probe.Raw);
        }

        private static void Delete(RealFormatterLogProbe probe, string? attemptId)
        {
            var fixture = new Fixture(probe, attemptId);
            Assert.IsType<NoContentResult>(fixture.DeleteAsOwner());
        }

        private sealed class Fixture
        {
            private readonly PlaybackSessionsController _controller;
            private readonly Guid _ownerId = Guid.NewGuid();
            private readonly PlaybackSession _session;

            public Fixture(RealFormatterLogProbe probe, string? attemptId)
            {
                var options = new MediaOptions { Profile = new DeviceProfile(), UserId = _ownerId };
                _session = new PlaybackSession(
                    PlaybackSessionId.NewId(),
                    PlaybackMediaKind.Video,
                    "play-session-42",
                    new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
                    new PlaybackPlan(PlayMethod.DirectPlay, default),
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    attemptId);

                var manager = new Mock<IPlaybackSessionManager>();
                manager.Setup(m => m.Get(_session.Id)).Returns(_session);
                manager.Setup(m => m.Delete(_session.Id)).Returns(true);

                _controller = new PlaybackSessionsController(
                    manager.Object,
                    Mock.Of<IItemLookupService>(),
                    Mock.Of<IUserManager>(),
                    Mock.Of<IMediaSourceManager>(),
                    Mock.Of<IV2PlanStore>(),
                    Mock.Of<IPlaybackLiveStreamResolver>(),
                    Mock.Of<IPlaybackLiveWiringDiagnosticsStore>(),
                    Mock.Of<IMediaEncoder>(),
                    probe.LoggerFor<PlaybackSessionsController>());
            }

            public ActionResult DeleteAsOwner() => DeleteAs(_ownerId, isAdministrator: false);

            public ActionResult DeleteAs(Guid userId, bool isAdministrator)
            {
                var claims = new System.Collections.Generic.List<Claim>
                {
                    new(InternalClaimTypes.UserId, userId.ToString("N"))
                };

                if (isAdministrator)
                {
                    claims.Add(new Claim(ClaimTypes.Role, UserRoles.Administrator));
                }

                _controller.ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role))
                    }
                };

                return _controller.DeletePlaybackSession(_session.Id);
            }
        }
    }
}
