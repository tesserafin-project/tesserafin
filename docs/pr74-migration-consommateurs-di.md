# PR74 Migration de consommateurs lecture-seule vers IItemLookupService

Scope: auditer toutes les classes de production qui injectent `ILibraryManager` par
constructeur, isoler celles dont l'usage du champ est strictement limité à
`GetItemById` (dans ses variantes exposées par `IItemLookupService`), puis migrer 2-3
d'entre elles vers `IItemLookupService` pour valider que l'abstraction introduite en PR71
est réellement utile à des consommateurs autres que `LibraryManager` lui-même.

Méthode : `grep` des champs `private/protected readonly ILibraryManager _xxx;` dans tout le
repo (hors tests, hors `LibraryManager.cs`), puis extraction automatisée (script Python) des
appels `_xxx.Méthode(...)` par fichier, suivie d'une vérification manuelle ligne à ligne de
chaque candidat retenu (comptage `_libraryManager` total vs appels `GetItemById` détectés,
pour détecter les usages non capturés par la regex : propriétés multi-lignes, événements,
appels transmis en argument). `graphify query`/`explain` ont été essayés en préambule
(instruction du hook `graphify-out/`) mais ne fournissent pas la granularité "usage de champ
par classe" nécessaire à cet audit ; le grep structuré a été la méthode effective.

## Vue d'ensemble des comptes

- **156** classes de production injectent `ILibraryManager` par constructeur (un premier
  passage grep en avait trouvé 159 ; 3 faux positifs retirés après relecture ciblée —
  `Reefin.Api/Helpers/StreamingHelpers.cs`, `Reefin.Server.Core/Library/ResolverHelper.cs` et
  `Reefin.Controller/Library/LibraryManagerExtensions.cs` sont des classes **statiques** dont
  les méthodes prennent `ILibraryManager` en paramètre — ce n'est pas de l'injection par
  constructeur, elles sont hors périmètre de cet audit).
  - **41** se contentent de transmettre le paramètre à un constructeur de base ou à un objet
    composé construit localement, sans le stocker dans un champ propre à la classe — ce ne
    sont pas des "consommateurs" au sens de cette tâche. Non auditées individuellement
    (catégorie, cf. ci-dessous).
  - **115** déclarent un champ `ILibraryManager` propre et l'utilisent (ou non) directement.
- Parmi ces 115 : **18** n'appellent que `GetItemById` (toutes variantes confondues).
  - **6** de ces 18 utilisent exclusivement l'overload `GetItemById<T>(Guid id, Guid userId)`,
    qui **n'est pas** sur `IItemLookupService` (nécessite `IUserManager` en interne) → non
    éligibles sans résolution locale du `User`.
  - **12** sont éligibles au sens strict de la tâche (overloads `GetItemById(Guid)`,
    `GetItemById<T>(Guid)`, `GetItemById<T>(Guid, User?)` uniquement).
- **3 migrées** dans cette PR (voir plus bas), **9 éligibles restantes** pour une PR
  ultérieure.

## Catégorie : injection transmise sans stockage de champ (41, non éligible, non auditée ligne à ligne)

Ces classes déclarent `ILibraryManager libraryManager` en paramètre de constructeur et le
transmettent tel quel, sans le stocker dans un champ propre :

- la plupart (37) le passent à `base(...)` — le champ réel appartient à la classe de base
  (`MetadataService<TItemType, TIdType>`, `BaseXmlSaver`, `BaseNfoSaver`,
  `BaseFolderImageProvider`) qui, elle, utilise `ILibraryManager` pour bien plus que
  `GetItemById` (queries, `GetLibraryOptions`, etc.) — donc hors périmètre par transitivité ;
