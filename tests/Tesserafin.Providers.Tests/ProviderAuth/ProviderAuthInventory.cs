using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only — these are JSON DTOs.

namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>
    /// The repository-owned provider authentication inventory, as loaded from
    /// <c>ci/provider-auth-inventory.json</c>.
    /// </summary>
    /// <remarks>
    /// The inventory is contract data: it declares, for every production provider under
    /// <c>Tesserafin.Providers</c> that issues an outbound request, which host it talks to, whether
    /// it authenticates, how, from which configuration property, and what it does when that property
    /// is empty. It carries no credential values. <see cref="ProviderAuthAuditor"/> compares it
    /// against the compiled assembly and fails when the two disagree in either direction.
    /// </remarks>
    public sealed class ProviderAuthInventory
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>Gets or sets the inventory schema version.</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; }

        /// <summary>Gets or sets the audited assembly's file name.</summary>
        [JsonPropertyName("assembly")]
        public string Assembly { get; set; } = string.Empty;

        /// <summary>Gets or sets the substrings that mark a string as carrying authentication material.</summary>
        [JsonPropertyName("authMarkers")]
        public IList<string> AuthMarkers { get; set; } = new List<string>();

        /// <summary>Gets or sets the declared providers.</summary>
        [JsonPropertyName("providers")]
        public IList<ProviderAuthEntry> Providers { get; set; } = new List<ProviderAuthEntry>();

        /// <summary>Gets or sets the permitted string constants.</summary>
        [JsonPropertyName("constantAllowlist")]
        public ConstantAllowlist ConstantAllowlist { get; set; } = new();

        /// <summary>
        /// Locates and loads <c>ci/provider-auth-inventory.json</c> by walking up from the test
        /// assembly's directory until the repository root is found.
        /// </summary>
        /// <returns>The loaded inventory.</returns>
        public static ProviderAuthInventory Load()
        {
            var path = Locate("ci/provider-auth-inventory.json");

            return JsonSerializer.Deserialize<ProviderAuthInventory>(File.ReadAllText(path), SerializerOptions)
                   ?? throw new InvalidOperationException("the provider authentication inventory deserialised to null");
        }

        /// <summary>Locates a repository-relative path from the test assembly's location.</summary>
        /// <param name="relative">The repository-relative path.</param>
        /// <returns>The absolute path.</returns>
        public static string Locate(string relative)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"could not locate '{relative}' above {AppContext.BaseDirectory}");
        }

        /// <summary>Gets every declared provider that authenticates.</summary>
        /// <returns>The configured providers.</returns>
        public IEnumerable<ProviderAuthEntry> Configured()
            => Providers.Where(p => string.Equals(p.Classification, "configured", StringComparison.Ordinal));
    }
}
