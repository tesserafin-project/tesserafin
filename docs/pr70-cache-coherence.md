# PR70 cache coherence

Scope: fix the stale-cache bug in `LibraryManager.DeleteItemsUnsafeFast` documented by PR69, and
centralize all writes to the item lookup cache (`_cache`, a `FastConcurrentLru<Guid, BaseItem>`)
behind four private helpers. Production change is confined to
`Reefin.Server.Core/Library/LibraryManager.cs`.

## Bug fixed

`DeleteItemsUnsafeFast` deleted metadata paths, external data files, and repository rows for the
given items, but never touched `_cache`. A subsequent `GetItemById` for a deleted item's id could
still return the stale cached instance instead of `null` (or a fresh repository lookup). Fixed by
invalidating the cache for every deleted item's id right after the persistence-layer delete call.

## Helpers introduced

Four private helpers on `LibraryManager` are now the sole write path to `_cache` (plus the existing
`_cache.TryGet` read in `GetItemById(Guid)`):

- `ShouldCacheItem(BaseItem item)` - the caching policy extracted verbatim from the old
  `RegisterItem` body (IItemByName cached only if `MusicArtist`; non-folder cached only if `Video`
  or `LiveTvChannel`; otherwise cached). No behavior change.
- `RegisterItemInCache(BaseItem item)` - `if (ShouldCacheItem(item)) _cache.AddOrUpdate(...)`.
- `RemoveItemFromCache(Guid id)` - wraps `_cache.TryRemove` for a single id.
- `RemoveItemsFromCache(IEnumerable<Guid> ids)` - wraps `_cache.TryRemove` for a batch of ids.

`RegisterItem` (public, `ILibraryManager`) keeps its signature and now delegates to
`RegisterItemInCache`. `GetItemById(Guid)` is unchanged - it already calls `RegisterItem` on a
repository hit, so it now goes through the same primitive transitively. `DeleteItem`'s inline
`_cache.TryRemove` calls (item + recursive children) are replaced with
`RemoveItemFromCache`/`RemoveItemsFromCache`. `DeleteItemsUnsafeFast` gains a
`RemoveItemsFromCache` call after its persistence delete. `CreateItems`, `UpdateImagesAsync`, and
`UpdateItemsAsync` already called the public `RegisterItem` and needed no change.

## Invariant

Every write to `_cache` goes through `RegisterItemInCache`, `RemoveItemFromCache`, or
`RemoveItemsFromCache`. The only other `_cache` access in `LibraryManager.cs` is the `TryGet` read
in `GetItemById(Guid)`. No other file touches `_cache` (it is a private field).

## Test change

`LibraryManagerItemLookupTests.DeleteItemsUnsafeFast_CacheableItem_DoesNotInvalidateCache` (PR69,
characterizing the bug) is renamed to
`DeleteItemsUnsafeFast_CacheableItem_InvalidatesCache` and its assertions flipped to mirror the
existing `DeleteItem_CacheableItem_InvalidatesCache` test: `GetItemById` after
`DeleteItemsUnsafeFast` misses the cache, calls the repository once, and returns `null`.

## Validation Gate

- `dotnet build Reefin.Server.Core/Reefin.Server.Core.csproj` - 0 errors.
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"` - 21/21 passed.
- `dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj` - 232/232 passed.
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj` - 623/627 passed, 4 skipped (pre-existing Windows-only `ManagedFileSystemTests` cases, unrelated to this change).

Known warning during the gate: `NU1903` for `SQLitePCLRaw.lib.e_sqlite3`; unrelated to this change.