- `Reefin.Providers/MediaInfo/ProbeProvider.cs` fait exception : il ne dérive d'aucune classe
  de base concernée, mais construit lui-même `FFProbeVideoInfo` et `AudioFileProber` (composition
  locale) en leur transmettant `libraryManager` — ces deux classes sont déjà auditées
  individuellement plus bas (elles déclarent leur propre champ) et ne sont pas éligibles
  (`GetLibraryOptions`, `UpdatePeople` en plus de `GetItemById`).

Liste (41) : les ~30 sous-classes de `MetadataService<,>` dans `Reefin.Providers/**`
(`BookMetadataService`, `MovieMetadataService`, `SeriesMetadataService`, etc.), les 7
sous-classes de `BaseNfoSaver`/`BaseXmlSaver` (`Reefin.XbmcMetadata/Savers/*`,
`Reefin.LocalMetadata/Savers/*`), les 3 sous-classes de `BaseFolderImageProvider`
(`FolderImageProvider`, `MusicAlbumImageProvider`, `PhotoAlbumImageProvider`), et
`Reefin.Providers/MediaInfo/ProbeProvider.cs`.

## Candidats éligibles (12) — usage limité à GetItemById(/<T>)/(Guid)/(Guid,User?)

| # | Classe | Fichier | Méthode(s) `ILibraryManager` utilisées | Migré ? |
|---|--------|---------|------------------------------------------|---------|
| 1 | `SeriesNfoSeasonProvider` | `Reefin.XbmcMetadata/Providers/SeriesNfoSeasonProvider.cs` | `GetItemById(Guid)` | **Oui** |
| 2 | `LiveTvMediaSourceProvider` | `src/Reefin.LiveTv/LiveTvMediaSourceProvider.cs` | `GetItemById(Guid)` (via extension `string` avant migration) | **Oui** |
| 3 | `MediaInfoHelper` | `Reefin.Api/Helpers/MediaInfoHelper.cs` | `GetItemById<T>(Guid)` | **Oui** |
| 4 | `InstantMixController` | `Reefin.Api/Controllers/InstantMixController.cs` | `GetItemById<T>(Guid, User?)` (6 sites) | Non — contrôleur API, reporté |
| 5 | `MediaInfoController` | `Reefin.Api/Controllers/MediaInfoController.cs` | `GetItemById<T>(Guid, User?)` | Non — contrôleur API, reporté |
| 6 | `PlaystateController` | `Reefin.Api/Controllers/PlaystateController.cs` | `GetItemById<T>(Guid, User?)` | Non — contrôleur API, reporté |
| 7 | `UniversalAudioController` | `Reefin.Api/Controllers/UniversalAudioController.cs` | `GetItemById<T>(Guid, User?)` | Non — contrôleur API, reporté |
| 8 | `PlaybackSessionsController` | `Reefin.Api/Controllers/PlaybackSessionsController.cs` | `GetItemById<T>(Guid)` | Non — contrôleur API, reporté |
| 9 | `SearchController` | `Reefin.Api/Controllers/SearchController.cs` | `GetItemById<T>(Guid)` | Non — contrôleur API, reporté |
| 10 | `MediaSourceManager` | `Reefin.Server.Core/Library/MediaSourceManager.cs` | `GetItemById<T>(Guid, User)` + `GetItemById(Guid)` | Non — 14 dépendances constructeur, classe centrale à haut risque, reporté |
| 11 | `SessionManager` | `Reefin.Server.Core/Session/SessionManager.cs` | `GetItemById(Guid)` (5 sites) | Non — classe très large/centrale (sessions), reporté |
| 12 | `Group` (SyncPlay) | `Reefin.Server.Core/SyncPlay/Group.cs` | `GetItemById(Guid)` (6 sites) | Non — instancié manuellement (`new Group(...)`) par `SyncPlayManager`, qui devrait aussi être migré (champ `ILibraryManager` non utilisé directement, seulement transmis) ; regroupé avec #4-11 pour rester à 3 migrations cette PR |

