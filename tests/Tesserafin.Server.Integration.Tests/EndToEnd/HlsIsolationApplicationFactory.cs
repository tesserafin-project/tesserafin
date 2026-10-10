using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// POLISH-2-R4. The server of <see cref="E2eApplicationFactory"/> - real authentication, real
/// routes, real ffmpeg - with one thing a test can ask of its file system: that the removal a
/// released transcode schedules for later FAILS, as it does when a file is still in use.
/// </summary>
/// <remarks>
/// That is the barrier <see cref="HlsOutputIsolationEndToEndTests"/> stands behind. A released
/// job's files normally stay for a second and a half; a test that had to arrive inside that
/// window would be measuring its own speed. A removal that failed leaves them for good, with no
/// job on the output, and that state - not a race against a timer - is what is replayed into.
///
/// Only the removal that belongs to no segment request fails. What a segment request removes
/// itself is the repair under test, and is left alone.
/// </remarks>
public sealed class HlsIsolationApplicationFactory : E2eApplicationFactory
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services
            .AddHttpContextAccessor()
            .AddSingleton<IFileSystem, FailingRemovals>());
    }
}
