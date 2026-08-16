using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// The real server, with one substitution: the credential service reads a clock this suite can
/// move. Only that service is re-registered — replacing the container-wide <c>TimeProvider</c>
/// would move time for session bookkeeping and scheduled tasks too, and a boundary test that also
/// perturbs the session table proves less, not more.
/// </summary>
public class MediaBoundaryApplicationFactory : TesserafinApplicationFactory
{
    /// <summary>
    /// Gets the clock the credential service reads, at validation as well as at minting.
    /// </summary>
    public SteppableTimeProvider Clock { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPlaybackCredentialService>();
            services.AddSingleton<IPlaybackCredentialService>(
                provider => new PlaybackCredentialService(Clock, provider.GetRequiredService<IRandomSecretSource>()));
        });
    }
}
