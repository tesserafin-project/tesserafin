using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Server.HealthChecks;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// The standard test host with the database probe — and only the database probe — replaced,
    /// through the ordinary DI container (#91 / [A5]).
    /// </summary>
    public sealed class HealthApplicationFactory : TesserafinApplicationFactory
    {
        public SwitchableDatabaseHealthProbe Probe
            => (SwitchableDatabaseHealthProbe)Services.GetRequiredService<IDatabaseHealthProbe>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDatabaseHealthProbe>(sp => new SwitchableDatabaseHealthProbe(sp));
            });
        }
    }
}
