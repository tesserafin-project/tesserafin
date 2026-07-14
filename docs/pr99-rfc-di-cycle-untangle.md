> **REMPLACÉ** par `docs/rfc-di-query-user-views-v2.md` (PR105) — rejeté en revue externe pour cycles cachés (`IUserRootFolderProvider` aliasé sur `LibraryManager` ; cycle caché via `IChannelManager`), voir `docs/major-rewrite-plan-v13.md` § « Revue externe après PR99 ».

# RFC — Démêlage du cycle DI query / user-views / channel

- **PR** : PR99 (design uniquement, aucun code de production)
- **Statut** : proposé
- **Dépend de** : PR85–PR90 (surface query, `ItemQueryScopeService`, scoping static-free)
- **Précède** : PR100–PR104 (application de la découpe)

Aucun code ici. Ce RFC modélise le cycle réel, compare trois découpes et en retient une avec des critères de sortie **mesurables**.

---

## 1. Le cycle réel (arêtes vérifiées dans le code)

Dépendances de constructeur constatées :

| Nœud | Dépend directement de (extrait pertinent) |
| --- | --- |
| `LibraryManager` | `Lazy<IUserViewManager>` (L77/L160/L260), + ~28 autres |
| `UserViewManager` | **`ILibraryManager`** (L32), `IChannelManager`, `ILiveTvManager`, `ICollectionManager`, `ITVSeriesManager`, `IItemSortService`, `ILocalizationManager`, `IServerConfigurationManager` |
| `ItemQueryService` | **`IUserViewManager`** (L30), `IChannelManager`, `ICollectionManager`, `ITVSeriesManager`, `IItemLookupService`, `IItemRepository`, `IItemQueryScopeService`, … |
| `ItemQueryScopeService` | **`IUserViewManager`** (L39), `IItemLookupService`, `IItemSortService`, `IUserRootFolderProvider` |
| `IChannelManager` (ChannelManager) | `IDtoService`, `ILibraryManager`, … |

### 1.1 La composante fortement connexe (SCC)

Le cœur du cycle est une **boucle à deux nœuds**, cassée uniquement par un `Lazy` :

```text
LibraryManager ──Lazy<IUserViewManager>──▶ UserViewManager ──ILibraryManager──▶ LibraryManager
```

`Lazy<IUserViewManager>` (L77) est le pansement historique : sans lui, le conteneur DI refuserait de construire la paire. `LibraryManager.UserViewManager` (L260, `=> _userViewManagerFactory.Value`) diffère la résolution jusqu'au premier appel.

Deux arêtes supplémentaires entrent dans la même SCC :

```text
ItemQueryService      ──IUserViewManager──▶ UserViewManager ──▶ … ──▶ LibraryManager
ItemQueryScopeService ──IUserViewManager──▶ UserViewManager ──▶ … ──▶ LibraryManager
```

Et une branche channel parallèle :

```text
UserViewManager / ItemQueryService ──IChannelManager──▶ ChannelManager ──IDtoService/ILibraryManager──▶ …
```

### 1.2 Observation décisive

`IUserViewManager` expose **trois** méthodes :

```text
Folder[] GetUserViews(UserViewQuery)                    // catalogue de vues
UserView GetUserSubView(Guid, CollectionType?, …)        // factory d'une sous-vue
List<...> GetLatestItems(LatestItemsQuery, DtoOptions)   // latest-items (utilise IDtoService)
```

Or les **deux** consommateurs qui referment le cycle par `IUserViewManager` n'appellent **que** `GetUserViews` :

- `LibraryManager` (L2002) : `UserViewManager.GetUserViews(new UserViewQuery …)` — seul appel.
- `ItemQueryScopeService` (L128) : `_userViewManager.GetUserViews(new UserViewQuery …)` — seul appel.

Le cycle ne dépend donc pas de tout `IUserViewManager`, mais d'une **seule feuille** : `GetUserViews`.

---

## 2. Trois découpes comparées

### Découpe (1) — diviser `IUserViewManager` en catalogue / factory / latest-items

```text
IUserViewCatalog      : GetUserViews(UserViewQuery)
IUserViewFactory      : GetUserSubView(...)
IUserViewLatestItems  : GetLatestItems(..., DtoOptions)
```

- **Pour** : correspond exactement à la surface (3 méthodes → 3 ports). `GetUserViews` isolé permet de couper le cycle à sa seule arête utile.
- **Contre** : ne suffit pas seul si l'implémentation de `GetUserViews` dépend encore d'`ILibraryManager`.

### Découpe (2) — séparer un exécuteur query « feuille » de l'orchestrateur user-scoped

Extraire l'implémentation de `GetUserViews` dans un service **cycle-free** bâti sur les ports feuilles déjà existants (introduits PR76–PR85) — `IItemLookupService`, `IUserRootFolderProvider`, `IItemSortService` — **sans** `ILibraryManager`. `UserViewManager` délègue alors `GetUserViews` à ce leaf.

