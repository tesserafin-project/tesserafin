using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that records every request and answers from a canned body,
    /// so a test can assert both that a request was issued and that none was.
    /// </summary>
    public sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<Uri> _requests = new();
        private readonly Lock _sync = new();
        private readonly string _body;

        /// <summary>Initializes a new instance of the <see cref="RecordingHandler"/> class.</summary>
        /// <param name="body">The response body to answer with.</param>
        public RecordingHandler(string body = "{}")
        {
            _body = body;
        }

        /// <summary>Gets every request URI this handler was asked to send.</summary>
        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            lock (_sync)
            {
                _requests.Add(request.RequestUri!);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
