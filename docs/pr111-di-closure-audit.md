# PR111 — Audit et clôture mesurée du chantier DI query/user-views

Audit + documentation, aucun refactoring. Vérifie, preuve à l'appui (`file:line`,
sorties `graphify path`, comptages exacts), les 6 critères de sortie reformulés en
PR105b (RFC `docs/rfc-di-query-user-views-v2.md` §9/PR111), réévalue les 4
exceptions runtime bénies (§8), inventorie les chemins résiduels vers l'ex-SCC, et
statue sur la clôture du chantier. Un test de verrouillage architectural est
ajouté (aucun équivalent préexistant ne couvrait ce cas précis).

Méthode : `graphify path` bidirectionnel entre les 4 ex-membres de la SCC, lecture
intégrale des constructeurs de `LibraryManager.cs`, `UserViewManager.cs`,
`ChannelManager.cs`, `LiveTvManager.cs` et des 6 leaves PR106-108, `git show
--stat` sur chacun des 6 commits PR106a→PR110 pour établir la liste exacte des
fichiers de production touchés, `git blame` pour distinguer code nouveau et code
préexistant sur les faux positifs de grep.

## Critère 1 — `Lazy<IUserViewManager>` : zéro occurrence

```
grep -rn "Lazy<IUserViewManager>" --include=*.cs .
```

**Zéro résultat**, code de production comme enregistrements DI. Confirmé dans
`Reefin.Server.Core/Library/LibraryManager.cs` (champ, ctor param et propriété
tous absents — voir §5) et dans `Reefin.Server.Core/ApplicationHost.cs` (plus
aucune ligne `AddTransient(provider => new Lazy<IUserViewManager>(...))`).

## Critère 2 — cycle de construction cassé

Re-dérivation des 6 arêtes du tableau §7.2 du RFC, ctor par ctor :

