# PR67 children/sort closure audit v2

Scope: final execution-path audit after PR62-PR66 reopened and repaired the children/sort migration.

## Closure Criterion

No known normal DI application path reaches `ILibraryManager.Sort`. The only remaining `ILibraryManager.Sort` traffic is through explicitly legacy/obsolete compatibility APIs or fallback paths for old plugin overrides.

## Verified Execution Paths

`UserView.GetChildren(..., IItemSortService)`:

- `UserView` now overrides the service-aware method.
- It calls `GetItemList(..., itemSortService)` directly.
- `UserViewBuilder` is constructed with the service and uses `IItemSortService.Sort`.
- Locked by `UserViewGetChildren_ServiceAwarePathDoesNotUseStaticSort`.

`UserView.GetRecursiveChildren(..., IItemSortService)`:

- `UserView` now overrides the service-aware method.
- The recursive query setup is shared with the legacy method, then dispatched through `GetItemList(..., itemSortService)`.
- Locked by `UserViewGetRecursiveChildren_ServiceAwarePathDoesNotUseStaticSort`.

`BoxSet` via `ItemQueryService`:

- `BoxSet.SupportsRawQueryItems` is now `false`, so the generic fast path no longer calls `Folder.GetRawQueryItems` for BoxSet.
- `ItemQueryService` falls back to `Folder.GetItems(..., itemSortService)` / `GetItemList(..., itemSortService)`.
- `BoxSet.GetItemsInternal(..., itemSortService)` materializes raw children through `GetChildren(..., itemSortService)`.
- Locked by `GetItems_BoxSetNonRecursive_UsesItemSortServiceInsteadOfStaticSort` and `GetItemList_BoxSetNonRecursive_UsesItemSortServiceInsteadOfStaticSort`.

Direct `BoxSet` service-aware path:

- `BoxSet.GetChildren(..., itemSortService)` sorts directly with `IItemSortService`.
- It avoids the old three-argument-to-four-argument redispatch that caused the legacy double sort.
- Locked by `BoxSetGetChildren_ServiceAwarePathSortsOnceWithoutStaticSort`.

`Season` service-aware path:

- `Season.GetChildren(..., itemSortService)` delegates to `GetEpisodes(..., itemSortService)`.
- The sort service reaches `Series.GetSeasonEpisodes`.
- Locked by `SeasonGetChildren_ServiceAwarePathPassesSortServiceToGetEpisodes`.

`UserRootFolder` service-aware path:

- `UserRootFolder.GetItemsInternal(..., itemSortService)` sorts user views with `IItemSortService`.
- Application callers in `Reefin.Api` and `Reefin.Server.Core` pass `_itemSortService` to `GetChildren`/`GetItems` entry points where applicable.

User-scoped recursive calls:

- The meaningful service-aware recursive surface is the virtual `GetRecursiveChildren(User, InternalItemsQuery, out int, IItemSortService)`.
- `YearsController` and `MusicManager` use that surface where the query/user route can reach overridden folder behavior.
- No-op non-virtual overloads were removed in PR65.

Legacy/plugin wrappers:

- Old virtual methods are unchanged.
- Base service-aware overloads still redispatch to old virtual methods for subclasses/plugins that do not override the new service-aware method.
- This preserves unknown plugin behavior by design.

## Remaining `ILibraryManager.Sort` Sites

- `Series.GetSeasonEpisodes(..., itemSortService: null)` legacy fallback.
- `BoxSet` old direct `GetChildren`/`GetRecursiveChildren` path.
- `UserViewBuilder.SortAndPage(..., ILibraryManager)` obsolete legacy overload.
- `BaseItem.GetThemeSongs/GetThemeVideos`, obsolete until Plugin SDK v2.

These are not known normal DI application paths after PR67.

## Validation Gate

- `dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj --no-restore`
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --no-restore`

Known warning during the gate: `NU1903` for `SQLitePCLRaw.lib.e_sqlite3`; this is unrelated to the children/sort migration.
