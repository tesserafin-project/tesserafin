using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tesserafin.Providers.Plugins.AudioDb;
using Tesserafin.Providers.Tests.Plugins;
using Xunit;

namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>
    /// The provider-authentication structural gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gitleaks is necessary here and not sufficient. It found the 27-character fixture in the web
    /// repository and it would find a 32-character hexadecimal key, but it reported the contaminated
    /// server image as clean: the credentials were .NET <c>const string</c>s inlined into the
    /// <c>#US</c> metadata heap as UTF-16LE, which Gitleaks does not decode, and two of the three
    /// were six and eight characters long — below any entropy or length threshold a generic scanner
    /// applies. No amount of Gitleaks tuning reaches them.
    /// </para>
    /// <para>
    /// This gate closes that gap from the other direction: it ignores what a value looks like
    /// entirely and asks only where it is used. It runs inside <c>dotnet test</c>, so
    /// <c>./ci/run.sh</c> already executes it and a future provider credential fails the merge gate.
    /// </para>
    /// </remarks>
    public sealed class ProviderAuthAuditTests
    {
        private static readonly ProviderAuthInventory Inventory = ProviderAuthInventory.Load();

        // Assembled at run time so that no credential-shaped literal exists in this file's committed
        // bytes — the same discipline the gate itself enforces on production code.
        private static string LongKey => string.Concat(Enumerable.Repeat("0f1e2d3c", 4));

        private static string ShortKey => string.Concat("19", "50", "03");

        private static string MediumKey => string.Concat("2c9d", "9507");

        [Fact]
        public void ProductionProvidersAssembly_HasNoProviderAuthViolations()
        {
            var violations = new ProviderAuthAuditor(Inventory).Audit(ProvidersAssemblyPath());

            Assert.True(
                violations.Count == 0,
                "provider authentication audit failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }

        [Fact]
        public void Inventory_DeclaresEveryProviderThatIssuesAnOutboundRequest()
        {
            // A cross-check on the inventory itself: the hosts it declares must be exactly the hosts
            // the audited assembly composes requests for. `allowedHostStrings` is what makes that
            // checkable, and the audit fails on any host string not declared there.
            Assert.NotEmpty(Inventory.Providers);
            Assert.All(Inventory.Providers, p => Assert.False(string.IsNullOrWhiteSpace(p.Host)));
            Assert.All(Inventory.Providers, p => Assert.False(string.IsNullOrWhiteSpace(p.MissingKeyBehaviour)));
            Assert.All(
                Inventory.Configured(),
                p =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(p.ConfigurationType));
                    Assert.False(string.IsNullOrWhiteSpace(p.ConfigurationProperty));
                    Assert.NotEmpty(p.CredentialReaders);
                });
        }

        [Fact]
        public void Inventory_ContainsNoCredentialValue()
        {
            // The inventory is committed, so it must be provably value-free. Anything that reaches
            // an auth boundary in it would be a credential by this gate's own definition.
            var text = File.ReadAllText(ProviderAuthInventory.Locate("ci/provider-auth-inventory.json"));

            foreach (var provider in Inventory.Providers.Where(p => p.AuthBoundary is not null))
            {
                var index = 0;
                while ((index = text.IndexOf(provider.AuthBoundary!, index, StringComparison.Ordinal)) >= 0)
                {
                    var after = text[(index + provider.AuthBoundary!.Length)..];
                    Assert.True(
                        after.StartsWith('"') || after.StartsWith("\\\"", StringComparison.Ordinal),
                        $"the inventory continues past the {provider.Name} authentication boundary");
                    index += provider.AuthBoundary.Length;
                }
            }
        }

        public static TheoryData<string, string, string> DetectionControls()
        {
            var data = new TheoryData<string, string, string>();

            // 1. The former TheMovieDb shape: a long key concatenated into a request URL.
            data.Add(
                "tmdb-long-key",
                "unregistered-auth-path",
                Fixture("Tmdb", $$"""const string Url = "https://api.themoviedb.org/3/movie?api_key=" + "{{LongKey}}";"""));

            // 2. A six-character TheAudioDB-style key folded into the declared base URL.
            data.Add(
                "audiodb-six-character-key",
                "auth-boundary-not-terminal",
                Fixture("AudioDb", $$"""const string Url = "https://www.theaudiodb.com/api/v1/json/" + "{{ShortKey}}";"""));

            // 3. An eight-character OMDb-style key in the declared apikey parameter.
            data.Add(
                "omdb-eight-character-key",
                "auth-boundary-not-terminal",
                Fixture("Omdb", $$"""const string Url = "https://www.omdbapi.com?apikey=" + "{{MediumKey}}";"""));

            // 4. Compile-time concatenation across two named constants.
            data.Add(
                "compile-time-concatenation",
                "auth-boundary-not-terminal",
                Fixture("Concat", $$"""
                    const string Root = "https://www.omdbapi.com?apikey=";
                    const string Key = "{{MediumKey}}";
                    const string Url = Root + Key;
                    """));

            // 5. Interpolation of a constant into a query URL.
            data.Add(
                "constant-interpolation",
                "auth-boundary-not-terminal",
                Fixture("Interpolate", $$"""
                    const string Key = "{{MediumKey}}";
                    const string Url = $"https://www.omdbapi.com?apikey={Key}";
                    """));

            // 6. A literal Authorization header.
            data.Add(
                "literal-authorization-header",
                "unregistered-auth-path",
                Fixture("Header", $$"""const string Header = "Authorization: Bearer {{LongKey}}";"""));

            // 7. A value split into constant fragments, which the compiler folds back together.
            data.Add(
                "constant-fragments",
                "auth-boundary-not-terminal",
                Fixture("Fragments", $$"""
                    const string A = "{{MediumKey[..2]}}";
                    const string B = "{{MediumKey[2..5]}}";
                    const string C = "{{MediumKey[5..]}}";
                    const string Url = "https://www.omdbapi.com?apikey=" + A + B + C;
                    """));

            // 8. An authenticated request to a host no inventory entry declares.
            data.Add(
                "unregistered-provider",
                "unregistered-auth-path",
                Fixture("Unregistered", $$"""const string Url = "https://metadata.example.invalid/v2/lookup?access_token={{LongKey}}";"""));

            return data;
        }

        [Theory]
        [MemberData(nameof(DetectionControls))]
        public void Audit_DetectsCredentialShapes(string control, string expectedRule, string source)
        {
            using var directory = new TempDirectory();
            var assembly = ControlFixtureCompiler.Compile(directory.Path, "Control." + control, source);

            var violations = new ProviderAuthAuditor(Inventory).Audit(assembly, policeInventory: false);

            Assert.Contains(violations, v => string.Equals(v.Rule, expectedRule, StringComparison.Ordinal));
        }

        public static TheoryData<string, string> AcceptanceControls()
        {
            var data = new TheoryData<string, string>();

            // a. An anonymous public endpoint.
            data.Add(
                "anonymous-endpoint",
                Fixture("Anonymous", """
                    const string Url = "https://anonymous.example.invalid/ws/2/release/";
                    public static string Get() => Url;
                    """));

            // b. A credential supplied by operator configuration and appended at run time.
            data.Add(
                "operator-configured",
                Fixture("Configured", """
                    const string Root = "https://www.omdbapi.com?apikey=";
                    public static string Build(string operatorKey) => Root + operatorKey;
                    """));

            // c. Ordinary, non-authentication query parameters.
            data.Add(
                "ordinary-query-parameters",
                Fixture("Ordinary", """
                    const string Url = "https://metadata.example.invalid/lookup?i=tt0133093&plot=short&r=json&sort=year";
                    public static string Get() => Url;
                    """));

            return data;
        }

        [Theory]
        [MemberData(nameof(AcceptanceControls))]
        public void Audit_AcceptsLegitimateShapes(string control, string source)
        {
            using var directory = new TempDirectory();
            var assembly = ControlFixtureCompiler.Compile(directory.Path, "Control." + control, source);

            var violations = new ProviderAuthAuditor(Inventory).Audit(assembly, policeInventory: false);

            Assert.True(
                violations.Count == 0,
                $"control '{control}' should have been accepted:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }

        [Fact]
        public void Audit_AcceptsASyntheticCredentialThatOnlyExistsAtRunTime()
        {
            // The counterpart to every detection control above: a credential that is never a
            // compile-time constant is not a finding, however credential-shaped it looks. Nothing
            // here or in the emitted assembly contains the value; it is produced at run time inside
            // a directory that is deleted when this test returns.
            using var directory = new TempDirectory();
            var assembly = ControlFixtureCompiler.Compile(
                directory.Path,
                "Control.runtime-only",
                Fixture("RuntimeOnly", """
                    const string Root = "https://www.omdbapi.com?apikey=";
                    public static string Build() => Root + System.Guid.NewGuid().ToString("N");
                    """));

            File.WriteAllText(Path.Combine(directory.Path, "runtime-key.txt"), LongKey);

            var violations = new ProviderAuthAuditor(Inventory).Audit(assembly, policeInventory: false);

            Assert.Empty(violations.Select(v => v.ToString()));
        }

        [Fact]
        public void Audit_ReportsAStaleInventoryEntry()
        {
            // The inverse failure: an entry describing a code path that no longer exists. Proven
            // against an assembly that declares none of the inventory's boundaries.
            using var directory = new TempDirectory();
            var assembly = ControlFixtureCompiler.Compile(
                directory.Path,
                "Control.stale",
                Fixture("Stale", """const string Unrelated = "nothing to see here";"""));

            var violations = new ProviderAuthAuditor(Inventory).Audit(assembly);

            Assert.Contains(violations, v => string.Equals(v.Rule, "stale-inventory-entry", StringComparison.Ordinal));
        }

        [Fact]
        public void Audit_NeverQuotesTheValueItFound()
        {
            // A gate that prints the credential it found has leaked it into every CI log. Proven by
            // constructing a violation and asserting the value is absent from its rendered text.
            using var directory = new TempDirectory();
            var assembly = ControlFixtureCompiler.Compile(
                directory.Path,
                "Control.redaction",
                Fixture("Redaction", $$"""const string Url = "https://www.omdbapi.com?apikey=" + "{{MediumKey}}";"""));

            var violations = new ProviderAuthAuditor(Inventory).Audit(assembly, policeInventory: false);

            Assert.NotEmpty(violations);
            Assert.All(violations, v => Assert.DoesNotContain(MediumKey, v.ToString(), StringComparison.Ordinal));
        }

        [Fact]
        public void AuditAgreesWithTheProvidersItClaimsToCover()
        {
            // The AudioDB root is the one anonymous URL constant the audit accepts for that host, so
            // this asserts the accepted form directly rather than trusting the allowlist's spelling.
            Assert.Equal("https://www.theaudiodb.com/api/v1/json", AudioDbApi.ApiRoot);
        }

        private static string Fixture(string name, string body) => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            namespace Control.{{name}}
            {
                public static class Fixture
                {
                    {{body}}
                }
            }
            """);

        private static string ProvidersAssemblyPath()
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "Tesserafin.Providers.dll");
            Assert.True(File.Exists(candidate), $"the audited assembly is not next to the test assembly: {candidate}");
            return candidate;
        }
    }
}
