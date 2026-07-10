# PR72 Parent/owner service-aware overloads

Scope: add `IItemLookupService`-aware overloads for `BaseItem`'s parent/owner accessors, alongside
the existing static-`LibraryManager`-based methods. No caller is migrated yet - this PR only adds
the alternate code path.

## Added overloads

`Reefin.Controller/Entities/BaseItem.cs`, each placed directly after its historical sibling:

- `BaseItem GetOwner(IItemLookupService lookup)` - after `GetOwner()` (~849).
- `BaseItem GetParent(IItemLookupService lookup)` - after `GetParent()` (~1000).
- `IEnumerable<BaseItem> GetParents(IItemLookupService lookup)` - after `GetParents()` (~1011).
  Chains via `GetParent(lookup)` at every hop (`parent = parent.GetParent(lookup)`), never falling
  back to the parameterless `GetParent()`.
- `T FindParent<T>(IItemLookupService lookup) where T : Folder` - after `FindParent<T>()` (~1023).
  Iterates `GetParents(lookup)`, not `GetParents()`.

All four are non-virtual, matching the historical methods and the `Folder.GetChildren(...,
IItemSortService)` precedent (`Reefin.Controller/Entities/Folder.cs:1581-1594`) - no plugin
override-compatibility concern the way a `virtual` change would raise. The original methods are
untouched byte-for-byte; plugins depending on them see no behavior change.

`GetParent(lookup)`, `FindParent<T>(lookup)` and `GetOwner(lookup)` validate eagerly with
`ArgumentNullException.ThrowIfNull(lookup)`. `GetParents(lookup)` is a `yield` iterator with no
explicit null check of its own (matching `GetParents()`, which also validates nothing); it
delegates to `GetParent(lookup)` on first enumeration, which performs the check there - consistent
with the deferred-execution semantics of iterator methods.

## Nullability deviation from the plan

The plan's signature sketch used `BaseItem?`/`T?`. `BaseItem.cs` opens with `#nullable disable`
(line 1), so those overloads were written without `?` annotations - `public BaseItem
GetParent(IItemLookupService lookup)`, not `public BaseItem? GetParent(...)` - to match every
other method in the file and avoid a new `CS8632` ("nullable annotation in a disabled context")
warning. Confirmed clean: `dotnet build Reefin.Server.Core/Reefin.Server.Core.csproj` produced no
new warnings beyond the pre-existing `NU1903` (SQLitePCLRaw advisory).

## Tests

`tests/Reefin.Controller.Tests/Entities/BaseItemLookupServiceTests.cs` (new file, 11 tests),
`[Collection(BaseItemStaticStateFixture.Name)]`.

Each test sets `BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object`
in the constructor - a strict mock with zero setups, so any call the code under test makes to the
static throws `MockException` and fails the test. This is the proof that the new overloads never
touch the static:

- `GetParent(lookup)`: resolves via the lookup mock; empty `ParentId` returns null *without*
  consulting the lookup (`Times.Never`); null `lookup` throws.
- `GetParents(lookup)`: walks a 2-level chain (`Movie -> Folder -> Folder`), asserting every hop
  resolved through the lookup mock (`Times.Once` per id) - this is what would catch a
  half-migrated loop that calls the static `GetParent()` past the first hop; empty chain case
  returns empty without a parent.
- `FindParent<T>(lookup)`: finds a `BoxSet` (a `Folder` subclass) two hops up through `Folder` and
  `Movie` intermediates; returns null when the type is absent from the chain; null `lookup` throws.
- `GetOwner(lookup)`: resolves via `OwnerId`/lookup mock; empty `OwnerId` returns null without
  consulting the lookup; null `lookup` throws.

## Validation Gate

- `dotnet build Reefin.Server.Core/Reefin.Server.Core.csproj` - 0 errors, 6 warnings (all
  pre-existing `NU1903`).
- `dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj` - 243/243 passed
  (232 baseline + 11 new).
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"` - 24/24 passed (unchanged).
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj` - 626/630 passed, 4 skipped (pre-existing Windows-only `ManagedFileSystemTests` cases, unrelated to this change).
