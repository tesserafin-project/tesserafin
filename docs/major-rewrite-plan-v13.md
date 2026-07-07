# Revue de faisabilité — plan "Jellyfin/Reefin 13" (major volontairement incompatible)

Basé sur lecture du plan proposé + vérification directe dans le repo (graphify + grep). Fichiers/lignes cités = état réel au moment de la revue.

> Document déplacé de `graphify-out/` vers `docs/` (2026-07-07) : `graphify-out/` est un répertoire de sortie généré, non versionné, risque d'écrasement par un re-run graphify. Ce fichier est la source de vérité du chantier — à committer avec le repo.

## Constat préalable important — nom du repo

Ce repo n'est **pas** "Jellyfin" au sens strict : c'est un fork nommé **Reefin** (`origin` = `github.com/all3f0r1/reefin`, `README.md:1` "Reefin", solution `Reefin.sln`). Le rename `MediaBrowser.*` / `Emby.*` → nouveau nom est **déjà en cours**, mais vers `Reefin.*`, pas `Jellyfin.*` :
- `Reefin.Data`, `Reefin.Api`, `Reefin.Extensions`, `Reefin.Database.*`, `Reefin.LiveTv`, `Reefin.Server` existent déjà à côté de `MediaBrowser.Controller`, `MediaBrowser.Model`, `Emby.Server.Implementations` non renommés.

Le point 13 du plan ("renommer MediaBrowser/Emby → Jellyfin") doit donc être reformulé : le rename est déjà amorcé sous un autre nom cible.

