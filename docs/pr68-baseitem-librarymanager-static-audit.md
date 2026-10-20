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

DEBT-SERVER-2 (2026-10-19): membership resolution itself now lives in `ICollectionFolderResolver`
(`CollectionFolderResolver`, built on `IItemLookupService` and `IUserRootFolderProvider` only, guarded in
`UserViewCatalogDependencyTests` against reaching `ILibraryManager` or a service locator).
`LibraryManager.GetCollectionFolders` is a delegating facade, so the entity sites above still reach it
through the `BaseItem.LibraryManager` static; moving them onto the port is the next step.
`GetLibraryOptions` and content-type resolution are unchanged. `CollectionFolderMembershipTests` pins
membership and the visibility, deletion and library-option rules that read it.

DEBT-SERVER-3 (2026-10-19): tag inheritance and the tag filter have a port, `ITagVisibilityService`
(`TagVisibilityService`, built on `IItemLookupService` and `ICollectionFolderResolver` only). The
combination and the filter stay single, as static entity helpers (`BaseItem.CombineInheritedTags`,
`BaseItem.EvaluateTagVisibility`) used by both the service and the static-backed `GetInheritedTags()`.
`ItemAccessService` (`GetVisibleItemById`, and `LibraryManager.GetItemById<T>(id, user)` through it)
passes the service down `IsVisibleStandalone`/`IsVisible`, so the entity overrides keep their rules
while the tags no longer come through the static. The service-taking members are new overloads; the
published `IsVisible(User, bool)`, `IsVisibleStandalone(User)`, `IsParentalAllowed(User, bool)` and
`IsVisibleStandaloneInternal(User, bool)` stay (ABI gate) and forward with no service, i.e. the static. Still static on that path: the parent walk and the
enabled/blocked-library restriction in `IsVisibleStandaloneInternal`, box set and channel visibility.
Still static elsewhere: every caller that passes no service, and `ItemPersistenceService`'s stored
inherited tags (the persistence service sits below the resolver, so it cannot take the port without a
cycle). `InheritedTagVisibilityTests` pins inheritance and the filter on both paths.

DEBT-SERVER-4 (2026-10-20): the parents and the library restriction of the standalone check have a port,
`IStandaloneVisibilityService` (an `ITagVisibilityService` that also supplies the parents, the display
parent, the member libraries and the library ids; `TagVisibilityService`, now also built on
`IUserRootFolderProvider`). `ItemAccessService` takes it and passes it down as before; the entities
recognise it in `IsVisibleStandaloneInternal` and `IsParentalAllowed`, so the parent walk, the
enabled/blocked-library restriction, and the rating's display-parent walk and parent/library country code
no longer go through the static. The rules stay single: the same entity methods run on both paths, only
the data source differs (no service, or a plain `ITagVisibilityService`: the static). `UserRootFolder.Children`
resolved the libraries through the static since MEDIA-ACCESS-R2; `UserRootFolder.GetLibraries(IItemLookupService)`
does the same through the lookup, and `CollectionFolderResolver` and the service use it. No public
signature changed (one interface and one method added; local `ci/abi-compat.sh` 8/8).
`StandaloneVisibilityTests` pins the check on both paths and runs the access path under a strict static.
Still static on the access-by-id path:
- a rating's country code once neither the item, its parents nor its libraries carry one
  (`LibraryManager.GetLibraryOptions`, library options);
- box sets that are not legacy (`BoxSet.IsVisible`: `GetUserRootFolder().GetChildren(user, true)`,
  `GetLibraryFolderIds()` through `GetCollectionFolders`, linked children through `GetLinkedChild`, and
  `IsParentalAllowed(user, true)` on them without the service);
- channel content (`IsVisibleStandalone` for `SourceType.Channel`: `Channel.IsChannelVisible` reads the
  static `ChannelManager`, then `Channel.IsVisible(User)` without the service).

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