Vérification anti-faux-positif effectuée sur les 9 candidats non migrés : comptage
`grep -c _libraryManager` total du fichier comparé au nombre d'appels
`_libraryManager.GetItemById` détectés — l'écart est exactement 2 dans les 9 cas (déclaration
de champ + affectation dans le constructeur), ce qui exclut tout usage caché (propriété,
événement, appel multi-lignes, passage en argument).

## Candidats exclus : overload `GetItemById<T>(Guid, Guid userId)` (6)

Ces classes n'utilisent que `GetItemById`, mais via l'overload résolvant un `userId: Guid`
(`ILibraryManager.GetItemById<T>(Guid, Guid)`, absent d'`IItemLookupService` par design — cf.
remarque dans `IItemLookupService.cs`). Les migrer proprement demanderait de résoudre le
`User` localement (ajout d'une dépendance `IUserManager` déjà présente indirectement via
`User.GetUserId()` côté HTTP, mais pas de résolution `User` faite dans ces contrôleurs) : hors
périmètre "migration sûre et minimale" de cette PR.

| Classe | Fichier | Appel typique |
|---|---|---|
| `ItemLookupController` | `Reefin.Api/Controllers/ItemLookupController.cs` | `_libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId())` |
| `ItemRefreshController` | `Reefin.Api/Controllers/ItemRefreshController.cs` | idem |
| `LyricsController` | `Reefin.Api/Controllers/LyricsController.cs` | idem (5 sites) |
| `RemoteImageController` | `Reefin.Api/Controllers/RemoteImageController.cs` | idem (3 sites) |
| `SubtitleController` | `Reefin.Api/Controllers/SubtitleController.cs` | mixte : certains sites `(itemId, User.GetUserId())`, d'autres `(itemId)` seul |
| `VideoAttachmentsController` | `Reefin.Api/Controllers/VideoAttachmentsController.cs` | idem |

## Champs déclarés mais non éligibles pour d'autres raisons (extrait représentatif)

Le reste des 115 (115 − 18 = 97) utilise `ILibraryManager` pour au moins une méthode hors
`GetItemById` (mutation, query/listing, `GetCollectionFolders`, `GetLibraryOptions`,
`FindByPath`, `GetUserRootFolder`, événements, `Sort`, etc.) — exclu par les critères de la
tâche. Quelques cas particuliers relevés pendant l'audit, notables car non détectés par un
simple grep de noms de méthode :

