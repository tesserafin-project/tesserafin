# Prospective reference diagnostic

Part of [#360](https://github.com/tesserafin-project/tesserafin/issues/360).
Schema inspected at `00e8c5ae807549b1ffc146c3e7505015f7d20121`:
the active SQLite snapshot has no foreign key for `MediaSegments.ItemId`
or `CustomItemDisplayPreferences.UserId`. `PRAGMA foreign_key_check` alone
cannot report these missing references because the constraints do not exist.

## Run

Use Python 3.10+ (standard library only) on an existing Tesserafin database,
preferably a consistent backup obtained through the supported backup process:

```sh
python3 tools/data_model/diagnose_references.py /path/to/tesserafin.db
```

The tool opens SQLite with `mode=ro`, enables `query_only`, and reads both
relationships in one transaction. It never migrates, repairs or creates the
database. It reads committed WAL contents; it deliberately does not use
`immutable=1`. Do not copy just a live database's main file while ignoring its
WAL. For a stopped-server copy, preserve the complete SQLite backup consistently.
SQLite may need existing readable WAL/SHM sidecars or permission to manage them;
read-only means no SQL data/schema writes, not a guarantee about sidecar files.

JSON contains counts only, with no preference keys, values, user/item identifiers
or media paths. Per relationship it reports all rows, null references, orphan
rows and distinct missing parent keys. Nulls are reported separately because
SQLite foreign keys permit nulls, while these model properties are non-nullable.
Global and virtual preference `ItemId` scopes are intentionally not checked
against `BaseItems`: only the custom preference's `UserId` is checked.

| Exit code | Meaning |
| --- | --- |
| 0 | Both queried relationships have no missing or null references in this snapshot |
| 1 | At least one queried relationship has missing or null references; inspect counts |
| 2 | Input/query/schema error; no complete report was produced |

A zero result is not a full integrity check, schema-version certification,
performance measurement or approval to add a constraint. Required tables and
columns are checked, but this tool does not validate the entire EF schema.
Record the tested server revision and backup provenance alongside the report.
No production database was examined while developing this tool.

## Repair decisions before a migration

| Finding | Required investigation and bounded choice |
| --- | --- |
| Segment whose item is missing | Establish whether deletion left stale segments or whether the item must be restored. Recompute segments for an identifiable existing source only; do not invent a parent or bulk-delete segments. |
| Custom preference whose user is missing | Establish user deletion/import provenance. Restore the original identity only with evidence, or explicitly approve export/quarantine/removal of the affected rows. Never transfer them to another user by name. |
| Null reference | Diagnose the unexpected schema/data source before choosing a repair. |

Keep valid rows unchanged. Any later repair needs its own reviewed implementation,
backup, targeted selection, transaction/rollback tests and migrated-SQLite
preservation assertions. Choose delete behavior from actual service semantics;
this diagnostic chooses neither cascade nor retention. Index design and migration
timing remain separate. Legacy preference duplicates and the #369 concurrency
changes are outside this tool's scope; this lot does not depend on merging #369.

## Evidence and limits

```sh
python3 -m unittest discover -s tools/data_model -p 'test_*.py' -v
```

The reduced SQLite fixture tests known valid/orphaned references, shared missing
parents, global/virtual scopes, counts-only output, unchanged database bytes,
missing inputs/schema, corrupt input, empty tables, null references, parent collation,
committed WAL visibility with uncommitted rows excluded, one snapshot across both
relationships during a concurrent commit, and the script entry point. These are query/CLI controls,
not proof of EF migrations or server lifecycle behavior. Before any schema
hardening, run the tool against a database initialized by the real migrations
and against the relevant consistent backup; add lifecycle and migration tests
in that separate change. There is no repair command in this tool.
