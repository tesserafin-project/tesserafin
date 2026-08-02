using System.Collections.Generic;
using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only — these are JSON DTOs.

namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>The permitted string constants in provider plugin namespaces.</summary>
    public sealed class ConstantAllowlist
    {
        /// <summary>Gets or sets the namespaces whose string constants are policed.</summary>
        [JsonPropertyName("namespaces")]
        public IList<string> Namespaces { get; set; } = new List<string>();

        /// <summary>Gets or sets the permitted <c>Namespace.Type.Field</c> constant names.</summary>
        [JsonPropertyName("allowed")]
        public IList<string> Allowed { get; set; } = new List<string>();
    }
}
