# PR76 Audit et clôture — lookup v1

Scope : audit de clôture **plus durcissement** (contrairement à la première passe de cet audit,
cette PR modifie bien la production — cf. « Durcissement PR76 » plus bas). Objectifs :
(1) vérifier, preuve à l'appui (`file:line` + test), que les 10 chemins d'exécution du cache
d'items introduit par PR69-PR75 se comportent tous correctement après l'extraction PR75 vers
`Reefin.Server.Core/Library/ItemLookupService.cs` ; (2) verrouiller l'architecture cible
(`ItemLookupService` internal sealed, `LibraryManager` dépendant des deux ports d'interface,
gardes par tests) ; (3) classifier tous les appels directs à `IItemPersistenceService.DeleteItem` ;
(4) inventorier le périmètre d'ouverture du prochain cycle.

Méthode : lecture intégrale des sites pertinents de `LibraryManager.cs`, `ItemLookupService.cs`,
`BaseItem.cs`, `UserRootFolder.cs`, `ApplicationHost.cs`, des 4 routines de migration, et de la
suite de tests `LibraryManagerItemLookupTests.cs`/`BaseItemLookupServiceTests.cs`, complétée par
des `grep` ciblés (`_cache`, `RegisterItem(`, `new FastConcurrentLru`, `DeleteItem(`,
`LibraryManager.`).

## Verdict des 10 chemins

| # | Chemin | Verdict |
|---|--------|---------|
| 1 | Cache hit | VERT |
| 2 | Cache miss / read-through | VERT |
| 3 | Item non cacheable | VERT |
| 4 | Visibilité utilisateur | VERT |
| 5 | Parent (statique vs lookup) | VERT (test ajouté) |
| 6 | Owner (statique vs lookup) | VERT (test ajouté) |
| 7 | Suppression normale (DeleteItem) | VERT (item testé ; enfants non testés, gap documenté) |
| 8 | Suppression rapide (DeleteItemsUnsafeFast) | VERT |
| 9 | Root / user-root | VERT (par construction, non testé unitairement) |
| 10 | Cache unique partagé (plugins statiques) | VERT |

Détail de chaque chemin ci-dessous.

### 1. Cache hit — GetItemById via ItemLookupService, pas d'appel repo

`ItemLookupService.GetItemById(Guid)` (`Reefin.Server.Core/Library/ItemLookupService.cs:48-68`) :
`_cache.TryGet(id, out var item)` (ligne 55) retourne directement si présent, sans toucher
`_itemRepository`.

**Test** : `GetItemById_ItemPreRegisteredInCache_DoesNotCallRepository`
(`tests/Reefin.Server.Implementations.Tests/Library/LibraryManager/LibraryManagerItemLookupTests.cs:106-116`)
— enregistre puis relit, vérifie `_itemRepositoryMock.Verify(..., Times.Never)`.

### 2. Cache miss — read-through RetrieveItem + enregistrement si cacheable

`ItemLookupService.cs:60-65` : `item = _itemRepository.RetrieveItem(id)` puis
`if (item is not null) Register(item)`. `Register` (ligne 92-100) filtre par `ShouldCacheItem`
avant d'écrire dans `_cache`.

**Tests** : `GetItemById_CacheMissCacheableItem_CallsRepositoryOnce` (lignes 118-128) et
`GetItemById_CacheableTypeSecondCall_RepositoryCalledOnlyOnce` (théorie, lignes 141-153, sur
`CacheableItems()` : `Folder`, `Movie`, `LiveTvChannel`, `MusicArtist`).

### 3. Item non cacheable — relu à chaque fois

`ShouldCacheItem` (`ItemLookupService.cs:122-135`) exclut `IItemByName` non-`MusicArtist` et les
feuilles non-`Video`/`LiveTvChannel`. Comme `Register` ne fait rien pour ces items (ligne 96-99,
condition `if (ShouldCacheItem(item))` jamais vraie), chaque `GetItemById` déclenche un nouveau
`RetrieveItem`.

**Test** : `GetItemById_NonCacheableType_RepositoryCalledOnEveryLookup` (théorie, lignes 170-180,
sur `NonCacheableItems()` : `Genre`, `Person`, `Audio`, `Book`) — vérifie
`Times.Exactly(2)` sur deux appels consécutifs.

### 4. Visibilité utilisateur — GetItemById<T>(id, user), ItemIsVisible dans ItemLookupService

