# Data model reliability

Tracking issue: [#360](https://github.com/tesserafin-project/tesserafin/issues/360).
The initial audit targets `d2fd71e800ce440cfd3438bebc1c2cceb9ca945c`.
This document records bounded implementation contracts; it does not declare the
whole data model validated.

## Item display preferences: preserve the requested scope

`DisplayPreferencesManager.GetItemDisplayPreferences(userId, itemId, client)`
looks up a preference by the full `(UserId, ItemId, Client)` tuple. When no row
exists, it must persist that same tuple. A subsequent sequential read must reuse
the row, including after updating its sorting settings and creating a new manager.
Different users, scopes and clients must retain independent settings.

`ItemId` here is a display scope, not necessarily a `BaseItems` foreign key.
The API also accepts textual display scope names and hashes them to GUIDs.
`Guid.Empty` is an accepted global scope. `ListItemDisplayPreferences` excludes
that global scope and filters on both the requested user and client.

The original getter created every missing row with `Guid.Empty`, even for a
non-empty requested scope. Repeated reads of a missing non-empty scope could
therefore create multiple global rows. The POST controller assigned the requested
id before updating, but the GET controller's assignment only changed the returned
object after the getter had already saved the incorrectly scoped row.

The first correction persists the requested id at creation. It neither changes
the schema nor reconstructs old scope assignments. Existing `Guid.Empty` rows
cannot reliably be distinguished from legitimate global preferences, and their
original requested scopes cannot be inferred. Do not silently reassign or purge
them.

Regression coverage is in `DisplayPreferencesManagerTests`. It exercises the
real manager with SQLite initialized through the existing migrations, including
saved-value reloads, repeated reads, scope isolation, list filtering and
preservation of ambiguous legacy global rows.

## Display preferences: concurrent writes

Contract, enforced by `DisplayPreferencesManagerConcurrencyTests` on a migrated
SQLite file configured by the production provider (pooled connections, WAL, one
connection per context), under each database locking mode, and over HTTP by
`DisplayPreferencesControllerTests`:

- Concurrent first reads of one `(UserId, ItemId, Client)` scope create exactly
  one `DisplayPreferences` row and one `ItemDisplayPreferences` row, and every
  reader gets that row. This holds for the global scope (`Guid.Empty`), a
  virtual scope (a name hashed by the controller) and an item id, across
  distinct users and clients read at the same time.
- Concurrent updates of disjoint scopes keep each scope's own values when read
  back through a new connection and a new manager.
- Saving custom preferences or home sections replaces the previous set in one
  transaction. A save that fails after the delete has run leaves the previous
  set intact.
- Concurrent saves of one scope end with one writer's complete set (last write
  wins), never a mix.
- Over HTTP, a save followed by a read returns what was saved, a later save with
  fewer home sections or other custom keys removes the previous ones, and each
  user keeps their own preferences for the same scope.

A failed transactional save is rolled back explicitly. Disposing the transaction
alone does not reach the transaction interceptors, so in the `Pessimistic`
locking mode the process-wide database write lock would never be released and
every later write would block.

Limits:

- In the `Optimistic` locking mode, `OptimisticLockBehavior.OnSaveChanges`
  discards the result of `ExecuteAndCapture`: a failed save returns normally and
  the transaction commits the delete that preceded it. This affects every write
  in that mode and needs its own fix; the rollback tests are skipped there.
- `POST /DisplayPreferences/{id}` performs three separate saves (custom
  preferences, item preferences, display preferences with home sections). Each is
  atomic, but two concurrent requests can leave one request's custom preferences
  next to the other's home sections.

Before the fix, every home screen save added its sections without deleting the
previous ones. Existing databases may hold those accumulated `HomeSections` rows;
they are replaced on the next save of that scope, and no migration removes them.

Creation is serialized by a lock in the singleton manager, taken on a cache miss
only. It covers one server process per database, the supported SQLite setup.
`ItemDisplayPreferences` still has no unique index: nothing in the database
rejects a second row written by another process or by an older server.

## Item display preferences: pre-existing duplicates

Databases written before the fix can hold several `ItemDisplayPreferences` rows
for one scope. The server now reads and updates the oldest row (lowest `Id`) of
such a scope and leaves the others unchanged. It never merges, reassigns or
deletes them.

Read-only diagnostic (the same query is run by
`LegacyDuplicateScopes_AreClassifiedAndLeftUntouchedByConcurrentReads`):

```sql
SELECT UserId, ItemId, Client,
       COUNT(*) AS Rows,
       COUNT(DISTINCT SortBy || '|' || SortOrder || '|' || ViewType || '|' || IFNULL(IndexBy, '-')
           || '|' || RememberSorting || '|' || RememberIndexing) AS DistinctSettings,
       SUM(SortBy = 'SortName' AND SortOrder = 0 AND ViewType = 0 AND IndexBy IS NULL
           AND RememberSorting = 0 AND RememberIndexing = 0) AS DefaultRows,
       ItemId = '00000000-0000-0000-0000-000000000000' AS GlobalScope,
       MIN(Id) AS ReadRowId
FROM ItemDisplayPreferences
GROUP BY UserId, ItemId, Client
HAVING COUNT(*) > 1
ORDER BY UserId, ItemId, Client;
```

Each group falls into one of these classes:

| Class | Test | Example | Information lost by keeping one row |
|---|---|---|---|
| Identical | `GlobalScope = 0`, `DistinctSettings = 1` | two default rows for one item, from the creation race | none |
| Contradictory | `GlobalScope = 0`, `DistinctSettings > 1` | `SortName` ascending and `DateCreated` descending, remembered, for one item | the settings of every row but the kept one |
| Global, origin unknown | `GlobalScope = 1` | two default rows, or a default row and a remembered `PremiereDate` row, for `Guid.Empty` | possibly the preference of an item whose id was never stored |

`DefaultRows` counts rows still at the creation defaults: they were created by a
read and never saved with a user choice. Global rows were created with
`Guid.Empty` for any requested scope before #362, so even a single global row may
belong to an item; the diagnostic lists only the duplicated ones.

**Decision needed before any unique index on `(UserId, ItemId, Client)`.** For
each class, choose between keeping every row (no index), keeping one row (and
which: the oldest, which the server already reads, or the most recently changed,
which nothing records today), or keeping one row and exporting the others. For
the global class, also decide whether rows with user choices may be dropped at
all, given their scope cannot be recovered. Until that decision exists, no
migration adds the index or touches these rows. If a repair migration is chosen,
it must apply the rule in the same transaction as the index creation and prove
both preservation and rollback on a migrated copy.

## Next bounded work

1. Together with #351, reproduce deletion through `ItemPersistenceService` when
   `ParentId` and `AncestorIds` disagree. Assert conservation of every cascaded
   child's user data, preservation of linked-only media and rollback after an
   effective mutation. Also exercise a new parent and child saved in one batch
   and orphaned local-version deletion. Use migrated SQLite and the actual
   service paths, not a reduced SQL schema.
2. Diagnose incompatible rows before adding `MediaSegments.ItemId` and
   `CustomItemDisplayPreferences.UserId` foreign keys. Record targeted repair
   choices and prove migration preservation. Preference uniqueness waits for the
   decision above.
3. Define authoritative hierarchy and metadata representations, then address
   identity and playlist cardinality through explicit decisions. Optimize
   retrieval only after representative query measurements.

The requested-scope fix is independent of these deletion and migration decisions.
It completes only the getter correction in section 3 of #360.
