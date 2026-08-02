using System;
using System.IO;
using System.Net.Http;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.Serialization;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>
    /// Shared scaffolding for the operator-supplied provider credential tests.
    /// </summary>
    /// <remarks>
    /// The AudioDB and OMDb plugins expose their configuration through a static
    /// <c>Plugin.Instance</c>, and their unconfigured diagnostic is latched in a static field so one
    /// missing setting produces one log line rather than one per provider class. Both are
    /// process-wide, so the test classes that use this harness share an xUnit collection and never
    /// run concurrently — see <see cref="ProviderPluginStaticState"/>.
    /// </remarks>
    public static class ProviderPluginHarness
    {
        /// <summary>
        /// Builds application paths rooted in a disposable directory, so a plugin constructed in a
        /// test never reads or writes a real Tesserafin installation.
        /// </summary>
        /// <param name="root">Disposable directory to root every path in.</param>
        /// <returns>The mocked application paths.</returns>
        public static IApplicationPaths ApplicationPaths(string root)
        {
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(root, "plugins"));
            paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(root, "plugins", "configurations"));
            paths.SetupGet(p => p.CachePath).Returns(Path.Combine(root, "cache"));
            paths.SetupGet(p => p.DataPath).Returns(Path.Combine(root, "data"));
            return paths.Object;
        }

        /// <summary>
        /// Builds an XML serializer that never touches disk. Plugin configuration is set directly in
        /// these tests, so neither deserialization nor persistence is exercised here.
        /// </summary>
        /// <returns>The mocked serializer.</returns>
        public static IXmlSerializer XmlSerializer()
        {
            var serializer = new Mock<IXmlSerializer>();
            serializer
                .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
                .Throws(() => new FileNotFoundException("no persisted configuration in tests"));
            return serializer.Object;
        }

        /// <summary>
        /// Produces a syntactically valid but entirely fictional API key, assembled at runtime.
        /// </summary>
        /// <param name="length">Number of characters to produce.</param>
        /// <returns>The synthetic key.</returns>
        /// <remarks>
        /// Assembled rather than written as a literal so no credential-shaped constant exists in this
        /// repository's committed bytes, including its tests.
        /// </remarks>
        public static string SyntheticKey(int length)
        {
            var alphabet = string.Concat("abcdef", "012345", "6789");
            return string.Create(length, alphabet, (span, source) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = source[i % source.Length];
                }
            });
        }

        /// <summary>
        /// Builds an <see cref="IHttpClientFactory"/> whose clients all route through
        /// <paramref name="handler"/>, so a test can observe exactly which requests were issued.
        /// </summary>
        /// <param name="handler">The recording handler.</param>
        /// <returns>The mocked factory.</returns>
        public static IHttpClientFactory HttpClientFactory(RecordingHandler handler)
        {
            var factory = new Mock<IHttpClientFactory>();
            factory
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return factory.Object;
        }
    }
}
