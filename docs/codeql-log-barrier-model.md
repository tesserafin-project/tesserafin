# The `ToSingleLogLine` log-injection barrier model

Refs #203, #185, #94.

This document explains one security boundary and the CodeQL model that describes
it: what the boundary actually guarantees, why a model pack was needed at all,
exactly how narrow the model is, and what it costs to keep.

## 1. The defect the boundary exists for

This server ships two Serilog output configurations and they do not agree.

* The **text output template** in
  `Tesserafin.Server/Resources/Configuration/logging.json` — the default outside
  the container image — renders a logged value verbatim. A value containing
  `CR`/`LF` therefore produces a **second physical log record** whose timestamp,
  level and category the attacker chose.
* The **JSON-lines formatter** used inside the container image escapes the same
  value and emits one record.

So the defect is real on one shipped path and already mitigated on the other, and
the code must not depend on which formatter an operator selected.
`tests/Tesserafin.Server.Tests/LogRecordSeparationTests.cs` drives the real
Serilog pipeline for both formatters and proves each half of that statement.

## 2. What the boundary does, and does not do

`Tesserafin.Extensions.LogValueExtensions.ToSingleLogLine(string?)`:

* replaces every `CR` with the two visible characters `\r`;
* replaces every `LF` with the two visible characters `\n`;
* returns `null` and the empty string unchanged;
* returns a value containing neither separator unchanged **and by reference**;
* leaves `U+0085`, `U+2028` and `U+2029` exactly as they are, because neither
  shipped formatter splits a record on them;
* redacts nothing, hashes nothing, truncates nothing.

21 contract tests in `tests/Tesserafin.Extensions.Tests/LogValueExtensionsTests.cs`
hold that contract. They are not decorative: replacing the body with `return
value;` fails 8 of the 21, and 4 of the 6 formatter tests.

## 3. Why a model pack, and not just the fix

The five pilot call sites were changed to `value.ToSingleLogLine()` and the
hosted CodeQL analysis of pull request #204 still reported `cs/log-forging` at
four of them. The fifth site, `SchedulesDirect`, was deliberately written with
the escape **inline**:

```csharp
info.ListingsId.Replace("\r", "\\r", StringComparison.Ordinal)
               .Replace("\n", "\\n", StringComparison.Ordinal)
```

and its alert closed. That difference is the whole experiment, and the reason is
in the query source, not in the code. `csharp/ql/lib/semmle/code/csharp/security/dataflow/LogForgingQuery.qll`,
at the bundled version, contains:

```ql
private class StringReplaceSanitizer extends Sanitizer { ... getReplaceMethod() ... }
private class ExternalLogForgingSanitizer extends Sanitizer { barrierNode(this, "log-injection") }
```

`String.Replace` is a **built-in** barrier, so the inline form clears with no
configuration at all. The shared helper is not recognised, and CodeQL is not
wrong about that: `ToSingleLogLine` returns `value` unchanged on two of its three
paths, so data really does flow from parameter to return value. What CodeQL
cannot know is that those two paths are exactly the ones on which the value
contains no separator.

That leaves three options and only one of them is honest:

| Option | Verdict |
| --- | --- |
| Inline the two `Replace` calls at all 46 sites | Works, and duplicates a security-relevant contract 46 times with no test behind any copy. |
| Dismiss the alerts / `#pragma` / narrow the suite | Suppression. Rejected outright. |
| Declare the helper's **return value** a barrier for `log-injection` only | Describes the boundary once, in one reviewed line, and leaves every other flow reported. |

The second row of `ExternalLogForgingSanitizer` is the extension point CodeQL
itself provides for exactly this. Using it is not suppression, and the
distinction is not rhetorical:

* the **runtime implementation and its tests** prove the returned value cannot
  end a physical log record;
* the **model** states that fact to the analyser, for the `log-injection` kind
  and no other;
* the **hosted negative controls** prove the statement reaches no other method,
  no other call and no other query.