| # | Arête (RFC §7.2) | État actuel | Preuve |
|---|---|---|---|
| 1 | `LibraryManager --Lazy<IUserViewManager>--> UserViewManager` | **DISPARUE** | `LibraryManager.cs:160-193` (ctor) ne contient ni `IUserViewManager` ni `Lazy<IUserViewManager>` ; remplacée par `IUserViewCatalog userViewCatalog` (param direct, non-Lazy, `:170`) |
| 2 | `UserViewManager --ILibraryManager--> LibraryManager` | **RESTE** (sens unique) | ctor `UserViewManager.cs:53/61` ; usages dans `GetItemsForLatestItems` : `GetItemById` (`:141`), `GetUserRootFolder` (`:172`), `GetLatestItemList` (`:278,284,290`), `GetItemList` (`:294`) |
| 3 | `UserViewManager --IChannelManager--> ChannelManager` | **RESTE** (sens unique) | ctor `UserViewManager.cs:55/63` ; usage `GetLatestChannelItemsInternal` (`:144`, dans `GetItemsForLatestItems`) |
| 4 | `ChannelManager --ILibraryManager--> LibraryManager` | **RESTE** (sens unique) | ctor `ChannelManager.cs:72-75` ; 19 usages `_libraryManager.*` (grep `_libraryManager\.` sur `src/Reefin.LiveTv/Channels/ChannelManager.cs`) |
| 5 | `UserViewManager --ILiveTvManager--> LiveTvManager` | **DISPARUE** | ctor `UserViewManager.cs:60-66` : `ILibraryManager, ILocalizationManager, IChannelManager, IItemSortService, IUserViewCatalog, IUserViewFactory` — aucun `ILiveTvManager`. Le seul appelant historique (`GetUserViews`, ligne 145-148 de l'ancien RFC) a été retiré ; `IUserViewManager` n'importe plus `Reefin.Controller.LiveTv` |
| 6 | `LiveTvManager --ILibraryManager--> LibraryManager` | **RESTE** (sens unique, réduite) | ctor `LiveTvManager.cs:52-64` (toujours 12 params dont `ILibraryManager libraryManager` `:58`) ; 18 usages `_libraryManager.*` (grep), mais `GetInternalLiveTvFolder` (`:1266-1269`) délègue désormais à `_userViewFactory.GetNamedView(...)` — l'arête historique passant par `GetNamedView` (RFC §5, le point exact du rejet PR99) a disparu ; les 18 usages restants sont hors périmètre user-views (guide, timers, dto) |

`graphify path` bidirectionnel (sortie brute) :

```
$ graphify path "LibraryManager" "UserViewManager"
Shortest path (2 hops):
  LibraryManager --references [EXTRACTED]--> IUserViewFactory <--references [EXTRACTED]-- UserViewManager

$ graphify path "UserViewManager" "LibraryManager"
Shortest path (2 hops):
  UserViewManager --references [EXTRACTED]--> IItemSortService <--references [EXTRACTED]-- LibraryManager

$ graphify path "LibraryManager" "ChannelManager"
Shortest path (2 hops):
  LibraryManager --references [EXTRACTED]--> IServerConfigurationManager <--references [EXTRACTED]-- ChannelManager

$ graphify path "ChannelManager" "LibraryManager"
Shortest path (2 hops):
  ChannelManager --references [EXTRACTED]--> IFileSystem <--references [EXTRACTED]-- LibraryManager

$ graphify path "LibraryManager" "LiveTvManager"
Shortest path (2 hops):
  LibraryManager --implements [EXTRACTED]--> ILibraryManager <--references [EXTRACTED]-- LiveTvManager

$ graphify path "LiveTvManager" "LibraryManager"
Shortest path (2 hops):
  LiveTvManager --references [EXTRACTED]--> IServerConfigurationManager <--references [EXTRACTED]-- LibraryManager
```

Dans les 6 sens, le plus court chemin ne passe plus jamais par une arête ctor
directe `ILibraryManager`/`IUserViewManager`/`IChannelManager`/`ILiveTvManager`
entre les paires concernées — il passe par des leaves co-consommés
(`IUserViewFactory`, `IItemSortService`, `IServerConfigurationManager`,
`IFileSystem`) ou, pour `LiveTvManager→LibraryManager`, par l'implémentation
d'interface elle-même (`LibraryManager : ILibraryManager`), pas par une injection
de `LiveTvManager` dans `LibraryManager`. Le graphe interrogé (`graphify-out/`,
généré 2026-07-14 21:28, HEAD `3a42c1bf8e`) reflète l'état post-PR110.

**Conclusion** : les arêtes 1 et 5 (les deux qui, avec 2/3 et 4/6 respectivement,
fermaient les deux boucles distinctes identifiées §7.1 du RFC) sont supprimées.
Il ne reste que des arêtes à sens unique **vers** `LibraryManager`
(`UserViewManager→ILibraryManager`, `ChannelManager→ILibraryManager`,
`LiveTvManager→ILibraryManager`) et **vers** `ChannelManager`
(`UserViewManager→IChannelManager`) — aucune arête ne repart de `LibraryManager`
vers l'un des trois autres membres. **Une SCC à 4 membres ne peut plus exister** :
il faudrait une arête retour depuis `LibraryManager`, qui n'existe plus nulle
part (confirmé par grep, §5). Le graphe de construction est un DAG sur ce
périmètre.

## Critère 3 — une seule implémentation réelle de `GetUserViews`

`UserViewManager.GetUserViews` (`UserViewManager.cs:76-80`) :

```csharp
public Folder[] GetUserViews(UserViewQuery query)
{
    return _userViewCatalog.GetUserViews(query);
}
```

Passe-plat pur — un appel, un retour, aucune logique intermédiaire. Les anciens
helpers privés qui dupliquaient la logique de listing (`GetUserView(Folder,...)`,
la surcharge privée de groupement par liste) ont été supprimés en PR110 (zéro
autre appelant, vérifié par grep avant suppression selon le journal plan v13).

`UserViewCatalog.GetUserViews` (`Reefin.Server.Core/Library/UserViewCatalog.cs:144-261`,
118 lignes) est la seule implémentation réelle : parcours des enfants du root
folder, probe playlists/boxsets, groupement `IsUserSpecific`/`IsEligibleForGrouping`,
canaux/Live TV externes, filtrage `MyMediaExcludes`, tri via `IItemSortService` +
`OrderedViews`. Aucune trace de logique de listing dupliquée dans
`UserViewManager.cs` — les seules méthodes restantes
(`GetUserSubViewWithName`/`GetUserSubView`/`GetLatestItems`, `UserViewManager.cs:82-295`)
sont hors du périmètre `IUserViewCatalog` (PR109 ne couvre que `GetUserViews`,
confirmé RFC §9/PR109).

## Critère 4 — aucun nouveau fallback statique

Fichiers de production créés/modifiés par PR106a→PR110 (`git show --stat` sur
`1c8ae304b2, 91d6b45964, 13d57fb23b, 7f85e20333, 3c7a90b31f, 4ef9a90f54`,
tests exclus) :

```
Reefin.Controller/Library/IItemStore.cs                 (nouveau, PR106a)
Reefin.Controller/Library/IUserViewFactory.cs            (nouveau, PR106b)
Reefin.Controller/Library/IUserViewCatalog.cs             (nouveau, PR109)
Reefin.Controller/Channels/IChannelCatalog.cs              (nouveau, PR108)
Reefin.Controller/LiveTv/ILiveTvPresenceProvider.cs         (nouveau, PR108)
Reefin.Server.Core/Library/ItemStore.cs                  (nouveau, PR106a)
Reefin.Server.Core/Library/UserViewFactory.cs             (nouveau, PR106b)
Reefin.Server.Core/Library/UserRootFolderProvider.cs       (nouveau, PR107)
Reefin.Server.Core/Library/ChannelCatalog.cs                (nouveau, PR108)
Reefin.Server.Core/Library/LiveTvPresenceProvider.cs          (nouveau, PR108)
Reefin.Server.Core/Library/UserViewCatalog.cs                  (nouveau, PR109)
Reefin.Server.Core/Library/LibraryManager.cs             (modifié, PR106a/b/107/110)
Reefin.Server.Core/Library/ItemQueryScopeService.cs        (modifié, PR110)
Reefin.Server.Core/Library/UserViewManager.cs                (modifié, PR110)
Reefin.Server.Core/ApplicationHost.cs                          (modifié, PR106a→110)
src/Reefin.LiveTv/LiveTvManager.cs                              (modifié, PR110)
```

Grep `BaseItem\.LibraryManager|BaseItem\.ProviderManager|Folder\.GetItems|\.GetItemList\(` sur
ces 16 fichiers :

| Fichier | Occurrence | Verdict |
|---|---|---|
| `ApplicationHost.cs:572` | commentaire expliquant le piège évité | pas un appel |
| `ApplicationHost.cs:732,737` | `BaseItem.LibraryManager = Resolve<...>()`, `BaseItem.ProviderManager = Resolve<...>()` | **assignation** du statique au bootstrap (`SetStaticProperties`), préexistante, pas un fallback nouveau |
| `LibraryManager.cs:1675,1756,1814,1947` | `_itemRepository.GetItemList(query)` | méthode différente sur le champ d'instance `IItemRepository`, pas `Folder.GetItemList`/statique — faux positif de pattern |
| `IChannelCatalog.cs:23`, `ChannelCatalog.cs:39` | commentaires XML citant le chemin évité | pas un appel |
| `UserViewCatalog.cs:49,77,82,86,91-92` | commentaires XML décrivant la réécriture du probe (RFC §3) | pas un appel — le vrai appel est `_itemQueryServiceFactory.Value.GetItemList(folder, query)` (`:164`), pas `Folder.GetItemList` |
| `ItemQueryScopeService.cs:209` | commentaire | pas un appel |
| `UserViewManager.cs:294` | `_libraryManager.GetItemList(query, parents)` | appel sur le champ d'instance `ILibraryManager` (injecté), pas le statique ; **préexistant** — `git blame` : ligne introduite par `e91f569c154` (2017, Emby), inchangée par le diff PR110 (`GetItemsForLatestItems`, hors du périmètre `IUserViewCatalog`) |
| `LiveTvManager.cs:968` | `_libraryManager.GetItemList(...)` | idem, champ d'instance ; **préexistant** — `git blame` : `fd6aa72dac2` (2016), inchangé par PR110 |

**Zéro occurrence** de `BaseItem.LibraryManager`/`BaseItem.ProviderManager`/
`Folder.GetItems`/`Folder.GetItemList` en tant qu'appel réel dans le code
introduit ou modifié par PR106a-110. Les seules assignations du statique
(`ApplicationHost.cs:732,737`) sont le bootstrap historique, hors périmètre
(nécessaire pour que le statique existe du tout — pas un nouveau fallback).

Constat additionnel : `Folder.UserViewManager` (statique, `Folder.cs:55`,
`public static IUserViewManager UserViewManager { get; set; }`) reste assigné au
bootstrap (`ApplicationHost.cs:741`) et utilisé dans les surcharges de compat
pré-PR86 de `Folder.GetItems`/`GetItemList` (`Folder.cs:1026-1099,2034,2074`) et
dans `RecordingsManager.cs` — **fichier non touché par PR106-110**, hors
périmètre du critère 4, mentionné ici pour complétude (c'est le pendant
`IUserViewManager` du static `BaseItem.LibraryManager`, déjà documenté RFC §3/§8
comme piège connu non éliminé par ce chantier).

**Passe de contrôle complémentaire** (receiver-agnostique, insensible à la
casse, pour couvrir `folder.GetItems(` en plus de `Folder.GetItemList(`) :
`grep -rniE '\.getitems'` sur les mêmes 16 fichiers remonte 4 occurrences
supplémentaires, toutes des appels sur des champs d'instance injectés (pas la
classe statique `Folder`/`BaseItem`) et toutes préexistantes (`git blame`) :
`LibraryManager.cs:1808,1941` (`_itemRepository.GetItems(query)`, introduites
par `fe9f4e06d13`, 2020, ère Emby) et `LiveTvManager.cs:156,511`
(`_libraryManager.GetItemsResult(...)`, introduites par `40442f887ba`/`48facb797ed`,
2017/2018). Verdict du critère 4 inchangé.

