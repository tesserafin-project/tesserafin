# PR85b — Extraire le scoping de query dans un service feuille cycle-free

Préparation de PR86. L'orchestration read-only de `LibraryManager`
(`GetItemList`/`GetItemsResult(InternalItemsQuery)`) ne peut pas être déplacée
telle quelle vers `IItemQueryService` : cela formerait un cycle DI dur
`LibraryManager → IItemQueryService → IUserViewManager → ILibraryManager`
(tous non-lazy), et le scoping top-parent/user-view est soudé au lifecycle de
racine de `LibraryManager` (`GetUserRootFolder`).

PR85b isole le scoping dans un **service feuille**, `IItemQueryScopeService`,
qui ne dépend que de ports cycle-free, de sorte que `IItemQueryService` (PR86)
puisse construire les requêtes globales sans toucher `ILibraryManager`.

## Décision de câblage (cycle-free, sans nouveau `Lazy<T>`)

- `IItemQueryScopeService` est injecté **uniquement dans `ItemQueryService`**
  (consommateur PR86). Il **n'est jamais injecté dans `LibraryManager`** :
  sinon `LibraryManager → ScopeService → IUserRootFolderProvider(=LibraryManager)`
  reformerait un cycle.
- Le seul besoin lifecycle-racine (`GetUserRootFolder`, branche grouped-folders)
  est exposé par un port étroit `IUserRootFolderProvider`, implémenté par
  `LibraryManager` (qui possède déjà la méthode et le cache `_userRootFolder`).
  Extraire réellement le lifecycle racine est un chantier séparé (étape 10 du
  plan) ; jusque-là le port reste sur `LibraryManager`.
- `LibraryManager` **conserve** ses helpers privés de scoping
  (`SetTopParentIdsOrAncestors`, `AddUserToQuery`, `GetTopParentIdsForQuery`)
  pendant la transition. Duplication temporaire assumée : elle disparaîtra quand
  les consommateurs migreront vers le service (PR87+) et que `LibraryManager`
  pourra à son tour déléguer une fois le port racine sorti. Aucun `Lazy` ajouté.

## Surface

`IItemQueryScopeService` (Reefin.Controller/Library) :

```csharp
void SetTopParentIdsOrAncestors(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents);
void AddUserToQuery(InternalItemsQuery query, User user, bool allowExternalContent = true);
```

`IUserRootFolderProvider` (Reefin.Controller/Library) :

```csharp
Folder GetUserRootFolder();
```

## Impl (`ItemQueryScopeService`, internal sealed, Reefin.Server.Core/Library)

Déplace la logique des trois helpers depuis `LibraryManager`. Ports ctor
cycle-free :

- `IItemLookupService` — `GetItemById` (résolution des parents de vue dans
  `GetTopParentIdsForQuery`) ;
- `IUserViewManager` — `GetUserViews` (dans `AddUserToQuery`) ;
- `IItemSortService` — tri `GetChildren` (branche grouped-folders) ;
- `IUserRootFolderProvider` — `GetUserRootFolder` (branche grouped-folders).

Aucune dépendance à `ILibraryManager`.

## Vérification

- Build 0 erreur.
- `dotnet test tests/Reefin.Server.Implementations.Tests/` — parité comportementale
  (tests unitaires du scope service mirroir de PR85).
- **`dotnet test tests/Reefin.Server.Integration.Tests/`** — résolution via le
  conteneur réel (`ReefinApplicationFactory`/`TestAppHost`). Un cycle DI ne
  casserait aucun test unitaire mais ferait échouer la construction du
  `ServiceProvider` ici : c'est la garde anti-cycle obligatoire.
