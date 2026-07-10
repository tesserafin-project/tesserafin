# PR71 IItemLookupService

Scope: introduce `IItemLookupService`, a narrow read-only interface for id-based item lookup,
implemented by `LibraryManager` alongside `ILibraryManager`. No consumer is wired to the new
interface yet - this PR only establishes the contract and its DI registration.

## Contract

`Reefin.Controller/Library/IItemLookupService.cs` (namespace `Reefin.Controller.Library`) declares
three members, matching the existing `LibraryManager` implementations verbatim (no body changes,
no nullability adjustments needed):

- `BaseItem? GetItemById(Guid id)`
- `T? GetItemById<T>(Guid id) where T : BaseItem`
- `T? GetItemById<T>(Guid id, User? user) where T : BaseItem` (`User` =
  `Reefin.Database.Implementations.Entities.User`)

The interface doc frames it as a cache-aware, user-aware read boundary by id.

## Excluded on purpose

Everything else on `ILibraryManager` stays there:

- Mutation: `CreateItem(s)`, `RegisterItem`, `DeleteItem`, `DeleteItemsUnsafeFast`.
- Querying/listing: `GetItemList`, `GetItemIds`, and friends.
- Collection folder access, library options, `FindByPath`, `RetrieveItem`.
- `T? GetItemById<T>(Guid id, Guid userId)` - the user-id-resolving overload. It is a convenience
  wrapper around the `User?` overload (`_userManager.GetUserById(userId)` then delegate); keeping
  it off `IItemLookupService` avoids implicitly requiring lookup consumers to also depend on user
  resolution semantics. Callers needing that convenience keep depending on `ILibraryManager`.

`ILibraryManager.cs` itself is untouched - `IItemLookupService` does not inherit from or extend it,
and `ILibraryManager` does not inherit `IItemLookupService`. The only interface-list change is on
the `LibraryManager` class declaration.

## Implementation

`Reefin.Server.Core/Library/LibraryManager.cs`: class declaration changed from
`LibraryManager : ILibraryManager` to `LibraryManager : ILibraryManager, IItemLookupService`. The
three `GetItemById` overloads already implement the interface's shape; no method bodies changed.

## DI

`Reefin.Server.Core/ApplicationHost.cs`, right after the existing
`serviceCollection.AddSingleton<ILibraryManager, LibraryManager>();`:

```csharp
serviceCollection.AddSingleton<IItemLookupService>(sp => (IItemLookupService)sp.GetRequiredService<ILibraryManager>());
```

Same pattern already used for `IItemRepository`/`IItemQueryHelpers` resolving to the
`BaseItemRepository` singleton. This guarantees `IItemLookupService` and `ILibraryManager` resolve
to the exact same `LibraryManager` instance - one instantiation, two interface facets, so the
lookup cache (`_cache`) is shared regardless of which interface a consumer depends on.

## Tests

`LibraryManagerItemLookupTests.cs` gains three tests:

- `LibraryManager_IsAssignableToIItemLookupService` - `Assert.IsAssignableFrom<IItemLookupService>`.
- `GetItemById_ViaIItemLookupServiceReference_SameCacheBehaviorAndInstanceAsILibraryManager` -
  looks up through an `IItemLookupService` reference and through the concrete `_libraryManager`,
  asserts both return the same instance and the repository is never called (cache hit).
- `GetItemByIdGeneric_ViaIItemLookupServiceReference_InvisibleViaBlockedTag_ReturnsNull` - reuses
  the existing blocked-tag visibility setup, exercised through the `User?` overload via the
  `IItemLookupService` reference, confirming visibility rules apply identically across the
  interface boundary.

## Validation Gate

- `dotnet build Reefin.Server.Core/Reefin.Server.Core.csproj` - 0 errors (ApplicationHost.cs lives
  in this project).
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"` - 24/24 passed.
- `dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj` - 232/232 passed.
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj` - 626/630 passed, 4 skipped (pre-existing Windows-only `ManagedFileSystemTests` cases, unrelated to this change).

Known warning during the gate: `NU1903` for `SQLitePCLRaw.lib.e_sqlite3`; unrelated to this change.