## Critère 5 — paramètres ctor mesurés et expliqués

### `LibraryManager`

Ctor actuel (`LibraryManager.cs:160-193`) : **33 paramètres** (compté ligne à
ligne, confirmé par script). Référence RFC §7.5 : **30** avant PR106a.

| Delta | Paramètre | PR | Sens |
|---|---|---|---|
| −1 | `Lazy<IUserViewManager> userViewManagerFactory` | PR110 | retrait — confirmé absent du ctor (§1/§2 ci-dessus) |
| +1 | `IUserViewCatalog userViewCatalog` | PR110 | ajout, à la même position, injection **directe** (pas `Lazy<T>`) — `LibraryManager.cs:170` |
| +1 | `IItemStore itemStore` | PR106a | ajout, `LibraryManager.cs:191` |
| +1 | `IUserViewFactory userViewFactory` | PR106b | ajout, `LibraryManager.cs:192` |
| +1 | `IUserRootFolderProvider userRootFolderProvider` | PR107 | ajout, `LibraryManager.cs:193` |

Solde : 30 − 1 + 1 + 1 + 1 + 1 = **33**. Chaque ajout est justifié par la
XML-doc du ctor (`LibraryManager.cs:157-159`) : `itemStore` reçoit l'abonnement
`ItemSaved`→`ItemAdded` (`:221`), `userViewFactory`/`userRootFolderProvider`
portent la délégation des façades historiques (§6). Aucun paramètre n'a été
ajouté « pour faire un chiffre » — chacun correspond à un leaf réel introduit
par une PR antérieure et consommé par au moins un appel de délégation vérifié.

