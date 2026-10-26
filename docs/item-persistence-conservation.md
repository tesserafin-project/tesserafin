# Item deletion and history conservation

Related tracking: [#360](https://github.com/tesserafin-project/tesserafin/issues/360)
and [#351](https://github.com/tesserafin-project/tesserafin/issues/351).
Reproduction base: `d2fd71e800ce440cfd3438bebc1c2cceb9ca945c`.

## Scope of a destructive operation

`ItemPersistenceService.DeleteItem` prepares the transitive closure of the
requested ids through the persisted `BaseItems.ParentId` and `BaseItems.OwnerId`
relationships. `ParentId` has a database cascade; `OwnerId` identifies owned
extras and local versions that must be removed with their owner. Preparation
must visit both relationships at every level, regardless of `IsFolder`.
It rejects a closure containing the UserData placeholder before making changes.

`AncestorIds` is a query projection and does not determine which items this
destructive operation owns. A missing entry must not leave a cascaded child's
history unprepared; a stale entry must not authorize deleting unrelated media.
`LinkedChildren` is an association: deleting its parent removes the association,
not the linked media. This scope rule does not redefine every hierarchy query or
rebuild existing projections.

The traversal retains `WhereOneOrMany` parameter binding for large id sets.
It queries each frontier through the existing ParentId/OwnerId indexes. This is
a correctness change; no throughput improvement is claimed.

## One preparation path, caller-owned transaction

The public deletion method opens a transaction, computes the scope, detaches
UserData, cleans item-dependent rows and deletes the items before committing.
The preparation routine does not create or commit a transaction of its own.
For normalized item values, it records the values referenced by this deletion
scope, removes the scope's mappings, then deletes only those candidate values
that have no remaining references. Shared values and unrelated unmapped values
remain intact; this operation does not perform global value maintenance.

Saving a video may remove local alternate versions whose `OwnerId` is the video's
id. The save path queues these obsolete version ids, flushes its tracked item and
association changes, then uses that same preparation routine before committing
the existing save transaction. This includes descendants and owned extras of the
obsolete versions. Removing a linked alternate version's association does not
delete that media item.

A new parent attached in the same save batch is valid even though a database
query cannot see it before the first `SaveChanges`. Ancestor validation includes
the batch's item ids in addition to persisted ids, while still rejecting ancestor
ids that are neither persisted nor in the batch.

## Conservation policy and limits

The existing placeholder model, user/key isolation and retention date assignment
remain in use. When several deleted items share `(UserId, CustomDataKey)`, the
existing batch arbitration keeps the greatest `LastPlayedDate`, then `PlayCount`.
Incoming rows still replace conflicting pre-existing placeholder rows. Neither
the quality of that latter policy nor the determinism of complete ties is solved
here. There is no new purge, migration or change to the existing retention task.

Active/placeholder conflicts at reattachment, competing candidate media and
identity or retention policy changes remain work for #351 and the relevant
decisions in #360. These tests do not close either tracker.

## Reproduction and verification

`ItemPersistenceConservationTests` invokes the actual persistence service with
SQLite initialized by the existing migrations. It mocks domain item lookup for
save inputs; it is not an HTTP end-to-end test or a production database repair.

The initial eight-case regression control found seven failures and one success:

| Scenario | Initial implementation |
| --- | --- |
| Missing ancestor entries, multi-level ParentId cascade | Cascaded histories are lost |
| Stale ancestor entry pointing to unrelated linked media | Unrelated media is deleted |
| Nested owned extras and their children | SQLite foreign-key failure |
| New parent/child batch, both input orders | Child's ancestor projection is missing |
| Removed local version and its child | Histories are lost |
| Injected failure during local-version removal | Deletion is reached without history preparation |
| Injected failure during public deletion | Prior effective mutations roll back |

Additional guards cover the placeholder entering the deletion closure, and batch
versus successive deletions with shared content keys and an older placeholder.
Two further cases exercise targeted value cleanup through both entry points:
they reject the pre-existing global cleanup, retain shared/unrelated values and
remove values made unreferenced by the current operation.
Fixtures contain two users, unrelated media, content-pack memberships and media
segments. Successful deletion checks retained playback/favorite/stream settings,
cleanup of dependent rows, surviving linked media and `PRAGMA foreign_key_check`.

The rollback witness is a SQLite trigger reached only after UserData has moved
to the placeholder and the item's content-pack membership has been deleted.
Its diagnostic distinguishes that state from failure before preparation. Both
entry points must restore snapshots of every application table; the save witness
also exercises an earlier primary-item update and association removal.

Run the regression and existing content-pack deletion guard with:

```sh
dotnet test tests/Tesserafin.Server.Implementations.Tests/Tesserafin.Server.Implementations.Tests.csproj \
  --filter 'FullyQualifiedName~ItemPersistenceConservationTests|FullyQualifiedName~ContentPackItemDeletionTests'
```