`ItemLookupService.GetItemById<T>(Guid, User?)` (lignes 84-89) délègue à `ItemIsVisible`
(lignes 142-155) : `item is UserRootFolder || item.IsVisibleStandalone(user)`.

**Tests** : `GetItemByIdGeneric_UserNull_AlwaysReturnsItem`,
`GetItemByIdGeneric_ItemNotFound_ReturnsNull`,
`GetItemByIdGeneric_VisibleItemNoTagRestrictions_ReturnsItem`,
`GetItemByIdGeneric_InvisibleViaBlockedTag_ReturnsNull` (lignes 201-257) et
`GetItemByIdGeneric_UserRootFolderWithUser_AlwaysReturnsItem` (lignes 263-277, exception
`UserRootFolder`).

### 5. Parent — BaseItem.GetParent(lookup) + GetParent() statique résolvent pareil

`BaseItem.GetParent()` (`Reefin.Controller/Entities/BaseItem.cs:1014-1023`) passe par le
statique `LibraryManager.GetItemById(parentId)` (type `ILibraryManager`, résolu via
`ApplicationHost.SetStaticProperties`, `Reefin.Server.Core/ApplicationHost.cs:689`) →
`LibraryManager.GetItemById` (`Reefin.Server.Core/Library/LibraryManager.cs:1607-1610`) délègue
intégralement à `_itemLookupService.GetItemById`. `BaseItem.GetParent(IItemLookupService lookup)`
(`BaseItem.cs:1031-1042`, introduit PR72) appelle `lookup.GetItemById(parentId)` directement. Les
deux chemins retombent sur la même instance `ItemLookupService` en production (DI, cf. point 10),
donc résolvent la même entrée de cache.

**Test avant cet audit** : `BaseItemLookupServiceTests` (`tests/Reefin.Controller.Tests/Entities/BaseItemLookupServiceTests.cs`)
verrouillait que `GetParent(lookup)` n'appelle jamais le statique (mock strict), mais aucun test
n'exerçait les *deux* chemins ensemble sur un même cache pour prouver qu'ils retournent la même
instance. **Gap comblé** : test ajouté,
`GetParent_StaticAndLookupOverload_ResolveSameInstanceFromSameCache`
(`LibraryManagerItemLookupTests.cs`, nouvelle section 15) — enregistre un parent via
`_libraryManager.RegisterItem`, pointe `BaseItem.LibraryManager` sur `_libraryManager`, appelle
`child.GetParent()` (statique) et `child.GetParent(_itemLookupService)` (lookup), et vérifie
`Assert.Same` entre les deux résultats et zéro appel repo.

### 6. Owner — idem GetOwner

Même schéma : `BaseItem.GetOwner()` (`BaseItem.cs:849-853`) vs `BaseItem.GetOwner(IItemLookupService lookup)`
(`BaseItem.cs:861-867`).

**Gap comblé** : test ajouté, `GetOwner_StaticAndLookupOverload_ResolveSameInstanceFromSameCache`
(`LibraryManagerItemLookupTests.cs`, section 15), même schéma que le point 5.

### 7. Suppression normale — DeleteItem invalide item + enfants via IItemCacheStore

`LibraryManager.DeleteItem(BaseItem, DeleteOptions, BaseItem, bool)`
(`LibraryManager.cs:412-607`) : `children = item.IsFolder ? ((Folder)item).GetRecursiveChildren(false) : []`
(ligne 545-547), puis après `_persistenceService.DeleteItem(...)` (ligne 596) :
`_itemCacheStore.Remove(item.Id)` (ligne 597) et
`_itemCacheStore.RemoveRange(children.Select(child => child.Id))` (ligne 598).

**Test (item seul)** : `DeleteItem_CacheableItem_InvalidatesCache`
(`LibraryManagerItemLookupTests.cs:319-341`) — enregistre, supprime, revérifie miss + appel
repo unique.

**Gap non comblé (documenté, pas trivial)** : la branche `children` (ligne 598,
`Folder.GetRecursiveChildren` → `RemoveRange`) n'est exercée par aucun test `DeleteItem`. La
primitive `RemoveRange` elle-même est verrouillée indirectement par
`DeleteItemsUnsafeFast_CacheableItem_InvalidatesCache` (ligne 347-364), mais pas le câblage
spécifique "dossier avec enfants réels → `GetRecursiveChildren` → `RemoveRange`" dans
`DeleteItem`. Construire ce test demande de faire fonctionner `Folder.GetRecursiveChildren`, qui
s'appuie sur la machinerie de requête d'items (`ItemQueryService`/repository), non trivial à
mocker proprement dans ce fixture AutoFixture/AutoMoq existant — non ajouté, conformément à la
consigne "ajoute le test si simple".