`Lazy<T>` recensés dans `LibraryManager.cs` : **3** — `Lazy<ILibraryMonitor>`
(`:75`), `Lazy<IProviderManager>` (`:76`), `Lazy<IExternalDataManager>` (`:102`).
`Lazy<IUserViewManager>` (`:77` dans l'état RFC §7.4) a disparu ; aucun nouveau
`Lazy<T>` n'a été ajouté à `LibraryManager` lui-même (le nouveau `IUserViewCatalog`
est injecté direct, pas Lazy — cf. tableau ci-dessus).

Classe : `internal class LibraryManager : ILibraryManager, IItemLookupService`
(`LibraryManager.cs:66`) — `IUserRootFolderProvider` a bien disparu de la liste
d'interfaces implémentées (comparé à RFC §1, qui la citait encore présente
avant PR107).

### `UserViewManager`

Ctor actuel (`UserViewManager.cs:60-66`) : **6 paramètres** —
`ILibraryManager, ILocalizationManager, IChannelManager, IItemSortService,
IUserViewCatalog, IUserViewFactory`.

Journal plan v13 (entrée PR110) annonce « 8→6 params (ILiveTvManager/
ICollectionManager/ITVSeriesManager/IServerConfigurationManager morts retirés) ».
Vérifié par soustraction : 8 (avant) − 4 (retraits : `ILiveTvManager`,
`ICollectionManager`, `ITVSeriesManager`, `IServerConfigurationManager`, tous
absents du ctor actuel — confirmé, aucune trace) + 2 (ajouts : `IUserViewCatalog`,
`IUserViewFactory`) = 6. Confirmé exact, pas seulement le solde net.

## Critère 6 — zéro service locator, façades délèguent

`IServiceProvider` — grep sur `LibraryManager.cs`, `UserViewManager.cs`,
`ChannelManager.cs`, `LiveTvManager.cs` et les 6 leaves PR106-108
(`ItemStore.cs`, `UserViewFactory.cs`, `UserRootFolderProvider.cs`,
`ChannelCatalog.cs`, `LiveTvPresenceProvider.cs`, `UserViewCatalog.cs`) :
**zéro occurrence** dans les 10 fichiers.

Façades historiques — délégation vérifiée sans duplication :

- `ILibraryManager.GetUserRootFolder` → `LibraryManager.cs:1186-1189` :
  `return _userRootFolderProvider.GetUserRootFolder();`
- `ILibraryManager.GetNamedView` (4 surcharges) / `GetShadowView` →
  `LibraryManager.cs:2773-2836`, chacune un appel unique vers
  `_userViewFactory.GetNamedView(...)`/`GetShadowView(...)` avec les mêmes
  arguments, zéro logique intermédiaire.
