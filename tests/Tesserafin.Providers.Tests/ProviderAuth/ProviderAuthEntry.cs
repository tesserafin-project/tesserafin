using System.Collections.Generic;
using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only — these are JSON DTOs.

namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>One declared provider in the provider authentication inventory.</summary>
    public sealed class ProviderAuthEntry
    {
        /// <summary>Gets or sets the provider's product name.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the outbound host this provider talks to.</summary>
        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        /// <summary>Gets or sets which assembly composes the request URL for that host.</summary>
        [JsonPropertyName("hostOwner")]
        public string HostOwner { get; set; } = string.Empty;

        /// <summary>Gets or sets the authentication mechanism, in prose.</summary>
        [JsonPropertyName("authentication")]
        public string Authentication { get; set; } = string.Empty;

        /// <summary>Gets or sets the query parameter or header carrying the credential.</summary>
        [JsonPropertyName("parameterName")]
        public string? ParameterName { get; set; }

        /// <summary>Gets or sets where the credential comes from.</summary>
        [JsonPropertyName("configurationSource")]
        public string ConfigurationSource { get; set; } = string.Empty;

        /// <summary>Gets or sets <c>anonymous</c> or <c>configured</c>.</summary>
        [JsonPropertyName("classification")]
        public string Classification { get; set; } = string.Empty;

        /// <summary>Gets or sets the configuration type declaring the credential property.</summary>
        [JsonPropertyName("configurationType")]
        public string? ConfigurationType { get; set; }

        /// <summary>Gets or sets the configuration property holding the credential.</summary>
        [JsonPropertyName("configurationProperty")]
        public string? ConfigurationProperty { get; set; }

        /// <summary>Gets or sets the type that turns the credential into a request.</summary>
        [JsonPropertyName("authType")]
        public string? AuthType { get; set; }

        /// <summary>
        /// Gets or sets the exact string constant at which the credential begins — the point in a
        /// request URL after which nothing may be a compile-time constant.
        /// </summary>
        [JsonPropertyName("authBoundary")]
        public string? AuthBoundary { get; set; }

        /// <summary>Gets or sets prose explaining the boundary, for readers of the inventory.</summary>
        [JsonPropertyName("authBoundaryNote")]
        public string? AuthBoundaryNote { get; set; }

        /// <summary>Gets or sets the methods permitted to read the credential property.</summary>
        [JsonPropertyName("credentialReaders")]
        public IList<string> CredentialReaders { get; set; } = new List<string>();

        /// <summary>Gets or sets the string constants permitted to mention this provider's host.</summary>
        [JsonPropertyName("allowedHostStrings")]
        public IList<string> AllowedHostStrings { get; set; } = new List<string>();

        /// <summary>Gets or sets what the provider does when no credential is configured.</summary>
        [JsonPropertyName("missingKeyBehaviour")]
        public string MissingKeyBehaviour { get; set; } = string.Empty;
    }
}
