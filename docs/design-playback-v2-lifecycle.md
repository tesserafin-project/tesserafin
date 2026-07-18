# Design — cycle de vie complet de la lecture v2 : `POST` → `GET` → `PUT` → `DELETE`, repli legacy atomique et identifiants de tentative

- **PR** : numéro non alloué avant ce document (même convention que
  `docs/pr116d-url-contract-design.md`) — design uniquement, aucun code de production, ni côté
  `reefin` ni côté `reefin-web`
- **Statut** : proposé — **bloquant** pour toute tranche d'implémentation du cycle de vie v2
- **Dépend de** : PR112/PR112b (contrat client v2, `docs/pr92-design-playback-api-and-diagnostics.md`),
  PR115a-d (canary, kill switch, seuils d'arrêt), **PR117** (`GET Playback/Sessions/{id}/Stream`,
  fusionné `15b95cb7d3`, `docs/pr116d-url-contract-design.md`), **PR118** (durcissement du contrat de
  session, fusionné `ca9ac78d8b` + `f5ecfc2a43`), PR116 (`docs/pr116-client-migration-design.md`)
- **Côté `reefin-web`** : PR116a/b/c/d/e fusionnées sur `origin/main` (`c8a3738594` au moment de
  l'audit) — voir l'avertissement méthodologique §0
- **Précède** : la tranche d'implémentation du cycle de vie (dite « X2 » dans le pilotage courant)
- **Dépôts concernés** : `reefin` (ce document, seul fichier écrit) et `reefin-web` (audité en lecture
  seule, aucun fichier écrit)

Ce document **audite** le cycle de lecture v2 réellement en place à travers les deux dépôts, puis fige
les décisions de cycle de vie qui manquent encore. Il ne re-litige pas ce que PR117 et PR118 ont déjà
tranché et fusionné : la sémantique du `PUT`, le contrat d'erreur du `GET .../Stream`, l'autorisation
propriétaire-ou-admin et l'isolation par requête du `StreamInfo` **existent en code** et sont cités
comme acquis (§1.1, §6.1, §6.2), pas reproposés.

La conclusion structurante est double, et les deux moitiés sont des constats de code, pas des
préférences :

1. **Le legacy `PlaybackInfo` ne peut pas disparaître** dans l'état actuel du contrat v2 — non pas à
   cause d'une poignée de champs résiduels, mais parce que `PlaybackInfoResponse` ne porte que **trois**
   champs (§2.1) dont l'un, `MediaSources`, est l'objet `MediaSourceInfo` complet dont dépend la
   quasi-totalité de `playbackmanager.js` en dehors de la construction d'URL. Le protocole v2 n'expose
   aujourd'hui **aucune** projection de cet objet. C'est le vrai verrou, et il détermine que la cible
   n'est pas « retirer le legacy » mais « rendre la tentative de lecture cohérente entre les deux ».
2. **Le cycle v2 n'est pas un cycle** : côté client, il n'existe qu'**un seul** point d'appel
   (`POST` + `GET`), placé dans **un seul** chemin de démarrage. Aucun `PUT`, aucun `DELETE` n'est
   jamais émis (§1.2), et le changement de piste retombe intégralement en legacy sans que la session v2
   créée en soit informée ni détruite (§1.3). Le cycle cible demandé (`POST` → `GET` → `PUT`/`GET` →
   `DELETE`) est donc **entièrement à construire côté client** ; côté serveur, les quatre verbes
   existent déjà.

---

## 0. Avertissement méthodologique — la branche auditée

`docs/pr116-client-migration-design.md` et `docs/pr116d-url-contract-design.md` désignent la branche de
travail `reefin-web` par des noms de tranche. Au moment de cet audit, le tronc réel de
`all3f0r1/reefin-web` est **`main`** (`gh repo view --json defaultBranchRef` → `main`), pas `master` :
`master` est une branche locale résiduelle qui suit encore `jellyfin/jellyfin-web` en amont et **ne
contient aucune** des tranches PR116a-e. Toute citation `reefin-web` de ce document porte sur
`origin/main` à `c8a3738594`.

C'est une observation à valeur opérationnelle, pas une remarque de forme : un audit mené sur `master`
conclurait que le chemin v2 client n'existe pas.

---

## 1. État des lieux — le cycle réel, de bout en bout

### 1.1 Serveur — les quatre verbes existent déjà, et sont durcis

`Reefin.Api/Controllers/PlaybackSessionsController.cs`, `[Route("Playback/Sessions")]` `[Authorize]`
(`:33-35`) :

| Verbe | Méthode | Lignes | Requête | Réponse |
|---|---|---|---|---|
| `POST Playback/Sessions` | `CreatePlaybackSession` | `:97-118` | `CreatePlaybackSessionRequest` | `PlaybackSessionResponse` |
| `PUT Playback/Sessions/{id}` | `ReplacePlaybackSession` | `:133-172` | `ReplacePlaybackSessionRequest` | `PlaybackSessionResponse` |
| `DELETE Playback/Sessions/{id}` | `DeletePlaybackSession` | `:182-205` | — | `204` |
| `GET Playback/Sessions/{id}/Stream` | `GetPlaybackSessionStream` | `:225-313` | `?startTimeTicks=` | `PlaybackSessionStreamDescriptor` |

Il n'y a **pas** de `PATCH` : PR92 §3 l'a remplacé par `PUT` (« le `PATCH` n'est pas un patch »,
`:121-124`) — la méthode interne du manager s'appelle toujours `Patch` (`:162`), écart de nommage
délibéré et documenté.

Trois points déjà tranchés par **PR118** (`ca9ac78d8b`, `f5ecfc2a43`) et donc **hors débat ici** :

- **Isolation par requête du `StreamInfo`.** En repli legacy, le resolver renvoie *l'instance même*
  retenue dans `session.Plan.StreamInfo` ; deux `GET .../Stream` concurrents avec des
  `startTimeTicks` distincts se volaient mutuellement `PlaySessionId`/`StartPositionTicks`.
  `StreamInfo.WithRequestContext(playSessionId, startPositionTicks)`
  (`Reefin.Model/Dlna/StreamInfo.cs`, appelé `PlaybackSessionsController.cs:296-298`) fait la copie
  superficielle et n'estampille que ces deux scalaires, sans jamais muter l'instance que la session
  conserve. `f5ecfc2a43` a remplacé un `Clone()` public par ce nom d'intention précisément parce qu'un
  `Clone()` générique invitait de futurs appelants à supposer une copie isolée qu'il ne fournit pas
  (`StreamOptions`, `MediaSource`, `DeviceProfile` restent partagés).
- **Autorisation.** `EnsureCallerOwnsSessionOrIsAdmin` (`:327-341`) garde les **trois** verbes qui
  opèrent sur une session existante (`GET .../Stream` `:248`, `PUT` `:154`, `DELETE` `:196`), toujours
  vérifiée contre la session **stockée**, jamais contre le corps de la requête.
- **Ordre de validation du jeton.** Le contrôle `User.GetToken()` précède désormais toute résolution
  (`:273-277`), donc tout effet diagnostics/métriques.

### 1.2 Client — un seul point d'appel, dans un seul chemin

Recensement exhaustif des appels `Playback/Sessions` hors SDK généré et hors tests, sur `origin/main` :

| Fichier | Rôle |
|---|---|
| `src/scripts/playbackSessionShadow.ts:98` | `POST` shadow (PR116b), fire-and-forget, **jamais** suivi d'un `GET .../Stream` — conforme à `docs/pr116d-url-contract-design.md` §5 |
| `src/scripts/playbackSessionShadowTrigger.ts` | chargeur paresseux du précédent (PR116e) |
| `src/scripts/playbackSessionV2Url.ts:204` (`POST`) et `:102` (`GET .../Stream`) | **le** chemin de lecture réelle (PR116d) |
| `src/components/playback/playbackmanager.js:3485` | **l'unique** site d'appel de `applyV2PlaybackUrlToStreamInfo` |

**Aucun `PUT`, aucun `DELETE` n'est émis nulle part.** Le SDK généré ne les expose pas non plus au
chemin de lecture (`src/lib/reefin-sdk/spec/version.json` est figé sur une spécification antérieure à
PR117 — `playbackSessionV2Url.ts:30-37` documente que le `GET .../Stream` est appelé par un wrapper
écrit à la main pour cette raison).

### 1.3 Le cycle réel, phase par phase

| Phase | Ce qui se passe réellement (flag `enableV2PlaybackPath` **actif**) | Preuve |
|---|---|---|
| **Démarrage de lecture** | `getPlaybackMediaSource` → `POST Items/{id}/PlaybackInfo` legacy (toujours), puis `createStreamInfo()` construit un `streamInfo` legacy **complet**, puis `applyV2PlaybackUrlToStreamInfo` tente le v2 et **écrase** `url`/`playMethod`/`playSessionId`/`mimeType`/URL de la piste de sous-titres par défaut | `playbackmanager.js:3419-3426`, `:3470-3477`, `:3485-3496` ; `playbackSessionV2Url.ts:402-412` |
| **Changement de piste** (audio, sous-titres, débit, `EnableDirectPlay=false` en retry) | `changeStream()` → `getPlaybackInfo()` **legacy** → `createStreamInfo()` → `changeStreamToUrl()`. **Aucun** appel v2. La lecture repasse silencieusement en URL legacy, et la session v2 créée au démarrage n'est ni mise à jour ni détruite | `playbackmanager.js:2022-2133`, absence de `applyV2PlaybackUrlToStreamInfo` dans ce bloc |
| **Seek** | `canPlayerSeek(player)` → `player.currentTime()` local, aucune requête. Sinon → `changeStream()`, donc legacy (ligne ci-dessus) | `playbackmanager.js:2023-2026` |
| **Retry** (échec direct play) | `onPlaybackError` → `changeStream(..., { EnableDirectPlay: false })` → legacy. La porte `enablePlaybackRetryWithTranscoding` lit `streamInfo.mediaSource.SupportsTranscoding`, champ que le v2 n'écrase pas, donc encore valide | `playbackmanager.js:4408-4468`, `:4388-4399`, `:4450-4459` |
| **Arrêt** | `apiClient.stopActiveEncodings(playSessionId)` puis rapport `PlaybackStopped`. Tant qu'aucun changement de piste n'a eu lieu, le `playSessionId` est bien celui du v2 et la corrélation serveur tient (le serveur l'a estampillé dans la query string, `StreamingHelpers.cs:528`) — mais **aucun `DELETE Playback/Sessions/{id}`** n'est émis | `playbackmanager.js:2148-2153`, `:2034`, `:4470`, `:4543` |
| **Expiration de session** | Purement serveur : balayage toutes les 30 min, TTL 6 h sur `UpdatedAt` (`PlaybackSessionManager.ExpiryTtl`/`SweepInterval`, `SweepExpired`). Éviction anticipée par `OnTranscodingJobEnded` et `OnPlaybackStopped`, tous deux via `DeleteByPlaySessionId` | `Reefin.MediaEncoding/Playback/PlaybackSessionManager.cs:20-21`, `:295-307`, `:369-381`, `:410-427` |

**Conséquence directe et vérifiable** : en `DirectPlay` (aucun job de transcodage à terminer), une
session v2 dont le client n'émet ni `DELETE` ni signal `PlaybackStopped` exploitable survit jusqu'au
balayage TTL — jusqu'à **6 h 30**. Ce n'est pas une fuite dangereuse (le magasin est en mémoire et
borné par le nombre de tentatives réelles), mais c'est la preuve que le cycle de vie n'est aujourd'hui
fermé que par le serveur, jamais par le client.

**Deux conséquences supplémentaires, constatées et non théoriques.**

**(a) Le `playSessionId` v2 est détruit au premier changement de piste.** `createStreamInfo` ré-extrait
`playSessionId` de la query string de l'URL qu'il vient de construire
(`playbackmanager.js:3757`, `getParam('playSessionId', mediaUrl)`). Sur le chemin de démarrage v2, cette
valeur est ensuite écrasée par l'UUID que le client a frappé (`playbackSessionV2Url.ts:404`) — et les
deux coïncident, puisque le serveur estampille dans l'URL le `PlaySessionId` reçu du client. Mais
`changeStream` reconstruit un `streamInfo` **entièrement legacy** (`:2096`) : la valeur redevient celle
de l'URL legacy fraîchement obtenue. À partir de ce point, l'`stopActiveEncodings` de fin de lecture
porte sur un autre `PlaySessionId` que la session v2 encore vivante côté serveur, qui ne sera donc plus
évincée que par le TTL.

**(b) Les heuristiques de retry sont aveugles au v2.** `onPlaybackError` classe la nature du retry en
cherchant des sous-chaînes **legacy** dans `streamInfo.url` : `transcodereasons`,
`allowvideostreamcopy=false`, `allowaudiostreamcopy=false` (`playbackmanager.js:4419-4430`). Sous v2,
`streamInfo.url` est l'URL du descripteur, qui ne porte pas ces paramètres. `isAlreadyFallbacking` et
les deux drapeaux de copie de flux évaluent donc `false` quel que soit l'état réel, et le retry
sélectionne des paramètres inadaptés. Ce n'est pas une régression théorique : c'est un chemin de code
atteignable dès aujourd'hui avec le flag actif.

**Conséquence structurelle** : le `PlaybackSessionId` renvoyé par le `POST` n'est **stocké nulle
part** côté client. `resolveV2PlaybackUrl` le consomme localement (`playbackSessionV2Url.ts:302-306`)
et ne le retourne pas — `V2PlaybackUrlResult` (`:151-166`) ne le contient pas, et
`V2PatchableStreamInfo` (`:343-350`) ne prévoit pas de champ pour lui. **Un client qui ne conserve pas
l'identifiant de session ne peut structurellement pas émettre de `PUT` ni de `DELETE`.** C'est le
premier changement à faire, et il est côté client uniquement (§6.3).

---

## 2. La question centrale — ce que seul `PlaybackInfo` legacy fournit

### 2.1 Le contrat legacy tient en trois champs

`Reefin.Model/MediaInfo/PlaybackInfoResponse.cs` ne porte que trois propriétés :

| Champ | Ligne | Alimenté par |
|---|---|---|
| `MediaSources` (`IReadOnlyList<MediaSourceInfo>`) | `:25` | `MediaInfoHelper.cs:167` (`GetPlaybackMediaSources`) ou `:182` (`GetLiveStream`), clone JSON profond `:197`, puis mutation par appareil via `SetDeviceSpecificData` (`MediaInfoController.cs:202-222`) et `SortMediaSources` (`:224`) |
| `PlaySessionId` (`string?`) | `:31` | `MediaInfoHelper.cs:210` — `Guid.NewGuid().ToString("N")`, **frappé par le serveur**, uniquement sur la branche « au moins une source » |
| `ErrorCode` (`PlaybackErrorCode?`) | `:37` | `MediaInfoHelper.cs:191` — `??= NoCompatibleStream` quand zéro source ; court-circuite le `POST` à `MediaInfoController.cs:192-195` |

La question « quels champs sont legacy-only » a donc une réponse plus brutale que prévu : **les trois
le sont**, et le premier est un objet entier.

### 2.2 Champ par champ, avec le consommateur web

`PlaybackSessionResponse` (`:43-53`) et `PlaybackSessionStreamDescriptor` (`:36-41`) constituent la
totalité de ce que le protocole v2 renvoie. Confrontés au legacy :

| Donnée legacy | Équivalent v2 | Consommateur web |
|---|---|---|
| `PlaySessionId` | **Fourni par le client** en v2 (`CreatePlaybackSessionRequest.PlaySessionId`) — inversion de responsabilité, pas un manque | `playbackmanager.js:941-951` (`self.playSessionId`), `:2034`, `:2148-2153` (`stopActiveEncodings`), `:2716` (`PlayState.PlaySessionId`) ; en legacy il est ré-extrait de la query string de l'URL construite (`:3757`, `getParam('playSessionId', mediaUrl)`) |
| `ErrorCode` | **Aucun.** v2 signale l'absence de plan par un `422` nu, sans corps (`PlaybackSessionsController.cs:109-112`) | `playbackmanager.js:801-807` (`validatePlaybackInfoResult`) mappe `ErrorCode` vers une chaîne de traduction `PlaybackError.*` ; `NoCompatibleStream` a un message dédié |
| `MediaSources[].Id`, `.Name`, `.RunTimeTicks`, `.Container`, `.Path` | **Aucun** | sélection de version/source, affichage, calculs de durée — hors chemin d'URL |
| `MediaSources[].MediaStreams[]` (le tableau complet) | **Aucun.** `SelectedStreams` ne porte que des **index** (`Video`/`Audio` `int?`, `Subtitle` = index + méthode de livraison) | tout le sélecteur de pistes, la liste des sous-titres, `getTextTracks()` (`playbackmanager.js:3773+`), les sous-titres secondaires, `trackHasSecondarySubtitleSupport` (`:3457-3468`) — un index sans tableau où l'indexer est inutilisable |
| `MediaSources[].MediaStreams[].DeliveryUrl` (sous-titre externe) | **Porté depuis PR117** par `PlaybackSessionStreamDescriptor.SubtitleUrl`, mais **uniquement pour la piste par défaut** | `playbackSessionV2Url.ts:352-362` n'écrase que la piste `isDefault` de `textTracks`/`tracks` ; les autres pistes gardent leur URL legacy |
| `MediaSources[].MediaAttachments[].DeliveryUrl` | **Aucun** — explicitement hors périmètre de PR117 (`docs/pr116d-url-contract-design.md` §7) | polices de sous-titres ASS/SSA |
| `MediaSources[].DefaultAudioStreamIndex`, `.DefaultSubtitleStreamIndex`, `.DefaultSecondarySubtitleStreamIndex` | partiellement : `SelectedStreams.Audio`/`.Subtitle.Index`. **Rien** pour le sous-titre secondaire | `playbackmanager.js:3427-3455` (arbitrage complet du sous-titre secondaire avant même `createStreamInfo`) |
| `MediaSources[].RequiresOpening`, `.OpenToken`, `.LiveStreamId` | **Aucun** — le flux `OpenMediaSource`/TV en direct est hors périmètre v2 (`docs/pr116d-url-contract-design.md` §7) | `RequiresOpening` : `playbackmanager.js:3840` (unique) ; `OpenToken` : `:700` (unique) ; `LiveStreamId` : `:609`, `:2714`, `:3647`, `:3695-3696`, `:3841` |
| `MediaSources[].RequiresClosing`, `.DirectStreamUrl` | **Aucun** | **aucun consommateur dans tout `src/`** — champs legacy morts côté web, à ignorer dans tout arbitrage de parité |
| `MediaSources[].TranscodingUrl` | **Remplacé** par `PlaybackSessionStreamDescriptor.Url` (PR117) | unique consommateur `playbackmanager.js:3713` ; écrasé par `playbackSessionV2Url.ts:402`. Voisins : `TranscodingSubProtocol` `:3715-3716`, `TranscodingContainer` `:3720` |
| `MediaSources[].SupportsDirectPlay`, `.SupportsDirectStream`, `.SupportsTranscoding` | **Équivalent partiel** : `PlaybackSessionResponse.Method` (`DirectPlay`/`Remux`/`Transcode`) donne le résultat, pas les possibilités | `SupportsDirectPlay` `:771`, `:3681`, `:3709`, `:3739` ; `SupportsDirectStream` `:675`, `:782`, `:3682` ; `SupportsTranscoding` `:682`, `:783`, `:3712`, **`:4395` (porte de retry)** ; méthode mappée par `PLAY_METHOD_MAP` (`playbackSessionV2Url.ts:116-120`) |
| `MediaSources[].TranscodeReasons` | **Équivalent supérieur** : `PlaybackSessionResponse.Reasons` (`ReasonCode`), plus riche que les bits legacy | jamais lu depuis `PlaybackInfo` côté web — seulement depuis `TranscodingInfo` de session (`playerstats.js:196,199`, `DeviceCard.tsx:113-126`) |

### 2.3 Verdict

**Le legacy ne peut pas disparaître, et le retirer n'est pas la cible.** Ce n'est pas une question de
champs résiduels : v2 ne renvoie **aucune projection de `MediaSourceInfo`**, par une décision de design
assumée (PR92 §4.2 : « pas de `StreamInfo`, pas d'URL ffmpeg, pas de chemin »). `playbackmanager.js`
a besoin de l'objet `mediaSource` bien au-delà de la construction d'URL — il le passe à
`createStreamInfo`, `getTextTracks`, `onPlaybackStarted`, à la sélection de pistes et à la reprise.

**Conséquence pour la cible.** L'appel `PlaybackInfo` legacy reste un **prérequis** de toute tentative
de lecture, y compris v2. La cible « legacy obtenu à la demande si v2 échoue » (§6.2) doit donc être
comprise dans son sens strict et vérifiable : c'est **la construction du `streamInfo` exécutable** qui
devient à la demande, pas la récupération du `MediaSourceInfo`. Prétendre l'inverse conduirait à une
tranche d'implémentation impossible. Cette précision **ne rend pas** la cible caduque ; elle en fixe le
périmètre exact.

---

## 3. État réel du flag et du kill switch

### 3.1 `enableV2PlaybackPath` (client)

`src/scripts/settings/appSettings.js:293-299`. Getter/setter à un seul argument, persistance
`appSettings` (stockage local du navigateur, par appareil), **défaut `false`**
(`toBoolean(this.get('enableV2PlaybackPath'), false)`, `:298`).

Un seul lecteur : `playbackSessionV2Url.ts:271-272`, comme valeur par défaut de la couture injectable
`deps.isEnabled`. Lu **à chaque tentative**, jamais mis en cache — bascule immédiate, sans rechargement.
Flag **distinct** de celui du shadow PR116b (`:261-263`), comme l'exige
`docs/pr116d-url-contract-design.md` §5.

Quand il est faux : `resolveV2PlaybackUrl` retourne `null` **avant tout appel réseau** (`:274-276`),
donc aucun `POST`, aucun `GET`.

**Il n'existe aucune interface pour le basculer.** Aucun `.tsx`/`.jsx`/`.html` ne référence le flag ;
les seuls écrivains sont le setter lui-même (`:295`) et les tests. Le basculer exige un appel console
ou une écriture manuelle dans `localStorage` (`appSettings.js:314-316` / `:323-324`). Le flag est donc
**par appareil et par navigateur**, non synchronisé, et invisible pour un opérateur. À arbitrer dans la
tranche de déploiement — hors périmètre de ce document, mais à ne pas découvrir en production.

### 3.2 `PlaybackShadowOptions.Mode` (serveur)

`Reefin.Model/Configuration/PlaybackShadowOptions.cs` :

| Propriété | Ligne | Défaut | Validation |
|---|---|---|---|
| `Enabled` | `:38` | `false` | — (drapeau pré-PR115a, conservé pour compatibilité) |
| `Mode` | `:46` | `PlaybackEngineMode.Legacy` | — |
| `CanaryPercentage` | `:56-60` | `0` | `Math.Clamp(0, 100)` |
| `SampleRate` | `:71-75` | `1.0` | `NaN → 0.0`, sinon `Clamp(0,1)` |
| `MaxExecutionMs` | `:85-89` | `50` | `Math.Max(1, …)` |
| `StopThresholds` | `:98` | `new()` | jamais `null` |

`GetEffectiveMode()` (`:108-109`) : `Mode == Legacy && Enabled ? Shadow : Mode`. Méthode et non
propriété, pour que le sérialiseur XML de configuration ne la persiste pas comme un troisième bouton
(`:104-105`). `PlaybackEngineMode` (`PlaybackEngineMode.cs:9-38`) : `Legacy = 0`, `Shadow = 1`,
`Canary = 2`, `V2 = 3`.

**Comportement par défaut, sans configuration : `Legacy`.** Le moteur v2 ne fait aucun travail ; tout
`GET .../Stream` sert donc une URL construite par le planificateur legacy, avec `ServedBy = 0`
(`PlaybackSessionResponse.LegacyDecisionVersion`) et un `FallbackReason` typé.

Lecteurs en production :

| Lecteur | Ce qu'il lit |
|---|---|
| `Reefin.Model/Configuration/ServerConfiguration.cs:295` | site de liaison (`PlaybackShadow`) |
| `Reefin.MediaEncoding/Playback/PlaybackLiveStreamResolver.cs:83` | **le kill switch** — `GetEffectiveMode()`, relu à **chaque** résolution |
| `Reefin.MediaEncoding/Playback/ShadowPlaybackSessionPlanner.cs:165-166,178,185,212+` | mode effectif, `CanaryPercentage`, `SampleRate`, `MaxExecutionMs` |
| `Reefin.MediaEncoding/Playback/PlaybackStopThresholdGuard.cs:88` | `StopThresholds` |
| `Reefin.Api/Controllers/PlaybackDiagnosticsMetricsController.cs:57` | `StopThresholds` (garde de la surface de métriques) |
| `Reefin.Api/Helpers/MediaInfoHelper.cs:134` | accesseur passé au garde-fou qu'il construit lui-même (`:133-137`) |

Câblage DI : `Reefin.Server.Core/ApplicationHost.cs:678-689` injecte des
`Func<PlaybackShadowOptions>` re-lus par appel — **le kill switch prend effet à la requête suivante,
sans redémarrage**, sur le chemin legacy comme sur `GET .../Stream`.

---

## 4. `ResolveV2PlaybackUrlParams` — ce qui est transporté, ce qui est jeté

`src/scripts/playbackSessionV2Url.ts:122-134` transporte **six** champs : `api`, `itemId`, `mediaType`,
`userId`, `mediaSourceId`, `startTimeTicks`.

Le `POST` est construit à `:204-215` : `Capabilities: buildClientCapabilities()` et
`Constraints: buildPlaybackConstraints({ startTimeTicks })` — **un seul** champ de contrainte est
fourni. `buildPlaybackConstraints` (`src/scripts/reefinPlaybackCapabilities.ts:772-792`) remplit tout
le reste avec ses valeurs par défaut.

Or, au site d'appel (`playbackmanager.js:3485-3496`), `playbackmanager` a **déjà calculé** :

| Grandeur déjà calculée | Où | Contrainte v2 correspondante | Sort |
|---|---|---|---|
| `options.audioStreamIndex` | `playbackmanager.js:3404-3408` (préférences mémorisées incluses) | `PreferredAudioStreamIndex` | **jeté** → `null` |
| `options.subtitleStreamIndex` | `:3409-3413` | `PreferredSubtitleStreamIndex` | **jeté** → `null` |
| `mediaSource.DefaultSecondarySubtitleStreamIndex` | `:3428-3446` | *aucune* | **jeté** (pas de contrainte v2) |
| `maxBitrate` | `:3504` (`playerData.maxStreamingBitrate`) | `MaxBitrate` | **jeté** → `null` |
| `enableDirectPlay` / `enableDirectStream` (retry) | `changeStream` `:2076-2077` | `AllowDirectPlay` / `AllowDirectStream` | **jeté** → `true`/`true` |
| `allowVideoStreamCopy` / `allowAudioStreamCopy` | `:2078-2079` | `AllowVideoStreamCopy` / `AllowAudioStreamCopy` | **jeté** → `true`/`true` |
| préférences de sous-titres de l'utilisateur | `user.Configuration.SubtitleMode`, langues | `SubtitleMode`, `PreferredSubtitleLanguages` | **jeté** → `Default`, `[]` |

**Ce n'est pas seulement un manque de complétude : c'est une divergence de décision.** Le `POST` v2
planifie avec des contraintes par défaut pendant que le `streamInfo` legacy déjà construit décrit une
décision prise avec les contraintes réelles. Les deux peuvent sélectionner des pistes audio/sous-titres
différentes, ou un débit différent. Le `streamInfo` publié est alors **hybride** : URL et `playMethod`
issus d'une décision v2 « par défaut », tout le reste (pistes, textTracks, mediaSource) issu de la
décision legacy contrainte. Rien ne le détecte aujourd'hui.

**Symétriquement, côté serveur, quatre contraintes transportées par le DTO sont ignorées.**
`ReverseConstraintsMapper.ApplyTo` (`src/Reefin.Playback.Dlna/ReverseConstraintsMapper.cs:38-52`)
n'écrit que neuf des treize champs de `PlaybackConstraints`. Ses propres remarques (`:20-28`) le
documentent : `AllowTranscoding` est appliqué séparément par le contrôleur
(`PlaybackSessionsController.cs:369-375`), tandis que **`SubtitleMode`, `PreferredSubtitleLanguages` et
`StartTimeTicks` « ne sont simplement pas reportés »**. Un client qui les enverrait correctement
n'obtiendrait donc toujours pas l'effet attendu : le contrat les accepte, le pipeline les perd.

C'est le cadrage de la lane W2. **Ce document ne l'implémente pas** ; il en fige le constat : le
travail est des **deux** côtés, et le côté serveur (les trois contraintes perdues dans le mapper
inverse) est le préalable — les câbler côté client d'abord ne produirait aucun effet observable.

---

## 5. `PlaybackAttemptId` — état et cadrage repris

**Fait établi, non re-litigé.** Aucun mécanisme de propagation de `PlaybackAttemptId` n'existe côté
serveur : la chaîne n'apparaît nulle part dans le dépôt `reefin` (ni `.cs`, ni `.md`, ni test), ni dans
`reefin-web` sur `origin/main`.

Le cadrage produit (PR #33, `docs/major-rewrite-plan-v13.md`, note « deux identifiants distincts »)
le pose en cible :

- **`RequestId` / `TraceId`** — unique par requête HTTP, **généré par le serveur**, tracing transport,
  ne survit pas à la requête.
- **`PlaybackAttemptId`** — **généré par le client**, partagé entre l'appel `PlaybackInfo` legacy,
  l'appel shadow/v2 et toutes les requêtes rattachées à une même tentative de lecture. C'est lui qui
  corrèle une tentative de bout en bout.
- Ni l'un ni l'autre n'est `PlaySessionId`.

Ce document reprend ce cadrage tel quel et se borne à en tirer les conséquences de contrat (§6.3, §6.4).

---

## 6. Décisions

### 6.1 Le cycle cible et la sémantique de chaque verbe

```text
POST   Playback/Sessions              → planifie. Renvoie PlaybackSessionResponse (Id, Method, …)
GET    Playback/Sessions/{id}/Stream  → projette la décision courante en URL exécutable
PUT    Playback/Sessions/{id}         → re-planifie intégralement (changement de piste, débit, retry)
GET    Playback/Sessions/{id}/Stream  → nouvelle URL pour la décision re-planifiée
DELETE Playback/Sessions/{id}         → termine la tentative
```

**Sémantique du `PUT` — acquise, pas proposée.** PR92 §3 puis `PlaybackSessionsController.cs:121-124`
l'ont tranchée : le `PUT` est un **remplacement intégral** du plan, pas un patch. Le corps est un
`ReplacePlaybackSessionRequest` complet ; il **ne porte pas** de `PlaySessionId`
(`ReplacePlaybackSessionRequest.cs:16-22`), qui reste celui de la session ciblée par la route.
`PlaybackSessionManager.Patch` (`:164-220`) conserve `CreatedAt`, rafraîchit `UpdatedAt`, et applique
la discipline « attacher-ou-supprimer » sur `IV2PlanStore`/`IShadowDiagnosticsStore` de sorte qu'une
re-planification sans nouvelle capture **évince** l'enregistrement périmé (`:138-158` pour `Create`,
symétrique pour `Patch`).

**Décision — le `PUT` est le verbe du changement de piste.** Un changement de piste audio ou
sous-titre, un changement de débit ou un retry `EnableDirectPlay=false` est une **nouvelle décision sur
la même tentative** : même `PlaybackAttemptId`, même `PlaySessionId`, même `PlaybackSessionId`. Il ne
doit donc **pas** créer une nouvelle session (ce que ferait un second `POST` avec un `PlaySessionId`
différent), ni réutiliser l'ancienne URL. Le client émet `PUT {id}` puis `GET {id}/Stream`.

**Idempotence.** Trois régimes distincts, tous déjà en code :

- `POST` avec un `PlaySessionId` **déjà connu** est idempotent par remplacement :
  `StoreOrReplace` (`PlaybackSessionManager.cs:309-332`) réutilise le `PlaybackSessionId` existant
  (`:318-320`) au lieu d'en créer un. La garantie « au plus une session par `PlaySessionId` » tient.
- `PUT {id}` est idempotent au sens fort : rejouer le même corps produit le même plan et le même
  `PlaybackSessionId`, seul `UpdatedAt` bouge.
- `GET {id}/Stream` **n'est pas** idempotent au sens strict, et c'est délibéré
  (`docs/pr116d-url-contract-design.md` §3.1) : il **réévalue** le kill switch, le garde-fou de seuils
  et la résolution du plan à chaque appel. C'est ce qui rend le kill switch réellement immédiat. Le
  contrat associé est explicite et **conservé** : `ServedBy`/`FallbackReason` du `GET` font foi pour
  ce qui sera servi ; `DecisionVersion` du `POST`/`PUT` n'est qu'un signal de planification.

**Expiration.** `ExpiryTtl = 6 h` sur `UpdatedAt`, balayage toutes les 30 min
(`PlaybackSessionManager.cs:20-21`, `:295-307`) ; éviction anticipée par `TranscodingJobEnded`
(`:369-381`) et `PlaybackStopped` (`:410-427`). Un `PUT` rafraîchit `UpdatedAt`, donc une lecture
longue avec changements de piste ne peut pas expirer sous le lecteur. Une session expirée est
indiscernable d'une session inconnue : `Get` renvoie `null` (`:254-260`), donc `404`.

**Décision — pas de nouveau TTL, pas de heartbeat.** Le client doit émettre `DELETE` à l'arrêt ; le TTL
reste le filet de sécurité pour les clients qui disparaissent. Ajouter un heartbeat introduirait un
trafic périodique pour un magasin en mémoire déjà borné.

**Contrat d'erreur — état constaté et une correction requise.**

| Statut | Endpoint | Condition | Preuve |
|---|---|---|---|
| `400` | `POST`, `PUT` | validation ; `ArgumentException` portant **toutes** les violations jointes | `PlaybackSessionRequestValidator.cs:42-45` |
| `400` | `GET .../Stream` | `startTimeTicks < 0` | `:233-236` |
| `403` | `GET`, `PUT`, `DELETE` | ni propriétaire ni administrateur | `:327-341`, appelé `:154`, `:196`, `:248` |
| `403` | `GET .../Stream` | jeton d'accès absent | `:273-277` |
| `404` | `POST`, `PUT` | utilisateur ou item introuvable (`ResourceNotFoundException`) | `:346-347` |
| `404` | `PUT`, `DELETE`, `GET` | session inconnue ou expirée | `:148-152`, `:190-194`, `:238-242` |
| `409` | `GET .../Stream` | `session.PlaySessionId` absent | `:256-259` |
| `409` | `GET .../Stream` | session sans plan exécutable (créée par `Track`, pas par un plan) | `:261-267` |
| `422` | `POST` | aucun plan viable | `:109-112`, corps vide |

Le corps d'erreur est **`text/plain`**, pas RFC 7807 : `ExceptionMiddleware.cs:93` fixe
`MediaTypeNames.Text.Plain` et `:99` écrit la chaîne brute.

**Écart à corriger (bloquant pour X2).** `PUT` déclare `422` (`:138`) mais ne peut pas le renvoyer :
un `Patch` renvoyant `null` est mappé en `404` (`:162-166`), indiscernable d'un identifiant inconnu.
La **même** condition métier — « aucun plan viable » — produit donc `422` sur `POST` et `404` sur
`PUT`. Un client ne peut pas distinguer « ta session a disparu, recommence par un `POST` » de « ta
nouvelle piste n'est pas jouable, garde la session et propose autre chose ». C'est exactement la
décision que le client doit prendre lors d'un changement de piste. **Décision : `PUT` doit renvoyer
`422` quand la session existe et que la re-planification ne produit aucun plan, et `404` seulement
quand l'identifiant est inconnu.** Le contrôleur dispose déjà de l'information : `existingSession` a
été résolu à `:148` avant l'appel à `Patch`.

De même, les deux `409` du `GET .../Stream` (`:258` et `:266`) portent le même statut pour deux
situations opposées — l'une réparable par un `PUT` fournissant un `PlaySessionId`, l'autre
structurellement non servable. **Décision : les distinguer**, soit par deux statuts, soit par un code
d'erreur typé dans le corps (§6.4).

### 6.2 Repli — v2 d'abord, legacy à la demande, sans demi-mutation

**État actuel.** Le repli est aujourd'hui l'**inverse** de la cible. `createStreamInfo()` construit un
`streamInfo` legacy **complet et inconditionnel** (`playbackmanager.js:3470-3477`), puis
`applyV2PlaybackUrlToStreamInfo` vient **écraser** cinq champs dessus (`playbackSessionV2Url.ts:402-412`).
Le legacy n'est pas « à la demande » : il est un préalable systématique, et l'objet publié est un
hybride (§4).

**Ce qui est déjà correct et ne doit pas être défait.** `applyV2PlaybackUrlToStreamInfo` résout
**toutes** les URL absolues (`apiClient.getUrl`) **avant** la première mutation
(`playbackSessionV2Url.ts:384-400`), précisément pour qu'un `getUrl()` qui échoue à mi-chemin ne laisse
pas un `streamInfo` à moitié patché. `resolveV2PlaybackUrl` ne lève ni ne rejette jamais : toute
défaillance — flag off, réseau, `4xx`/`5xx` (y compris les `409`/`403` du contrat), réponse sans `Url`
— retourne `null` et laisse `streamInfo` intact (`:259-331`). L'atomicité **au sein de l'application**
est acquise. Ce qui manque, c'est l'atomicité **de la décision**.

**La preuve que l'objet publié est hybride, champ par champ.** Sur un succès v2, cinq champs sont
écrasés et tous les autres restent issus de la décision legacy. Trois d'entre eux deviennent alors
faux, pas seulement redondants :

| Champ non écrasé | Valeur conservée | Pourquoi c'est faux sous v2 |
|---|---|---|
| `transcodingOffsetTicks` (`playbackmanager.js:3747`, posé à `:3728`) | `startPosition` si **legacy** avait choisi un transcodage non-HLS | si v2 renvoie `DirectPlay`/`Remux`, l'objet porte un décalage de transcodage non nul à côté d'une URL directe — le décalage de position appliqué au lecteur est erroné |
| `mimeType` (`:3745`) | type MIME du conteneur legacy | `playbackSessionV2Url.ts:406` ne l'écrase **que** pour HLS ; un flux v2 progressif garde le type MIME de la décision legacy |
| `mediaSource` (`:3751`), `liveStreamId` (`:3756`) | objets legacy | cohérents avec le `MediaSourceInfo` (§2.3), mais leurs index de pistes décrivent la décision legacy, pas celle que le v2 a réellement planifiée (§4) |

Ces trois lignes sont la justification concrète de la décision ci-dessous : aucun ajout de champ au
patch ne referme le problème, seule une construction d'un seul tenant le fait.

**Décision — l'objet publié est construit entièrement d'un côté ou entièrement de l'autre.**

1. `playbackmanager` obtient d'abord le `MediaSourceInfo` (appel `PlaybackInfo` legacy — prérequis
   incompressible, §2.3). Cet appel **ne construit aucun `streamInfo`**.
2. Si le flag est actif, la voie v2 est tentée : `POST` puis `GET .../Stream`, avec les contraintes
   réelles (§4). Elle construit **son propre** objet `streamInfo` candidat, complet, dans une variable
   locale, à partir du `mediaSource` obtenu en 1 et du descripteur v2.
3. Si et seulement si la voie v2 échoue (n'importe quelle branche de la matrice de repli existante),
   `createStreamInfo()` legacy est appelé — **à ce moment-là**, pas avant — et construit l'objet
   candidat.
4. Un seul point de publication : `playerData.streamInfo = <candidat>` (`playbackmanager.js:3505`),
   atteint avec un objet issu d'**une seule** des deux voies. Aucune mutation croisée, aucun écrasement
   de champ.

Ce que cela garantit et que l'état actuel ne garantit pas : il n'existe plus d'objet dont l'URL vient
d'une décision et les pistes d'une autre. L'invariant vérifiable est **« `streamInfo.playMethod`,
`streamInfo.url`, `streamInfo.textTracks` et les index de pistes proviennent tous du même plan »** — un
invariant testable, contrairement à « pas de demi-mutation », qui l'est déjà.

L'ordre des étapes 2 et 3 est ce qui rend le legacy « à la demande » au sens strict et vérifiable :
avec le flag actif et la voie v2 nominale, `createStreamInfo()` **n'est pas appelé**.

### 6.3 Les trois identifiants que `streamInfo` doit conserver

| Identifiant | Généré par | Quand | Durée de vie | Consommé par |
|---|---|---|---|---|
| **`PlaybackSessionId`** (`Guid`, `Reefin.Controller/MediaEncoding/PlaybackSessionId.cs:9`) | **serveur**, `NewId()` `:15`, unique site : `PlaybackSessionManager.StoreOrReplace:323` | au premier `POST`/`Track` pour un `PlaySessionId` donné | **réutilisé** à travers les re-planifications (`StoreOrReplace:318-320`, `Patch:190-191`) ; détruit par `DELETE`, `TranscodingJobEnded`, `PlaybackStopped`, ou le balayage TTL 6 h | clé de route des trois verbes `{id}` ; clé des trois magasins par session |
| **`PlaySessionId`** (`string`) | **client** en v2 (`CreatePlaybackSessionRequest.PlaySessionId`) ; **serveur** en legacy (`MediaInfoHelper.cs:210`, `Guid.NewGuid().ToString("N")`) | à la création de la tentative | index `_byPlaySessionId` (`PlaybackSessionManager.cs:32`) ; sa fin (`TranscodingJobEnded`, `PlaybackStopped`) détruit le `PlaybackSessionId` | `stopActiveEncodings` (`playbackmanager.js:2148-2153`), `PlayState.PlaySessionId` (`:2716`), corrélation `ITranscodeManager`, garde `409` du `GET .../Stream` (`:256`) |
| **`PlaybackAttemptId`** | **client** (cadrage PR #33) | à l'ouverture de la tentative, **avant** le premier appel — legacy comme v2 | toute la tentative, y compris à travers les `PUT` ; **survit** à un repli v2 → legacy, c'est son unique raison d'être | corrélation de bout en bout, journalisation, diagnostics — **jamais** une décision de lecture |

**Décision — `streamInfo` conserve les trois.** Aujourd'hui il n'en garde qu'un : `playSessionId`
(`playbackmanager.js:3757` en legacy, écrasé par `playbackSessionV2Url.ts:404` en v2 — au démarrage les
deux valeurs coïncident, car le serveur estampille dans l'URL le `PlaySessionId` reçu du client ; mais
elles divergent dès le premier changement de piste, §1.3 (a)). Faire porter `playSessionId` par la
session plutôt que par l'URL est donc une conséquence directe de la décision « le `PUT` est le verbe du
changement de piste ».

Il manque les deux autres :

- **`playbackSessionId`** — sans lui, ni `PUT` ni `DELETE` ne sont émettables (§1.3).
  `V2PlaybackUrlResult` (`playbackSessionV2Url.ts:151-166`) doit le retourner et
  `V2PatchableStreamInfo` (`:343-350`) le porter. **Changement client uniquement.**
- **`playbackAttemptId`** — généré une fois par tentative, avant l'appel `PlaybackInfo` legacy, et
  transmis aux deux protocoles. **Changement client et serveur** (§6.4).

**Distinction à tenir explicitement.** `PlaybackAttemptId` ≠ `PlaySessionId` : ce dernier est réutilisé
par le `POST` idempotent et est l'unité que le cycle de vie du transcodage détruit. `PlaybackAttemptId`
≠ `RequestId`/`TraceId` : ce dernier est serveur, par requête, et ne survit pas à la requête. Un repli
v2 → legacy change de protocole ; c'est très exactement le cas que `PlaybackAttemptId` doit rendre
observable, et qu'aucun des deux autres ne couvre.

### 6.4 Le contrat serveur doit-il évoluer ? — **OUI**

Trois changements. Aucun n'est cosmétique ; chacun bloque une partie du cycle cible.

**(1) `PlaybackAttemptId` — nouveau champ, additif, quatre surfaces.**

| Surface | Changement |
|---|---|
| `Reefin.Api/Models/PlaybackSessionDtos/PlaybackPlanRequestBase.cs` | nouveau champ `string? PlaybackAttemptId` (hérité par `CreatePlaybackSessionRequest` et `ReplacePlaybackSessionRequest`) |
| `Reefin.Controller/MediaEncoding/PlaybackSession.cs:23-30` | nouveau membre `string? PlaybackAttemptId` sur le record ; conservé par `StoreOrReplace`/`Patch` |
| `Reefin.Api/Controllers/MediaInfoController.cs` — `POST Items/{itemId}/PlaybackInfo` | nouveau paramètre optionnel `playbackAttemptId` (query + corps `PlaybackInfoDto`), suivant la convention query-gagne déjà en place (`:157-171`) — **c'est ce qui rend l'identifiant partagé entre legacy et v2**, sans quoi il ne corrèle rien |
| `PlaybackDiagnosticDetail` / `PlaybackSessionListItem` | exposition admin de l'identifiant |

Contraintes de sécurité, non négociables : valeur **opaque**, plafonnée en longueur, jamais utilisée
comme clé d'autorisation ni de recherche cross-utilisateur, jamais interpolée dans une URL ou une ligne
de commande, jamais journalisée avec un jeton. C'est un identifiant de corrélation, pas une capacité.
Absent ou invalide, il est ignoré (jamais un `400` : un identifiant de diagnostic ne doit pas pouvoir
casser une lecture).

**(2) `PUT Playback/Sessions/{id}` — correction du statut, changement de comportement observable.**
`422` quand la session existe et que la re-planification ne produit aucun plan ; `404` réservé à
l'identifiant inconnu (§6.1). Aucun changement de DTO. **C'est un changement de contrat** : un client
qui traite aujourd'hui le `404` comme « recommence par un `POST` » verra un `422` là où il voyait un
`404`.

**(3) Désambiguïsation des deux `409` du `GET .../Stream`.** Réparable par `PUT` (absence de
`PlaySessionId`) vs structurellement non servable (session issue de `Track`). Deux statuts distincts,
ou un code d'erreur typé. À trancher en implémentation ; le corps étant `text/plain`, l'introduction
d'un code typé implique de changer le format de réponse d'erreur de ces endpoints — non trivial, à
peser contre l'option « deux statuts », qui est additive.

**Ce qui, en revanche, ne doit PAS changer** — et c'est ce qui rend cette PR bornée : les verbes
existent tous les quatre ; `PlaybackSessionStreamDescriptor` ne bouge pas ; `PlaybackSessionResponse`
ne bouge pas ; `PlaybackSessionId` reste serveur ; le TTL reste à 6 h ; le contrat TOCTOU
`ServedBy`/`FallbackReason` reste tel quel ; l'invariant de parité exécutable reste tel quel.

**Le reste du travail est client.** Conserver `playbackSessionId`, émettre `PUT` au changement de piste
et `DELETE` à l'arrêt, inverser l'ordre de construction (§6.2), transporter les contraintes réelles
(§4). Ce n'est pas un changement de contrat, c'est de la consommation du contrat existant.

---

## 7. Ce qui n'invalide pas la cible, et la seule réserve qui la borne

Aucune découverte de cet audit n'invalide le cycle cible `POST` → `GET` → `PUT`/`GET` → `DELETE`. Les
quatre verbes existent, sont autorisés correctement (PR118) et ont une sémantique cohérente.

**Une réserve borne le périmètre, sans l'arrêter** (§2.3) : « legacy à la demande » ne peut pas
signifier « plus d'appel `PlaybackInfo` ». L'appel legacy reste le seul fournisseur de
`MediaSourceInfo`, dont dépend tout le reste de `playbackmanager.js`. Ce qui devient à la demande est
la **construction du `streamInfo` exécutable**. Une tranche d'implémentation qui viserait la
suppression de l'appel `PlaybackInfo` échouerait ; celle décrite en §6.2 n'échouera pas.

**Cinq écarts constatés qui doivent être corrigés dans la tranche, pas contournés** : le `422`
manquant sur `PUT` (§6.1) ; les contraintes réelles jetées des deux côtés (§4) ; l'absence de
conservation du `PlaybackSessionId` côté client (§1.3) ; les heuristiques de retry qui cherchent des
paramètres legacy dans une URL v2 (§1.3 (b)) ; les champs `transcodingOffsetTicks`/`mimeType` laissés
à leur valeur legacy sur succès v2 (§6.2). Les deux derniers sont des **défauts actifs** dès
aujourd'hui avec le flag actif, pas des manques de la cible : ils justifient à eux seuls que la tranche
X2 ne se contente pas d'ajouter des verbes.

---

## 8. Contrainte produit — aucun client nommé dans une décision serveur

**Le chemin de décision de lecture est conforme.** Aucune correspondance par nom de client n'y
subsiste :

- il n'existe **aucun** répertoire `Profiles` dans le dépôt — l'arbre `Dlna/Profiles/*.xml` de Jellyfin,
  qui appariait les appareils par nom, a été entièrement retiré ; aucune référence
  `DeviceIdentification`/`FriendlyName`/`ModelName` dans `Reefin.Model/Dlna/*.cs` ;
- aucune comparaison `ClientName`/`AppName`/`DeviceName`/`UserAgent` dans
  `src/Reefin.Playback.Engine`, `src/Reefin.Playback.Decision`, `src/Reefin.Playback.Dlna`,
  `Reefin.MediaEncoding/Playback`, `Reefin.Api/Helpers` ni `Reefin.Model/Dlna` ;
- `PlaybackLiveStreamResolver.Resolve` (`:61-149`) ne branche que sur le mode effectif (`:84`), le
  garde-fou de seuils (`:93`), la résolution du plan (`:98`), l'égalité de `SourceId` (`:111`) et une
  exclusion Dolby Vision/HDR par codec (`:121`) — tout est capacité ou configuration ;
- `CanaryCohort.IsInCohort(Guid userId, string? deviceId, int percentage)`
  (`Reefin.MediaEncoding/Playback/CanaryCohort.cs:28`) est un hachage identité+appareil, pas un nom de
  client.

**Deux violations existantes, hors chemin de lecture, signalées comme demandé** :

1. `Reefin.Server.Implementations/Security/AuthorizationContext.cs:152` —
   `!authInfo.Client.Contains("chromecast", StringComparison.OrdinalIgnoreCase)` conditionne la mise à
   jour des informations de jeton. C'est le cas le plus net de nom de client codé en dur dans une
   décision serveur du dépôt.
2. `Reefin.Api/Controllers/ImageController.cs:1953` —
   `userAgent.Contains("android", …)` sélectionne un format d'image. Reniflage de client plutôt que
   négociation de capacité.

**Côté client, le nom émis sur le fil reste `'Jellyfin Web'`**, identique sur les deux protocoles :
`src/lib/reefin-sdk/index.ts:45` (`REEFIN_CLIENT_IDENTITY = { name: 'Jellyfin Web' }`, envoyé comme
`Client="…"` dans l'en-tête `MediaBrowser`), `src/components/apphost.js:10`,
`src/utils/image.ts:87`. Les deux passerelles v2 réutilisent `api.authorizationHeader`
(`playbackSessionV2Url.ts:66`, `:105` ; `playbackSessionShadow.ts:42`), donc le serveur ne peut pas
distinguer le trafic v2 du trafic legacy par l'identité du client — ce qui est **exactement** le
comportement voulu par la contrainte produit. Aucune donnée de capacité (`buildClientCapabilities`,
`reefinPlaybackCapabilities.ts:734`) ne porte de nom de client.

Aucune des deux violations n'est une décision de lecture, donc aucune ne viole la règle telle qu'elle est
formulée aujourd'hui. Les deux la violeraient si la règle était étendue au serveur entier — ce que ce
document recommande, sans le trancher ici. Bénin et hors sujet :
`DisplayPreferencesManager.cs:34,51,68,79,89` compare `pref.Client`, mais comme clé de partition de
stockage, jamais comme branche de comportement.

---

## 9. Critères de sortie de ce document

- [x] Cycle réel audité de bout en bout, des deux côtés, avec `fichier:ligne` (§1).
- [x] Question centrale tranchée sur preuve de code : `PlaybackInfoResponse` n'a que trois champs, et
      `MediaSources` n'a **aucun** équivalent v2 — le legacy ne peut pas disparaître, et la cible est
      reformulée en conséquence sans être abandonnée (§2).
- [x] Flag client et kill switch serveur : lecteurs, défauts, immédiateté (§3).
- [x] `ResolveV2PlaybackUrlParams` : six champs transportés, sept grandeurs déjà calculées et jetées
      côté client, trois contraintes perdues côté serveur dans le mapper inverse (§4). Cadrage W2, non
      implémenté.
- [x] `PlaybackAttemptId` : absence confirmée, cadrage PR #33 repris sans re-litige (§5).
- [x] Décisions tranchées : sémantique `PUT`, idempotence par verbe, expiration, contrat d'erreur avec
      un écart nommé comme bloquant (§6.1) ; repli atomique avec point de publication unique et
      invariant testable (§6.2) ; trois identifiants avec générateur, durée de vie et consommateurs
      (§6.3).
- [x] Réponse **oui** sur l'évolution du contrat serveur, avec la liste exacte des changements
      d'endpoint et de DTO (§6.4).
- [x] Conformité de la contrainte produit vérifiée sur le chemin de décision ; deux violations
      existantes hors chemin signalées (§8).
- [x] Deux défauts **actifs** du chemin v2 actuel identifiés et attribués à la tranche
      d'implémentation, pas laissés implicites : heuristiques de retry aveugles au v2 (§1.3 (b)) et
      champs `transcodingOffsetTicks`/`mimeType` périmés sur succès v2 (§6.2).
- [x] Aucune découverte n'invalide le cycle cible ; la seule réserve (§7) en borne le périmètre sans
      l'arrêter — la tranche X2 est débloquée sous réserve des trois changements de contrat de §6.4.