- `IUserViewManager.GetUserViews` → `UserViewManager.cs:76-80` : passe-plat vers
  `_userViewCatalog.GetUserViews(query)` (critère 3).

Orchestration query : une seule (`IItemQueryService`, en place depuis PR86,
inchangé par ce chantier).

## Réévaluation des 4 exceptions runtime bénies (RFC §8)

### Exception 1 — `Lazy<IProviderManager>.Value` post-persistance (PR106b)

Chemin : `UserViewFactory.GetNamedView`/`GetShadowView` →
`ProviderManager.QueueRefresh(...)` — 4 sites (`UserViewFactory.cs:144,206,264,341`),
chacun dans la branche `if (refresh)`, après `_itemStore.CreateItem(item, null)`
(persistance+enregistrement, `:128,198,248,319`). DI :
`ApplicationHost.cs:560` (`AddTransient(provider => new
Lazy<IProviderManager>(provider.GetRequiredService<IProviderManager>))`).

**Statut** : toujours nécessaire — `ProviderManager` référence toujours
`ILibraryManager` dans son propre ctor (`ProviderManager.cs:117`, inchangé), un
`IProviderManager` direct romprait toujours I1. **Conserver telle quelle** :
c'est la même construction que `LibraryManager` utilise pour lui-même
(`LibraryManager.cs:76/276`), le timing (après persistance) est vérifié par test
(PR106b, `UserViewFactoryTests`/`LibraryManagerUserViewFactoryTests`), et c'est
strictement le comportement legacy reproduit à l'identique.

### Exception 2 — `UpdateToRepositoryAsync` → static `BaseItem.LibraryManager` (PR106b)

`BaseItem.UpdateToRepositoryAsync` (`Reefin.Controller/Entities/BaseItem.cs:2369-2370`) :

```csharp
public virtual async Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
 => await LibraryManager.UpdateItemAsync(this, GetParent(), updateReason, cancellationToken).ConfigureAwait(false);
```

`LibraryManager` ici est le statique `BaseItem.LibraryManager` (type
`ILibraryManager`, `BaseItem.cs:477`).

**Constat élargi par rapport au RFC/journal PR106b** (« vérifie dans le code, ne
crois pas le RFC sur parole ») : la RFC et le journal PR106b décrivent un seul
site de contact — la surcharge `GetNamedView(string, CollectionType, string)`
sans `parentId` (`UserViewFactory.cs:205`). **Un second site existe** :
`UserViewFactory.cs:327`, dans la surcharge `GetNamedView(string name, Guid
parentId, CollectionType? viewType, string sortName, string uniqueId)` (celle
**avec** `parentId`, utilisée par `UserViewManager.GetUserSubViewWithName` pour
les sous-vues) :

```csharp
if (viewType != item.ViewType)
{
    item.ViewType = viewType;
    item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
}
```

Conditionnel (`viewType != item.ViewType`, cas de mise à jour d'une sous-vue
existante avec un type différent), mais réel : ce chemin est emprunté par
`GetUserSubViewWithName`/`GetUserSubView` (`UserViewManager.cs:82-93`), donc
potentiellement par tout appelant de sous-vues (playlists/boxsets sous-vues,
etc.), pas seulement par le cas « vue racine sans parentId » que le RFC
documentait. L'exception 2 est donc **plus large** que documenté — même nature
(static préexistant, reproduit verbatim, hors graphe de construction I1 intact),
mais deux sites au lieu d'un.

**Statut** : toujours nécessaire, portée corrigée. **Conserver telle quelle**
(comportement iso-legacy sur les deux sites, aucune régression), mais **mettre à
jour le §8 du RFC** pour citer les deux sites plutôt qu'un seul (fait ci-dessous).
Reste couvert par le chantier statics séparé (`docs/major-rewrite-plan-v13.md`,
hors périmètre RFC v2) — aucune recommandation de traitement immédiat au-delà de
la correction documentaire.

### Exception 3 — `Lazy<IReadOnlyList<ILiveTvService>>.Value` à chaque `GetEnabledUsers` (PR108)

Chemin : `LiveTvPresenceProvider.IsLiveTvEnabled` (`LiveTvPresenceProvider.cs:126-130`)
lit `Services.Count` (propriété `:106`, `=> _servicesFactory.Value`), appelée
depuis `GetEnabledUsers` (`:109-112`) — donc à **chaque** appel, y compris depuis
`UserViewCatalog.GetUserViews` (`UserViewCatalog.cs:231`). DI :
`ApplicationHost.cs:568`. `DefaultLiveTvService` (l'implémentation in-tree de
`ILiveTvService`) prend `ILibraryManager` directement dans son propre ctor —
matérialiser `.Value` matérialise donc `DefaultLiveTvService`, une arête
runtime réelle vers `ILibraryManager`.

