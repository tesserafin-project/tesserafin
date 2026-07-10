# PR75 Extraction du cache d'items vers ItemLookupService

Scope : auditer les 5 invariants du cache d'items de `LibraryManager` (introduit par PR69/
PR70) pour décider si son extraction vers un service dédié est sûre, puis réaliser
l'extraction si l'audit est vert.

## Phase 1 — audit de décision

Méthode : lecture intégrale de `Reefin.Server.Core/Library/LibraryManager.cs` (3708 lignes
avant extraction) autour de chaque site touchant `_cache`, `RegisterItem`,
`_persistenceService.DeleteItem`, complétée par des `grep` ciblés sur tout le repo pour les
appelants externes de `RegisterItem(` et `IItemPersistenceService`.

### Invariant 1 — Points d'enregistrement cartographiés

**VERT.** `grep -n "RegisterItem(" **/*.cs` (hors tests) ne trouve que 8 sites :
- `LibraryManager.cs:326` — définition de `RegisterItem(BaseItem item)`, seul point d'entrée
  qui touchait `_cache` en écriture (via `RegisterItemInCache`).
- 5 appels internes, tous via `RegisterItem(...)`, jamais `_cache.AddOrUpdate` direct :
  `CreateRootFolder` (PlaylistsFolder, ligne 1163), `GetItemById` (cache miss, ligne 1662),
  `CreateItems` (ligne 2343), `UpdateImagesAsync` (deux branches, lignes 2420 et 2498),
  `UpdateItemsAsync` (alternates nouvellement créées, ligne 2558).
- 1 seul appelant externe hors tests : `Reefin.Controller/Entities/UserRootFolder.cs:169`
  (`LibraryManager.RegisterItem(item)` dans `ValidateChildrenInternal`, ré-enregistrement des
  enfants après validation) — passe par le point d'entrée public, pas d'accès direct au cache.

### Invariant 2 — Invalidations centralisées

**VERT.** `grep -n "_cache" LibraryManager.cs` (avant extraction) ne trouvait que 6
occurrences : la déclaration du champ, sa construction, le `TryGet` de lecture dans
`GetItemById` (ligne 1653), et les 3 mutations dans `RegisterItemInCache` (`AddOrUpdate`,
ligne 361), `RemoveItemFromCache` et `RemoveItemsFromCache` (`TryRemove`, lignes 370/380).
Aucun autre accès direct à `_cache` dans le fichier.

### Invariant 3 — Aucun chemin de suppression ne laisse de stale entry

**ROUGE initialement, corrigé (trivial, dans le périmètre).** `DeleteItem` (ligne ~636) et
`DeleteItemsUnsafeFast` (ligne ~449) invalidaient bien le cache juste après
`_persistenceService.DeleteItem(...)`. Mais un troisième site a été trouvé par
`grep -n "_persistenceService.DeleteItem(" LibraryManager.cs` :
`ValidateTopLibraryFolders` (ligne 1470, dans la boucle de nettoyage des `CollectionFolder`
dont le dossier physique a disparu) appelait `_persistenceService.DeleteItem(toDelete.ToArray())`
**sans** invalider le cache. `CollectionFolder` est un `Folder` (donc `ShouldCacheItem` ==
`true`) : si l'item était déjà en cache, `GetItemById` aurait continué à retourner l'entrée
supprimée en base. **Corrigé** en ajoutant l'invalidation manquante (portée sur le nouveau
port, voir Phase 2) juste après la suppression.

Note distincte, **hors périmètre et non bloquante** : 4 routines de migration (
`20260508120000_MergeDuplicateMusicArtists.cs`, `20260508130000_MergeDuplicatePeople.cs`,
`20260115120000_FixIncorrectOwnerIdRelationships.cs`, référence dans
`20260525010000_CleanupOrphanedExternalData.cs`) injectent `IItemPersistenceService`
directement et appellent `DeleteItem` sans jamais passer par `LibraryManager` ni par son
cache — ni avant ni après cette PR. Ce comportement est **préservé à l'identique** par
l'extraction (le cache change de propriétaire, ces routines ne le voyaient déjà pas) ; il ne
remet donc pas en cause l'invariant d'iso-comportement visé par cette PR, mais reste une
dette pré-existante documentée ici pour traçabilité.

