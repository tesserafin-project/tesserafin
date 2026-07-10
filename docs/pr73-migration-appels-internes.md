# PR73 Migration des appels internes de LibraryManager

Scope: migrer les appels internes de `LibraryManager.cs` vers les overloads service-aware
ajoutés en PR72 (`GetParent(IItemLookupService)`, `GetParents(IItemLookupService)`,
`GetOwner(IItemLookupService)`, `FindParent<T>(IItemLookupService)`), en passant `this` -
`LibraryManager` implémente `IItemLookupService` depuis PR71. Aucun changement de
comportement : les overloads service-aware font la même résolution `GetItemById`, seul le
chemin (instance vs static `BaseItem.LibraryManager`) change. Fichier de production touché :
`Reefin.Server.Core/Library/LibraryManager.cs` uniquement.

## Sites migrés

| # | Méthode | Avant (file:line) | Après |
|---|---------|--------------------|-------|
| 1 | `DeleteItem(BaseItem, DeleteOptions, bool)` | `LibraryManager.cs:393` `item.GetOwner() ?? item.GetParent()` | `item.GetOwner(this) ?? item.GetParent(this)` |
| 2 | `CreateItems` (fallback parent des event args `ItemAdded`) | `LibraryManager.cs:2369` `parent ?? item.GetParent()` | `parent ?? item.GetParent(this)` |
| 3 | `UpdateImagesAsync` | `LibraryManager.cs:2412` `item.GetParent()` | `item.GetParent(this)` |
| 4 | `GetCollectionFolders(BaseItem, IEnumerable<Folder>)` (boucle parent) | `LibraryManager.cs:2662` `item.GetParent()` | `item.GetParent(this)` |
| 5 | `GetCollectionFolders(BaseItem, IEnumerable<Folder>)` (fallback owner) | `LibraryManager.cs:2671` `item.GetOwner()` | `item.GetOwner(this)` |
| 6 | `GetInheritedContentType` | `LibraryManager.cs:2741` `item.GetParents()` | `item.GetParents(this)` |
| 7 | `GetTopFolderContentType` (boucle vers le top parent) | `LibraryManager.cs:2789` `item.GetParent()` | `item.GetParent(this)` |

Tous les sites sont dans des méthodes d'instance de `LibraryManager` (y compris le site 2, qui
est dans un `foreach` inline, pas une lambda capturant un autre `this`), donc `this` est
directement disponible - aucune signature publique modifiée, aucun constructeur touché.

Aucun appel `.FindParent<T>()` (sans lookup) n'existait dans `LibraryManager.cs` avant cette
PR ; rien à migrer sur ce point.

## Grep de vérification (zéro résultat attendu)

```
grep -nE '\.GetParent\(\)|\.GetParents\(\)|\.GetOwner\(\)|\.FindParent<[^>]+>\(\)' Reefin.Server.Core/Library/LibraryManager.cs
```

Résultat : aucune ligne. Les 7 sites listés ci-dessus utilisent désormais tous la variante
`(this)`.

## Exceptions

Aucune. Les 7 sites recensés par le grep pré-migration ont tous pu être migrés vers `this` -
aucun n'est dans un contexte static ni dans une lambda capturant une instance différente de
`LibraryManager`.

## Tests

Ajout dans
`tests/Reefin.Server.Implementations.Tests/Library/LibraryManager/LibraryManagerItemLookupTests.cs` :
`DeleteItem_ParentResolution_UsesInstanceLookupNotStaticLibraryManager`.

- `BaseItem.LibraryManager` est réglé sur `new Mock<ILibraryManager>(MockBehavior.Strict).Object`
  (zéro setup) : tout appel à ce mock lève une `MockException` et fait échouer le test - c'est
  la preuve que la résolution du parent dans `DeleteItem` (site 1 du tableau ci-dessus) passe
  entièrement par l'instance `this`, pas par le static.
- `BaseItem.ConfigurationManager` reste configuré avec le mock existant
  (`_configurationManagerMock.Object`), comme dans `DeleteItem_CacheableItem_InvalidatesCache` -
  ce static reste légitimement nécessaire (`GetInternalMetadataPath` etc.), hors périmètre de
  cette PR.
- Item `LiveTvChannel` avec `ParentId` pointant vers un `Folder` préalablement enregistré via
  `RegisterItem` (donc résolu depuis le cache de l'instance, sans toucher le repository ni le
  static). L'événement `ItemRemoved` est capturé et son `Parent` comparé (`Assert.Same`) au
  `Folder` enregistré, prouvant une résolution correcte de bout en bout via `this`.
- Le choix de `LiveTvChannel` reprend le montage du test `DeleteItem_CacheableItem_
  InvalidatesCache` existant (`SourceType` LiveTV, `IsFolder` false, `Path` null) pour garder
  inertes les chemins secondaires de `DeleteItem` (suppression fichier, `ChannelManager`, etc.)
  et isoler la résolution du parent.

Pas de test ajouté pour les sites 2-7 (`CreateItems`, `UpdateImagesAsync`,
`GetCollectionFolders`, `GetInheritedContentType`, `GetTopFolderContentType`) : leurs chemins
traversent soit `GetInheritedTags`/`IsVisibleStandalone` (autre static légitime hors
périmètre, comme documenté dans les tests existants du fichier), soit nécessitent un montage
équivalent sans plus-value de preuve par rapport au test `DeleteItem` déjà ajouté - le
changement de code est mécanique et identique dans les 7 cas (ajout de `(this)`).

## Validation Gate

- `dotnet build Reefin.Server.Core/Reefin.Server.Core.csproj` - 21 projets, 0 erreur, 6
  warnings (tous `NU1903` pré-existants).
- `dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj` - 243/243 réussis.
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"` - 25/25 réussis (24 baseline + 1 nouveau).
- `dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj` - 627/631 réussis, 4 ignorés (`ManagedFileSystemTests` Windows-only, pré-existants, sans rapport avec ce changement).