Le code source lui-même (`LiveTvPresenceProvider.cs:61-69`) flague explicitement
cette exception comme **« not yet blessed »**, demandant un arbitrage explicite
au « RFC/PR109-110 boundary » — jamais tranché avant PR111.

**Statut : tranché ici, PR111.** **Conserver telle quelle**, ajoutée à la liste
bénie du RFC §8 (mise à jour ci-dessous). Justification :
- Le construction-graphe (I1) reste intact — le `Lazy<T>` exclut cette arête du
  graphe eager, seule protection requise pour éviter un cycle de construction.
- Le comportement runtime est **matériellement identique au legacy** :
  `LiveTvManager` (non migré) tient déjà `IEnumerable<ILiveTvService> services`
  en champ direct dans son propre ctor (`LiveTvManager.cs:63`), donc les
  implémentations `ILiveTvService` (dont `DefaultLiveTvService`) étaient déjà
  matérialisées eager côté legacy à chaque construction de `LiveTvManager` — ce
  n'est pas une nouvelle matérialisation introduite par PR108, seulement un
  déplacement du point d'évaluation (`.Value` au lieu du ctor).
- L'usage est strictement `Count`-only (`Services.Count > 1`), jamais
  d'invocation de méthode sur les instances de service.

**Recommandation pour un chantier futur** (pas un blocage de clôture) : un port
étroit « service count » (`int GetLiveTvServiceCount()` ou équivalent, résolu
sans matérialiser les instances) éliminerait même cette arête runtime résiduelle.
Non nécessaire pour la clôture — la RFC elle-même proposait cette alternative
« si non accepté », et le comportement iso-legacy justifie l'acceptation.

### Exception 4 — `Lazy<IItemQueryService>.Value` sur le probe playlists/boxsets (PR109)

Chemin : `UserViewCatalog.GetUserViews` (`UserViewCatalog.cs:162-175`), forcé
quand une bibliothèque `playlists`/`boxsets` existe :

```csharp
var items = _itemQueryServiceFactory.Value.GetItemList(
    folder,
    new InternalItemsQuery(user) { ParentId = folder.ParentId });
```

`ItemQueryService` (`Reefin.Server.Core/Library/ItemQueryService.cs:38`) prend
`IUserViewManager` **et** `IChannelManager` directs, non-Lazy, dans son propre
ctor — pour son propre fallback de compat pré-PR86
(`folder.GetItems(...)`/`GetItemList(...)`, `ItemQueryService.cs:55,65`). DI :
`ApplicationHost.cs:577`.

Comme l'exception 3, le code source (`UserViewCatalog.cs:66-78`) flague
explicitement **« not yet blessed »**, demandant l'arbitrage au « RFC/PR110-111
boundary ».

**Statut : tranché ici, PR111.** **Conserver telle quelle**, ajoutée à la liste
bénie du RFC §8. Justification :
- I1 intact (même mécanisme `Lazy<T>`).
- Comportement runtime **matériellement équivalent au legacy** : le probe
  historique (`UserViewManager.cs:74-83` dans l'état RFC, avant PR109) passait
  déjà par `Folder.GetItemList(...)` → static `BaseItem.LibraryManager` (RFC §3)
  — le listing touchait donc *déjà* la SCC au runtime avant ce chantier. Le
  remplacement par `Lazy<IItemQueryService>.Value` ne fait que déplacer le point
  de contact (interface DI au lieu de statique), pas l'introduire.
- **Risque de récursion réel et testé** : `UserViewManager → IUserViewCatalog →
  Lazy<IItemQueryService> → ItemQueryService → IUserViewManager` referme un
  cycle d'objets (pas de construction, un vrai retour vers l'instance) —
  couvert explicitement par `UserViewManagerDelegationRecursionTests` (PR110),
  qui prouve statiquement et par test que le fast-path
  (`CanUseRawQueryItemsFastPath`) est toujours emprunté pour ce probe précis, de
  sorte que `ItemQueryService` n'invoque jamais réellement
  `_userViewManager`/`_channelManager` sur ce chemin (`SupportsRawQueryItems`
  vrai sur `CollectionFolder`, pas de récursion en pratique). Testé avec un
  décorateur à garde de ré-entrance sur le vrai cycle d'objets — vert.

**Recommandation pour un chantier futur** (pas un blocage) : un port
probe-only étroit sur `IItemQueryService.GetItemList(Folder,
InternalItemsQuery)` (sans les dépendances `IUserViewManager`/`IChannelManager`
de compat) éliminerait le cycle d'objets résiduel — mentionné explicitement par
la RFC comme alternative « si non accepté ».