If the implementation ever stops being true, the required `Tests` job goes red —
CodeQL's trust in the model is backed by a gate CodeQL does not own.

## 4. The model, exactly

`.github/codeql/extensions/csharp-log-barriers/ext/log-value-extensions.model.yml`
holds one row:

```yaml
extensions:
  - addsTo:
      pack: codeql/csharp-all
      extensible: barrierModel
    data:
      - ["Tesserafin.Extensions", "LogValueExtensions", false, "ToSingleLogLine", "(System.String)", "", "ReturnValue", "log-injection", "manual"]
```

Column order is the declaration in
`csharp/ql/lib/semmle/code/csharp/dataflow/internal/ExternalFlowExtensions.qll`
at `codeql-cli/v2.26.0`:
`barrierModel(namespace, type, subtypes, name, signature, ext, output, kind, provenance)`.

* `subtypes` is `false`: overrides are not covered.
* `signature` was obtained from a real CodeQL database of this repository with
  `utils/modeleditor/FrameworkModeEndpoints.ql`, not guessed.
* `ext` is `""`. 3344 of the 3346 model rows shipped in `codeql/csharp-all`
  7.0.0 use `""`; the only other value anywhere in the C# corpus is
  `Attribute.Getter`.
* `output` is `ReturnValue` — the sanitised value that comes **back**, never the
  argument that goes in.
* `kind` is `log-injection`, the only kind `cs/log-forging` consumes. The row
  reaches no other query.
* There is no wildcard in any field, no `neutralModel`, no `sinkModel`, no
  `sourceModel`, no summary, no threat-model change, and no model for
  `String.Replace` or for any other helper or overload.

The shape mirrors the single `barrierModel` row that `codeql/csharp-all` 7.0.0
ships for itself: `System.Web.HttpRequest.get_RawUrl -> ReturnValue`,
`url-redirection`.

## 5. How an unpublished pack reaches the analysis

The workflow is **advanced setup**: `github/codeql-action` v4.37.0
(`99df26d4f13ea111d4ec1a7dddef6063f76b97e9`), `security-extended`, manual C#
build, category `/language:csharp`.

**The action pin is a floor, not an exact toolchain.** `src/defaults.json` at that
commit names CodeQL CLI 2.26.0 / bundle `codeql-bundle-v2.26.0`, but a hosted
runner resolves whatever newer CodeQL its tool cache already holds. Measured on
this repository's own run:

| | action default | actually used on the runner |
| --- | --- | --- |
| CodeQL CLI | 2.26.0 | **2.26.2** |
| `codeql/csharp-all` | 7.0.0 | **7.1.1** |
| `codeql/csharp-queries` | 1.7.5 | **1.9.0** |

`barrierNode(this, "log-injection")` and the nine-column `barrierModel`
declaration are identical at both versions; that was checked at
`codeql-cli/v2.26.0` and `codeql-cli/v2.26.2` before the range was chosen. This
drift is why `extensionTargets` is `^7.0.0` rather than an exact patch: an exact
pin would turn the Thursday scheduled scan red without anything in this
repository changing.

Naming a pack in `packs:` makes `codeql database init` resolve it against the
GitHub Container registry. For an unpublished pack that is a hard `403`. The
CLI's own answer is a per-user configuration file, so
`.github/workflows/ci-codeql-analysis.yml` writes, for the C# matrix entry only:

```
database init --search-path $GITHUB_WORKSPACE/.github/codeql/extensions
database run-queries --additional-packs $GITHUB_WORKSPACE/.github/codeql/extensions
resolve extensions-by-pack --additional-packs $GITHUB_WORKSPACE/.github/codeql/extensions
```

All three lines are necessary and none is interchangeable with another:

* `database init` resolves a model pack through `--search-path`;
* `database run-queries` does not, and needs `--additional-packs`;
* `run-queries` resolves the model packs recorded in the database by spawning
  **`resolve extensions-by-pack` as a separate process**, which reads this file
  under its own command scope and therefore needs its own line.

