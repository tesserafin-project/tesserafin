# Data model reliability

Tracking issue: [#360](https://github.com/tesserafin/tesserafin/issues/360).
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

A failed transactional save is rolled back explicitly. The rollback tests run in
every locking mode (see the locking modes section below).

Limits:

- `POST /DisplayPreferences/{id}` performs three separate saves (custom
  preferences, item preferences, display preferences with home sections). Each is
  atomic, but two concurrent requests can leave one request's custom preferences
  next to the other's home sections.

Before the fix, every home screen save added its sections without deleting the
previous ones. Existing databases may hold those accumulated `HomeSections` rows;
they are replaced on the next save of that scope, and no migration removes them.

Creation is serialized by a lock in the singleton manager, taken on a cache miss
only. It covers one server process per database, the supported SQLite setup.
`ItemDisplayPreferences` has a unique index on its scope since the
`DeduplicateItemDisplayPreferences` migration.

## Database locking modes: transactions and the write lock

Contract, enforced under `NoLock`, `Optimistic` and `Pessimistic` on a migrated
SQLite file configured by the production provider:

- A save that fails reaches the caller in every mode, whether SQLite refuses it
  (a unique key) or anything else does; the transaction that ran a delete before
  it is rolled back, as a new connection reads it
  (`LockingBehaviorSaveFailureTests`). `Optimistic` still retries a save that
  finds the database locked, and only that (`IsRetriedAndSucceeds`).
- In `Pessimistic`, the process-wide write lock is owned by the EF Core
  connection of the transaction, never by a thread. It is taken by
  `BeginTransaction` and released exactly once, by the first of: commit,
  rollback, a failed commit or rollback, the disposal of the transaction (with or
  without an exception, synchronous or `DisposeAsync`), and the closing or
  disposal of the connection. Each acquisition is identified by the id of its
  transaction: an earlier transaction of the same context that is disposed again
  after the next one began releases nothing, and the next transaction keeps
  every other connection out until it ends. Saves and commands of the owning
  connection run inside the lock; those of every other connection wait for it.
  (`LockingBehaviorLifecycleTests`).
- The flow that holds the lock may resume on any thread between
  `BeginTransactionAsync` and the commit: the tests force a real thread change
  between every step, and check commit, explicit rollback, disposal after an
  exception, cancellation before the commit, a connection opened by the caller
  (released while it stays open), a context reused after its transaction, and
  concurrent transactions from distinct threads, synchronous and asynchronous.
- Every scenario runs under a deadline and is followed by a write from another
  thread under a deadline, so a leaked lock fails the test instead of suspending
  the suite; the seed of each test does not go through the mode under test.

Before this change `Pessimistic` used a static, thread-affine
`ReaderWriterLockSlim`. An asynchronous transaction that resumed on another
thread either deadlocked (the new thread waited for the lock its own flow held)
or threw `SynchronizationLockException` at commit, leaving the lock held by a
thread that no longer exists, so every later database access of the process
waited forever. `ContentPackManager`, `BackupService`, `KeyframeRepository`,
`ItemPersistenceService.ReattachUserDataAsync`/`SaveImagesAsync` and
`CleanDatabaseScheduledTask` run such transactions.

Limits:

- Ownership by connection means a flow that holds a transaction on one context
  and then writes or reads through a second context waits for itself. No
  current transaction does that; a congestion line is logged after one second.
- The lock covers one server process; other processes only meet SQLite's own
  busy timeout.

## Replace-a-set writes

Inventory of the writes that delete or modify rows before `SaveChanges`:

| Shape | Writes | State |
| --- | --- | --- |
| Transaction, delete, save, commit | `MediaStreamRepository.SaveMediaStreams`, `ChapterRepository.SaveChapters`, `MediaAttachmentRepository.SaveMediaAttachments`, `KeyframeRepository.SaveKeyframeDataAsync`, `DisplayPreferencesManager` | Atomic in every mode (`ReplaceSetRepositoryTests`, `DisplayPreferencesManagerConcurrencyTests`) |
| Delete committed alone, then save | `ItemPersistenceService.SaveImagesAsync` | Was not atomic: a failed save left the item without images in every mode. Now one transaction |
| Item deletion and user data reattachment | `ItemPersistenceService.DeleteItem`, `ReattachUserDataAsync` | Atomic in every mode (`UserDataLockingModeTests`; policies in `ItemPersistenceConservationTests`) |
| Separate statements, convergent on retry | `LinkedChildrenService.RerouteLinkedChildren`, `PeopleValidationTask` merge, `MediaSegmentManager`, `TrickplayManager` | Not changed: a failure leaves derived data that the next scan or run regenerates or completes |

## Item display preferences: pre-existing duplicates

Databases written before #369 can hold several `ItemDisplayPreferences` rows for
one scope `(UserId, ItemId, Client)`. The `DeduplicateItemDisplayPreferences`
migration applies the maintainer decision on #360, in its transaction:

| Class | Test | Kept active | Other rows |
|---|---|---|---|
| Identical | `GlobalScope = 0`, `DistinctSettings = 1` | lowest `Id` | archived in full, removed |
| Contradictory | `GlobalScope = 0`, `DistinctSettings > 1` | lowest `Id` (the row the server already read) | archived in full, removed |
| Global, origin unknown | `GlobalScope = 1` | lowest `Id` | archived in full, removed; no attempt to recover their scope |

No field is merged and no "most recent" row is chosen: nothing records when a row
changed. A lone global row is not a duplicate and is left in place. Then the
unique index `IX_ItemDisplayPreferences_UserId_ItemId_Client` replaces the index
on `UserId`. Every statement can run again on a migrated database (a full-system
restore writes back an earlier migration history), and `Down` puts every
archived row back with its id. A full-system restore of a backup taken before
the migration applies the same rule (`ItemDisplayPreferencesScopes`).

`ItemDisplayPreferencesArchive` holds every column of each removed row, its
original `Id`, `KeptId` (the active row of its scope), `DuplicateClass`
(0 identical, 1 contradictory, 2 global) and `ArchivedAt` (UTC). It is part of
full-system backups. To bring an archived row's settings back onto the active
row of its scope:

```sql
UPDATE ItemDisplayPreferences
SET (ViewType, RememberIndexing, IndexBy, RememberSorting, SortBy, SortOrder) =
    (SELECT ViewType, RememberIndexing, IndexBy, RememberSorting, SortBy, SortOrder
     FROM ItemDisplayPreferencesArchive WHERE Id = :archivedId)
WHERE Id = (SELECT KeptId FROM ItemDisplayPreferencesArchive WHERE Id = :archivedId);
```

Read-only diagnostic of the duplicates (run by
`ItemDisplayPreferencesDeduplicationMigrationTests` before and after the
migration; it returns nothing on a migrated database):

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

## Next bounded work

1. Together with #351, reproduce deletion through `ItemPersistenceService` when
   `ParentId` and `AncestorIds` disagree. Assert conservation of every cascaded
   child's user data, preservation of linked-only media and rollback after an
   effective mutation. Also exercise a new parent and child saved in one batch
   and orphaned local-version deletion. Use migrated SQLite and the actual
   service paths, not a reduced SQL schema.
2. Diagnose incompatible rows before adding `MediaSegments.ItemId` and
   `CustomItemDisplayPreferences.UserId` foreign keys. Record targeted repair
   choices and prove migration preservation.
3. Define authoritative hierarchy and metadata representations, then address
   identity and playlist cardinality through explicit decisions. Optimize
   retrieval only after representative query measurements.

The requested-scope fix is independent of these deletion and migration decisions.
It completes only the getter correction in section 3 of #360.
