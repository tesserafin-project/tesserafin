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
    private const string CounterControlsRelativePath = "ci/hostile-controls/counter-controls.json";
    private const string RunnerRelativePath = "ci/hostile-controls/run.py";
    private const string UndeclaredReportingProbe = "ci/hostile-controls/prove-undeclared-reporting.py";
    private const string SchemaLockdownProbe = "ci/hostile-controls/prove-schema-lockdown.py";
    private const string LocalCiScriptRelativePath = "ci/run.sh";

    /// <summary>
    /// The one grader autotest allowed to keep failures it does not declare, and only by naming
    /// every one of them (#153-LTV-R9).
    /// </summary>
    private const string Cc10Id = "cc-10-a-declared-red-with-undeclared-collateral-is-reported-as-such";

    /// <summary>
    /// The generic opt-out #153-LTV-R8 found, kept here by name so that reintroducing it anywhere in
    /// either control document fails this suite rather than silently switching a gate off.
    /// </summary>
    private const string RemovedOptOut = "allowUndeclaredFailures";

    /// <summary>
    /// The narrow, cc-10-only exemption that replaced it. It is legal in the autotests only.
    /// </summary>
    private const string Cc10Exemption = "expectUndeclaredFailures";

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
        "r1-serve-a-file-after-the-binding-expired",
        "r1-drop-caller-named-media-source-comparison"
    };

    /// <summary>
    /// The closed control schema, mirrored from ci/hostile-controls/run.py. A key the runner does
    /// not read is a key a reviewer reads as load-bearing; R8's opt-out was the reverse — a key the
    /// runner DID read, that the roster was free to set, and that switched off the collateral gate.
    /// </summary>
    private static readonly HashSet<string> _rosterControlKeys = new(StringComparer.Ordinal)
    {
        "id", "stage", "status", "expect", "property", "timeoutSeconds", "gate", "mutations",
        "historicalId", "note", "historicalMutations", "supersededReason"
    };

    private static readonly HashSet<string> _autotestControlKeys = new(StringComparer.Ordinal)
    {
        "id", "stage", "status", "expect", "property", "timeoutSeconds", "gate", "mutations",
        "note", Cc10Exemption
    };

    private static readonly Dictionary<string, HashSet<string>> _gateKeys = new(StringComparer.Ordinal)
    {
        ["dotnet-test"] = new(StringComparer.Ordinal) { "kind", "project", "filter", "expectedTests" },
        ["rig"] = new(StringComparer.Ordinal) { "kind", "scenario", "requires" },
        ["source"] = new(StringComparer.Ordinal) { "kind", "file", "absentPattern" },
        ["inventory"] = new(StringComparer.Ordinal) { "kind", "script" }
    };

    private static readonly HashSet<string> _mutationKeys = new(StringComparer.Ordinal)
    {
        "file", "find", "replace", "count"
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

    /// <summary>
    /// The second defence of #153-LTV-R9 step 2: the ROSTER carries no opt-out mechanism at all.
    /// </summary>
    /// <remarks>
    /// The runner refuses these keys itself; this is the independent check that does not depend on
    /// the runner being the version that refuses them. #153-LTV-R8 found `allowUndeclaredFailures`
    /// neutralising the collateral gate for any line that set it — an ordinary roster control with
    /// nine undeclared failures exited 1 without it and 0 with it. It is gone, at every value, and
    /// the cc-10 exemption that replaced it is not a roster mechanism either.
    /// </remarks>
    [Fact]
    public void TheRosterManifest_CarriesNoUndeclaredFailureOptOut()
    {
        var raw = File.ReadAllText(Path.Combine(RepositoryRoot(), ManifestRelativePath));

        Assert.False(
            raw.Contains(RemovedOptOut, StringComparison.Ordinal),
            $"'{ManifestRelativePath}' mentions '{RemovedOptOut}'. That opt-out was removed by "
            + "#153-LTV-R9: a roster control may not keep a failure it does not declare, at any value.");

        Assert.False(
            raw.Contains(Cc10Exemption, StringComparison.Ordinal),
            $"'{ManifestRelativePath}' mentions '{Cc10Exemption}'. That list is the grader's own "
            + $"exemption, legal on '{Cc10Id}' inside {CounterControlsRelativePath} and nowhere else; "
            + "naming its collateral does not buy a production control the exemption either.");

        foreach (var control in Controls())
        {
            Assert.DoesNotContain(RemovedOptOut, control.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            Assert.DoesNotContain(Cc10Exemption, control.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The roster schema is CLOSED: a key nothing reads is refused rather than ignored.
    /// </summary>
    [Fact]
    public void TheRosterManifest_DeclaresNoKeyOutsideTheClosedSchema()
    {
        foreach (var control in Controls())
        {
            AssertClosedShape(control, _rosterControlKeys, ManifestRelativePath);
        }
    }

    /// <summary>
    /// The grader's own autotests are closed too, and carry the cc-10 special case in exactly the
    /// shape #153-LTV-R9 step 2 mandates: present, on that id alone, non-empty, all strings.
    /// </summary>
    [Fact]
    public void TheGraderAutotests_CarryTheCc10ExemptionAndNothingElse()
    {
        var autotests = CounterControls();

        foreach (var control in autotests)
        {
            AssertClosedShape(control, _autotestControlKeys, CounterControlsRelativePath);
        }

        var carriers = autotests
            .Where(c => c.TryGetProperty(Cc10Exemption, out _))
            .Select(c => Text(c, "id"))
            .ToList();

        Assert.Equal(new[] { Cc10Id }, carriers);

        var cc10 = autotests.Single(c => string.Equals(Text(c, "id"), Cc10Id, StringComparison.Ordinal));
        var names = cc10.GetProperty(Cc10Exemption).EnumerateArray().ToList();

        Assert.True(
            names.Count > 0,
            $"'{Cc10Id}' declares an EMPTY {Cc10Exemption}. That list is at once the permission and "
            + "the oracle: empty, it would permit collateral while asserting nothing about it.");

        foreach (var name in names)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(name.GetString()),
                $"'{Cc10Id}' declares a blank entry in {Cc10Exemption}.");
        }

        Assert.Equal(names.Count, names.Select(n => n.GetString()!).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("RED", Text(cc10, "expect"));
    }

    /// <summary>
    /// #153-LTV-R9 step 4: the two grader self-proofs are wired into the mandatory local gate.
    /// </summary>
    /// <remarks>
    /// A probe no gate runs proves nothing — which is the same defect, one level up, as the one the
    /// probes exist to close. Each is asserted separately and by name, so deleting one invocation
    /// from ci/run.sh fails on an assertion that says which probe is missing. "Exactly once" is part
    /// of the contract: a second invocation would double a build the gate already pays for.
    /// </remarks>
    [Fact]
    public void TheLocalCiGate_InvokesEachGraderProbeExactlyOnce()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, LocalCiScriptRelativePath));

        foreach (var probe in new[] { UndeclaredReportingProbe, SchemaLockdownProbe })
        {
            Assert.True(
                File.Exists(Path.Combine(root, probe)),
                $"'{probe}' is missing from the repository.");

            // The literal command, not a mention: the script also NAMES both probes in the comment
            // that says what they replay, and a comment runs nothing.
            var invocation = $"python3 \"$REPO_ROOT/{probe}\"";
            var invocations = Occurrences(script, invocation);
            Assert.True(
                invocations == 1,
                $"'{LocalCiScriptRelativePath}' invokes '{probe}' {invocations} time(s), expected "
                + "exactly 1. That probe is the only thing that proves the hostile-control grader can "
                + "still fail (#153-LTV-R9 step 4); unrun, it is evidence nobody replays.");
        }

        Assert.True(
            Occurrences(script, "PROBE_UNDECLARED_STATUS") >= 2
            && Occurrences(script, "PROBE_SCHEMA_STATUS") >= 2,
            $"'{LocalCiScriptRelativePath}' runs the probes but does not read their exit status; a "
            + "gate that ignores what it ran is not a gate.");
    }

    private static void AssertClosedShape(JsonElement control, HashSet<string> allowed, string document)
    {
        var id = Text(control, "id");

        foreach (var property in control.EnumerateObject())
        {
            Assert.True(
                allowed.Contains(property.Name),
                $"'{id}' in '{document}' declares '{property.Name}', which is outside the closed "
                + "control schema; the runner grades such a control ERROR.");
        }

        var gate = control.GetProperty("gate");
        var kind = gate.GetProperty("kind").GetString()!;
        Assert.True(_gateKeys.ContainsKey(kind), $"'{id}' declares an unknown gate kind '{kind}'.");

        foreach (var property in gate.EnumerateObject())
        {
            Assert.True(
                _gateKeys[kind].Contains(property.Name),
                $"'{id}' declares gate key '{property.Name}', which a '{kind}' gate does not read.");
        }

        foreach (var mutation in control.GetProperty("mutations").EnumerateArray())
        {
            foreach (var property in mutation.EnumerateObject())
            {
                Assert.True(
                    _mutationKeys.Contains(property.Name),
                    $"'{id}' declares mutation key '{property.Name}', which nothing reads.");
            }
        }
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

    private static IReadOnlyList<JsonElement> CounterControls()
        => Document(CounterControlsRelativePath).GetProperty("controls").EnumerateArray().ToList();

    private static JsonElement Manifest() => Document(ManifestRelativePath);

    private static JsonElement Document(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"'{relativePath}' is missing; the controls are not replayable from the repository.");
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
