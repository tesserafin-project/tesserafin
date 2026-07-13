# PR82 — Audit de clôture du chantier lookup / hierarchy

Clôture de la série **PR76 → PR82** (poursuite de PR69–PR75). Objectif du
chantier : donner au cache d'items un propriétaire identifiable, séparer *lookup*
(trouver un item), *access* (visibilité utilisateur) et *hierarchy* (parent /
owner / ancêtres), et sortir les consommateurs runtime du static
`BaseItem.LibraryManager` pour ces responsabilités.

## Récapitulatif des PR

| PR | Objet | Commit |
|----|-------|--------|
| PR76 | Durcir `ItemLookupService` (`internal sealed`), `LibraryManager` dépend des ports, guard réflexion, test invalidation `ValidateTopLibraryFolders` | `73afe7f` |
| PR77 | Séparer lookup / visibilité : `IItemAccessService.GetVisibleItemById` extrait d'`IItemLookupService` | `38ef624` |
| PR78 | 6 contrôleurs API hors `ILibraryManager` (lookup-only / access-only / both-ports null-routing) | `b70473c` |
| PR79 | Consommateurs centraux (SessionManager, Group+SyncPlayManager, MediaSourceManager) + champs morts (ItemQueryService, PlaylistsController) | `a89c98f` |
| PR80 | `IItemHierarchyService` (façade mince sur les overloads service-aware de `BaseItem`) | `75f4883` |
| PR81 | `Episode.GetSeries(IItemLookupService)` (seul consommateur lookup-aware migré) ; 5 relations différées | `b41d54a` |
| PR82 | Cet audit de clôture | (ce commit) |

## État vérifié

Build complet : **0 erreur**. Tests : **243** Controller, **648** Server
Implementations (644 réussis, 4 ignorés Windows-only), **89** API — **0 échec**.
Aucune régression.

## 1. Appels statiques restants à `LibraryManager.GetItemById`

**29 occurrences, toutes dans `Reefin.Controller/Entities/` — zéro dans les
services DI (`Reefin.Server.Core`, `Reefin.Api`).**