| Classe | Fichier | Raison réelle |
|---|---|---|
| `SystemManager` | `Reefin.Server.Core/SystemManager.cs` | `GetVirtualFolders()` appelé sur la ligne suivante (retour à la ligne après `_libraryManager`) — invisible à une regex mono-ligne naïve |
| `OptimizeDatabaseTask` | `Reefin.Server.Core/ScheduledTasks/Tasks/OptimizeDatabaseTask.cs` | accès à la **propriété** `_libraryManager.IsScanRunning` (pas un appel de méthode) |
| `BackupService` | `Reefin.Server.Implementations/FullSystemBackup/BackupService.cs` | idem, `IsScanRunning` |
| `RefreshMediaLibraryTask` | `Reefin.Server.Core/ScheduledTasks/Tasks/RefreshMediaLibraryTask.cs` | cast vers le type concret `(LibraryManager)_libraryManager` pour appeler une méthode interne (`ValidateMediaLibraryInternal`) — dépendance à l'implémentation, pas à l'abstraction |
| `ItemQueryService` | `Reefin.Server.Core/Library/ItemQueryService.cs` | champ déclaré et affecté mais **jamais utilisé** (mort) — hors périmètre (nettoyage de champ mort n'est pas l'objet de cette PR) |
| `PlaylistsController` | `Reefin.Api/Controllers/PlaylistsController.cs` | idem, champ mort |
| `DynamicHlsController`, `AudioHelper`, `DynamicHlsHelper` | `Reefin.Api/Controllers/DynamicHlsController.cs`, `Reefin.Api/Helpers/AudioHelper.cs`, `Reefin.Api/Helpers/DynamicHlsHelper.cs` | champ transmis tel quel à un objet construit localement (`StreamState`, etc.), jamais appelé directement — "passthrough" |
| `ArtistsPostScanTask`, `GenresPostScanTask`, `MusicGenresPostScanTask`, `StudiosPostScanTask` | `Reefin.Server.Core/Library/Validators/*PostScanTask.cs` | passthrough vers `new XValidator(_libraryManager, ...)` |
| `SyncPlayManager` | `Reefin.Server.Core/SyncPlay/SyncPlayManager.cs` | passthrough vers `new Group(..., _libraryManager)` (cf. candidat #12 ci-dessus) |
| `RefreshChannelsScheduledTask` | `src/Reefin.LiveTv/Channels/RefreshChannelsScheduledTask.cs` | passthrough vers `new ChannelPostScanTask(..., _libraryManager)` |

Les migrations routines (`Reefin.Server/Migrations/Routines/*.cs`, 10 fichiers) utilisent
toutes au moins une méthode de mutation (`DeleteItem`, `DeleteItemsUnsafeFast`) ou de listing
(`GetItemList`, `GetVirtualFolders`) en plus ou à la place de `GetItemById` : non éligibles.

## Classes migrées (3)

### 1. `SeriesNfoSeasonProvider` (`Reefin.XbmcMetadata/Providers/SeriesNfoSeasonProvider.cs`)

Provider NFO de saison, résolution du chemin de la série parente. Un seul appel :
`_libraryManager.GetItemById(info.ParentId)?.Path` → `_itemLookupService.GetItemById(info.ParentId)?.Path`.
Champ + paramètre constructeur `ILibraryManager libraryManager` → `IItemLookupService itemLookupService`.
Aucune instanciation manuelle trouvée (`grep new SeriesNfoSeasonProvider(` → 0 résultat) ; la
classe est résolue par `ApplicationHost.GetExports<IMetadataProvider>()` (résolution DI
standard via le conteneur), donc `IItemLookupService` (déjà enregistré comme le même singleton
que `ILibraryManager`) se résout sans changement supplémentaire. Choisi pour sa petite taille
(89 lignes, 8 dépendances) et son usage isolé.

### 2. `LiveTvMediaSourceProvider` (`src/Reefin.LiveTv/LiveTvMediaSourceProvider.cs`)

Provider de source média Live TV. Un seul appel, via l'extension `string`
`LibraryManagerExtensions.GetItemById(this ILibraryManager, string)` (qui n'existe que sur
`ILibraryManager`, pas sur `IItemLookupService`) :
`(LiveTvChannel)_libraryManager.GetItemById(id)` (avec `id: string`) →
`(LiveTvChannel)_itemLookupService.GetItemById(new Guid(id))` (appel direct de l'overload
`Guid`, comportement strictement identique — l'extension ne faisait que `new Guid(id)` puis
déléguait au même overload). Résolu via `GetExports<IMediaSourceProvider>()`. Aucune
instanciation manuelle trouvée.

### 3. `MediaInfoHelper` (`Reefin.Api/Helpers/MediaInfoHelper.cs`)

Helper de streaming, un seul appel : `_libraryManager.GetItemById<BaseItem>(request.ItemId)`
→ `_itemLookupService.GetItemById<BaseItem>(request.ItemId)`. Enregistré explicitement en DI
(`serviceCollection.AddScoped<MediaInfoHelper>()` dans `ApplicationHost.cs`). Une seule
instanciation manuelle trouvée, dans un test :
`tests/Reefin.Api.Tests/Helpers/MediaInfoHelperTests.cs` — `Mock.Of<ILibraryManager>()` →
`Mock.Of<IItemLookupService>()` (le mock n'était de toute façon jamais configuré avec
`.Setup`, donc aucun changement de comportement de test).

## Pourquoi ces 3 et pas d'autres

- Fichiers isolés à une seule responsabilité, un seul site d'appel `GetItemById`, faible
  couplage (8-9 dépendances constructeur max).
- Aucune instanciation manuelle en dehors du conteneur DI (sauf le test `MediaInfoHelper`,
  trivial à adapter).
- Couvrent 3 assemblies différents (`Reefin.XbmcMetadata`, `Reefin.LiveTv`,
  `Reefin.Api`), démontrant que `IItemLookupService` est consommable au-delà du seul
  `Reefin.Server.Core`.
- Les contrôleurs API éligibles (#4-9) ont été volontairement écartés cette PR : la consigne
  demande d'éviter les "gros contrôleurs API", et bien qu'individuellement chacun soit petit,
  ils partagent un pattern (résolution `User?` via `_userManager.GetUserById`) qui mériterait
  d'être traité en lot dans une PR dédiée plutôt qu'au cas par cas ici.
- `MediaSourceManager`/`SessionManager` sont des classes centrales à fort rayon d'impact
  (beaucoup de dépendants) : migrer leur type de champ est mécaniquement sûr (même signature
  de méthode), mais le risque de régression perçu ne se justifie pas pour 3 migrations
  "preuve de valeur".
- `Group`/`SyncPlayManager` demandent une migration couplée à 2 fichiers (le champ
  `SyncPlayManager._libraryManager` n'est lui-même qu'un passthrough) ; laissé pour une PR
  dédiée au module SyncPlay.

## Reste pour plus tard

- 6 contrôleurs API éligibles à `IItemLookupService` (`InstantMixController`,
  `MediaInfoController`, `PlaystateController`, `UniversalAudioController`,
  `PlaybackSessionsController`, `SearchController`).
- `MediaSourceManager`, `SessionManager` : mêmes garanties de sécurité que les 3 migrées,
  simplement reportés par prudence/volume.
- `Group` + `SyncPlayManager` (migration couplée, 2 fichiers).
- 6 contrôleurs utilisant `GetItemById<T>(Guid, Guid userId)` — nécessitent d'abord de
  résoudre le `User` localement avant de pouvoir cibler `IItemLookupService`.
- Nettoyage optionnel hors périmètre : champs `ILibraryManager` morts dans `ItemQueryService`
  et `PlaylistsController`.

## Vérification

```
dotnet build Reefin.sln
dotnet test tests/Reefin.Controller.Tests/Reefin.Controller.Tests.csproj
dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj --filter "FullyQualifiedName~LibraryManagerItemLookupTests"
dotnet test tests/Reefin.Server.Implementations.Tests/Reefin.Server.Implementations.Tests.csproj
dotnet test tests/Reefin.Api.Tests/Reefin.Api.Tests.csproj
dotnet test tests/Reefin.XbmcMetadata.Tests/Reefin.XbmcMetadata.Tests.csproj
dotnet test tests/Reefin.LiveTv.Tests/Reefin.LiveTv.Tests.csproj
```

Résultats :

- `dotnet build Reefin.sln` : 41 projets, 0 erreur, 230 warnings (pré-existants, aucun
  nouveau lié à cette PR).
- `Reefin.Controller.Tests` : 243/243 réussis.
- `LibraryManagerItemLookupTests` (filtre) : 25/25 réussis.
- `Reefin.Server.Implementations.Tests` (suite complète) : 627/631 réussis, 4 ignorés
  (`ManagedFileSystemTests` Windows-only, pré-existants, sans rapport avec ce changement).
- `Reefin.Api.Tests` : 89/89 réussis (inclut `MediaInfoHelperTests` adapté).
- `Reefin.XbmcMetadata.Tests` : 37/37 réussis (inclut le provider migré).
- `Reefin.LiveTv.Tests` : 52/52 réussis (inclut le provider migré).
