using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.LiveTv;
using Xunit;
using SdProvider = Tesserafin.LiveTv.Listings.SchedulesDirect;

namespace Tesserafin.LiveTv.Tests.SchedulesDirect
{
    /// <summary>
    /// Controls covering the Schedules Direct <c>/token</c> request body.
    /// The listings provider credentials are administrator-supplied configuration, so the
    /// body must be produced by a serializer that escapes them rather than by concatenation.
    /// </summary>
    public class SchedulesDirectTokenRequestTests : IDisposable
    {
        private readonly string _cachePath;
        private bool _disposed;

        public SchedulesDirectTokenRequestTests()
        {
            _cachePath = Path.Combine(Path.GetTempPath(), "sd-token-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cachePath);
        }

        public static TheoryData<string> HostileUsernames() => new()
        {
            // Closes the JSON string literal.
            "bo\"b",
            // Trailing backslash swallows the closing quote as an escape.
            "bob\\",
            // Attempts to inject an additional JSON member.
            "x\",\"password\":\"deadbeef",
        };

        [Theory]
        [MemberData(nameof(HostileUsernames))]
        public async Task GetToken_UsernameWithJsonMetacharacters_ProducesWellFormedBodyWithVerbatimUsername(string username)
        {
            var (body, expectedPasswordHash) = await CaptureTokenRequestAsync(username, "provider-password");

            // Must be well-formed JSON. Concatenation produces an unparseable or restructured body.
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal(username, root.GetProperty("username").GetString());
            Assert.Equal(expectedPasswordHash, root.GetProperty("password").GetString());
            Assert.Equal(2, root.EnumerateObject().Count());
        }

        [Fact]
        public async Task GetToken_OrdinaryUsername_BodyUnchanged()
        {
            const string Username = "sd-user@example.com";
            var (body, expectedPasswordHash) = await CaptureTokenRequestAsync(Username, "provider-password");

            // The legitimate operation must keep producing the exact wire format Schedules Direct expects.
            Assert.Equal(
                "{\"username\":\"" + Username + "\",\"password\":\"" + expectedPasswordHash + "\"}",
                body);
        }

        private async Task<(string Body, string ExpectedPasswordHash)> CaptureTokenRequestAsync(string username, string password)
        {
            using var handler = new CapturingHandler();
            using var httpClient = new HttpClient(handler, disposeHandler: false);

            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var appPaths = new Mock<IApplicationPaths>();
            appPaths.SetupGet(x => x.CachePath).Returns(_cachePath);

            using var provider = new SdProvider(
                NullLogger<SdProvider>.Instance,
                httpClientFactory.Object,
                appPaths.Object);

            var info = new ListingsProviderInfo
            {
                Username = username,
                Password = password,
                ListingsId = "TEST-LINEUP",
            };

            try
            {
                // Drives the real production path: Validate -> HasLineup -> GetToken -> GetTokenInternal.
                await provider.Validate(info, validateLogin: false, validateListings: true).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The stub handler rejects every request; only the outbound token body is under test.
            }

            var tokenRequest = handler.Requests.FirstOrDefault(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal));
            Assert.False(tokenRequest.Uri is null, "No Schedules Direct /token request was issued.");

#pragma warning disable CA5350 // Schedules Direct is always SHA1.
            var expectedPasswordHash = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(password)));
#pragma warning restore CA5350

            return (tokenRequest.Body, expectedPasswordHash);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    Directory.Delete(_cachePath, true);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }

            _disposed = true;
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            public List<(Uri Uri, string Body)> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                Requests.Add((request.RequestUri!, body));

                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{}"),
                };
            }
        }
    }
}