- **Pour** : casse la back-edge `UserViewManager → ILibraryManager` pour le chemin catalogue.
- **Contre** : nécessite d'auditer ce que `GetUserViews` touche réellement dans `ILibraryManager` et de le rerouter vers les ports feuilles.

### Découpe (3) — port de catalogue de chaînes sans `IDtoService`

Extraire `IChannelCatalog` (liste/lookup de chaînes brutes) de `ChannelManager`, sans dépendance à `IDtoService` (qui rapatrie tout le graphe DTO/library).

- **Pour** : coupe la branche channel du cycle.
- **Contre** : orthogonal au verrou principal (user-views) ; `IDtoService` est un second gros nœud, chantier distinct.

---

## 3. Découpe retenue — **(1) ⊕ (2) fusionnées** : un leaf `IUserViewCatalog`

Les découpes (1) et (2) **convergent** grâce à l'observation §1.2 : les deux seuls consommateurs qui referment le cycle n'utilisent que `GetUserViews`.

**Plan :**

1. Introduire `IUserViewCatalog { Folder[] GetUserViews(UserViewQuery); }`.
2. Implémenter `UserViewCatalog` **cycle-free**, sur `IItemLookupService` + `IUserRootFolderProvider` + `IItemSortService` (+ `IChannelManager`/`ILiveTvManager` si le catalogue inclut les vues externes — à isoler ; sinon paramétrer `IncludeExternalContent`), **sans `ILibraryManager`**.
3. Migrer les deux consommateurs du cycle vers le leaf :
   - `LibraryManager` : `Lazy<IUserViewManager>` → `IUserViewCatalog` (plus de `Lazy`).
   - `ItemQueryScopeService` : `IUserViewManager` → `IUserViewCatalog`.
4. `UserViewManager` conserve `GetUserSubView`/`GetLatestItems` et **délègue** `GetUserViews` au leaf (une seule implémentation, pas de duplication).

**Effet sur la SCC :** l'arête `LibraryManager → Lazy<IUserViewManager>` disparaît ; l'arête `ItemQueryScopeService → IUserViewManager` disparaît ; le leaf `UserViewCatalog` ne pointe que vers des ports feuilles. La boucle à deux nœuds est **rompue sans `Lazy`**.

La découpe (3) (channels/`IDtoService`) est **reportée** : elle ne bloque aucun des critères ci-dessous et constitue un chantier séparé (PR post-104).

---

## 4. Critères de sortie **mesurables** (vérifiés à PR104)

- [ ] **≥ 1 `Lazy<T>` supprimé** : `Lazy<IUserViewManager>` retiré de `LibraryManager` (L77/L160/L260).
- [ ] **`ItemQueryScopeService` sans `IUserViewManager`** : son constructeur ne reçoit plus `IUserViewManager` (remplacé par `IUserViewCatalog`).
- [ ] **Façades query de `LibraryManager` capables de déléguer** : les méthodes query de `LibraryManager` peuvent router vers `IItemQueryService` / le leaf, sans reconstruire l'orchestration.
- [ ] **Aucun `IServiceProvider`** comme échappatoire (constaté aujourd'hui : aucun dans ces nœuds — ne pas en introduire).
- [ ] **Constructeur `LibraryManager` réduit d'au moins trois dépendances** (objectif PR104 global).
- [ ] **Parité** : tests de parité sur `GetUserViews` (leaf vs implémentation historique) verts ; aucune régression des tests existants.

---

## 5. Séquence PR100–PR104

| PR | Contenu | Critère |
| --- | --- | --- |
| PR100 | Introduire `IUserViewCatalog` + `UserViewCatalog` (leaf cycle-free) + **tests de parité** contre le `GetUserViews` historique | leaf vert, aucune arête vers `ILibraryManager` |
| PR101 | Migrer `ItemQueryScopeService` : `IUserViewManager` → `IUserViewCatalog` | scope service sans `IUserViewManager` |
| PR102 | Migrer `LibraryManager` : `Lazy<IUserViewManager>` → `IUserViewCatalog` ; retirer l'arête DI | `Lazy` supprimé |
| PR103 | `UserViewManager.GetUserViews` **délègue** au leaf ; supprimer l'implémentation dupliquée | une seule implémentation |
| PR104 | Clôture : façades query de `LibraryManager` délèguent ; mesurer (Lazy, params ctor, duplication) ; MAJ roadmap | tous les critères §4 |

---

## 6. Risques

1. **`GetUserViews` touche `ILibraryManager` plus que prévu.** Mitigation : PR100 audite chaque accès et le reroute vers un port feuille ; si un accès n'a pas de port feuille, l'introduire d'abord (comme PR76–PR85).
2. **Vues externes (channels/live tv) dans le catalogue.** Si `GetUserViews` compose des vues channel/livetv, le leaf garde `IChannelManager`/`ILiveTvManager` (qui ne referment pas la boucle `LibraryManager↔UserViewManager`) — la découpe (3) réglera leur propre cycle plus tard.
3. **Parité comportementale.** Le leaf doit reproduire exactement l'ordre/filtrage de `GetUserViews` ; tests de caractérisation obligatoires avant migration (PR100).