### Invariant 4 — Règles de cache par type couvertes

**VERT.** La suite de caractérisation PR69 (`LibraryManagerItemLookupTests`, 25 tests avant
cette PR) verrouille `ShouldCacheItem` via `CacheableItems()` (`Folder`, `Movie`,
`LiveTvChannel`, `MusicArtist`) et `NonCacheableItems()` (`Genre`, `Person` — `IItemByName`
non-`MusicArtist` — , `Audio`, `Book` — feuilles non-`Video`/`LiveTvChannel`).

### Invariant 5 — Cas root/user-root séparés

**VERT.** `RootFolder` (propriété, ligne 225) délègue à `CreateRootFolder()` qui construit
l'`AggregateFolder` via `GetItemById`/DB/`ResolvePath`/`DeepCopy` puis `RegisterItem(folder)`
pour le `PlaylistsFolder` — aucun accès direct à `_cache`. `GetUserRootFolder()` (ligne 1168)
suit le même schéma : `GetItemById(newItemId) as UserRootFolder`, fallback `ResolvePath` +
`DeepCopy<Folder, UserRootFolder>` si absent, toujours via le point d'entrée public. La
construction spéciale (verrous `_rootFolderSyncLock`/`_userRootFolderSyncLock`, DB,
`ResolvePath`, `DeepCopy`, `PlaylistsFolder`) reste intégralement dans `LibraryManager` — elle
n'a pas été déplacée.

### Verdict global Phase 1

4/5 invariants verts d'emblée ; le 5e (invariant 3) avait un trou trivialement corrigeable
dans le périmètre (une ligne, même fichier, même pattern que les deux sites déjà corrects).
Corrigé → **extraction autorisée**.

## Phase 2 — extraction réalisée

### Architecture retenue

- **`Reefin.Server.Core/Library/ItemLookupService.cs`** (nouveau, public) : possède le
  `FastConcurrentLru<Guid, BaseItem>` (même construction :
  `configurationManager.Configuration.CacheSize`), le read-through via
  `IItemRepository.RetrieveItem`, la politique `ShouldCacheItem` (déplacée telle quelle), la
  visibilité `ItemIsVisible` (déplacée telle quelle, y compris l'exception
  `item is UserRootFolder`), et les 3 variantes `GetItemById` de `IItemLookupService`.
  Dépendances : uniquement `IItemRepository` + `IServerConfigurationManager` — **aucune**
  dépendance vers `ILibraryManager` (vérifié : constructeur à 2 paramètres, pas de cycle).
- **`Reefin.Server.Core/Library/IItemCacheStore.cs`** (nouveau, `internal`) : port de
  lifecycle distinct de `IItemLookupService` — `Register(BaseItem)`, `Remove(Guid)`,
  `RemoveRange(IEnumerable<Guid>)`. `ItemLookupService` implémente les deux interfaces.
  **Visibilité `internal`** : `LibraryManager` vit dans le même assembly
  (`Reefin.Server.Core`), et `Reefin.Server.Core/Properties/AssemblyInfo.cs` déclare déjà
  `[assembly: InternalsVisibleTo("Reefin.Server.Implementations.Tests")]` — le projet de test
  y a donc accès sans avoir à rendre le port public. Choix documenté explicitement dans le
  fichier (remarks XML doc).
- **DI (`ApplicationHost.cs`)** : remplacement du double-singleton PR71
  (`AddSingleton<ILibraryManager, LibraryManager>()` +
  `AddSingleton<IItemLookupService>(sp => (IItemLookupService)sp.GetRequiredService<ILibraryManager>())`)
  par un triple-singleton pointant vers **une seule** instance concrète :
  ```csharp
  serviceCollection.AddSingleton<ItemLookupService>();
  serviceCollection.AddSingleton<IItemLookupService>(sp => sp.GetRequiredService<ItemLookupService>());
  serviceCollection.AddSingleton<IItemCacheStore>(sp => sp.GetRequiredService<ItemLookupService>());
  serviceCollection.AddSingleton<ILibraryManager, LibraryManager>();
  ```
