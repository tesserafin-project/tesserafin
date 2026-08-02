# Provider-authentication structural audit

A repository-owned gate that fails `./ci/run.sh` when a third-party provider credential is
compiled into the server.

It exists because Gitleaks cannot close this hole, and saying so precisely matters more
than adding another scanner.

## Why Gitleaks is necessary and not sufficient

Gitleaks is a good scanner and it stays. It found the one credential-shaped literal in the
web repository's test tree, and it would find a 32-character hexadecimal key sitting in a
`.cs` file today.

It reported the contaminated server image as **clean**, and it was right to by its own
rules:

1. **The values were not in files it decodes.** They were C# `const string`s, so the
   compiler inlined them into the assembly's `#US` user-string heap as UTF-16LE. Gitleaks
   does not decode .NET metadata heaps, and a plain ASCII `strings` pass over the DLL
   returns nothing either.
2. **Two of the three were too short and too plain to match anything.** TheAudioDB's key
   was six characters; OMDb's was eight. Any generic rule that fired on those would fire on
   a thousand innocuous strings per build. No entropy or length threshold reaches them —
   not because the threshold is set wrong, but because there is no threshold that could be
   set right.

The conclusion is not "tune Gitleaks harder". It is that a credential is not identifiable
from what it looks like. It is identifiable from **where it is used**.

## What this audit checks instead

The audit reads the *compiled* `Tesserafin.Providers.dll` and the repository-owned
inventory at [`ci/provider-auth-inventory.json`](../ci/provider-auth-inventory.json), and
compares them in both directions.

Reading the compiled assembly rather than the source is what makes it complete. The C#
compiler resolves constant concatenation, constant interpolation and value-splitting into
a single literal *before* emitting it, so all three evasions collapse into one observable:

```
"https://www.omdbapi.com?apikey=" + KEY          // concatenation
$"https://www.omdbapi.com?apikey={KEY}"          // interpolation
"https://www.omdbapi.com?apikey=" + A + B + C    // fragments
```

all become the identical folded literal. A source-level pattern scan would miss every one
of them; a compiled scan gets all three for free, and cannot be evaded by a fourth spelling
nobody has thought of yet.

### The rules

| Rule | What fails |
|---|---|
| `auth-boundary-not-terminal` | A string literal that begins with a declared authentication boundary and continues past it. `…omdbapi.com?apikey=` is fine; one character more is a compiled-in credential. |
| `undeclared-host-string` | A string literal mentioning a declared provider's host that is not one of that provider's declared `allowedHostStrings`. |
| `unregistered-auth-path` | A string literal carrying an authentication marker (`apikey=`, `access_token=`, `client_secret=`, `Authorization:`, `Bearer `, …) that matches no declared boundary — an authenticated call to a provider nobody registered. |
| `undeclared-string-constant` | Any `const string` in a policed provider namespace that the inventory does not name. This is what catches a bare key constant *before* it is concatenated into anything, with no heuristic involved. |
| `undeclared-credential-reader` | A method that reads a declared credential configuration property without being declared as a reader of it. This is the provenance rule, and it is also what makes a credential reaching a log call or an exception message a gate failure. |
| `stale-credential-reader`, `stale-inventory-entry` | The inverse: an inventory entry describing a code path that no longer exists. A stale inventory is as much a defect as a missing one — it is how a gate quietly stops covering anything. |

### The inventory

`ci/provider-auth-inventory.json` declares, for **every** production provider under
`Tesserafin.Providers` that issues an outbound request — not only the three that shipped a
credential — its name, outbound host, authentication mechanism, parameter or header name,
configuration source, anonymous/configured classification, owning configuration property,
and expected missing-key behaviour.

It contains **no credential values**, and a test asserts that: anything in the file that
continued past an authentication boundary would be a credential by the audit's own
definition. That test is what stops the inventory becoming the place a key gets written
down.

Adding a provider, an outbound host, a string constant in provider code, or a new reader of
a credential property all require an edit to this file. That is the design, not friction:
it turns an otherwise invisible change into a reviewable one.

## Where it runs

The audit is a test in `tests/Tesserafin.Providers.Tests/ProviderAuth`, so
`./ci/run.sh` — which runs `dotnet test` over the whole solution — already executes it, and
the hosted `Tests` workflow does too. There is no separate script to remember to run and no
second CI entry point to keep in sync.

## Controls

The audit has controls in both directions, all compiled at test run time with Roslyn into a
temporary directory that is deleted when the test finishes.

Compiling them is the point: the evasions this audit must defeat are performed by the
compiler, so a control that skipped the compiler would prove nothing about them.

**Eight detection controls** — each must be caught, for the right reason:

1. the former long TheMovieDb key shape concatenated into a request URL,
2. a six-character TheAudioDB-style key folded into the declared base URL,
3. an eight-character OMDb-style key in the declared `apikey` parameter,
4. compile-time concatenation across two named constants,
5. interpolation of a constant into a query URL,
6. a literal `Authorization: Bearer …` header,
7. a value split into three constant fragments and rejoined,
8. an authenticated request to a host no inventory entry declares.

**Four acceptance controls** — each must pass cleanly, so the gate is not merely "fails on
everything":

1. an anonymous public endpoint,
2. an endpoint whose credential comes from operator configuration at run time,
3. ordinary, non-authentication query parameters,
4. a synthetic credential that exists only at run time, inside a disposable directory.

Plus one control asserting the audit **never quotes the value it found**. A gate that prints
the credential it detected has published it to every CI log that ever ran.

No control commits a credential-shaped fixture: every synthetic value in the test source is
assembled at run time from fragments, exactly as this audit requires of production code.

## What it does not do

- It does not scan git history. That is Gitleaks' job, and the historical disposition for
  the inherited values is recorded separately.
- It does not police assemblies other than `Tesserafin.Providers.dll`. That is where every
  provider lives today; a provider landing elsewhere would need this widened, and the
  inventory's `assembly` field is where that starts.
- It does not replace Gitleaks. The two cover disjoint failure modes and both run.
