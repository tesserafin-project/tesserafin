using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// A real Kestrel listener running only the middleware under test, driven over a raw socket so
    /// the request line reaches the server byte for byte.
    /// </summary>
    /// <remarks>
    /// Same shape as the redirect suite added for the base-url middleware:
    /// <see cref="System.Net.Http.HttpClient"/> normalises the request target before it reaches the
    /// wire, which silently drops exactly the inputs these tests exist to try. Using the real
    /// listener is also what makes "the parser refused this" a measurement rather than a claim.
    /// </remarks>
    internal sealed class RawKestrelServer : IAsyncDisposable
    {
        private readonly IHost _host;

        private RawKestrelServer(IHost host, int port)
        {
            _host = host;
            Port = port;
        }

        public int Port { get; }

        public static async Task<RawKestrelServer> StartAsync(
            Action<IApplicationBuilder> configure,
            Action<IServiceCollection>? services = null,
            IReadOnlyDictionary<string, string?>? configuration = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                    .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(
                        configuration ?? new Dictionary<string, string?>()))
                    .ConfigureServices(collection => services?.Invoke(collection))
                    .Configure(configure))
                .Build();

            await host.StartAsync().ConfigureAwait(false);

            var address = host.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            return new RawKestrelServer(host, new Uri(address).Port);
        }

        public Task<RawResponse> SendAsync(string requestLine, string? hostHeader = null)
            => SendRawAsync(
                requestLine + "\r\nHost: " + (hostHeader ?? "127.0.0.1") + "\r\nConnection: close\r\n\r\n");

        public async Task<RawResponse> SendRawAsync(string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", Port).ConfigureAwait(false);

            using var stream = client.GetStream();
            var bytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var buffer = new byte[8192];
            var received = new StringBuilder();
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) > 0)
                {
                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (OperationCanceledException)
            {
                // Server kept the connection open; whatever arrived is enough to inspect.
            }

            return RawResponse.Parse(received.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }
    }
}
