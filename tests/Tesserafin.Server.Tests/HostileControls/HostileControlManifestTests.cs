using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Tesserafin.Server.Tests.HostileControls;

/// <summary>
/// The hostile controls that defend the Live TV stack must be replayable from the repository
/// (#153-LTV-R1, LTV-R0 finding 4).
/// </summary>
/// <remarks>
/// WHAT WAS WRONG. The S0 and S1 rosters — 9 and 12 mutations — were run from python harnesses
/// that live outside the tree, hardcode one worktree path and mutate it in place. Nothing a
/// reviewer clones can replay them, so every "RED" grade in those ledgers is a claim about a
/// script the reviewer cannot see. LTV-R0 could not reproduce a single one and had to define three
/// controls of its own instead.
///
/// WHY THIS TEST IS NOT VACUOUS. Asserting a file exists would pass against an empty manifest. So
/// this reads the manifest and requires it to be *applicable*: every mutation must name a file
/// that exists and an anchor that occurs in that file EXACTLY ONCE, every control must name the
/// test its gate runs and that test must exist in the suite, and the S0/S1 rosters must be
/// accounted for control by control. A manifest whose anchors have rotted fails here rather than
/// at 3 a.m. in the middle of a roster run.
/// </remarks>
public sealed class HostileControlManifestTests
{
    private const string ManifestRelativePath = "ci/hostile-controls/manifest.json";
    private const string RunnerRelativePath = "ci/hostile-controls/run.py";

    /// <summary>
    /// The identifiers #153-LTV-R1 requires on top of the replayed S0 and S1 rosters.
    /// </summary>
    private static readonly string[] _requiredR1Controls =
    {
        "r1-drop-item-job-comparison",
        "r1-drop-media-source-job-comparison",
        "r1-drop-play-session-job-comparison",
        "r1-read-capability-from-the-query",
        "r1-accept-a-query-with-no-validated-provenance",
        "r1-restore-the-ffprobe-http-auto-fetch",
        "r1-restore-segment-id-only-resolution",
        "r1-serve-a-file-after-the-binding-expired"
    };

    [Fact]
    public void AVersionedRunnerAndManifest_ExistInTheRepository()
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), ManifestRelativePath)), $"'{ManifestRelativePath}' is missing.");
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), RunnerRelativePath)), $"'{RunnerRelativePath}' is missing.");
    }

    [Fact]
    public void TheManifest_ReplaysBothHistoricalRostersControlByControl()
    {
        var controls = Controls();

        var s0 = controls.Where(c => string.Equals(Stage(c), "S0", StringComparison.Ordinal)).ToList();
        var s1 = controls.Where(c => string.Equals(Stage(c), "S1", StringComparison.Ordinal)).ToList();

        Assert.Equal(9, s0.Count);
        Assert.Equal(12, s1.Count);

        foreach (var control in s0.Concat(s1))
        {
            var status = Text(control, "status");
            Assert.True(
                status is "REPLAYED" or "SUPERSEDED",
                $"'{Text(control, "id")}' declares status '{status}'; a historical control is either REPLAYED or SUPERSEDED.");
        }
    }

    [Fact]
    public void TheManifest_CarriesEveryR1ControlTheMissionNames()
    {
        var ids = Controls().Select(c => Text(c, "id")).ToHashSet(StringComparer.Ordinal);

        foreach (var required in _requiredR1Controls)
        {
            Assert.Contains(required, ids);
        }
    }

    [Fact]
    public void EveryControl_DeclaresATimeoutAndARestorationRule()
    {
        var manifest = Manifest();

        Assert.Equal(
            "byte-identical",
            manifest.GetProperty("restoration").GetProperty("rule").GetString());

        foreach (var control in Controls())
        {
            Assert.True(
                control.GetProperty("timeoutSeconds").GetInt32() > 0,
                $"'{Text(control, "id")}' declares no timeout, so a hang could not be graded HUNG.");
        }
    }

    /// <summary>
    /// The anti-vacuity gate. Every mutation has to be applicable against the tree as it stands.
    /// </summary>
    [Fact]
    public void EveryMutation_AnchorsExactlyOnceInAFileThatExists()
    {
        var root = RepositoryRoot();
        var mutated = 0;

        foreach (var control in Controls())
        {
            var id = Text(control, "id");
            var mutations = control.GetProperty("mutations").EnumerateArray().ToList();

            if (Text(control, "expect") == "PASS")
            {
                Assert.Empty(mutations);
                continue;
            }

            Assert.True(mutations.Count > 0, $"'{id}' expects a red but mutates nothing.");

            foreach (var mutation in mutations)
            {
                var relative = mutation.GetProperty("file").GetString()!;
                var path = Path.Combine(root, relative);
                Assert.True(File.Exists(path), $"'{id}' mutates '{relative}', which does not exist.");

                var body = File.ReadAllText(path);
                var anchor = mutation.GetProperty("find").GetString()!;
                Assert.False(anchor.Length == 0, $"'{id}' declares an empty anchor.");

                var occurrences = Occurrences(body, anchor);
                Assert.True(
                    occurrences == 1,
                    $"'{id}' anchor occurs {occurrences} times in '{relative}', expected exactly 1.");

                Assert.NotEqual(anchor, mutation.GetProperty("replace").GetString());
                mutated++;
            }
        }

        Assert.True(mutated >= 19, $"only {mutated} mutations are applicable; the roster cannot be replayed.");
    }

    /// <summary>
    /// Every control names the test that decides it, and that test exists.
    /// </summary>
    [Fact]
    public void EveryNamedGateTest_ExistsInTheSuite()
    {
        var sources = Directory
            .GetFiles(Path.Combine(RepositoryRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        var checkedNames = 0;
        foreach (var control in Controls())
        {
            var id = Text(control, "id");
            var gate = control.GetProperty("gate");
            var kind = gate.GetProperty("kind").GetString();

            if (!string.Equals(kind, "dotnet-test", StringComparison.Ordinal))
            {
                // A rig or source gate names no test method; it is decided elsewhere and is
                // required to say so.
                Assert.True(
                    kind is "rig" or "source" or "inventory",
                    $"'{id}' declares an unknown gate kind '{kind}'.");
                continue;
            }

            foreach (var name in gate.GetProperty("expectedTests").EnumerateArray())
            {
                var method = name.GetString()!;
                var simple = method[(method.LastIndexOf('.') + 1)..];
                Assert.True(
                    sources.Any(source => source.Contains(simple, StringComparison.Ordinal)),
                    $"'{id}' expects test '{method}', which is in no test source.");
                checkedNames++;
            }
        }

        Assert.True(checkedNames >= 15, $"only {checkedNames} gate tests were checked; the manifest names too few.");
    }

    private static int Occurrences(string body, string needle)
    {
        var count = 0;
        var index = body.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = body.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string Text(JsonElement element, string property)
        => element.GetProperty(property).GetString() ?? string.Empty;

    private static string Stage(JsonElement control) => Text(control, "stage");

    private static IReadOnlyList<JsonElement> Controls()
        => Manifest().GetProperty("controls").EnumerateArray().ToList();

    private static JsonElement Manifest()
    {
        var path = Path.Combine(RepositoryRoot(), ManifestRelativePath);
        Assert.True(File.Exists(path), $"'{ManifestRelativePath}' is missing; the controls are not replayable from the repository.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Tesserafin.sln above '{AppContext.BaseDirectory}'.");
    }
}
