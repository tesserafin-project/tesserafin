using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Constants;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// #301: beside a token of the same user on another device, a capability's device is still its
/// own session's. The user and the device never come from different credentials.
/// </summary>
public class CapabilityOwnerDeviceTests
{
    private const string CapabilityDevice = "capability-device";
    private const string TokenDevice = "token-device";
    private static readonly Guid _user = new("aaaaaaaa11114444888800000000cccc");

    [Theory]
    [InlineData(CapabilityDevice, true)]
    [InlineData(TokenDevice, false)]
    public void TheCapabilitysSessionDeviceDecides(string jobDevice, bool expected)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.SetupGet(m => m.Sessions).Returns(
        [
            new SessionInfo(sessionManager.Object, NullLogger.Instance) { Id = "capability-session", DeviceId = CapabilityDevice },
        ]);
        var authorizer = new HlsJobOwnershipAuthorizer(Mock.Of<IHlsSegmentBindingRegistry>(), sessionManager.Object);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(InternalClaimTypes.UserId, _user.ToString("N")),
                    new Claim(InternalClaimTypes.DeviceId, TokenDevice),
                    new Claim(InternalClaimTypes.IsApiKey, bool.FalseString),
                ],
                "test")),
        };
        context.Features.Set(new ValidatedPlaybackCapability(
            "capability-value",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "source",
            "play",
            PlaybackCapabilityScope.Media,
            new PlaybackCapabilityValidation(true, PlaybackCapabilityFailure.None, Guid.NewGuid(), _user, "capability-session", "play")));

        Assert.Equal(expected, authorizer.OwnsJob(context, _user, jobDevice));
    }
}