Omitting either of the last two initialises green and then dies at query time
with

```
ERROR: Could not find extension pack 'tesserafin/csharp-log-barriers@0.0.1'.
A fatal error occurred: A 'codeql resolve extensions-by-pack' operation failed
```

Repeated `--additional-packs` values accumulate rather than replace, so this does
not displace the action's own `pr-diff-range` extension pack.

`init` then resolves the pack from the checkout, records it in the database's own
`temp/analysisConfig.json`, and materialises its rows into
`temp/extension-pack/`.

The pack is never published. It has no dependency, no registry reference and
needs no token or secret.

## 6. The three ways this breaks, and which are silent

| Failure | Behaviour of CodeQL alone |
| --- | --- |
| Pack deleted or renamed | **Loud.** `packs:` still names it, `database init` dies with the registry 403, `Analyze csharp` is red. |
| `extensionTargets` no longer matches the bundled `codeql/csharp-all` | **Silent.** `WARNING: Extension pack '...' is unused.` and the analysis completes green with the barrier not applied. |
| A second row added, a field widened to `*`, `subtypes` flipped to `true` | **Silent.** CodeQL does not object to a model covering more code than was reviewed. |

`ci/verify-codeql-model-pack.sh` closes the two silent ones. It runs inside the
required `Analyze csharp` job, after `Initialize CodeQL`, with no
`continue-on-error`, and fails if any of the following is not true:

1. `codeql resolve packs` finds the pack exactly once, at version `0.0.1`, inside
   `.github/codeql/extensions/csharp-log-barriers`;
2. the pack's `extensionTargets` range is exactly the reviewed `^7.0.0` and the
   `codeql/csharp-all` the CLI actually bundles is inside that major;
3. `codeql resolve extensions-by-pack` reports no `is unused` warning and
   resolves exactly **one** data extension from this repository, of predicate
   `barrierModel`, with `rowCount` 1, from exactly the committed file;
4. that row is byte-for-byte the tuple above, with no `*` and no `subtypes: true`,
   the file declares exactly one extensible predicate, and `ext/` holds exactly
   one model file;
5. `temp/analysisConfig.json` of the database being analysed lists exactly
   `["tesserafin/csharp-log-barriers@0.0.1"]`, and the model file materialised
   into the database is identical to the committed one.

Point 5 is the one that distinguishes "resolvable on disk" from "active in this
analysis".

## 7. Maintenance cost, stated rather than hidden

CodeQL model packs are GitHub **public preview** infrastructure. The mechanism
used here — `packs:` plus a per-user `--search-path` — is the only route that
keeps the pack unpublished, and it depends on CLI behaviour that is documented
but not contractual.

**Revalidate whenever `github/codeql-action`, the CodeQL CLI or
`codeql/csharp-all` changes.** Concretely, a version bump requires:

1. re-reading `LogForgingQuery.qll` at the new version to confirm
   `barrierNode(this, "log-injection")` still exists;
2. re-deriving the signature from a database built with the new extractor;
3. updating `extensionTargets` **and** the expected tuple in
   `ci/verify-codeql-model-pack.sh`;
4. re-running the hosted negative controls recorded in #203.

Until step 3 is done the job is red, which is the intended behaviour: the pin is
narrow on purpose, and a mismatch must not be able to pass as a green analysis.

## 8. Scope of this change

The five pilot sites are `ExceptionMiddleware`, `QuickConnectManager`,
`BackupService`, `UserManager` and `SchedulesDirect`. **41 `cs/log-forging`
alerts remain open and untouched** on `master`; #203 stays open for that
rollout. No alert was dismissed, no query suite was reduced, no `#pragma`,
`SuppressMessage`, `NoWarn` or path exclusion was added, and no source, sink or
threat model was changed.
