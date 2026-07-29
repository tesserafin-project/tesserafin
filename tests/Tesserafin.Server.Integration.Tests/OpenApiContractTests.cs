using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Tesserafin.Server.Core;
using Xunit;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Guards the committed OpenAPI contract: determinism, version provenance, and drift.
    ///
    /// <para>
    /// These tests are the OpenAPI stage of the mandatory local merge gate. They live in the
    /// test suite rather than as extra steps in <c>ci/run.sh</c> deliberately: the suite already
    /// boots the server once, so the drift check costs a single extra HTTP call instead of a
    /// second Docker container, and <c>ci/run.sh</c> stays a thin, hard-to-break wrapper.
    /// </para>
    ///
    /// <para>
    /// Two modes, selected by <see cref="OpenApiContract.WriteEnvironmentVariable"/>:
    /// unset (the gate) verifies the committed files match the server; set (only by
    /// <c>ci/openapi-generate.sh</c>) rewrites them. Without that split, the "check" would be
    /// checking a file it had just written and could never fail.
    /// </para>
    /// </summary>
    public sealed class OpenApiContractTests : IClassFixture<TesserafinApplicationFactory>
    {
        private readonly TesserafinApplicationFactory _factory;
        private readonly ITestOutputHelper _outputHelper;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenApiContractTests"/> class.
        /// </summary>
        /// <param name="factory">The application factory.</param>
        /// <param name="outputHelper">The xunit output helper.</param>
        public OpenApiContractTests(TesserafinApplicationFactory factory, ITestOutputHelper outputHelper)
        {
            _factory = factory;
            _outputHelper = outputHelper;
        }

        /// <summary>
        /// Two cold generations of the contract must be byte-identical.
        ///
        /// <para>
        /// "Cold" matters: <c>CachingOpenApiProvider</c> memoises the document for five minutes
        /// per application instance, so hitting <c>/api-docs/openapi.json</c> twice against the
        /// same server would compare a cached object with itself and prove nothing. Each half of
        /// this comparison therefore comes from its own freshly booted application, which
        /// re-runs schema generation from scratch.
        /// </para>
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task Contract_IsByteIdentical_AcrossColdGenerations()
        {
            byte[] first;
            byte[] second;

            await using (var factoryA = new TesserafinApplicationFactory())
            {
                first = await GenerateCanonicalAsync(factoryA);
            }

            await using (var factoryB = new TesserafinApplicationFactory())
            {
                second = await GenerateCanonicalAsync(factoryB);
            }

            var firstHash = OpenApiContract.Fingerprint(first);
            var secondHash = OpenApiContract.Fingerprint(second);
            _outputHelper.WriteLine("cold generation #1 sha256 = {0}", firstHash);
            _outputHelper.WriteLine("cold generation #2 sha256 = {0}", secondHash);

            Assert.Equal(firstHash, secondHash);
            Assert.Equal(first, second);
        }

        /// <summary>
        /// <c>info.version</c> must be the running server's assembly version, so that a pinned
        /// contract names a real server build. Not a literal, not a build timestamp.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task InfoVersion_ComesFromServerAssemblyVersion()
        {
            var canonical = await GenerateCanonicalAsync(_factory);
            var expected = typeof(ApplicationHost).Assembly.GetName().Version?.ToString(3);

            Assert.False(string.IsNullOrEmpty(expected));
            Assert.Equal(expected, OpenApiContract.ReadInfoVersion(canonical));
        }

        /// <summary>
        /// Drift gate: the committed contract must equal what this server produces.
        /// Under <see cref="OpenApiContract.WriteEnvironmentVariable"/> it regenerates instead.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task CommittedContract_MatchesRunningServer()
        {
            var repoRoot = OpenApiContract.FindRepositoryRoot();
            var specPath = Path.Combine(repoRoot, OpenApiContract.SpecRelativePath);
            var lockPath = Path.Combine(repoRoot, OpenApiContract.LockRelativePath);

            var canonical = await GenerateCanonicalAsync(_factory);
            var version = OpenApiContract.ReadInfoVersion(canonical);
            var fingerprint = OpenApiContract.Fingerprint(canonical);
            var lockBytes = OpenApiContract.BuildLockFile(version, fingerprint);

            if (Environment.GetEnvironmentVariable(OpenApiContract.WriteEnvironmentVariable) == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
                await File.WriteAllBytesAsync(specPath, canonical, TestContext.Current.CancellationToken);
                await File.WriteAllBytesAsync(lockPath, lockBytes, TestContext.Current.CancellationToken);
                _outputHelper.WriteLine("REGENERATED {0} (version {1}, sha256 {2})", OpenApiContract.SpecRelativePath, version, fingerprint);
                return;
            }

            Assert.True(
                File.Exists(specPath),
                FormattableString.Invariant(
                    $"{OpenApiContract.SpecRelativePath} is missing. Generate it with: {OpenApiContract.RegenerateCommand}"));

            var committed = await File.ReadAllBytesAsync(specPath, TestContext.Current.CancellationToken);
            var committedFingerprint = OpenApiContract.Fingerprint(committed);
            _outputHelper.WriteLine("committed sha256 = {0}", committedFingerprint);
            _outputHelper.WriteLine("server    sha256 = {0}", fingerprint);

            if (committedFingerprint != fingerprint)
            {
                // Keep both documents. Two hashes say the contract moved; only the documents say
                // HOW, and a cross-machine divergence cannot be reproduced on the machine that
                // reports it. The hosted job uploads this directory on failure.
                await WriteDriftEvidenceAsync(repoRoot, committed, committedFingerprint, canonical, fingerprint);
            }

            Assert.True(
                committedFingerprint == fingerprint,
                OpenApiContract.BuildDriftMessage(committedFingerprint, fingerprint));

            // The sidecar is derived purely from the spec above, so once the spec matches this
            // can only disagree if the sidecar was hand-edited or committed stale.
            var committedLock = await File.ReadAllBytesAsync(lockPath, TestContext.Current.CancellationToken);
            Assert.True(
                committedLock.AsSpan().SequenceEqual(lockBytes),
                FormattableString.Invariant(
                    $"{OpenApiContract.LockRelativePath} does not match {OpenApiContract.SpecRelativePath}. Regenerate both with: {OpenApiContract.RegenerateCommand}"));
        }

        private async Task WriteDriftEvidenceAsync(
            string repoRoot,
            byte[] committed,
            string committedFingerprint,
            byte[] generated,
            string generatedFingerprint)
        {
            var evidenceDirectory = Path.Combine(repoRoot, OpenApiContract.DriftEvidenceRelativePath);
            Directory.CreateDirectory(evidenceDirectory);

            var committedPath = Path.Combine(evidenceDirectory, "committed.json");
            var generatedPath = Path.Combine(evidenceDirectory, "generated.json");
            var hashesPath = Path.Combine(evidenceDirectory, "hashes.txt");

            await File.WriteAllBytesAsync(committedPath, committed, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(generatedPath, generated, TestContext.Current.CancellationToken);

            // Repo-relative names only: an absolute path would name a private working directory
            // on whichever machine produced the evidence, and the artifact is meant to travel.
            await File.WriteAllTextAsync(
                hashesPath,
                FormattableString.Invariant(
                    $"{committedFingerprint}  committed.json{Environment.NewLine}{generatedFingerprint}  generated.json{Environment.NewLine}"),
                TestContext.Current.CancellationToken);

            _outputHelper.WriteLine(
                "drift evidence written to {0}/ (committed.json, generated.json, hashes.txt)",
                OpenApiContract.DriftEvidenceRelativePath);
        }

        private static async Task<byte[]> GenerateCanonicalAsync(TesserafinApplicationFactory factory)
        {
            using HttpClient client = factory.CreateClient();
            using var response = await client.GetAsync("/api-docs/openapi.json", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return OpenApiContract.Canonicalize(raw);
        }
    }
}
