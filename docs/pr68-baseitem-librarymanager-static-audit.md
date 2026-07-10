# PR68 BaseItem.LibraryManager static audit

Scope: renewed audit of `BaseItem.LibraryManager` static usage after the children/sort migration closed in PR67.

This is an audit only. Do not continue threading `IItemSortService` into unrelated signatures from here.

## Summary

`LibraryManager` static usage in `Reefin.Controller/Entities` is not one single dependency class. It splits into several different ownership problems:

- lookup and parent resolution;
- query/listing;
- collection folders and library options;
- mutation and registration;
- alternate-version maintenance;
- images and extras;
- legacy sort fallbacks.

The next architectural candidate is likely read-only lookup/parent resolution, but only after mapping cache ownership and visibility behavior. `GetItemById` is not a simple facade: it participates in lookup semantics that may involve cache, repository retrieval, registration, and visibility/context variants elsewhere in the stack.

## Counts From PR68 Snapshot

`LibraryManager.` references in `Reefin.Controller/Entities` are concentrated in:

- `BaseItem.cs`: 35 matches.
- `Video.cs`: 30 matches.
- `Folder.cs`: 27 matches.
- `Series.cs`: 11 matches.
- Smaller entity files: 1-4 matches each.

These counts are only triage signals. The migration boundary must be based on execution behavior and ownership, not text count.

## Category Map

### Lookup And Parent Resolution

Representative sites:

- `BaseItem.GetParent()` resolves `ParentId` through `LibraryManager.GetItemById`.
- `Season.Series` and `Episode.Series`/`Episode.Season` resolve IDs through `GetItemById`.
- `UserView.GetItemsInternal` resolves `DisplayParentId`/`ParentId` through `GetItemById`.
- `CollectionFolder.PhysicalFolders`, `AggregateFolder.Children`, and `UserRootFolder.Children` resolve stored IDs.
- `Video` resolves primary/alternate/owner IDs in several paths.

Risks before migration:

- Parent lookup affects visibility, display parent behavior, update routing, metadata inheritance, and path ownership.
- Some lookups are userless; others have user-aware variants outside these entity methods.
- Replacing this statically without a cache/repository/register map risks duplicating or bypassing item registration semantics.

Required next audit before code changes:

- Map `ILibraryManager.GetItemById` overloads and their use of cache vs repository.
- Map `RetrieveItem`, `RegisterItem`, root-folder population, and stale item behavior.
- Identify call sites where parent lookup must preserve visibility or collection-folder context.

### Query And Listing

Representative sites:

- Named item entities (`Genre`, `Person`, `Studio`, `Year`, `MusicArtist`, `MusicGenre`) call `GetItemList`.
- `Folder.QueryRecursive`, `QueryWithPostFiltering`, item-id handling, and linked-child batching call `GetItemsResult`/`GetItemList`.
- `Series` uses `GetCount`, `GetItemList`, and `GetItemsResult` for seasons/episodes.
- `Playlist.GetPlaylistItems` still has direct library queries for genre/artist expansion.

Status:

- The children/sort work moved specific `Folder.GetItems` traffic behind `IItemQueryService` and service-aware overloads.
- Broader query/listing statics remain a separate migration, likely requiring a read/query service boundary wider than sorting.

### Collection Folders And Options

Representative sites:

- `BaseItem.IsVisible`, language/country resolution, inherited metadata, and top-parent IDs use `GetCollectionFolders` and `GetLibraryOptions`.
- `Series.CreatePresentationUniqueKey` and grouping behavior use library options and collection folders.
- `BoxSet.GetLibraryFolderIds` depends on user root children and collection-folder mapping.
- `Folder.GetNonCachedChildren` uses content type and library options during physical resolution.

Risks before migration:

- These calls combine configuration, physical paths, collection scoping, and grouping preferences.
- A future replacement probably needs a collection-context/options service, not raw `GetItemById` plumbing.

### Mutation And Registration

Representative sites:

- `Folder.AddChild`, validation, replacement handling, and alternate cleanup call `CreateItem`, `CreateItems`, `DeleteItem`, and image updates.
- `BaseItem.UpdateToRepositoryAsync` calls `UpdateItemAsync` with `GetParent()`.
- `UserRootFolder` registers virtual children.
- `Video` alternate-version handling creates/deletes items.

Risks before migration:

- Mutations are not read-only and often pair repository writes with cache/register side effects.
- These should not be bundled into a lookup migration.

### Alternate Versions

Representative sites:

- `Video` and `Folder` use `GetItemById`, `CreateItem`, and `DeleteItem` to maintain primary/alternate relationships.

Risks before migration:

- Alternate-version repair depends on path-derived IDs, parent ownership, file existence, and deletion promotion behavior.
- This likely needs its own focused service boundary after lookup ownership is clear.

### Images And Extras

Representative sites:

- `BaseItem` refresh flow calls `FindExtras`, `GetItemList`, `DeleteItem`, and `UpdateImagesAsync`.
- `BaseItem.GetExtras` queries theme/trailer/special-feature extras.
- `GetThemeSongs`/`GetThemeVideos` remain obsolete wrappers with legacy sort fallback until Plugin SDK v2.

Risks before migration:

- Extras mix filesystem scanning, owned-item lifecycle, repository listing, and API-facing theme media behavior.
- This should remain separate from parent lookup.

### Legacy Sort Fallbacks

Remaining known sites after PR67:

- `Series.GetSeasonEpisodes(..., itemSortService: null)`.
- Old direct `BoxSet` child methods.
- `UserViewBuilder.SortAndPage(..., ILibraryManager)` obsolete overload.
- `BaseItem.GetThemeSongs/GetThemeVideos` obsolete methods.

Status:

- These are accepted compatibility/fallback paths, not normal DI application traffic.

## Recommended Next Series

Start with a read-only lookup/parent audit, not a broad static removal.

Minimum map before implementation:

- `ILibraryManager.GetItemById` overload semantics.
- cache hit/miss behavior;
- repository retrieval behavior;
- item registration behavior;
- root/user-root special cases;
- visibility/user-aware variants;
- `BaseItem.GetParent`, `FindParent`, `GetOwner`, display parent, and collection-folder interactions.

Only after that map is complete should a small additive lookup abstraction be introduced. The first implementation tranche should avoid mutations, alternate-version repair, images/extras, and collection options.