### 8. Suppression rapide — DeleteItemsUnsafeFast invalide tous les items

`LibraryManager.DeleteItemsUnsafeFast` (`LibraryManager.cs:358-410`) :
`_persistenceService.DeleteItem([.. pathMaps.Select(f => f.Item.Id)])` (ligne 408) puis
`_itemCacheStore.RemoveRange(pathMaps.Select(f => f.Item.Id))` (ligne 409) — invalide tous
les items passés, pas seulement le premier.

**Test** : `DeleteItemsUnsafeFast_CacheableItem_InvalidatesCache`
(`LibraryManagerItemLookupTests.cs:347-364`).

### 9. Root et user-root — construction via ItemLookupService, pas de cache résiduel

Preuve par construction : `grep -n "_cache" Reefin.Server.Core/Library/LibraryManager.cs` → **0
résultat**. `LibraryManager` n'a plus aucun champ ni référence `_cache` depuis l'extraction PR75
— toute lecture/écriture passe obligatoirement par `_itemLookupService`/`_itemCacheStore`.

`CreateRootFolder()` (`LibraryManager.cs:1076-1126`) : `GetItemById(GetNewItemId(...))` (ligne
1080, délègue), fallback `ResolvePath(...).DeepCopy<Folder, AggregateFolder>()` si absent en
cache, puis `RegisterItem(folder)` (ligne 1123, uniquement pour le `PlaylistsFolder` — la
racine elle-même n'est pas ré-enregistrée si nouvellement construite ; comportement préexistant,
inchangé par PR75, hors périmètre de cet audit). `GetUserRootFolder()`
(`LibraryManager.cs:1127-1172`) : même schéma, `GetItemById(newItemId) as UserRootFolder` (ligne
1144) puis fallback `DeepCopy` sans appel `_cache` direct.

**Non testé unitairement** : aucun test n'exerce `CreateRootFolder`/`GetUserRootFolder`
directement (ils touchent le système de fichiers réel — `Directory.CreateDirectory`,
`_fileSystem.GetDirectoryInfo`, `ResolvePath`) ; le seul test touchant `UserRootFolder` dans la
suite (`GetItemByIdGeneric_UserRootFolderWithUser_AlwaysReturnsItem`) exerce la règle de
visibilité, pas la construction root. Non ajouté : la machinerie I/O nécessaire dépasse le
"test simple" visé par la consigne. Le verdict VERT repose donc sur l'absence de `_cache`
résiduel (preuve par construction ci-dessus) et la relecture de code, pas sur un test dédié —
cohérent avec le verdict "VERT" déjà posé par l'audit PR75 (invariant 5) sur la même base.

### 10. Cache unique partagé entre LibraryManager et ItemLookupService (plugins statiques)

`grep -rn "new FastConcurrentLru" **/*.cs` → 4 résultats au total dans le repo, dont **un seul**
concerne le cache d'items : `ItemLookupService.cs:44`
(`FastConcurrentLru<Guid, BaseItem>`). Les 3 autres sont des caches non liés, avec des types
clé/valeur différents : `DotIgnoreIgnoreRule.cs:29` et `:33` (`FastConcurrentLru<string, ...>`,
cache de règles `.ignore`) et `UserDataManager.cs:48`
(`FastConcurrentLru<string, UserItemData>`, cache de données utilisateur). L'invariant visé
("un seul cache d'items, jamais un second") tient : zéro autre `FastConcurrentLru<Guid, BaseItem>`
dans le repo.

DI (`Reefin.Server.Core/ApplicationHost.cs:561-564`) :
```csharp
serviceCollection.AddSingleton<ItemLookupService>();
serviceCollection.AddSingleton<IItemLookupService>(sp => sp.GetRequiredService<ItemLookupService>());
serviceCollection.AddSingleton<IItemCacheStore>(sp => sp.GetRequiredService<ItemLookupService>());
serviceCollection.AddSingleton<ILibraryManager, LibraryManager>();
```
Un seul singleton `ItemLookupService` concret, exposé sous 3 types. `LibraryManager` reçoit ce
même singleton par injection de constructeur — depuis le durcissement PR76, sous la forme des
deux ports d'interface `IItemLookupService` + `IItemCacheStore`
(`LibraryManager.cs:94-95,176-177`), plus aucun champ de type concret. Le câblage est verrouillé
par le test DI décrit dans « Durcissement PR76 ».

