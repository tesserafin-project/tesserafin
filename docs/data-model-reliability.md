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

Read-only diagnostic for duplicate tuples, before considering any unique index:

```sql
SELECT UserId, ItemId, Client, COUNT(*) AS RowCount
FROM ItemDisplayPreferences
GROUP BY UserId, ItemId, Client
HAVING COUNT(*) > 1;
```

Regression coverage is in `DisplayPreferencesManagerTests`. It exercises the
real manager with SQLite initialized through the existing migrations, including
saved-value reloads, repeated reads, scope isolation, list filtering and
preservation of ambiguous legacy global rows. It does not establish concurrent
get-or-create uniqueness or define a policy for pre-existing duplicate tuples.
Those require a separate decision and migration plan.

## Next bounded work

1. Together with #351, reproduce deletion through `ItemPersistenceService` when
   `ParentId` and `AncestorIds` disagree. Assert conservation of every cascaded
   child's user data, preservation of linked-only media and rollback after an
   effective mutation. Also exercise a new parent and child saved in one batch
   and orphaned local-version deletion. Use migrated SQLite and the actual
   service paths, not a reduced SQL schema.
2. Diagnose incompatible rows before adding `MediaSegments.ItemId` and
   `CustomItemDisplayPreferences.UserId` foreign keys or preference uniqueness.
   Record targeted repair choices and prove migration preservation.
3. Define authoritative hierarchy and metadata representations, then address
   identity and playlist cardinality through explicit decisions. Optimize
   retrieval only after representative query measurements.

The requested-scope fix is independent of these deletion and migration decisions.
It completes only the getter correction in section 3 of #360.