| Fichier | # | Statut |
|---------|---|--------|
| `Entities/Video.cs` | 9 | Différé — alternate versions / extras (hors périmètre, cf. PR68/PR81) |
| `Entities/BaseItem.cs` | 7 | Wrappers de compatibilité paramless (`GetParent()`, `GetOwner()`, etc.) — conservés exprès ; les overloads `(IItemLookupService)` existent à côté |
| `Entities/UserView.cs` | 4 | Différé — résolution parent dans `GetItemsInternal` (couplé au domaine query/listing) |
| `Entities/TV/Episode.cs` | 2 | `Series` (wrapper compat, overload `GetSeries(lookup)` ajouté PR81) ; `Season` (différé) |
| `Entities/Folder.cs` | 2 | Wrappers / résolution enfants (query/listing) |
| `Entities/UserRootFolder.cs` | 1 | Différé — `LoadChildren` (override signature fixe, pas d'appelant lookup-aware) |
| `Entities/TV/Season.cs` | 1 | Différé — `Series` (pas d'appelant lookup-aware) |
| `Entities/CollectionFolder.cs` | 1 | Différé — `GetPhysicalFolders` branche cache |
| `Entities/AggregateFolder.cs` | 1 | Différé — `LoadChildren` |

**Conclusion :** le static de lookup ne subsiste plus que comme *chemin de
compatibilité au niveau des entités* (qui n'ont pas de DI) et dans les domaines
explicitement différés. Aucun consommateur runtime injecté ne résout d'item via
le static.

## 2. Appels statiques restants à `GetParent()` / `GetOwner()` paramless

**12 occurrences dans les services DI**, principalement `LibraryController` (5),
puis PlaylistManager, ItemNamingService, FileRefresher, PlaylistImageProvider,
UserDataChangeNotifier, DtoService, CollectionImageProvider (1 chacun).

Ce sont des appelants qui ne détiennent pas d'`IItemLookupService`. Depuis PR80,
la migration est *mécaniquement possible* via `IItemHierarchyService`
(`GetParent`/`GetOwner`/`GetAncestors`/`FindAncestor`) : ce sont les candidats
naturels d'un futur passage, une fois qu'on décide d'injecter le port hierarchy
dans ces classes. Non bloquant pour la clôture.

## 3. Consommateurs DI restant sur `ILibraryManager`

**68 classes** conservent un champ `ILibraryManager` (vs 156 au départ, cf.
audit PR74). La grande majorité l'utilisent pour des **queries/listing**, des
**mutations** (`UpdateItem`, `DeleteItem`, `CreateItem`), des **collection
folders/options**, ou `FindByPath` — c.-à-d. des responsabilités **hors** du
périmètre lookup/hierarchy. Les usages purement lecture-par-id ont été migrés
(PR78/79) ou étaient des champs morts (supprimés PR79).

## 4. Dépendance cachée de la visibilité

`IsVisibleStandalone` (qui traverse parents / tags hérités / collection folders
via les statics de `BaseItem`) était appelé directement par 3 services hors
`ItemAccessService`. **PR82 a migré `SyncPlay/Group.HasAccessToQueue`** vers
`IItemAccessService.GetVisibleItemById<BaseItem>` (avec test-garde qui échoue si
`IsVisibleStandalone` est appelé directement). Restent **2 services** :
`EntryPoints/LibraryChangedNotifier`, `Dto/DtoService`.

PR77 a **localisé** la dépendance statique de visibilité dans `ItemAccessService`
pour le chemin `GetVisibleItemById`, mais ne l'a pas **éliminée** globalement :
ces 2 appels directs restent une dette connue. Frontière honnête : la visibilité
n'est pas encore un port unique ; c'est un chantier distinct (« visibility »),
pas une régression.

**Balayage PR78–79 (`IsVisibleStandalone` / `IsVisible` / `IsParentalAllowed`) :**
2 résidus `.IsVisible(user)` — `ItemsController:357` (item déjà résolu) et
`PlaylistsController:537` (filtre de collection `GetManageableItems`). Ce sont des
formes **filtre-de-collection**, pas des lookups par-id ; `GetVisibleItemById`
(strictement par-id) ne les couvre pas. Différés jusqu'à un port de filtrage
visible (chantier « visibility »), hors périmètre PR82.

## 5. Compatibilité plugin

- Les 3 implémentations concrètes (`ItemLookupService`, `ItemAccessService`,
  `ItemHierarchyService`) sont **`internal sealed`** : aucun producteur externe
  ne peut muter le cache ni contourner les interfaces (guard réflexion PR76).
- Les 3 ports (`IItemLookupService`, `IItemAccessService`,
  `IItemHierarchyService`) sont des **interfaces publiques additives**.
- Toutes les méthodes/propriétés historiques de `BaseItem` (`GetParent()`,
  `GetOwner()`, `GetParents()`, `FindParent<T>()`, `Episode.Series`,
  `Season.Series`, ...) sont **conservées** comme wrappers de compatibilité :
  aucune surface publique existante retirée. **Plugins non impactés.**

## 6. Statut des 5 relations différées (décision PR81 « consumed-only »)

Migratabilité décidée côté **appelant** : ces relations sont des getters de
propriété ou des overrides à signature fixe, sans `IItemLookupService` en portée.

| Relation | Blocage | Rattachement futur |
|----------|---------|--------------------|
| `Season.Series` | Aucun appelant lookup-aware | query/listing ou hierarchy |
| `Episode.Season` | Aucun appelant lookup-aware | query/listing ou hierarchy |
| `UserView` (DisplayParent/Parent) | Dans `GetItemsInternal`, pas de lookup en portée | query/listing |
| `CollectionFolder.PhysicalFolders` | Branche cache, override | collection folders/options |
| `AggregateFolder.Children` | `LoadChildren` override signature fixe | query/listing |
| `UserRootFolder.Children` | `LoadChildren` override signature fixe | query/listing |

Seul `Episode.Series` avait un appelant lookup-aware (`SessionManager`) et a été
migré (PR81).

## Critère de clôture

- **Aucun producteur externe ne peut muter le cache** — ✅ (impl `internal
  sealed`, guard réflexion).
- **Aucune suppression runtime connue ne contourne le lifecycle du cache** —
  ✅ (PR76, chemins de suppression `LibraryManager` invalident).
- **Aucun consommateur DI runtime ne résout d'item par id via le static** —
  ✅ (les 29 static `GetItemById` restants sont tous entity-level compat ou
  domaines différés).
- **Séparation lookup / access / hierarchy** — ✅ (3 ports distincts).

Le chantier **lookup / hierarchy est clos**.

## Prochain domaine : query / listing

Extension ou division propre d'`IItemQueryService`. Restent des chantiers
**séparés** (classification PR68) :

- collection folders / options ;
- mutations (`Create`/`Update`/`Delete`) ;
- alternate versions de `Video`, extras / images ;
- queries récursives ;
- centralisation de la visibilité (`IsVisibleStandalone`, dette § 4).

Les relations entity différées (§ 6) seront reprises avec le domaine qui leur
fournit enfin un appelant porteur d'un `IItemLookupService`/`IItemHierarchyService`.