### Synthèse

Les 4 exceptions sont **conservées telles quelles**, aucune n'a été éliminée par
ce chantier (hors périmètre RFC), toutes restent I1-safe (construction). Les
exceptions 3 et 4, non tranchées avant PR111 (le code lui-même le signalait),
sont ici explicitement **blessed** avec justification écrite — mise à jour du
RFC §8 ci-dessous. Deux chantiers futurs nommés mais non requis pour la clôture :
port « service count » (exception 3), port probe-only (exception 4). Le
chantier statics (exception 2, portée élargie à 2 sites) reste dans le
périmètre séparé déjà documenté (`docs/major-rewrite-plan-v13.md`).

## Chemins résiduels vers l'ex-SCC (inventaire complet)

Le rapport PR110 (journal plan v13) en listait 3 ; vérifiés et un 4e (implicite,
le cycle d'objets exception 4) ajouté :

1. **`UserViewManager → ILibraryManager`** — `GetItemsForLatestItems`
   (`UserViewManager.cs:141,172,278,284,290,294`), directe, sens unique.
2. **`UserViewManager → IChannelManager`** — `GetLatestChannelItemsInternal`
   (`UserViewManager.cs:144`, dans `GetItemsForLatestItems`), directe, sens unique.
3. **`ChannelManager → ILibraryManager`** — ctor direct (`ChannelManager.cs:75`),
   19 usages, sens unique, hors périmètre user-views (features, delete, DTOs,
   media sources — RFC §4).
4. **`LiveTvManager → ILibraryManager`** — ctor direct (`LiveTvManager.cs:58`),
   18 usages, sens unique, hors périmètre user-views (`GetNamedView` migré,
   §2 ci-dessus) ; usages restants = guide, timers, recording DTOs.
5. **`ItemQueryService → IUserViewManager`** et **`→ IChannelManager`** — ctor
   direct, non-Lazy (`ItemQueryService.cs:38`), pour le fallback de compat
   `Folder.GetItems`/`GetItemList` pré-PR86 (`:55,65`) — feuille entrante, jamais
   appelée eager grâce à `Lazy<IItemQueryService>` partout où elle est atteinte
   depuis la SCC (exception 4 ci-dessus, `UserViewCatalog.cs:131`).
6. **Cycle d'objets runtime (pas de construction)** :
   `UserViewManager → IUserViewCatalog → Lazy<IItemQueryService> →
   ItemQueryService → IUserViewManager` (retour à la même instance) — cassé au
   niveau construction par le `Lazy<T>`, mais un vrai cycle de références
   d'objets existe et pourrait boucler à l'exécution si `ItemQueryService`
   empruntait son slow-path sur le probe playlists/boxsets. Prouvé ne jamais se
   produire pour ce probe précis par `UserViewManagerDelegationRecursionTests`
   (test à garde de ré-entrance, PR110) — voir exception 4.
7. **Static `Folder.UserViewManager`** (`Folder.cs:55`) — assigné au bootstrap
   (`ApplicationHost.cs:741`), lu dans les surcharges de compat pré-PR86 de
   `Folder.GetItems`/`GetItemList` (`Folder.cs:1026-1099,2034,2074`) et
   `RecordingsManager.cs` — pendant `IUserViewManager` du static `BaseItem.LibraryManager`
   documenté RFC §3/§8, hors périmètre PR106-110 (fichiers non touchés).

Aucun de ces 7 chemins ne referme de cycle de **construction** (I1) — tous
passent soit par une arête à sens unique sans retour, soit par un `Lazy<T>`
explicitement conçu pour ce faire. Le chemin 6 est le seul qui soit un vrai
cycle de **références d'objets** à l'exécution ; il est neutralisé par le
comportement de fast-path du code existant, pas par construction du graphe —
distinction documentée explicitement dans le test dédié.

## Verdict final

**Le chantier DI query/user-views est clos.** Les 6 critères de sortie PR105b
(RFC §9/PR111) sont tous vérifiés :

1. `Lazy<IUserViewManager>` : zéro occurrence — ✅
2. Cycle de construction cassé : les arêtes 1 et 5 du tableau §7.2 ont disparu,
   `graphify path` bidirectionnel confirme l'absence de chemin direct à 2 hops
   entre les 4 ex-membres dans les deux sens ; la SCC à 4 membres ne peut plus
   se reformer (aucune arête ne repart de `LibraryManager`) — ✅
3. Une seule implémentation réelle de `GetUserViews` (`UserViewCatalog`,
   `UserViewManager.GetUserViews` délègue sans dupliquer) — ✅
4. Zéro nouveau fallback statique dans le code PR106a-110 (les seules
   occurrences trouvées sont soit des commentaires, soit du code préexistant
   confirmé par `git blame`, soit l'assignation de bootstrap déjà existante) — ✅
5. Paramètres ctor mesurés et expliqués sans objectif chiffré : `LibraryManager`
   30→33 (+3, chaque delta justifié), `UserViewManager` 8→6 (confirmé exact),
   `Lazy<T>` `LibraryManager` 4→3 — ✅
6. Zéro service locator (`IServiceProvider` absent des 10 nœuds vérifiés) ;
   orchestration query unique (`IItemQueryService`, inchangé depuis PR86) ;
   façades historiques délèguent sans dupliquer — ✅

**Reste à faire (hors périmètre de clôture, chantiers futurs nommés, pas
bloquants)** :

- Chantier statics (`BaseItem.LibraryManager`/`Folder.UserViewManager`) —
  périmètre séparé déjà documenté ; l'exception 2 a une portée élargie à
  documenter dans le RFC (fait ci-dessous).
- Port « service count » pour retirer l'exception 3 (`LiveTvPresenceProvider`).
- Port probe-only pour retirer l'exception 4 (`UserViewCatalog`), ce qui
  éliminerait aussi le cycle d'objets résiduel #6.
- Les 4 arêtes à sens unique restantes (§ « chemins résiduels » #1-4) sont
  attendues et non problématiques (ce sont des consommateurs légitimes de
  `LibraryManager`/`ChannelManager`, pas des membres d'une SCC) — aucune action
  requise.

Prochain jalon (RFC/plan v13) : gate playback pré-canary (après PR111).

## Test de verrouillage ajouté

Aucun test préexistant n'asserte que le ctor de `LibraryManager` n'a pas de
paramètre `IUserViewManager`/`Lazy<IUserViewManager>`, ni que le ctor de
`UserViewManager` n'a pas de paramètre `ILiveTvManager`/`ICollectionManager`/
`ITVSeriesManager` — les tests `DiWiring_...` existants (`UserViewFactoryTests`,
`ChannelCatalogTests`, `LiveTvPresenceProviderTests`, `UserRootFolderProviderTests`,
`UserViewCatalogTests`) portent tous sur les 6 nouveaux leaves, pas sur
`LibraryManager`/`UserViewManager` eux-mêmes.

Ajouté : `tests/Reefin.Server.Implementations.Tests/Library/Pr111SccClosureLockTests.cs`
(2 tests, motif réflexion pur `GetConstructors().Single()`, pas d'instanciation ni
de mock, à l'image des `DiWiring_...` existants) :

- `DiWiring_LibraryManagerConstructorGraph_NoUserViewManagerEdgeDirectOrLazy` —
  verrouille (a) : aucun paramètre ctor de `LibraryManager` n'est
  `IUserViewManager` ni `Lazy<IUserViewManager>`.
- `DiWiring_UserViewManagerConstructorGraph_NoDeadPr110Dependencies` —
  verrouille (b) : aucun paramètre ctor de `UserViewManager` n'est
  `ILiveTvManager`/`ICollectionManager`/`ITVSeriesManager`.

Délibérément **pas** de vérification que `UserViewManager` n'a pas
`ILibraryManager`/`IChannelManager` — ces deux-là restent des dépendances
légitimes (chemins résiduels #1-2 ci-dessus) ; les y interdire ferait échouer le
test sur du code correct.

## Mises à jour de documentation

- `docs/major-rewrite-plan-v13.md` : ligne « LibraryManager god-object » du
  tableau de statut par pilier passe à « SCC cassée (PR106a-110), clôture
  auditée (PR111) », prochain jalon = gate playback pré-canary.
- `docs/rfc-di-query-user-views-v2.md` : ligne « **Clos** : 2026-07-14 (PR111,
  `docs/pr111-di-closure-audit.md`) » ajoutée en tête d'en-tête ; §8 complété
  d'une phrase pointant vers la réévaluation PR111 (exceptions 3/4 tranchées,
  exception 2 élargie à 2 sites).

## Vérification

```
dotnet build Reefin.sln
dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj
```

Résultats :

- `dotnet build Reefin.sln` : 0 erreur (16 avertissements préexistants, dont les
  `NU1903` SQLitePCLRaw déjà connus).
- `Reefin.Server.Implementations.Tests` : **791 réussis / 0 échec / 4 ignorés**
  (baseline PR110 : 789 verts/4 ignorés + 2 nouveaux tests de verrouillage =
  791 — réconcilié).