- **`LibraryManager`** : `_cache`, `ShouldCacheItem`, `RegisterItemInCache`,
  `RemoveItemFromCache`, `RemoveItemsFromCache`, `ItemIsVisible` supprimés. Nouveau champ
  `_itemLookupService` (type concret `ItemLookupService`, +1 paramètre constructeur en fin de
  liste pour limiter le diff) — un seul objet injecté sert à la fois de lecture
  (`IItemLookupService`) et de lifecycle (`IItemCacheStore`), conformément au double rôle de
  `ItemLookupService`. `RegisterItem` délègue à `_itemLookupService.Register`. `GetItemById`
  (3 variantes `IItemLookupService`) délèguent intégralement. `GetItemById<T>(Guid, Guid
  userId)` reste sur `LibraryManager` (résolution `IUserManager` puis délégation à la variante
  `(id, User?)`) — hors périmètre `IItemLookupService` par design (PR71). `DeleteItem` et
  `DeleteItemsUnsafeFast` invalident via `_itemLookupService.Remove`/`RemoveRange`.
  `ValidateTopLibraryFolders` (ligne ~1470) invalide désormais aussi via
  `_itemLookupService.RemoveRange(toDelete)` (fix de l'invariant 3). `LibraryManager` reste
  `IItemLookupService` en délégation pure (nécessaire pour les appels `this` introduits en
  PR73, ex. `item.GetOwner(this)`). `RootFolder`/`GetUserRootFolder`/`CreateRootFolder`
  inchangés en substance (toujours via `GetItemById`/`RegisterItem`).

### Plomberie de test

`tests/Reefin.Server.Implementations.Tests/Library/LibraryManager/LibraryManagerItemLookupTests.cs` :
le fixture AutoFixture/AutoMoq construit désormais une **vraie** instance `ItemLookupService`
(pas un mock) à partir des mocks déjà gelés (`Mock<IItemRepository>`,
`Mock<IServerConfigurationManager>`), puis l'enregistre pour le type concret et pour
`IItemLookupService` (`fixture.Register`) afin que `LibraryManager` et les tests partagent le
même cache réel — reproduisant le câblage single-instance d'`ApplicationHost`. Aucune
assertion des 25 tests existants n'a été affaiblie ; seule la construction a changé. 2 tests
ajoutés (26e et 27e) :
- `ItemLookupService_Standalone_CacheMissThenHit_ReadsThroughOnceThenServesFromCache` — le
  service seul, sans `LibraryManager`, fait bien le read-through puis sert depuis le cache.
- `GetItemById_LibraryManagerAndItemLookupService_ReturnSameInstanceFromSameCache` —
  `LibraryManager.GetItemById` et `ItemLookupService.GetItemById` retournent la même instance
  d'objet (même cache, pas une copie indépendante).

## Résultats de vérification

- `dotnet build Reefin.sln` : 41 projets, 0 erreur.
- `dotnet test tests/Reefin.Controller.Tests` : 243 réussis.
- `dotnet test tests/Reefin.Server.Implementations.Tests --filter LibraryManagerItemLookupTests` :
  27 réussis (25 existants + 2 nouveaux).
- `dotnet test tests/Reefin.Server.Implementations.Tests` (suite complète) : 629 réussis, 4
  ignorés (Windows-only, tolérés).
- `dotnet test tests/Reefin.Api.Tests` : 89 réussis.

## Écarts et choix notables

- Le port interne s'appelle `IItemCacheStore` (nom retenu parmi les suggestions de la tâche).
- Visibilité `internal` conservée (pas de fuite publique) grâce à `InternalsVisibleTo` déjà en
  place pour `Reefin.Server.Implementations.Tests`.
- `LibraryManager.RetrieveItem(Guid)` (méthode publique `ILibraryManager`, passthrough brut
  non caché vers `IItemRepository`, distincte du `GetItemById` caché) est restée inchangée sur
  `LibraryManager` : elle n'appartient pas au périmètre de `IItemLookupService` (qui est
  caché par construction) et n'a aucun appelant externe hors tests.
- Le bug de l'invariant 3 (`ValidateTopLibraryFolders`) a été corrigé directement dans le code
  déplacé plutôt que corrigé puis déplacé en deux commits séparés — un seul commit couvre
  audit + fix + extraction.