`BaseItem.LibraryManager` (statique, type `ILibraryManager`) est assigné une seule fois au
démarrage par `ApplicationHost.SetStaticProperties()` (`ApplicationHost.cs:689`,
`BaseItem.LibraryManager = Resolve<ILibraryManager>()`) — la même instance `LibraryManager`
résolue par le conteneur DI, donc le même singleton `ItemLookupService` derrière les deux ports. Un plugin qui n'utilise que le
statique (`BaseItem.LibraryManager.GetItemById(...)`) et un service DI qui injecte
`IItemLookupService` directement voient donc rigoureusement le même cache.

**Tests** : `LibraryManager_IsAssignableToIItemLookupService`,
`GetItemById_ViaIItemLookupServiceReference_SameCacheBehaviorAndInstanceAsILibraryManager`
(lignes 370-390), et surtout
`GetItemById_LibraryManagerAndItemLookupService_ReturnSameInstanceFromSameCache`
(lignes 467-482) — instancie `_libraryManager` et `_itemLookupService` séparément dans le
fixture de test (même câblage qu'`ApplicationHost`) et vérifie `Assert.Same` entre les deux
résultats.

## Points additionnels

### Cohérence RegisterItem externe — UserRootFolder.cs:169

`UserRootFolder.ValidateChildrenInternal` (`Reefin.Controller/Entities/UserRootFolder.cs:152-171`) :
```csharp
foreach (var item in Children)
{
    LibraryManager.RegisterItem(item);
}
```
`LibraryManager` ici est le statique `BaseItem.LibraryManager` (type `ILibraryManager`) →
`LibraryManager.RegisterItem` (`Reefin.Server.Core/Library/LibraryManager.cs:337-342`) →
`_itemCacheStore.Register(item)` — atterrit bien dans le cache unique décrit au point 10, pas
dans un cache parallèle. Confirmé par construction (même chemin que tout autre appelant
`ILibraryManager.RegisterItem`).

### Trous connus documentés (PR75) — 4 routines de migration sans invalidation

Toujours présents, comportement inchangé (vérifié par relecture directe des 4 fichiers) :

- `Reefin.Server/Migrations/Routines/20260508120000_MergeDuplicateMusicArtists.cs:208` —
  `_persistenceService.DeleteItem(unresolvedIds)`.
- `Reefin.Server/Migrations/Routines/20260508130000_MergeDuplicatePeople.cs:210` — idem.
- `Reefin.Server/Migrations/Routines/20260115120000_FixIncorrectOwnerIdRelationships.cs:166` —
  idem.
- `Reefin.Server/Migrations/Routines/20260525010000_CleanupOrphanedExternalData.cs:20` —
  référence en commentaire au même pattern (`IItemPersistenceService.DeleteItem` sans
  invalidation), pas d'appel direct dans ce fichier.

Ces 4 routines injectent `IItemPersistenceService` directement par constructeur et n'ont jamais
vu `ILibraryManager`, `IItemLookupService` ni `IItemCacheStore` — ni avant PR69, ni après PR75.
L'extraction n'a fait que changer le *propriétaire* du cache (`LibraryManager` → `ItemLookupService`),
pas la visibilité de ces routines sur lui : comportement rigoureusement iso.

**Risque évalué — cache froid au moment des migrations, plausible mais non prouvé par
instrumentation directe.** Preuve indirecte par la séquence de démarrage
(`Reefin.Server/Program.cs:207-219`) : `ReefinMigrationService.MigrateStepAsync(CoreInitialisation, ...)`
s'exécute avant `appHost.InitializeServices(...)` (ligne 212 puis 215), et
`MigrateStepAsync(AppInitialisation, ...)` s'exécute juste après `InitializeServices` mais avant
`_reefinHost.StartAsync()` qui démarre Kestrel (lignes 218 et 227). `ReefinMigrationStageTypes.AppInitialisation`
est documenté comme « Last step before running the server »
(`Reefin.Server/Migrations/Stages/ReefinMigrationStageTypes.cs:20-23`). Autrement dit : aucune
requête HTTP ni tâche de scan de bibliothèque ne peut avoir peuplé `ItemLookupService._cache`
avant que les migrations ne s'exécutent, puisque le serveur n'accepte pas encore de connexions à
ce stade. Le cache est donc *très probablement* froid ou quasi-froid à l'exécution de ces 4
routines — mais ceci n'a pas été vérifié par instrumentation/log runtime, seulement par lecture
de l'ordonnancement du code de démarrage ; à confirmer si le risque devient bloquant pour une PR
future qui toucherait ces routines. Le risque résiduel (item supprimé en base mais laissé en
cache si un caller avait déjà peuplé l'entrée avant la migration, scénario peu probable au
démarrage) reste non nul mais faible, et hors du périmètre "iso-comportement" de cette série.

## Durcissement PR76 — modifications de production

L'audit des 10 chemins n'a révélé aucun trou fonctionnel (tous VERT), mais la clôture du cycle
verrouille l'architecture par trois modifications de production :

1. **`ItemLookupService` passe `internal sealed`**
   (`Reefin.Server.Core/Library/ItemLookupService.cs:31`). Hors de l'assembly, le service n'est
   plus atteignable que via `IItemLookupService` (lectures) ; dans l'assembly, le cycle de vie
   passe par `IItemCacheStore`. Garde par test :
   `ItemLookupService_ConcreteType_StaysInternalSealed`
   (`LibraryManagerItemLookupTests.cs:559`, `Assert.False(IsVisible)`, `Assert.False(IsPublic)`,
   `Assert.True(IsSealed)`) — le type concret ne peut pas redevenir public sans casser la suite.

2. **`LibraryManager` dépend des deux interfaces au lieu du concret**
   (`Reefin.Server.Core/Library/LibraryManager.cs:94-95,176-177`) : le champ concret
   `ItemLookupService _itemLookupService` est remplacé par `IItemLookupService _itemLookupService`
   (lectures : `GetItemById` ×3, lignes 1607-1641) et `IItemCacheStore _itemCacheStore`
   (cycle de vie : `Register` ligne 341, `RemoveRange` lignes 409/598/1431, `Remove` ligne 597).
   Chaque usage est routé vers le bon port — plus aucun site n'appelle le concret.

3. **`LibraryManager` passe `internal`** (`LibraryManager.cs:66`). Conséquence mécanique du
   point 2 : `IItemCacheStore` est internal, donc un constructeur public d'une classe publique
   ne peut pas le recevoir (CS0051). Vérifié avant le changement : aucun assembly hors
   Reefin.Server.Core ne référence le type concret `LibraryManager` — tous les consommateurs
   passent par `ILibraryManager`/`IItemLookupService` ; seuls les tests utilisent le concret,
   couverts par `InternalsVisibleTo("Reefin.Server.Implementations.Tests")`
   (`Reefin.Server.Core/Properties/AssemblyInfo.cs:18`, déjà en place, rien à ajouter).

La DI d'`ApplicationHost` (`ApplicationHost.cs:561-564`) est inchangée : le câblage
« un singleton concret, exposé sous `IItemLookupService` + `IItemCacheStore` » résout désormais
les deux paramètres d'interface du constructeur de `LibraryManager`. Ce câblage est verrouillé
par un nouveau test DI, `DiWiring_ApplicationHostStyleRegistration_BothPortsResolveSameSingleton`
(`LibraryManagerItemLookupTests.cs:536`) : un `ServiceCollection` reproduisant exactement les
trois enregistrements vérifie `Assert.Same` entre `IItemLookupService` et `IItemCacheStore`
résolus.

Fixtures adaptés : `LibraryManagerItemLookupTests` enregistre l'instance réelle sous
`IItemCacheStore` en plus des deux types existants ; `FindExtrasTests` construit désormais une
vraie `ItemLookupService` (Moq ne peut pas proxyfier une interface internal sans
`InternalsVisibleTo("DynamicProxyGenAssembly2")`, volontairement non ajouté). `BaseItemKindTests`
exclut les assemblies `*.Tests` de son scan réflexif des descendants de `BaseItem` (le contrat
`BaseItemKind` ne couvre que les types de production ; les nouveaux stubs de test du point
suivant le déclenchaient).

### Gap comblé — test d'invalidation dans ValidateTopLibraryFolders (correctif PR75)

Le correctif découvert en PR75 (un `CollectionFolder` dont le répertoire a disparu est supprimé
en base **et** invalidé du cache, `LibraryManager.cs:1428-1432`) n'avait aucun test. Ajouté :
`ValidateTopLibraryFolders_MissingCollectionFolderDirectory_InvalidatesCacheEntry`
(`LibraryManagerItemLookupTests.cs:577`). La machinerie lourde (construction des roots, refresh
métadonnées, validation des enfants) est neutralisée par deux stubs (`StubAggregateFolder`,
`StubUserRootFolder`) injectés par réflexion dans les champs privés `_rootFolder`/
`_userRootFolder`, avec `ValidateChildrenInternal`/`Children` surchargés en no-op et les statics
`BaseItem` (Logger/FileSystem/ProviderManager) pointés sur des mocks — ce qui reste sous test est
exactement la boucle de nettoyage : suppression en base (`DeleteItem` vérifié `Times.Once` avec
le bon id) puis miss de cache en relecture (`RetrieveItem` appelé, retour `null`).

## Classification des appels directs à IItemPersistenceService.DeleteItem

Grep exhaustif (`\.DeleteItem(` croisé avec les sites d'injection d'`IItemPersistenceService`,
hors tests) : 6 appels directs dans le repo, tous classés. Catégories : (a) exécution
strictement pré-cache (migrations démarrage), (b) IDs nécessairement absents du cache,
(c) invalidation requise.

| Appelant | file:line | Catégorie | Verdict |
|---|---|---|---|
| `LibraryManager.DeleteItemsUnsafeFast` | `LibraryManager.cs:408` | (c) | VERT — `_itemCacheStore.RemoveRange` ligne 409 |
| `LibraryManager.DeleteItem` | `LibraryManager.cs:596` | (c) | VERT — `Remove`/`RemoveRange` lignes 597-598 |
| `LibraryManager.ValidateTopLibraryFolders` | `LibraryManager.cs:1430` | (c) | VERT — `RemoveRange` ligne 1431 (correctif PR75, désormais testé) |
| `MergeDuplicateMusicArtists` | `Routines/20260508120000_...cs:208` | (a) | Toléré — stage de migration pré-Kestrel, cache froid (cf. analyse démarrage plus haut) |
| `MergeDuplicatePeople` | `Routines/20260508130000_...cs:210` | (a) | Toléré — idem |
| `FixIncorrectOwnerIdRelationships` | `Routines/20260115120000_...cs:166` | (a) | Toléré — idem |

(`20260525010000_CleanupOrphanedExternalData.cs:20` ne contient qu'une référence en commentaire,
pas d'appel.) Aucun appelant de catégorie (b) identifié, et surtout **aucun appelant runtime de
catégorie (c) sans invalidation** : les trois sites (c) passent tous par `IItemCacheStore` juste
après la suppression. Critère de clôture atteint — aucune suppression runtime connue ne
contourne le lifecycle du cache. Tous les autres `DeleteItem` du repo (PlaylistManager,
CleanDatabaseScheduledTask, RecordingsManager, GuideManager, ChannelManager/PostScanTask,
Video/Folder/BaseItem, LiveTvController, LibraryController, MigrateLinkedChildren) passent par
`ILibraryManager.DeleteItem` — donc par le chemin invalidant de `LibraryManager.DeleteItem` — ou
par l'API channel (hors cache d'items).

## Prochaine famille — statics BaseItem.LibraryManager (Season, Episode, CollectionFolder, AggregateFolder, Video)

Inventaire brut (`grep -n "LibraryManager\." <fichier>`), sans migration — périmètre d'ouverture
du cycle suivant.

### `Reefin.Controller/Entities/TV/Season.cs` — 1 appel réel

- `Season.cs:65` — `LibraryManager.GetItemById(seriesId) as Series` (propriété `Series`).
- (lignes 246, 281 : commentaires XML/inline mentionnant la façade obsolète
  `LibraryManager.Sort`, pas des appels réels — exclus du compte.)

### `Reefin.Controller/Entities/TV/Episode.cs` — 3 appels

- `Episode.cs:86` — `LibraryManager.GetItemById(seriesId) as Series` (propriété `Series`).
- `Episode.cs:101` — `LibraryManager.GetItemById(seasonId) as Season` (propriété `Season`).
- `Episode.cs:337` — `LibraryManager.GetLibraryOptions(this)`.

### `Reefin.Controller/Entities/CollectionFolder.cs` — 3 appels

- `CollectionFolder.cs:363` — `LibraryManager.GetItemById(i)` (via `.Select`, résolution de
  `PhysicalFolderIds`).
- `CollectionFolder.cs:366` — `LibraryManager.RootFolder.Children` (propriété, pas
  `GetItemById`).
- `CollectionFolder.cs:384` — `LibraryManager.FindByPath(path, true)`.

### `Reefin.Controller/Entities/AggregateFolder.cs` — 2 appels

- `AggregateFolder.cs:78` — `_childrenIds.Select(LibraryManager.GetItemById)` (group de
  résolution des enfants).
- `AggregateFolder.cs:139` — `LibraryManager.NormalizeRootPathList(files)`.

### `Reefin.Controller/Entities/Video.cs` — 21 appels

- `Video.cs:165` — `LibraryManager.GetLocalAlternateVersionIds` (propriété
  `HasLocalAlternateVersions`, appel implicite via `.Any()` ou équivalent).
- `Video.cs:260` — `LibraryManager.GetItemById(PrimaryVersionId.Value)`.
- `Video.cs:266` — `LibraryManager.GetLinkedAlternateVersions(video)`.
- `Video.cs:267` — `LibraryManager.GetLocalAlternateVersionIds(video)`.
- `Video.cs:277` — `LibraryManager.GetLinkedAlternateVersions(this)`.
- `Video.cs:278` — `LibraryManager.GetLocalAlternateVersionIds(this)`.
- `Video.cs:471` — `LibraryManager.GetNewItemId(i, typeof(Video))` (via `.Select`).
- `Video.cs:495` — `LibraryManager.GetItemById<Video>(i, user)` (via `.Select`).
- `Video.cs:557` — `LibraryManager.GetLocalAlternateVersionIds(this)`.
- `Video.cs:581` — `LibraryManager.GetNewItemId(path, GetType())`.
- `Video.cs:582` — `LibraryManager.GetItemById(id)`.
- `Video.cs:585` — `LibraryManager.GetContentType(this)`.
- `Video.cs:586` — `LibraryManager.ResolveAlternateVersion(path, GetType(), parent...)`.
- `Video.cs:591` — `LibraryManager.CreateItem(altVideo, GetParent())`.
- `Video.cs:600` — `LibraryManager.GetItemById(id)`.
- `Video.cs:602` — `LibraryManager.UpsertLinkedChild(Id, video.Id, LinkedChildType.LocalAlternate...)`.
- `Video.cs:632` — `LibraryManager.GetNewItemId(path, itemType)`.
- `Video.cs:638` — `LibraryManager.GetItemById(id)`.
- `Video.cs:641` — `LibraryManager.DeleteItem(orphanedVideo, new DeleteOptions {...})`.
- `Video.cs:647` — `LibraryManager.GetItemById(id)`.
- `Video.cs:650` — `LibraryManager.GetContentType(this)`.
- `Video.cs:651` — `LibraryManager.ResolvePath(...)`.
- `Video.cs:674` — `LibraryManager.CreateItem(video, parentFolder)`.
- `Video.cs:691` — `LibraryManager.GetLocalAlternateVersionIds(this)` (via `.SelectMany`).
- `Video.cs:692` — `LibraryManager.GetItemById` (via `.Select`, référence de méthode).
- `Video.cs:757` — `LibraryManager.GetItemById(PrimaryVersionId.Value) as Video`.
- `Video.cs:762` — `LibraryManager.GetLinkedAlternateVersions(primary)`.
- `Video.cs:773` — `LibraryManager.GetLinkedAlternateVersions(this)` (via `.Concat`).
- `Video.cs:783` — `LibraryManager.GetLocalAlternateVersionIds` (via `.SelectMany`, référence de
  méthode).
- `Video.cs:784` — `LibraryManager.GetItemById` (via `.Select`, référence de méthode).

(Note : `Video.cs` mélange largement `GetItemById` et des méthodes hors périmètre
`IItemLookupService` — `GetContentType`, `ResolveAlternateVersion`, `CreateItem`, `DeleteItem`,
`UpsertLinkedChild`, `NormalizeRootPathList`, `GetNewItemId`, `ResolvePath`,
`GetLinkedAlternateVersions`, `GetLocalAlternateVersionIds` — donc pas un candidat direct pour
une migration `IItemLookupService` simple ; à traiter au cas par cas si le prochain cycle s'y
intéresse.)

### Compte total par fichier

| Fichier | Appels statiques `LibraryManager.` |
|---|---|
| `Season.cs` | 1 |
| `Episode.cs` | 3 |
| `CollectionFolder.cs` | 3 |
| `AggregateFolder.cs` | 2 |
| `Video.cs` | 21 (dont ~9 `GetItemById`, le reste hors périmètre `IItemLookupService`) |

Aucune migration effectuée sur ces 5 fichiers dans cette PR — inventaire seul, conformément à la
consigne.

## État CI

`.github/workflows/ci-tests.yml` existe déjà et couvre le besoin : job `run-tests`, matrice
`ubuntu-latest`/`macos-latest`/`windows-latest`, déclenché sur `push` vers `master` et sur tout
`pull_request`, exécute `dotnet test Reefin.sln --configuration Release ...` — la solution
complète, qui inclut `Reefin.Controller.Tests` et `Reefin.Server.Implementations.Tests` (confirmé :
`grep -c "Reefin.Controller.Tests\|Reefin.Server.Implementations.Tests" Reefin.sln` → 2). `dotnet
test` implique un build préalable de tous les projets référencés. **Aucun nouveau workflow
ajouté** — le besoin exprimé par la tâche (build + tests Controller + tests Server
Implementations sur push/PR) est déjà couvert.

**Constat runtime GitHub (PR76, vérifié via `gh api repos/all3f0r1/reefin/commits/a2586bf2a2/check-runs`)** :
le gate se déclenche réellement — 10 check-runs sur le commit a2586bf2a2, dont les 3
`run-tests (ubuntu/macos/windows-latest)`. **Mais aucun job ne démarre** : l'annotation d'échec
est « The job was not started because recent account payments have failed or your spending limit
needs to be increased » (0 step exécuté, `steps: []`). Tous les runs `Tests` récents sur
`master` (8 derniers vérifiés via `gh run list`) sont en `failure` pour la même raison — échec
chronique de facturation GitHub Actions, préexistant à la série PR69-PR76, sans rapport avec le
code. Rien à corriger côté repo ; à régler dans les réglages Billing du compte GitHub. Les
baselines de cette PR ont donc été validées localement (cf. « Vérification »).

## Vérification

```
dotnet build Reefin.sln
dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj
dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"
dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj
dotnet test tests/Reefin.Api.Tests/Reefin.Api.Tests.csproj
```

Résultats (après durcissement PR76) :

- `dotnet build Reefin.sln` : 41 projets, 0 erreur, warnings préexistants uniquement.
- `Reefin.Controller.Tests` : 243/243 réussis.
- `LibraryManagerItemLookupTests` (filtre) : 32/32 réussis (27 existants + 2 GetParent/GetOwner
  + 3 durcissement : test DI, garde reflection, ValidateTopLibraryFolders).
- `Reefin.Server.Implementations.Tests` (suite complète) : 633/637 réussis, 0 échec, 4 ignorés
  (`ManagedFileSystemTests`, Windows-only, préexistants, tolérés).
- `Reefin.Api.Tests` : 89/89 réussis.

## Écarts et choix notables

- Modifications de production (cf. « Durcissement PR76 ») : `ItemLookupService` → `internal
  sealed` ; `LibraryManager` → dépendance aux deux ports d'interface + passage `internal`
  (conséquence CS0051 de l'internalité d'`IItemCacheStore`, aucun consommateur externe du
  concret). Le passage de `LibraryManager` en `internal` n'était pas explicitement demandé —
  c'est la résolution minimale du conflit d'accessibilité, documentée dans les remarks XML de la
  classe.
- Tests ajoutés : 2 (GetParent/GetOwner statique vs lookup, points 5-6) + 3 durcissement (DI
  same-instance, garde `internal sealed`, invalidation `ValidateTopLibraryFolders`). Le gap
  d'invalidation PR75 est donc **comblé** (il était initialement pressenti comme nécessitant une
  machinerie I/O disproportionnée ; l'injection de stubs par réflexion l'a rendu testable sans
  elle).
- `BaseItemKindTests` : exclusion des assemblies `*.Tests` du scan réflexif — nécessaire pour
  que des stubs `BaseItem` privés puissent exister dans les tests sans casser le contrat
  `BaseItemKind` (qui ne couvre que la production).
- Gaps de test documentés mais non comblés (jugés non triviaux) : branche `children` de
  `DeleteItem` (point 7), construction `CreateRootFolder`/`GetUserRootFolder` (point 9) — les
  deux nécessitent une machinerie I/O ou de requête d'items hors de la portée d'un fixture de
  test simple.
- Le risque "cache froid" sur les 3 routines de migration à `DeleteItem` direct est étayé par
  l'ordonnancement du démarrage (`Program.cs`) mais pas par une mesure runtime directe — nuance
  explicitement posée plutôt qu'affirmée comme prouvée.
- CI GitHub : gate présent et déclenché mais jobs jamais démarrés (facturation du compte, cf.
  « État CI ») — validation locale faisant foi pour cette PR.
