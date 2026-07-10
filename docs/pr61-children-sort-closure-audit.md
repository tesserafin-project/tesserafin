# PR61 children/sort closure audit

Scope: closure audit for the PR55-PR60 additive migration of the public `GetEpisodes`, `SortAndPage`, `Folder.GetItems`, `GetChildren`, and `GetRecursiveChildren` surfaces.

## Result

- Normal DI application paths in `Reefin.Api`, `Reefin.Server.Core`, `Reefin.Providers/Music`, and `Reefin.XbmcMetadata/Savers/ArtistNfoSaver` use service-aware overloads where the migration plan required it.
- Legacy public contracts remain in place as compatibility wrappers or fallbacks.
- Unknown plugins that override only the old virtual `GetChildren`/`GetRecursiveChildren` methods still work because the base service-aware overloads delegate to the old virtuals.
- `ILibraryManager.Sort` remains obsolete and is not removed; Plugin SDK v2 remains the removal boundary.

## Current call audit

`GetChildren` in DI application code:

- `Reefin.Api`: 5 calls, all pass `_itemSortService`.
- `Reefin.Server.Core`: 7 calls, all pass `_itemSortService`.
- Remaining `Reefin.Controller` calls are entity fallback/legacy paths, not normal DI application traffic.

`GetRecursiveChildren` in migrated concrete callers:

- `AlbumMetadataService`: service-aware.
- `ArtistMetadataService`: service-aware for the concrete recursive path; the tagged-item query path is unrelated.
- `MusicManager`: service-aware.
- `ArtistNfoSaver`: service-aware.
- `ItemUpdateController`: service-aware.
- `PlaylistManager`: service-aware while already in the PR59 migration area.
- `MetadataService<TItemType, TIdType>` intentionally remains on the old path to avoid broad fan-out.

`ILibraryManager.Sort` traffic still reachable:

- `Series.GetSeasonEpisodes(..., itemSortService: null)` legacy fallback.
- `BoxSet` old `GetChildren`/`GetRecursiveChildren` path legacy fallback.
- `UserViewBuilder.SortAndPage(..., ILibraryManager)` legacy overload fallback.
- `BaseItem.GetThemeSongs/GetThemeVideos`, now marked obsolete and retained for plugin compatibility.

## Pragmas still needed

- Sort-related `CS0618` suppressions remain only around legacy/fallback sort facades.
- Other `CS0618` suppressions in migrations, XML parsers, and legacy linked-child handling are unrelated to this children/sort migration.

## Static paths still reached

- Entity compatibility methods may still use `BaseItem.LibraryManager`/other statics because these are the preserved public/plugin contracts.
- Normal DI call paths migrated in PR59/PR60 no longer need those statics for the children/sort route covered by this series.

## Locked behavior

- PR57 characterizes the old BoxSet three-argument path as currently sorting twice.
- PR58 service-aware BoxSet path sorts once; the double sort is documented as legacy debt, not a functional contract.
- Base service-aware overload fallback preserves unknown plugin behavior by redispatching to the old virtual methods.
