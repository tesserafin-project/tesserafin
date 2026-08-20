using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Tesserafin.Api.Controllers;
using Xunit;

namespace Tesserafin.Server.Tests.Authorization;

/// <summary>
/// <c>/LiveTv/LiveStreamFiles/**</c> must stay authorized.
/// </summary>
/// <remarks>
/// The Live TV HLS defect was ffmpeg fetching this endpoint anonymously and being refused. The fix
/// hands the bytes to ffmpeg over stdin; it does not open the endpoint. Marking it
/// <c>[AllowAnonymous]</c> - which is what upstream Jellyfin does - would make Live TV work while
/// serving every tuner stream to any unauthenticated caller who can guess a stream id, so it is the
/// one change this branch must never make. See docs/live-tv-ffmpeg-stdin-handoff.md.
/// </remarks>
public class LiveStreamFileAuthorizationTests
{
    [Theory]
    [InlineData(nameof(LiveTvController.GetLiveStreamFile))]
    [InlineData(nameof(LiveTvController.GetLiveRecordingFile))]
    public void InternalMediaFileEndpoint_DemandsAuthenticationAndIsNeverAnonymous(string methodName)
    {
        var method = typeof(LiveTvController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        Assert.NotEmpty(method.GetCustomAttributes<AuthorizeAttribute>(true).ToArray());
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(true).ToArray());
        Assert.Empty(typeof(LiveTvController).GetCustomAttributes<AllowAnonymousAttribute>(true).ToArray());
    }
}
