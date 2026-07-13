# PR85 — Audit de clôture du query/listing actuel

Audit **sans code de production**. Objectif : cartographier tous les chemins de
lecture avant d'étendre `IItemQueryService` aux requêtes globales (PR86), et
consigner les tests de caractérisation manquants qui protégeront le déplacement
d'orchestration read-only hors de `LibraryManager`.

État vérifié au commit courant : build 0 erreur ; `IItemHierarchyService`
supprimé (PR83) ; frontière d'accès SyncPlay corrigée (PR82).

## 1. Classification des chemins de lecture

| # | Chemin | Propriétaire actuel | Surface |
|---|--------|---------------------|---------|
| 1 | Lookup par ID | `IItemLookupService.GetItemById` | migré (PR76-79) |
| 2 | Requête globale repository | `LibraryManager.GetItemList/GetItemsResult(InternalItemsQuery)` | **à extraire (PR86)** |
| 3 | Requête enfant d'un `Folder` | `IItemQueryService.GetItems/GetItemList(Folder, query)` | déjà extrait |
| 4 | Query utilisateur | `LibraryManager.AddUserToQuery` (privé) | **à extraire (PR86)** |
| 5 | Query récursive | `LibraryManager.SetTopParentIdsOrAncestors` (privé) | **à extraire (PR86)** |
| 6 | Raw fast path | `ItemQueryService` (EnableTotalRecordCount=false) | déjà extrait (folder-scoped) |
| 7 | Dispatch spécialisé (`UserView`, `BoxSet`, `Season`, `Series`, `Playlist`, channel) | `Folder`/`ItemQueryService` fallback | partiellement extrait |
| 8 | Post-filter / sort | `ItemQueryService.PostFilterAndSort` + `Folder` | partiellement extrait |

## 2. Orchestration read-only dans `LibraryManager` (cible PR86)

`GetItemList(InternalItemsQuery, allowExternalContent)` (L1647), `GetItemList`
(L1666), `GetItemsResult` (L1911), `GetCount` (L1671), `QueryItems` (L1787)
partagent la même séquence d'orchestration read-only :

1. **Résolution du parent de query** — si `Recursive && ParentId non vide` :
   `GetItemById(ParentId)` puis `SetTopParentIdsOrAncestors(query, [parent])`.
2. **Configuration utilisateur** — si `User != null` : `AddUserToQuery`, qui
   résout les user views en `TopParentIds` quand la query n'a aucun périmètre
   (`AncestorIds`/`ParentId`/`ChannelIds`/`TopParentIds`/`ItemIds`/`OwnerIds`
   tous vides).
3. **Top-parent / ancestor IDs** — `SetTopParentIdsOrAncestors` (L1938) :
   - collection folders / `UserView` → `TopParentIds` via `GetTopParentIdsForQuery` ;
   - `Playlist`/`BoxSet` avec `LinkedChildren` → `ItemIds` (les linked-children
     ne peuplent pas `AncestorIds`, une query récursive renverrait 0 ligne) ;
   - sinon → `AncestorIds` via `GetIdsForAncestorQuery`.
   - **Garde périmètre vide** : injection de `Guid.NewGuid()` pour éviter de
     scanner toutes les bibliothèques sur filtre vide (3 sites).
4. **Appel repository** — `_itemRepository.GetItemList` / `GetItems`.
5. **Total record count** — branche `EnableTotalRecordCount` :
   `true` → `_itemRepository.GetItems(query)` (avec count) ;
   `false` → `new QueryResult(StartIndex, null, GetItemList(query))`.

C'est **exactement** l'orchestration read-only que PR86 déplace vers
`IItemQueryService.GetItems/GetItemList(InternalItemsQuery)`. Aucune mutation,
création, suppression ni scan n'est impliquée dans ces cinq étapes.

## 3. Cartographie des appelants (méthodes ciblées)

| Famille | Appelants (hors tests, hors Entities) | Note |
|---------|----------------------------------------|------|
| `ILibraryManager.GetItemList(...)` | **38 fichiers** | contrôleurs, validators, scheduled tasks, image providers, similar-items, providers |
| `ILibraryManager.GetItemsResult(...)` | 2 fichiers | |
| `Folder.GetItems` / `Folder.GetItemList` | via `IItemQueryService` (folder-scoped) | déjà extrait |
| `IItemRepository.GetItemList` | `LibraryManager`, `ItemQueryService` | couche persistance, inchangée |
| `IItemQueryService` | 11 consommateurs (ItemsController, TvShowsController, PlaylistManager, SessionManager, RecordingsManager, DynamicImageProvider, CollectionFolderImageProvider, Folder, Playlist) | surface folder-scoped |

Répartition des 38 appelants `GetItemList` par nature (pré-tri pour PR87) :
- **contrôleurs simples** : GenresController, MusicGenresController, LibraryController, TvShowsController ;
- **providers / image providers** : Artist/Genre/MusicGenre/BaseFolder/CollectionFolder/DynamicImageProvider, ProviderManager ;
- **scheduled / read-only tasks** : AudioNormalization, MediaSegmentExtraction, ChapterImages, Trickplay×2, Subtitle, Lyric, CleanDatabase, Splashscreen ;
- **validators / post-scan** : Studios, Genres, People, Artists, CollectionPostScan ;
- **similar-items** : SimilarItemsManager + 5 providers ;
- **couplés mutations/root** (à exclure temporairement PR87) : PlaylistManager, UserViewManager, MusicManager, SearchManager, SessionManager, LibraryManager lui-même.

## 4. Couverture de test existante vs manquante

**Déjà couvert** (`ItemQueryServiceTests`, folder-scoped) :
- `GetItems/GetItemList(Folder, query)` délèguent aux managers du service ;
- `PostFilterAndSort` (user null / non-null / collapse) parité avec `Folder.GetItems` ;
- fallbacks (recursive, ItemIds, channel folder, UserView) ;
- fast path force `EnableTotalRecordCount=false` ;
- `BoxSet` non-récursif utilise `IItemSortService` au lieu du static sort.

**Gap identifié (bloquant avant PR86)** : **aucune** caractérisation de
l'orchestration **globale** de `LibraryManager` (§2). Tests à ajouter (parité,
via `_itemRepository` mocké) :
1. `GetItemList` recursive + `ParentId` résout le parent et pose le périmètre top-parent ;
2. `GetItemsResult` user non-null, query non scopée → user views résolues en `TopParentIds` ;
3. garde périmètre vide → un `TopParentIds`/`AncestorIds`/`ItemIds` non vide (fake GUID) injecté, jamais de scan global ;
4. `Playlist`/`BoxSet` avec `LinkedChildren` → route via `ItemIds`, pas `AncestorIds` ;
5. `EnableTotalRecordCount=true` → `GetItems` (count présent) ; `false` → `GetItemList` enveloppé, count `null` ;
6. `allowExternalContent=false` propagé jusqu'à `UserViewQuery.IncludeExternalContent`.

Ces six tests figent le comportement que PR86 devra reproduire à l'identique
après extraction.

## Critère de clôture PR85

- Chemins de lecture classés (§1) — ✅
- Orchestration read-only à déplacer isolée et documentée (§2) — ✅
- Appelants cartographiés et pré-triés pour PR87 (§3) — ✅
- Tests de caractérisation manquants ajoutés (§4) — voir commit PR85
- **Aucun code de production modifié** — ✅
