# PR69 lookup and cache characterization

Scope: characterization tests for `LibraryManager.GetItemById` (all overloads) and its
`FastConcurrentLru<Guid, BaseItem>` cache, as the read-only lookup/parent-resolution map called
for at the end of PR68's static audit. No production code changed.

New file: `tests/Reefin.Server.Implementations.Tests/Library/LibraryManager/LibraryManagerItemLookupTests.cs`
(21 tests), plus a local `LibraryManagerStaticStateFixture` collection definition
(`tests/Reefin.Server.Implementations.Tests/Library/LibraryManager/LibraryManagerStaticStateFixture.cs`)
so tests that mutate `BaseItem` statics run sequentially with each other.

## Locked Behavior

### `GetItemById(Guid)`

- `Guid.Empty` throws `ArgumentException`.
- Cache hit returns the cached instance directly; the repository (`IItemRepository.RetrieveItem`)
  is never called.
- Cache miss calls `RetrieveItem` exactly once, then registers the result via `RegisterItem` if
  non-null (so a second lookup of the same id is also a cache hit and does not call the repository
  again).

### `GetItemById<T>(Guid)`

- Cast pattern (`item is T`): an incompatible type (e.g. a `Movie` requested as `MusicArtist`)
  returns `null` rather than throwing.

### `GetItemById<T>(Guid, User?)` / `ItemIsVisible`

- `user is null` → item is always returned (visibility is not evaluated at all).
- `item is null` → always `null`.
- Otherwise: `item is UserRootFolder || item.IsVisibleStandalone(user)`. The `UserRootFolder` check
  short-circuits the `OR` - `IsVisibleStandalone` is **never evaluated** for a `UserRootFolder`, so
  it is always visible regardless of tags/rating that would otherwise block it.
- For a non-`UserRootFolder` item, visibility is characterized through the blocked-tags branch of
  `BaseItem.IsVisibleViaTags`: a blocked tag makes the item invisible; no tag restrictions plus no
  `Path` (so `IsVisibleStandaloneInternal`'s folder/collection check short-circuits on an empty
  `topParent.Path`) makes it visible. The blocked-tag branch calls `BaseItem.GetInheritedTags()`,
  which unconditionally calls the static `BaseItem.LibraryManager.GetCollectionFolders(this)` - this
  is why the invisible-case test has to set that static (via a mock returning an empty folder list).
- `Video`/`Movie` were deliberately avoided for these two tests: `Video.SourceType` calls
  `IsActiveRecording()`, which dereferences the static `Video.RecordingsManager` - unset in this
  test project, it throws `NullReferenceException`. `Audio` was used instead since it does not
  override `SourceType`.

### Cacheable vs. non-cacheable types (`RegisterItem`)

Confirmed by exercising `RegisterItem`/`GetItemById` directly (matches the code at
`LibraryManager.RegisterItem`, lines ~326-346):

- **Cacheable**: `Folder` (and subclasses, e.g. `UserRootFolder`), `Video` (and subclasses, e.g.
  `Movie`), `LiveTvChannel`, `MusicArtist` (the sole `IItemByName` implementor that is cached -
  it is `Folder`-derived, and the `IItemByName` branch explicitly excludes `MusicArtist` from the
  "don't cache" early return).
- **Not cacheable**: other `IItemByName` implementors (`Genre`, `Person` characterized here), and
  leaf items that are neither `Video` nor `LiveTvChannel` (`Audio`, `Book` characterized here). For
  these, every `GetItemById` call hits the repository - verified with `Times.Exactly(2)` across two
  consecutive calls.

### `RegisterItem` overwrite semantics

- Calling `RegisterItem` again with a **new instance** carrying the **same `Id`** replaces what
  `GetItemById` returns (`FastConcurrentLru.AddOrUpdate` semantics) - confirmed via
  `ReferenceEquals`/`Assert.Same` against the new instance and `Assert.NotSame` against the old one.
- `CreateItems` (with `parent: null`, a non-`Video` cacheable item so the alternate-version resolution
  branch is skipped) reaches the same `RegisterItem` call in its final loop, so a plain `CreateItems`
  call also populates the cache without ever touching the repository.

### `DeleteItem` invalidates the cache

- Confirmed with a cacheable, non-`Video`, non-folder item (`LiveTvChannel`, `SourceType.LiveTV`) to
  avoid the `Video` alternate-version machinery, `ChannelManager`, and real file I/O. With `Path`
  left `null` (`IsFileProtocol` false) and `DeleteOptions.DeleteFileLocation` at its `false` default,
  `IsInternalItem` and the `GetDeletePaths` branch are both inert.
- After `DeleteItem`, `_cache.TryRemove` runs, so the next `GetItemById` call for the same id misses
  the cache and calls the repository again (`Times.Once`), and `IItemPersistenceService.DeleteItem`
  is called with the deleted item's id.
- `GetInternalMetadataPath()` (called while computing metadata paths to delete) reads the static
  `BaseItem.ConfigurationManager.ApplicationPaths.InternalMetadataPath` - this is why the
  `DeleteItem`/`DeleteItemsUnsafeFast` tests set that static, joined via
  `LibraryManagerStaticStateFixture`.

### `DeleteItemsUnsafeFast` does **NOT** invalidate the cache - known bug

This is the headline finding, characterized exactly as described in the task brief:

- `DeleteItemsUnsafeFast` deletes metadata paths, calls `IExternalDataManager.DeleteExternalItemFiles`,
  and calls `IItemPersistenceService.DeleteItem(...)` - but never touches `_cache`.
- Consequence: after `DeleteItemsUnsafeFast` removes a **cacheable** item that was previously
  registered in the cache, `GetItemById` for that id still returns the stale cached instance, and
  the repository is **not** called again (`Times.Never`).
- Test: `DeleteItemsUnsafeFast_CacheableItem_DoesNotInvalidateCache`, with the comment:
  `// Comportement actuel (bug connu): PR70 ajoutera l'invalidation et inversera cette assertion.`
  When PR70 adds the invalidation, this test's assertions (`Assert.Same` + `Times.Never`) should
  flip to `Assert.NotSame`/`Assert.Null` + `Times.Once`, mirroring the `DeleteItem` test above.

## Deviations From The Task Brief

None of substance. All 11 requested scenarios were implemented as described, with two adaptations
driven by actual runtime behavior discovered while writing the tests (not by choice):

- Visibility tests (`GetItemById<T>(id, user)`) use `Audio` instead of `Movie`/`Video`, because
  `Video.SourceType` throws `NullReferenceException` via the unset `Video.RecordingsManager` static
  in this test project. `Audio` does not override `SourceType` and needed no extra static.
- `DeleteItem`/`DeleteItemsUnsafeFast` both required setting `BaseItem.ConfigurationManager` (for
  `GetInternalMetadataPath`), which was foreseeable from the brief but not spelled out; handled via
  the local `LibraryManagerStaticStateFixture` non-parallel collection.

## Validation Gate

- `dotnet build Reefin.Server.Core/Reefin.Server.Core.csproj --no-restore` - unaffected (test-only change).
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"` - 21/21 passed.
- `dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj` - 232/232 passed.
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj` - 623/627 passed, 4 skipped (pre-existing Windows-only `ManagedFileSystemTests` cases, unrelated to this change).

Known warning during the gate: `NU1903` for `SQLitePCLRaw.lib.e_sqlite3`; unrelated to this change.