**Décision (2026-07-06, confirmée avec l'utilisateur) : nom cible = `Reefin`.** Le fork assume une identité propre, pas de re-fusion amont prévue. Partout où le plan dit "Jellyfin.*" comme cible de rename, lire "Reefin.*". Le travail du point 13 devient : finir le rename `MediaBrowser.*`/`Emby.*` → `Reefin.*` déjà amorcé, pas repartir vers un autre nom.

## Verdict global

Direction technique saine : monolithe modulaire conservé, pas de microservices, pas de réécriture, C#/.NET conservé. Diagnostics (state global, DLNA comme modèle interne, LibraryManager god-object, plugins in-process) sont **confirmés par le code**, pas des suppositions. Le plan est faisable, mais c'est un chantier pluriannuel — il faut un ordre strict et des jalons de compatibilité, sinon risque de "grande réécriture qui ne finit jamais" (le piège que le plan dit vouloir éviter).

| # | Point | Faisabilité | Confirmé code ? |
|---|---|---|---|
| 1 | Nouveau protocole lecture (sessions serveur) | Faisable, priorité correcte | Oui |
| 2 | Abandon DLNA comme modèle interne | Faisable, gros volume | Oui |
| 3 | Monolithe modulaire (nouveaux projets) | Faisable, mécanique mais long | Partiel (déjà amorcé) |
| 4 | Découper `BaseItem` | Faisable, très invasif | Oui |
| 5 | Découper `LibraryManager` | Faisable, prioritaire | Oui |
| 6 | Jobs durables | Faisable, bien scoper | Non vérifié en détail |
| 7 | Plugin SDK v2 isolé (hors-process) | Faisable mais coûteux (IPC, perf) | Oui (risque confirmé) |
| 8 | Persistance EF Core + Postgres | Faisable, déjà en route | Oui (Sqlite seul provider) |
| 9 | API v2 | Faisable | Non vérifié en détail |
| 10 | Auth scopes | Faisable | Non vérifié en détail |
| 11 | Observabilité native | Faisable, coût modéré | Non vérifié en détail |
| 12 | Config snapshots versionnés | Faisable | Non vérifié en détail |
| 13 | Rename MediaBrowser/Emby → Reefin | **Terminé** (2026-07-07, voir suivi) | Oui |
| 14 | Labo compatibilité média | Faisable, gros ROI qualité | Non vérifié en détail |

## Détail des points vérifiés dans le code

### Point 1-2 — Protocole DLNA comme cœur de décision (confirmé)
- `MediaBrowser.Model/Dlna/StreamBuilder.cs` (community 14, ~2400+ lignes) est bien le point central : `GetVideoDirectPlayProfile()` L1281, `BuildVideoItem()` L649, `BuildStreamVideoItem()` L927, `GetVideoTranscodeProfile()` L840, `GetCompatibilityAudioCodec()` L2427.
- `DeviceProfile` (`MediaBrowser.Model/Dlna/DeviceProfile.cs`) + `ProfileCondition` + `CodecProfile` forment bien un modèle interne DLNA, pas juste un adaptateur externe.
- `Reefin.Api/Helpers/StreamingHelpers.cs` (`GetStreamingState()` L45) et `Reefin.Api/Helpers/MediaInfoHelper.cs` (`SetDeviceSpecificData()` L170) confirment la logique dispersée décrite dans le plan.
- Remplacer ça par un `PlaybackSessionPlanner` pur + protocole 3 opérations (create/patch/delete + diagnostics) est architecturalement correct. Attention : `StreamBuilder` est aussi utilisé par SyncPlay et Universal Audio (`UniversalAudioController.cs` L92) — la migration doit couvrir ces call sites, pas seulement DynamicHls.

### Point 3 — `DynamicHlsController` trop de dépendances (confirmé, légèrement en dessous de l'estimation)
`Reefin.Api/Controllers/DynamicHlsController.cs:79-90` : 11 dépendances injectées (`ILibraryManager`, `IUserManager`, `IMediaSourceManager`, `IServerConfigurationManager`, `IMediaEncoder`, `IFileSystem`, `ITranscodeManager`, `ILogger`, `DynamicHlsHelper`, `EncodingHelper`, `IDynamicHlsPlaylistGenerator`). Le plan dit "une dizaine" — exact. Remplacer par une façade unique (`playbackSessions.CreateAsync(...)`) est réaliste et réduit direct ce controller à peu de code.

### Point 4 — `BaseItem` + `SetStaticProperties` (confirmé, cité "dirty hack" dans le code lui-même)
`Emby.Server.Implementations/ApplicationHost.cs:669` : méthode `SetStaticProperties()`, commentaire ligne 667 **"Dirty hacks."** (littéral dans le code). Injecte 13+ managers dans des propriétés statiques de `BaseItem`/`CollectionFolder`/`Folder` (L672-687+) : `BaseItem.ChapterManager`, `.ChannelManager`, `.ConfigurationManager`, `.FileSystem`, `.ItemRepository`, `.LibraryManager`, `.MediaSourceManager`, `.ProviderManager`, etc. Le plan a raison de vouloir supprimer ça — c'est un anti-pattern reconnu par les mainteneurs eux-mêmes. Suppression faisable mais très invasive : chaque accès statique dans `BaseItem`/`Folder`/etc. doit devenir un paramètre de méthode ou un service injecté au niveau de l'appelant.

### Point 5 — `LibraryManager` (confirmé, chiffre exact)
`Emby.Server.Implementations/Library/LibraryManager.cs:137-161` : constructeur à **23 paramètres**, dont 4 `Lazy<T>` (`Lazy<ILibraryMonitor>`, `Lazy<IProviderManager>`, `Lazy<IUserViewManager>`, `Lazy<IExternalDataManager>`). `ApplicationHost.cs:550` et `:582` portent des commentaires **"TODO: Refactor to eliminate the circular dependencies here so that Lazy<T> isn't required"** — donc le besoin de découpage est déjà documenté par l'équipe elle-même, pas une supposition du plan. Découpage en `LibraryQueryService`/`LibraryMutationService`/`LibraryScanner`/etc. est cohérent et high-value en premier (moins risqué que toucher `BaseItem` en même temps).

### Point 7 — Plugins in-process (confirmé)
`Emby.Server.Implementations/Plugins/PluginLoadContext.cs` charge bien les assemblies via `AssemblyLoadContext` custom (`.Load()` L23). Les plugins officiels (`MusicBrainz`, `Tmdb`, `Omdb`, `AudioDb`, `ListenBrainz`, `StudioImages`) héritent `BasePlugin`/`BasePluginOfT` et s'enregistrent directement dans le même process. Aucune isolation de crash. Le passage à un modèle hors-process (gRPC/IPC) est faisable mais c'est le point le plus coûteux en ingénierie du plan (latence, sérialisation, debug plus dur) — à ne faire qu'après stabilisation de l'API v2 et du SDK contractuel, comme le plan le prévoit (étape 5), pas avant.

### Point 8 — Persistance (confirmé : SQLite seul fournisseur réel)
`find *Database*.csproj` ne retourne que `Reefin.Database.Implementations` et `Reefin.Database.Providers.Sqlite`. Aucun projet Postgres/Npgsql présent. Donc l'affirmation du plan "SQLite reste actuellement le seul fournisseur supporté" est exacte, pas une extrapolation. Objectif "un seul modèle de persistance, PostgreSQL après stabilisation" reste cohérent avec la structure actuelle (EF Core déjà en place, juste un provider).

## Points non vérifiés dans le code (jugement général, pas de citation)
Observabilité (11), config snapshots (12), auth scopes (10), API v2 formelle (9), jobs durables (6), labo de compatibilité (14) : direction raisonnable et standard dans l'industrie, mais je n'ai pas vérifié l'état actuel du code pour ces points précis. À auditer avant de committer un ordre de PR dessus, avec la même méthode (grep + citations) plutôt que de partir du texte du plan tel quel.

## Suivi rename point 13 (`MediaBrowser.*`/`Emby.*` → `Reefin.*`)

État initial (2026-07-06) : 157 namespaces `MediaBrowser.*` + 46 `Emby.*` restants, 11 `.csproj` non renommés (1194 fichiers `.cs`), contre 31 `.csproj` déjà en `Reefin.*`.

Ordre par taille croissante (nombre fichiers `.cs`) :
`Emby.Server.Implementations.Fuzz`(1) → `Emby.Photos`(3) → `MediaBrowser.LocalMetadata`(16) → `MediaBrowser.XbmcMetadata`(29) → `MediaBrowser.MediaEncoding`(30) → `MediaBrowser.Common`(42) → `Emby.Naming`(49) → `Emby.Server.Implementations`(153) → `MediaBrowser.Providers`(156) → `MediaBrowser.Model`(309) → `MediaBrowser.Controller`(407).

- [x] `Emby.Photos` → `Reefin.Photos` — commité (b3d251cd55, 1474ff5239).
- [x] `Emby.Server.Implementations.Fuzz` → `Reefin.Server.Implementations.Fuzz` — commité (990f136770, 1908450073). Dossier + `.csproj` + namespace + `fuzz.sh` (nom binaire produit) + `InternalsVisibleTo` dans `Emby.Server.Implementations`. Hors `Reefin.sln` (outil standalone). `Emby.Server.Implementations` lui-même volontairement pas renommé (153 fichiers, fait en dernier) — les refs `fuzz.sh` vers ce nom restent correctes telles quelles.
- [x] **Bloqueur environnement résolu** : SDK .NET 10 installé en cours de route, révélant que `Reefin.CodeAnalysis` (analyzer maison) était épinglé sur `Microsoft.CodeAnalysis.CSharp/Common/Analyzers` 5.6.0 alors que le SDK installé (10.0.109) héberge Roslyn 5.0.0.0 → CS9057 bloquant *tout* build du repo. Fixé (990f136770) en repin à 5.0.0/5.3.0, seul consommateur = ce projet. Build complet `Reefin.sln` vérifié après fix : **41 projets, 0 erreur**.
- [x] `MediaBrowser.LocalMetadata` → `Reefin.LocalMetadata` — commité (c854b0d2db). Build-vérifié (41 projets, 0 erreur). 2 régressions SA1210 (using mal placés après sed en masse) trouvées et corrigées avant commit.
- [x] `MediaBrowser.XbmcMetadata` → `Reefin.XbmcMetadata` — commité (4efc7c06c3). Build-vérifié (41 projets, 0 erreur). Plus large rayon d'action (touche `Emby.Server.Implementations`, `Reefin.Server/Startup.cs`, tout `tests/Reefin.XbmcMetadata.Tests`) — même classe de régression SA1210 sur 19 fichiers, tous corrigés avant commit.
- [x] `MediaBrowser.MediaEncoding` → `Reefin.MediaEncoding` — commité (59e5d97e62). Build-vérifié (41 projets, 0 erreur) + tests exécutés (109 passés, 1 skip macOS, 0 échec). A révélé un vrai bug (pas cosmétique) : `MediaEncoder.cs` appelait `Controller.Extensions.ConfigurationExtensions...` non qualifié, qui résolvait seulement par remontée d'espace de noms ancêtre (`MediaBrowser.MediaEncoding.Encoder` → `MediaBrowser` → sibling `MediaBrowser.Controller`). Renommer casse cette résolution implicite (CS0103). Corrigé en qualifiant pleinement. **Point de vigilance pour les projets restants** : chercher ce pattern (identifiants non qualifiés reposant sur la résolution d'espace de noms ancêtre) après chaque rename, le grep seul ne le détecte pas, seul un vrai build le révèle.
- [x] `MediaBrowser.Common` → `Reefin.Common` — commité (194ab0ae51). Rayon d'action le plus large jusqu'ici (264 fichiers externes référençaient ce namespace). Build/tests complets solution : 41 projets 0 erreur, 0 échec (2 échecs pré-existants confirmés indépendants dans `Reefin.Server.Tests.ParseNetworkTests`, environnement réseau sandbox). Trouvé + fixé 2 autres cas du même bug de résolution ancêtre-namespace (11 classes `MediaBrowser.Controller.Entities.*` utilisaient `[Common.RequiresSourceSerialisation]` non qualifié). Script `fix_usings.py` (scratch) écrit pour corriger en masse les régressions SA1210 (cascade sur ~230 fichiers, 8 itérations build/fix) — vérifié zéro faux positif sur fichiers déjà corrects avant usage en masse.
- [x] `Emby.Naming` → `Reefin.Naming` — commité (05424af95b). 64 fichiers externes, touche surtout l'arbre resolvers d'`Emby.Server.Implementations`. Même bug résolution ancêtre-namespace (`Naming.TV.X` non qualifié dans `SeriesResolver.cs`/`SeasonResolver.cs`) trouvé et fixé. Build 41 projets 0 erreur, tests clean (mêmes 2 échecs pré-existants réseau).
- [x] `Emby.Server.Implementations` → **`Reefin.Server.Core`** — commité (6f4d89568f). Collision de nom avec `Reefin.Server.Implementations` existant (couche services/persistance déjà extraite, distincte) : tranché avec l'utilisateur, nom cible = `Reefin.Server.Core`. `fuzz/Reefin.Server.Implementations.Fuzz` mis à jour cette fois (HintPath + `fuzz.sh`), car la cible du fuzzing est ce projet lui-même. Build 41 projets 0 erreur, tests clean (mêmes 2 échecs pré-existants réseau).
- [x] `MediaBrowser.Providers` → `Reefin.Providers` — commité (730e637243). Aucune collision. Même bug résolution ancêtre-namespace (`Model.MediaInfo.MediaInfo` non qualifié × 8, `Controller.Entities.TV.Episode` non qualifié × 2). Build 41 projets 0 erreur, tests clean (mêmes 2 échecs pré-existants réseau).
- [x] `MediaBrowser.Model` → `Reefin.Model` — commité (e0766af3eb). Rayon d'action le plus large jusqu'ici (851 fichiers externes). Build/tests complets solution : 41 projets 0 erreur, 0 échec (mêmes 2 échecs pré-existants réseau confirmés indépendants). 3 bugs de fond trouvés/fixés : 22 `Model.Entities.ExtraType`/`Model.MediaInfo.MediaProtocol` non qualifiés dans `MediaBrowser.Controller/Entities/*.cs` (qualifiés en `Reefin.Model.*`, pas `MediaBrowser.Model.*` — ce namespace n'existe plus) ; nouveau mode de bug : le paquet NuGet `MimeTypes` s'injecte via `contentFiles` avec `namespace $rootnamespace$`, donc atterrit maintenant dans `Reefin.Model` (suit RootNamespace du csproj renommé) — collision avec 2 appels test non qualifiés (`Reefin.Model.Tests` a pour ancêtre `Reefin.Model`), fixés par qualification complète ; `System.Web.HttpUtility` non qualifié également ombragé par le même mécanisme, fixé pareil. Script `fix_usings.py` a tourné sur ~480 fichiers en cascade sur 9 itérations build/fix (plus grosse vague jusqu'ici) ; 2 fichiers avec ligne vide coupant le bloc `using` en deux ont nécessité fix manuel (même piège que dans c854b0d2db).
- [x] `MediaBrowser.Controller` → `Reefin.Controller` — commité (52fffa0229). **Dernier projet, chantier terminé.** Aucune collision. 613 fichiers externes, plus gros rayon d'action du chantier. Nouveau variant du bug ancêtre-namespace : `Reefin.Controller.BaseItemManager.BaseItemManager` (namespace et classe même nom) — devient atteignable depuis `Reefin.Controller.Tests` une fois la racine renommée, masque la classe importée. Qualifié pleinement. Build 41 projets 0 erreur, tests clean (mêmes 2 échecs pré-existants réseau).

### Point 13 — TERMINÉ (2026-07-07)

Grep exhaustif post-commit final : **zéro** référence restante à `MediaBrowser.*`/`Emby.*` en tant que namespace dans le code (`.cs`/`.csproj`/`.sln`). Seuls 3 commentaires TODO historiques mentionnent encore "MediaBrowser.Api" (projet disparu, antérieur à ce fork) — pas du code, hors scope.

11 projets renommés au total (PR1 à PR11), tous vers `Reefin.*` sauf `Emby.Server.Implementations` → `Reefin.Server.Core` (collision de nom, tranché avec l'utilisateur). Chaque rename : build-vérifié (0 erreur solution complète) + tests exécutés (comparés aux 2 échecs pré-existants réseau, confirmés indépendants via `git stash`). Bug récurrent trouvé et fixé à chaque étape ou presque : identifiants non qualifiés reposant sur la résolution d'espace de noms ancêtre C#, cassés par renommage du sibling — jamais détectable par grep seul, seul un vrai build le révèle.

**Leçon procédure** : le rename `Emby.Photos` a été fait avant d'avoir un SDK build-checkable, ce qui a caché une régression réelle (ordre alphabétique des `using` cassé par le nouveau nom, SA1210) jusqu'au commit suivant. Désormais que le SDK est là, **chaque projet renommé doit être build-vérifié avant commit**, pas juste grep-vérifié.

## Risques principaux à surveiller pendant l'exécution
1. ~~**Ambiguïté de nom** (Jellyfin vs Reefin)~~ — **résolu** (2026-07-06) : cible `Reefin`, rename terminé (point 13).
2. ~~**`StreamBuilder` a plusieurs consommateurs**~~ — **infirmé** par PR1 (grep exhaustif) : `MediaInfoHelper` était le seul site d'instanciation de `StreamBuilder` ; SyncPlay et UniversalAudio passent déjà par lui.
3. **Plugin SDK v2 casse tous les plugins existants** — le plan l'assume explicitement (bien), mais ça doit être annoncé tôt aux mainteneurs de plugins tiers, pas seulement en fin de cycle.
4. **`BaseItem` et `LibraryManager` se touchent mutuellement** (LibraryManager manipule des `BaseItem`/`Folder` qui portent les statics) — découpler les statics (point 4) avant de découper `LibraryManager` (point 5) plutôt que l'inverse, sinon le découpage de `LibraryManager` doit composer avec un `BaseItem` encore instable.
5. **Cycle de vie des sessions hors transcodage** — la suppression repose uniquement sur `TranscodingJobEnded` (PR4). Dès que la création passe dans le chemin PlaybackInfo (PR5), le direct play (aucun job de transcodage, chemin le plus fréquent) et les probes `/PlaybackInfo` sans lecture ne meurent jamais : fuite réintroduite. Traité dans le scope obligatoire de PR5 ci-dessous.

## Suivi point 1 (sessions de lecture + protocole capacités v2)

- [x] PR1/N — `IPlaybackSessionPlanner` (décision pure, délégation à `StreamBuilder`), câblé dans `MediaInfoHelper` (commit eea0bf88da). Tests d'équivalence prouvant décisions identiques. Grep exhaustif : `MediaInfoHelper` était le **seul** site d'instanciation directe de `StreamBuilder` — l'affirmation du risque #2 ci-dessus (SyncPlay, UniversalAudio consommateurs séparés) est **obsolète/inexacte** dans l'état actuel du code ; ces call sites passent déjà par `MediaInfoHelper`, pas par leur propre `StreamBuilder`.
- [x] PR2/N — `IPlaybackSessionManager` : bookkeeping create/patch/delete en mémoire au-dessus du planner (commit c842cc1820). Câblé en DI, **pas encore câblé dans un controller**. Scope confirmé avec l'utilisateur avant construction (option "squelette du cycle de vie" retenue sur 3 propositions).
- [x] PR3/N — `IPlaybackSessionManager.Track(kind, plan)` : câblé dans `DynamicHlsController.GetVariantPlaylistInternal`, en lecture seule (diagnostic, pas de décision). Constat en cours de route : ce controller ne planifie rien — `TranscodeReasons` et tous les codecs/bitrate arrivent en query params déjà décidés par le client (probablement via un appel `/PlaybackInfo` antérieur, non vérifié ici) ; il n'y a pas de `MediaOptions` à ce niveau, seulement `StreamingRequestDto`/`StreamState`. D'où : `PlaybackPlan` reste (PlayMethod, TranscodeReasons) désormais stockés directement (`StreamInfo` devient optionnel) pour permettre `Track()` sans repasser par le planner ; `PlaybackSession.Request` devient nullable (`null` pour une session trackée). PlayMethod dérivé via `EncodingHelper.IsCopyCodec` sur les codecs de sortie vidéo+audio (idiome déjà utilisé partout dans `EncodingJobInfo`, pas une heuristique inventée) ; TranscodeReasons = `state.TranscodeReasons` (déjà parsé depuis le string client par `EncodingJobInfo`). **Delete non câblé** : le job de transcodage survit largement au-delà de cette requête HTTP (segments demandés ensuite par d'autres endpoints) et `TranscodeManager`/`TranscodingJob` n'exposent aucun hook de fin de job observable depuis ce controller — l'ajouter proprement demanderait un évènement sur `TranscodeManager` (changement plus large, pas fait ici). Scope confirmé avec l'utilisateur avant construction ("shallow lifecycle hook" retenu sur 3 propositions).
- [x] PR4/N — fin de session câblée sur fin de job de transcodage (commit 3028092a38). PR3 laissait une vraie fuite : sessions trackées jamais supprimées, dictionnaire croissant par requête variant-playlist. Fait : évènement `TranscodingJobEnded` sur `ITranscodeManager`, levé dans les 3 chemins de fin (`KillTranscodingJob`, `OnFfMpegProcessExited`, `OnTranscodeFailedToStart` — peut lever 2× pour un même job car kill déclenche aussi process-exit, documenté, handlers idempotents) ; `PlaybackSession.PlaySessionId` + dédoublonnage `Track()` (1 session max par PlaySessionId, re-track remplace le plan) + `DeleteByPlaySessionId` ; `PlaybackSessionManager` s'abonne à l'évènement (sens de dépendance : couche session observe couche transcodage, `TranscodeManager` ignore tout des sessions) ; stockage interne passé de `ConcurrentDictionary` à lock + 2 dictionnaires (sessions + index PlaySessionId) pour cohérence replace/delete. Décision prise en autonomie (advisor indisponible, utilisateur a dit de continuer) — la fuite rendait ce choix prioritaire sur l'alternative "retarget MediaInfoController". Limite restante : sessions trackées sans PlaySessionId n'ont toujours pas de hook de fin (acceptable pour couche diagnostic). Build 41 projets 0 erreur, suite complète solution 0 échec (même les 2 échecs réseau habituels passent ce run).
- [x] PR5/N — création de session déplacée vers le chemin PlaybackInfo (`MediaInfoHelper.SetDeviceSpecificData`, là où le plan est réellement calculé — sessions avec vrai `StreamInfo` et `Request` non-null). `Create` prend un `playSessionId` optionnel avec la même sémantique de dédoublonnage/remplacement que `Track` (1 session max par PlaySessionId, logique factorisée dans `StoreOrReplace` privé, partagée par `Create`/`Track`). La façade `DynamicHlsController` (remplacer les 11 dépendances) devient PR6+.
  **Scope obligatoire livré — cycle de vie hors transcodage** (risque #5, sinon la fuite corrigée en PR4 était réintroduite côté direct play) :
  - (a) abonnement à `ISessionManager.PlaybackStopped`, suppression par PlaySessionId — même patron de dépendance que `TranscodingJobEnded` (couche session observe, l'observé ignore tout des sessions) ;
  - (b) balayage TTL (6h, toutes les 30 min via `System.Threading.Timer`) en filet de sécurité pour les sessions jamais associées à une lecture (probe `/PlaybackInfo` sans lecture qui suit) ou dont aucun signal de fin n'arrive jamais ; TTL rafraîchi par `Create`/`Patch`/`Track` (`UpdatedAt`). Logique de balayage exposée en `internal SweepExpired(DateTimeOffset now)` (pas privée) pour rester testable sans dépendre du minuteur réel — `InternalsVisibleTo` vers le projet de tests déjà en place.
  `PlaybackSessionManager` implémente désormais `IDisposable` (désabonnement des deux évènements + arrêt du minuteur) ; classe scellée (`sealed`) pour satisfaire les analyzers CA1063/IDISP024 sans `Dispose(bool)` virtuel — pas de ressource native, pas de sous-classement prévu.
  Tests ajoutés dans `PlaybackSessionManagerTests` : dédoublonnage `Create` par PlaySessionId, suppression sur `PlaybackStopped`, balayage TTL (session expirée supprimée, session fraîche conservée). `MediaInfoHelperTests` mis à jour (mock `IPlaybackSessionManager` au lieu de l'ancien `IPlaybackSessionPlanner`, signature déjà obsolète depuis PR1).
  Build 41 projets 0 erreur, suite complète solution : 0 échec hors les 2 échecs réseau pré-existants confirmés indépendants (`Reefin.Server.Tests.ParseNetworkTests`).

- [x] PR6/N — premier pas de façade, scope minimal tranché avec l'utilisateur (squelette plutôt qu'un endpoint complet ou un audit) : `IPlaybackSessionManager.TrackTranscodeOutput(outputVideoCodec, outputAudioCodec, transcodeReasons, playSessionId)` encapsule la dérivation `PlayMethod` (copy codecs vidéo+audio → `DirectStream`, sinon `Transcode`, via `EncodingHelper.IsCopyCodec`) qui vivait auparavant dans `DynamicHlsController.GetVariantPlaylistInternal`. Le controller appelle désormais une seule méthode au lieu de calculer le plan à la main puis appeler `Track()`. Aucune des 10 autres dépendances du controller ni les autres endpoints touchés — reste pour un futur PR7+. Build 41 projets 0 erreur, suite complète 0 échec hors les 2 échecs réseau pré-existants.

### Audit PR7 (2026-07-07) — mapping dépendances × endpoints, `DynamicHlsController.cs` (2095 lignes)

Fait avant tout code, sur demande explicite (scope "auditer d'abord" retenu sur 3 propositions). Méthode : grep de chaque champ injecté, résolution manuelle de la méthode contenante par plage de lignes (7 endpoints publics + ~14 méthodes privées).

Endpoints et ce qu'ils utilisent réellement :
- `GetMasterHlsVideoPlaylist`/`GetMasterHlsAudioPlaylist` (L414-753, master.m3u8) : **une seule** dépendance chacun, `DynamicHlsHelper` (L525, L692). Déjà proprement isolés, rien à faire.
- `GetVariantHlsVideoPlaylist`/`GetVariantHlsAudioPlaylist` (L754-1095) : **zéro** dépendance directe — construisent un DTO et délèguent à `GetVariantPlaylistInternal`.
- `GetHlsVideoSegment`/`GetHlsAudioSegment` (L1096-1392) : **zéro** dépendance directe — délèguent à `GetDynamicSegment`.
- `GetLiveHlsStream` (L173-413, live.m3u8) : le plus lourd, 8 dépendances directes (`_mediaSourceManager`, `_userManager`, `_libraryManager`, `_serverConfigurationManager`, `_mediaEncoder`, `_encodingHelper`, `_transcodeManager`, `_logger`).

Cause racine des "11 dépendances" : **`StreamingHelpers.GetStreamingState(...)` est appelé 3 fois** (`GetLiveHlsStream` L1395, `GetVariantPlaylistInternal` L1395-1407, `GetDynamicSegment` équivalent L1438+) avec exactement les 7 mêmes arguments à chaque fois (`_mediaSourceManager`, `_userManager`, `_libraryManager`, `_serverConfigurationManager`, `_mediaEncoder`, `_encodingHelper`, `_transcodeManager`). Ce n'est pas de la logique controller — c'est du passthrough dupliqué 3×. C'est la vraie cible de la façade, pas les 11 dépendances prises une par une.

Déjà propres, pas des cibles PR7 :
- `_dynamicHlsHelper` : 1 seul point d'usage (master playlists), déjà isolé.
- `_dynamicHlsPlaylistGenerator` : 1 seul point d'usage (`GetVariantPlaylistInternal` L1433), déjà isolé.
- `_fileSystem` : cantonné à la fin de chaîne segment (`GetCurrentTranscodingIndex`, `DeleteLastFile`, `DeleteFile`) ; `GetLastTranscodingFile` le reçoit déjà en paramètre, pas en champ — découplage partiel déjà en place.
- `_transcodeManager` et `_encodingHelper` : usage large (cycle de vie du job de transcodage, construction des arguments ffmpeg dans `GetCommandLineArguments`/`GetAudioArguments`/`GetVideoArguments`). Logique domaine dense, risque élevé à extraire ici — relève plutôt du point 1-2 (abandon DLNA comme modèle interne), un chantier séparé du plan, pas de PR7.

Conclusion : la seule extraction à faible risque et fort ROI immédiat est de collapser les 3 appels dupliqués à `StreamingHelpers.GetStreamingState(...)` derrière un seul point (méthode privée du controller ou méthode sur la façade session). Ça ne réduit pas le nombre de dépendances injectées (les champs restent nécessaires), mais supprime la duplication et prépare le terrain si `IPlaybackSessionManager` (ou un nouveau service dédié) doit un jour posséder la résolution de `StreamState`.

- [x] PR7/N — collapsé les 3 appels dupliqués `StreamingHelpers.GetStreamingState(...)` (`GetLiveHlsStream`, `GetVariantPlaylistInternal`, `GetDynamicSegment`) derrière une seule méthode privée `ResolveStreamingState(streamingRequest, cancellationToken)`. Ne réduit pas le nombre de dépendances injectées (les 7 champs restent nécessaires en amont), supprime la duplication de 11 lignes d'arguments × 3. Les différences de cycle de vie entre sites d'appel préservées (`using var state` uniquement dans `GetVariantPlaylistInternal`, où `StreamState` est jetable en fin de méthode ; pas de `using` dans `GetLiveHlsStream`/`GetDynamicSegment`, où l'état survit à la requête HTTP via le job de transcodage). `_transcodeManager`/`_encodingHelper` (les 2 dépendances réellement larges) laissées de côté — relèvent du chantier point 1-2, pas de ce controller. Build 41 projets 0 erreur, suite complète 0 échec hors les 2 échecs réseau pré-existants.

## Jalons de compatibilité (point 1-2) — définis 2026-07-07

Le verdict global exige des jalons de compatibilité ; les voici pour la zone active. Le protocole v2 reste interne jusqu'à preuve de parité, la bascule client vient en dernier :

1. **J1 — cycle de vie étanche (interne)** : tout chemin de lecture crée et termine une session (transcodage via `TranscodingJobEnded` ; direct play via `PlaybackStopped` ; probe via TTL). Critère mesurable : le compteur de sessions revient à zéro après arrêt de toutes les lectures. Cible : fin de PR5.
2. **J2 — diagnostic exposé** : endpoint lecture seule (admin) listant les sessions, utilisé pour valider la parité des décisions v2 vs `/PlaybackInfo` en usage réel. PR6 amorce le nettoyage côté controller nécessaire, pas encore l'endpoint lui-même.
3. **J3 — API v2 create/patch/delete** exposée à côté de `/PlaybackInfo` (aucun retrait), migration des clients officiels un par un.
4. **J4 — retrait compat** : `/PlaybackInfo` + query params HLS dépréciés seulement après adoption par les clients officiels (dernière étape du plan, inchangée).

## Audit point 4 (2026-07-07) — `SetStaticProperties` / statics `BaseItem`

16 statics au total : 13 sur `BaseItem` (`Logger`, `LibraryManager`, `ConfigurationManager`, `ProviderManager`, `LocalizationManager`, `ItemRepository`, `ItemCountService`, `ChapterManager`, `FileSystem`, `UserDataManager`, `ChannelManager`, `MediaSourceManager`, `MediaSegmentManager`), plus `CollectionFolder.XmlSerializer`, `CollectionFolder.ApplicationHost`, `Folder.UserViewManager`.

Premier passage (agent Explore) : compte de fichiers où chaque static est **lu** (qualifié `BaseItem.X` + non qualifié dans `BaseItem.cs`/sous-classes). Classement obtenu, du plus petit au plus grand rayon d'action apparent : `LocalizationManager`/`ChapterManager`/`ItemRepository`/`ItemCountService`/`CollectionFolder.XmlSerializer` (1 fichier chacun) → ... → `LibraryManager` (29 fichiers, 148 lectures, appelants externes réels dans `Reefin.Providers`, confirmé le plus dur, à faire en dernier comme déjà noté).

**Correction après vérification manuelle** : ce compte de fichiers sous-estime le vrai risque. La métrique qui compte, c'est le nombre d'appelants de la **méthode qui contient** la lecture du static — pas le nombre de fichiers qui la lisent. Deux cas concrets qui inversent le classement naïf :
- `ChapterManager` semblait le plus simple (1 fichier, `BaseItem.cs` uniquement). En vérité, une des deux lectures est dans `GetImageInfo(ImageType, int)`, qui a **31 appelants** dans le repo — le vrai outlier du groupe "facile".
- `LocalizationManager` a une lecture dans le **setter** de la propriété `OriginalLanguage` (`BaseItem.cs:226`). Impossible d'ajouter un paramètre à un setter : supprimer ce static demande un changement de forme (méthode explicite plutôt que propriété) partout où `item.OriginalLanguage = ...` est appelé (8 sites trouvés). Les deux autres lectures (`IsParentalAllowed`, `GetParentalRatingScore`) n'ont qu'1 appelant chacune.
- `ItemRepository` (`Folder.cs`) : lectures dans `GetCachedChildren` (1 appelant) et `IsPlayed` (override, 3 appelants) — raisonnablement petit, confirmé.
- `ItemCountService` (`Folder.cs`) : lecture dans `FillUserDataDtoValues` (override, 3 appelants) — raisonnablement petit, confirmé.

Leçon méthode (même famille que le bug de résolution ancêtre-namespace du point 13) : **pour ce genre de refactor, l'audit doit compter les appelants de la méthode contenante, pas les fichiers qui lisent le static.** À refaire avant de scoper la suite de ce chantier.

## Ordre recommandé (reprend celui du plan, réordonné sur le point 3 ci-dessus)
1. Sessions de lecture + protocole capacités v2 (point 1-2) — priorité confirmée, zone la mieux comprise.
2. Suppression `SetStaticProperties` / statics `BaseItem` (point 4) **avant** découpage `LibraryManager`.
3. Découpage `LibraryManager` (point 5).
4. Persistance v2 (point 8) — déjà en route, continuer.
5. Plugin SDK v2 isolé (point 7) — après API v2 stabilisée, pas avant.
6. API v2 générale (point 9) + auth scopes (10) + observabilité (11) + config snapshots (12).
7. ~~Rename définitif (13)~~ — dans les faits, exécuté en **premier** (terminé 2026-07-07). Bon écart au plan : les refactors suivants ne renomment pas deux fois.
8. Suppression compat historique (dernière étape, conditionnée à l'adoption des nouveaux contrats par les clients officiels).
