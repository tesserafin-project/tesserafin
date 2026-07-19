# Real, TCP-bound Reefin server for browser end-to-end tests

`ci/serve-e2e.sh` boots a **real** Reefin server on a **real TCP port**, serving a **real
reefin-web build**, seeded with a real admin user and a real movie library — against a throwaway
data directory that never touches your actual Reefin installation.

## The gap this closes

`ci/run.sh` (the merge gate) and `ci/smoke-e2e.sh` (PR #32) exercise the server through
`Reefin.Server.Integration.Tests`' `WebApplicationFactory` — an **in-process `TestServer`**. That
factory's `ConfigureWebHostBuilder` never actually binds Kestrel: there is **no listening socket**.
That is fine for contract tests, but a real browser can never reach it.

reefin-web's `playwright.config.ts` says so in its own header and deliberately ships **no
`webServer` block**, which is exactly why
`npx playwright test tests/e2e/theme-glass.spec.ts` used to fail with:

```
connect ECONNREFUSED ::1:8096
```

`ci/serve-e2e.sh` is the missing link between the two repos.

## Prerequisites

| Tool | Why |
| --- | --- |
| .NET 10 SDK (`dotnet`) | builds and runs `Reefin.Server` on the host (no container needed) |
| `ffmpeg` | synthesizes the media fixtures — no binary test assets are committed |
| `curl`, `python3` | readiness probing and JSON handling in the script |
| A reefin-web **production bundle** | the server serves it; the specs call `page.goto('/')` |

## Building the reefin-web bundle

The specs drive a browser against the SPA, so the server must serve a real build. reefin-web is
**not** served by a dev server here — it is built once and handed to the Reefin server via
`--webdir`:

```bash
cd ../reefin-web
npm ci                      # first time only
npm run build:production    # -> ./dist (contains index.html)
```

`dist/` is gitignored in reefin-web, so this never dirties that repo. Make sure you build the
branch whose UI you intend to test — e.g. the Glass theme lives on `w13.8-reefin-glass`, and a
stale `dist/` from another branch will fail the Glass assertions in confusing ways.

## Usage

Foreground — boot, seed, print the URL, hold the server up until Ctrl-C:

```bash
./ci/serve-e2e.sh --webdir ../reefin-web/dist
```

One-shot — boot, seed, run a command with `REEFIN_E2E_*` exported, then tear everything down and
exit with the command's status (this is the CI-friendly form):

```bash
./ci/serve-e2e.sh --webdir ../reefin-web/dist \
    --exec 'cd ../reefin-web && npx playwright test tests/e2e/theme-glass.spec.ts'
```

### Options

| Option | Meaning |
| --- | --- |
| `--webdir PATH` | reefin-web production bundle to serve (default `../reefin-web/dist`) |
| `--port N` | TCP port to bind (default: an auto-detected **free** port) |
| `--exec CMD` | run `CMD` once seeded, then tear down; exit with its status |
| `--user NAME` | admin username (default `$REEFIN_E2E_USER` or `smokeadmin`) |
| `--password PW` | admin password (default `$REEFIN_E2E_PASSWORD` or `smokepass123`) |
| `--datadir PATH` | use `PATH` instead of a `mktemp -d` tree (implies `--keep`) |
| `--no-build` | skip `dotnet build`, reuse the existing binary |
| `--keep` | keep the temp tree on exit (prints its path and the server log) |
| `--timeout N` | readiness timeout in seconds (default 180) |

The script exports `REEFIN_E2E_BASE_URL`, `REEFIN_E2E_USER` and `REEFIN_E2E_PASSWORD` for
`--exec`, which is exactly what the reefin-web specs read. `REEFIN_E2E_CAPTURE_DIR` is honoured by
the specs themselves; set it to keep screenshots out of the reefin-web checkout.

## Three things that will bite you

These are not hypothetical — each one cost a debugging cycle while building this script.

### 1. The listen port comes from `network.xml`, not `ASPNETCORE_URLS`

`Reefin.Server/Extensions/WebHostBuilderExtensions.cs` calls `options.Listen(addr, httpPort)`
**explicitly**, with `httpPort` coming from `appHost.HttpPort`, which `ApplicationHost` reads from
`NetworkConfiguration.InternalHttpPort`. That overrides Kestrel's URL environment variable
entirely — `ASPNETCORE_URLS` and `--urls` are silently **ignored**. The script therefore writes a
`network.xml` into the config dir before boot. Unspecified elements fall back to
`NetworkConfiguration`'s own defaults.

### 2. `/System/Info/Public` is NOT a valid readiness probe

Reefin binds the configured port **twice in sequence**: first with
`Reefin.Server/ServerSetupApp/SetupServer.cs` — a placeholder host shown while the real app boots —
and then with the real application. `SetupServer` explicitly maps `/System/Info/Public` and answers
it **200** (with `StartupWizardCompleted=false`), while returning **503** for every other route.

So a readiness probe on `/System/Info/Public` goes green during startup, and every subsequent
seeding call then fails with 503. The script polls **`/Startup/User`** instead: `SetupServer` 503s
it via its catch-all, the real app answers 200. That is a true "the real app is serving" signal —
and it is the first seeding step anyway.

### 3. The access token goes *inside* the `Authorization` header

A bare `X-Emby-Token` header alongside a tokenless `Authorization` header is rejected with **401**.
Authenticated calls must fold the token into `Authorization`:

```
Authorization: MediaBrowser Client="...", Device="...", DeviceId="...", Version="...", Token="<token>"
```

This is the same shape reefin-web's specs send.

Related: the original failure was `ECONNREFUSED ::1:8096` — `localhost` resolving to IPv6 while the
server listens on IPv4. The printed base URL is always explicitly `127.0.0.1` to remove that
ambiguity.

## How seeding works

Entirely through the **real public API** — no database surgery, no invented endpoints:

1. `GET /Startup/User` — initializes the user manager, creating the default user.
2. `POST /Startup/User` — renames it to `$REEFIN_E2E_USER` and sets `$REEFIN_E2E_PASSWORD`.
3. `POST /Startup/Complete` — marks the setup wizard done, so the normal auth path is under test.
4. `POST /Users/AuthenticateByName` — proves the specs' credentials work and yields a token.
5. `POST /Library/VirtualFolders?collectionType=movies&refreshLibrary=true` — creates the library.
6. Poll `/UserViews` until a `CollectionType == "movies"` view actually materializes.

Step 6 matters: the library scan is **asynchronous**, so "server up" is not "library visible", and
`theme-glass.spec.ts` specifically looks for a `movies` view. Media fixtures are synthesized with
the same `testsrc`/`sine` lavfi recipe as
`tests/Reefin.Server.Integration.Tests/EndToEnd/EndToEndMediaFixtures.cs` and `HlsSmokeTests`, named
`<Title> (<Year>).mp4` inside a matching folder so the movie resolver registers them.

## Cleanup

A `trap` on `EXIT INT TERM` always runs, on success, failure, Ctrl-C and SIGTERM alike. It stops the
server (SIGINT, then SIGKILL as a backstop so no orphan holds the port) and removes the throwaway
data directory. Pass `--keep` to retain the tree — the script prints the directory and the server
log path.

## Verified result

Originally verified against `theme-glass.spec.ts` only, from a `w13.8-reefin-glass` reefin-web
checkout (2 passed).

Re-verified since against a reefin-web `main` production bundle, across the **full four-spec browser
suite** — no spec modified, no skip, `workers: 1` untouched:

```
Running 18 tests using 1 worker
  ✓   1-9  [chromium] › glass-interaction-profiles.spec.ts  (blur tokens, lowPower, reducedTransparency,
                        remote density, cumulative cascade, reducedMotion, no residue, Classic baseline+guard)
  ✓  10-12 [chromium] › home.spec.ts       (sections after sign-in, ?tab= sync, keyboard-operable tab strip)
  ✓  13-16 [chromium] › library.spec.ts    (both smoke-test movies, sort order, year round-trip, density persist)
  ✓  17-18 [chromium] › theme-glass.spec.ts (Classic flat, Glass frosted — on /home and /library)
  18 passed (45.9s)
```

Reproduce it exactly with:

```bash
./ci/serve-e2e.sh --webdir <reefin-web>/dist --port 8097 \
    --exec 'cd <reefin-web> && \
        REEFIN_E2E_CAPTURE_DIR=/tmp/glass-captures \
        npx playwright test \
            tests/e2e/home.spec.ts tests/e2e/library.spec.ts \
            tests/e2e/theme-glass.spec.ts tests/e2e/glass-interaction-profiles.spec.ts'
```

`--exec` exports `REEFIN_E2E_BASE_URL` / `REEFIN_E2E_USER` / `REEFIN_E2E_PASSWORD` and tears the
server down afterwards. Set `REEFIN_E2E_CAPTURE_DIR` (and Playwright's `--output`) outside the
reefin-web checkout if that tree must stay clean — `theme-glass.spec.ts` writes four PNG captures
(`classic-home`, `classic-library`, `glass-home`, `glass-library`), which are the Glass review
artifacts.

The `library.spec.ts` assertions depend on this script's exact movies-library contract: **exactly
two items**, `Smoke Test Movie` and `Transcode Probe`. Changing the fixtures breaks those specs.

## Proposed reefin-web change — described, deliberately NOT applied

**No reefin-web change is required.** All four browser specs (`home`, `library`, `theme-glass`,
`glass-interaction-profiles`) pass unmodified against a server started by this script; they already
read `REEFIN_E2E_BASE_URL` / `REEFIN_E2E_USER` / `REEFIN_E2E_PASSWORD`, which is exactly what
`--exec` exports.

There is, however, one worthwhile follow-up for whoever owns reefin-web:

- **`playwright.config.ts` could grow a `webServer` block** invoking this script, making
  `npx playwright test` self-contained instead of requiring a separately-started server:

  ```ts
  webServer: {
      command: '../reefin/ci/serve-e2e.sh --webdir ./dist --port 8096',
      url: 'http://127.0.0.1:8096/System/Info/Public',
      reuseExistingServer: !process.env.CI,
      timeout: 180_000
  }
  ```

- **That config's header comment is now stale.** It currently reads *"There is deliberately no
  `webServer` block: starting a Reefin server (dotnet + ffmpeg + media library) is out of scope for
  the test runner."* That premise no longer holds — `ci/serve-e2e.sh` does exactly that, in one
  command, with automatic cleanup.

It is **not applied here** for two reasons: this work owns `ci/*` in the reefin repo only and
treats reefin-web as read-only; and a `webServer` block hard-codes a cross-repo relative path,
which is a coupling reefin-web's maintainers should opt into deliberately rather than inherit.
Note also that the snippet above pins `--port 8096` because Playwright's `webServer.url` must be
known ahead of time — that gives up this script's free-port detection, so it can collide with an
already-running Reefin instance.

## What this does not cover

This harness proves the **infrastructure**: a real browser can reach a real, seeded Reefin server
serving a real reefin-web build. It does **not** exercise real playback. Still uncovered, for the
wave-2 E2E work: DirectPlay, Remux/DirectStream, transcode-to-HLS, external subtitle sidecars,
audio track selection, bitrate limiting, the kill switch, and session expiry. The fixtures the
script synthesizes (real H.264/AAC MP4s) are deliberately sufficient to back those scenarios when
someone writes them.
