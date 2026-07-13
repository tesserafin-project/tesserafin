# Playback compatibility lab

Data-only skeleton (PR93). Compares the **legacy** playback decision (projected to
`PlayMethod` + `TranscodeReason` + selected streams) against the **v2**
`PlaybackDecision` (see `docs/pr91-rfc-playback-decision-v2.md`), **by category** —
never by raw `StreamInfo` equality.

## Layout

- `schema/fixture.schema.json` — JSON Schema (draft-07) for fixture format v1.
- `fixtures/*.json` — one case per file.
- `fixtures/MANIFEST.md` — the 12 mandatory categories and their status.

## Adding a fixture

1. Copy an existing fixture in `fixtures/`, name it `<category>-<detail>.json`.
2. Fill `input` from the PR91 domain objects (`ClientCapabilities`,
   `MediaSourceSnapshot`, `PlaybackConstraints`, `PlaybackRequestContext`).
   No DLNA types, no file paths, no secrets.
3. Fill `expected` with the intended **v2** decision.
4. Add a row to `MANIFEST.md`.
5. Validate: `input`/`expected` must satisfy `schema/fixture.schema.json`.

## Runner

Not yet present. The C# runner that loads each fixture, runs both engines and
classifies divergences (`equivalent` / `expected-improvement` /
`known-v2-limitation` / `potential-regression` / `unexplained`) arrives in **PR98**,
once the v2 engine (PR96–PR97) exists. See `docs/pr93-compatibility-lab.md` §4.
