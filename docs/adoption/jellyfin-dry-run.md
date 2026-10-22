# Jellyfin adoption: read-only capture and offline simulation

This is the first bounded discovery/tooling slice under
[tracker #144](https://github.com/tesserafin-project/tesserafin/issues/144).
It inventories visible media references, users, watched/favourite state and resume
positions and produces a reviewable simulation. It does not create accounts,
copy/move media, transfer policy, write user state, change passwords or provide
an import execution command. Upgrading an existing Tesserafin server and restoring
a Tesserafin backup are different operations.

The separate [experimental state-apply tool](jellyfin-state-apply.md) consumes
these snapshots and mappings with narrower conflict rules. This capture/simulation
command remains GET-only/offline and never applies its candidate records itself.

## Selected source/version matrix

| Input | Selected adapter | Evidence / limitation |
| --- | --- | --- |
| Jellyfin | **10.10.7 only**, product `Jellyfin Server` | The tagged source was inspected at `aefb9b2cffca3ccb7a95d89af0d9fb2040de6ed3`. Tests use synthetic real-HTTP responses matching the selected surface, not a live household or an artifact-based Jellyfin→Tesserafin migration. |
| Tesserafin | Application version **1.0.0**, product `Tesserafin Server`, current server contract | The GET surface was checked against canonical OpenAPI at `5d4fcf2d65d7ee4ef4f35c0ae0422cd95d22742b`. The version alone cannot identify every historical build; this does not qualify every released artifact reporting 1.0.0. |
| Another Jellyfin version, unknown product/schema | Refused | Needs a separately inspected adapter and evidence. Do not downgrade an installation to use this prototype. |
| Jellyfin SQLite/config/backup files | Not read | No assumption that old/new database layouts or password formats are interchangeable. |

Source references (immutable tag commit):
[items and per-user DTO projection](https://github.com/jellyfin/jellyfin/blob/aefb9b2cffca3ccb7a95d89af0d9fb2040de6ed3/Jellyfin.Api/Controllers/ItemsController.cs),
[administrator user selection](https://github.com/jellyfin/jellyfin/blob/aefb9b2cffca3ccb7a95d89af0d9fb2040de6ed3/Jellyfin.Api/Helpers/RequestHelpers.cs),
[user inventory](https://github.com/jellyfin/jellyfin/blob/aefb9b2cffca3ccb7a95d89af0d9fb2040de6ed3/Jellyfin.Api/Controllers/UserController.cs),
[library inventory](https://github.com/jellyfin/jellyfin/blob/aefb9b2cffca3ccb7a95d89af0d9fb2040de6ed3/Jellyfin.Api/Controllers/LibraryStructureController.cs).
These contracts guide the prototype; source inspection is not end-to-end proof.

## Capture

Requires Python 3.10+ and an existing administrator token for each explicitly
selected server. Token files contain the token alone; create them privately and
restrict access to their owner. The tool does not sign in or handle passwords.
Snapshots and reports contain private names, media paths and history: keep them
outside the repository and do not attach them to a public issue.

From the repository root, with your actual server URLs and private file paths:

```sh
python3 tools/adoption/jellyfin_dry_run.py capture \
  --product jellyfin --url https://your-jellyfin.example/jellyfin \
  --token-file /private/jellyfin-token --output /private/source.json
python3 tools/adoption/jellyfin_dry_run.py capture \
  --product tesserafin --url https://your-tesserafin.example \
  --token-file /private/tesserafin-token --output /private/target.json
```

The only network operations are `GET /System/Info/Public`, `GET /Users`,
`GET /Library/VirtualFolders` and paginated `GET /Items` with an explicit `userId`
and user-data inclusion for each inventoried user. Redirects and environment
proxies are disabled. HTTPS uses normal certificate validation. Loopback HTTP
is permitted; a trusted LAN HTTP installation requires explicit `--allow-http`.
No credential is placed in a URL or exported. Errors omit server bodies, URLs,
private input values and tracebacks.

The capture caps users (64), combined user-item records (100,000), pages per user
(4,096), overall requests (1,024), response bytes (64 MiB total) and elapsed time (a three-minute budget checked between streamed reads, with
at most 15 seconds of socket inactivity). Exceeding a bound refuses a complete
output. Pagination checks totals, missing pages and repeated IDs. Collection is
not transactional: stop scans/edits for a stable inventory; even stable totals
cannot prove that no item changed between reads. GET authentication/access logs
may still be recorded by the server; the client sends no mutation operations.

Outputs are complete JSON files created owner-only (`0600`), published without
overwriting an existing file. Use a new filename for each capture. Hard-link
publication must be supported by the output filesystem; failure is a refusal,
not permission to overwrite an earlier recovery point. No captured real-user
snapshot or token belongs in a commit.

## Explicit mappings and simulation

Create a private mapping file, using IDs from those snapshots. Usernames and IDs
from different servers never imply the same account. Mapping two source users
to one target user is refused. Existing target accounts must be selected explicitly;
this lot does not create new ones.

```json
{
  "users": {"source-user-id": "target-user-id"},
  "paths": [{"source": "D:\\Movies", "target": "/media/movies"}]
}
```

Then run the planner entirely offline:

```sh
python3 tools/adoption/jellyfin_dry_run.py plan \
  --source /private/source.json --target /private/target.json \
  --mapping /private/mapping.json --output /private/simulation.json
```

Review the JSON counts, per-user mapping and each record status:

| Status | Meaning |
| --- | --- |
| `state-candidate` | Explicit user mapping, unique remapped path/type and known state permit a **candidate for future review**. The exact source flags/count/ticks/date are retained in the report. Nothing was applied. |
| `unchanged` / `no-source-state` | Equal observed state, or no positive source state to recover. No clearing of target data is proposed. |
| `user-unmapped` | Needs an explicit account choice. A future new account requires password setup/reset. Existing target passwords remain unchanged. |
| `source-state-unknown` / `target-state-unknown` | Missing/invalid fields are not false or zero. No write can be proposed from this observation. |
| `ambiguous-path` / `ambiguous-source-path` | Multiple target or source items share the remapped path/type; do not merge their history. |
| `provider-review` / `provider-conflict` | Provider IDs can suggest candidates, but never authorize a match across cuts/versions or override conflicting exact-path metadata. |
| `resume-runtime-unknown` / `resume-runtime-conflict` | A nonzero resume needs known equal runtimes and a position below the target duration. Matching runtimes alone still does not prove identical media. |
| `target-state-conflict` | Existing nonempty target state differs. Preserve it pending an explicit conflict policy. |
| `item-unmatched` / `unsupported-item` | No file match, unsafe/relative/remote path, or a type outside Movie/Episode/Audio. |

Paths are compared lexically, without opening media files or case folding.
Prefix remapping uses segment boundaries and the longest selected prefix.
`..`/`.` traversal is not normalized into a candidate. Names, provider IDs and
same-looking cross-server IDs are insufficient to establish file identity.
Library names/locations are inventoried; no library, access rule or media file
is created. Source password hashes, tokens and policy are excluded. A future
account-creation path must require new credentials/reset; existing mapped target
accounts keep their own credentials and permissions.

## Local evidence and next acceptance

Synthetic examples are in `tools/adoption/fixtures/`. To inspect their report,
choose a new private output path with the same `plan` command. Tests cover CLI
non-mutation, two-user HTTP capture, missing state, exact resume ticks, conflict
refusal, ambiguous paths, pagination, redirect/credential boundaries, resource
bounds and private/exclusive output:

```sh
python3 -m unittest discover -s tools/adoption -p 'test_*.py' -v
```

The read-only `Adoption dry run` CI workflow runs that suite. No runtime
integration, dependency, OpenAPI or database change is introduced.
Before any apply feature: select a real source/target artifact pair, rehearse on
disposable copies, prove per-user visibility and history preservation through
restart, settle new-account/password and conflict policies, define rollback and
prove interrupted/repeated imports safe. No public compatibility or completed
Migration Assistant claim is made by this prototype.
