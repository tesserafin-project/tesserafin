using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// The real server with a stubbed <see cref="ITranscodeManager"/>, so a play session the server
/// already knows about can be presented at minting without an encoder in the loop.
/// </summary>
/// <remarks>
/// This substitution is confined to its own server instance because the media boundary matrix does
/// reach the real transcode manager. The minting tests never request media, so nothing here can be
/// weakened by it — the only question these tests ask is what the minting endpoint refuses.
/// </remarks>
public sealed class MintingApplicationFactory : MediaBoundaryApplicationFactory
{
    /// <summary>
    /// Gets or sets the lookup the stub answers with. Returning null means "the server does not
    /// know this play session", which is the ordinary direct-play case.
    /// </summary>
    public Func<string, TranscodingJob?> TranscodingJobs { get; set; } = _ => null;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            var transcodeManager = new Mock<ITranscodeManager>(MockBehavior.Loose);
            transcodeManager
                .Setup(manager => manager.GetTranscodingJob(It.IsAny<string>()))
                .Returns((string playSessionId) => TranscodingJobs(playSessionId));

            services.RemoveAll<ITranscodeManager>();
            services.AddSingleton(transcodeManager.Object);
        });
    }
}
