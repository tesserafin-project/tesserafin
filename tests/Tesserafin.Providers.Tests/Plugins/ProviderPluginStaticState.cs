using Xunit;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>
    /// Serialises every test class that manipulates a plugin's process-wide static state — the
    /// <c>Plugin.Instance</c> singleton and the one-shot unconfigured-warning latch.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ProviderPluginStaticState
    {
        /// <summary>The xUnit collection name.</summary>
        public const string Name = "provider-plugin-static-state";
    }
}
