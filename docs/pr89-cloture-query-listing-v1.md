# PR89 — Clôture query/listing v1

Bilan mesuré de la série PR85→PR88 (extraction de la surface query globale hors
de `LibraryManager`). Aucun code de production dans PR89 : inventaire, parité,
mesures, et constat honnête du blocage restant.

## Mesures avant / après

| Métrique | Diagnostic initial | Après PR88 | Delta |
|----------|--------------------|-----------|-------|
| Constructeur `LibraryManager` (paramètres) | ~23 → ~30 | **29** | **≈ inchangé** |
| Consommateurs injectant `ILibraryManager` (ctor) | 156 (départ) → 68 (PR82) | ~ idem | **−2 nets sur ce chantier** |
| Appelants `ILibraryManager.GetItemList/GetItemsResult` | — | **37 fichiers** | compat inventoriée |
| Consommateurs migrés hors `ILibraryManager` (PR87) | — | **2** (ChapterImagesTask, SuggestionsController) | sinks uniquement |
| Relations différées absorbées (PR88) | — | **1** (`Season.Series` dans DynamicImageProvider) | consumed-only |

**Constat central : `LibraryManager` n'a pas maigri sur ce chantier.** Ses
méthodes query restent en place comme surfaces de compatibilité (elles ne peuvent
pas déléguer au nouveau service sans former un cycle DI). Le résultat concret de
PR85→PR88 est l'existence d'une surface query cycle-free (`IItemQueryService`
global + `IItemQueryScopeService`) avec de **vrais** consommateurs (DynamicImageProvider,
ChapterImagesTask, SuggestionsController, et les chemins folder-scoped existants),
pas une réduction du god-object.

## Blocage structurel identifié (PR87)

`IItemQueryService` dépend **non-lazy** de `IUserViewManager` et `IChannelManager`.
Le graphe ne se résout aujourd'hui que grâce au `Lazy<IUserViewManager>` de
`LibraryManager`. Conséquence : tout consommateur situé **sous** la chaîne
`ProviderManager ← DtoService ← ChannelManager` (validators, image providers,
similar-items, live-tv, managers) forme un cycle DI dur en injectant
`IItemQueryService` :

```
consommateur → IItemQueryService → IChannelManager → IDtoService
             → IProviderManager → …(le consommateur)
```

Seuls les **sinks** DI (contrôleurs, scheduled tasks — rien n'en dépend) sont
migrables. Les 37 appelants compat restants sont majoritairement mid-graph et
resteront non-migrables tant que la toile de `Lazy`
`UserViewManager ↔ ChannelManager ↔ ProviderManager` n'est pas démêlée — c'est
l'**étape 10** du plan général (suppression des cycles DI / `Lazy<T>`).

**Recommandation :** avancer l'étape 10 (démêlage DI) **avant** toute nouvelle
tentative de migration des consommateurs query mid-graph. Sans cela, PR87-style
ne peut plus faire maigrir `LibraryManager`.

## Parité (tests de caractérisation)

La parité entre l'orchestration historique de `LibraryManager` et le nouveau
chemin service est figée par :

- `LibraryManagerGlobalQueryTests` (PR85) — orchestration globale de `LibraryManager` (7 tests) ;
- `ItemQueryScopeServiceTests` (PR85b) — scoping extrait (8 tests) ;
- `ItemQueryServiceTests` (PR86) — surface globale `GetItems/GetItemList(query)` (7 tests) ;
- `EpisodeGetSeriesTests` / `SeasonGetSeriesTests` (PR81/PR88) — relations lookup-aware.

Ces suites prouvent que le service reproduit à l'identique le comportement de
`LibraryManager` (résolution parent, scoping user/top-parent, gardes périmètre
vide, branche `EnableTotalRecordCount`, LinkedChildren→ItemIds).

## Fallback static résiduel (constat honnête)

Le chemin service **n'est pas intégralement static-free**. Dans
`IItemQueryScopeService.GetTopParentIdsForQuery`, la branche générique (ni
`UserView`, ni `CollectionFolder`) appelle `item.GetTopParent()`, qui traverse
`GetParents()` → `GetParent()` → static `BaseItem.LibraryManager`.

Ce n'est **pas une régression** (comportement identique à l'original de
`LibraryManager`, copié verbatim en PR85b), mais une dépendance static héritée à
lever au chantier hiérarchie/`BaseItem` statics (threading d'un
`IItemLookupService` à travers `GetTopParent`/`GetParents`), hors périmètre
query/listing v1.

## Critères de clôture PR89

- Aucun **nouveau** code applicatif n'appelle `ILibraryManager.GetItemList/GetItemsResult` — ✅ (les nouveaux consommateurs passent par `IItemQueryService`).
- Anciens appels restants inventoriés comme compat — ✅ (37 fichiers, §inventaire).
- Chemins spécialisés couverts par tests de parité — ✅.
- Aucun fallback service-aware **inconnu** vers un static — ✅ (le seul, `GetTopParent`, est identifié et documenté).
- Mesure avant/après du constructeur `LibraryManager` — ✅ (29, inchangé).
- Mesure avant/après des consommateurs `ILibraryManager` — ✅ (−2 nets).

**Objectif de la série partiellement atteint.** La surface query cycle-free
existe et a de vrais consommateurs, mais `LibraryManager` ne maigrit pas : le
verrou est le Lazy-web DI, pas la surface query. La suite productive n'est plus
d'ajouter des méthodes query mais de démêler les cycles DI (étape 10), ou de
pivoter vers le moteur de décision playback non-DLNA (PR90+).
