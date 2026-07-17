# Design — PR116, migration des clients vers l'API de lecture v2 (reefin-web d'abord)

- **PR** : PR116 (design uniquement, aucun code de production, ni côté `reefin` ni côté `reefin-web`)
- **Statut** : proposé
- **Dépend de** : PR112/PR112b (`docs/pr92-design-playback-api-and-diagnostics.md` — contrat client
  v2, `Playback/Sessions`), PR115a-d (canary — **fusionné**, `master` `reefin` à `4c20bf00a6`,
  2026-07-17)
- **Dépôts concernés** : `reefin` (serveur, ce document) et `reefin-web`
  (`all3f0r1/reefin-web`, fork de `jellyfin-web` — le front officiel, pas `jellyfin-web` upstream ;
  branche de travail au moment de la rédaction : `w13.5-rfc-design-system`, lecture seule pour ce
  document, voir avertissement méthodologique en tête de §1.1)
- **Précède** : tranches web PR116a-d (`reefin-web`) ; retrait des adaptateurs temporaires côté
  serveur (§4.3, hors PR116 au sens strict — dépend d'une condition serveur indépendante de
  l'adoption client)

Ce document fige le **périmètre et l'ordre** de la migration des clients vers le protocole de
lecture v2 (PR91/PR92/PR112), en prenant `reefin-web` comme premier (et pour l'instant seul) client
concret. Aucun câblage, aucun changement de comportement — au même sens que PR92/PR115b avant lui.
La conclusion structurante de ce document (§1.3, §3) est qu'une partie de la cible demandée par le
brief initial (« PR116c : consommer la réponse v2 pour démarrer la lecture ») **ne peut pas être
livrée avec le contrat serveur actuel** — `PlaybackSessionResponse` ne porte aucune URL exécutable.
Ce n'est pas un défaut de conception à corriger en passant : PR92 §4.2 l'exclut explicitement
(« Pas de `StreamInfo`, pas d'URL ffmpeg, pas de chemin »). C'est un **prérequis serveur non
encore designé**, nommé explicitement en §3 (tranche bloquée) et §4.3, pas absorbé silencieusement
dans une tranche qui prétendrait le résoudre.

---

## 1. État des lieux

### 1.1 Comment `reefin-web` négocie la lecture aujourd'hui

**Avertissement de méthode** : `reefin-web` a été inspecté en lecture seule, sans changer de
branche (`w13.5-rfc-design-system` au moment de la rédaction — RFC-0005 design system, sans rapport
avec la lecture). Rien dans le diff de cette branche vs sa base ne touche `playbackmanager.js`
(vérifié par les commits de tête, tous documentation RFC-0005) ; les constats ci-dessous sont donc
considérés représentatifs de l'état réel du client, mais n'ont pas été vérifiés contre `master`
`reefin-web` directement — écart méthodologique assumé, à revalider si `master` a divergé depuis.

`reefin-web` ne parle **pas** le protocole v2 (`Playback/Sessions`). Le flux réel, tracé dans
`src/components/playback/playbackmanager.js` :

1. **Construction du profil d'appareil** — `apphost.js:50-61` (`getDeviceProfile`) appelle
   `profileBuilder` (`src/scripts/browserDeviceProfile.js`, 1671 lignes) qui construit un
   `DeviceProfile` DLNA complet (détection `canPlayH264`/`canPlayHevc`/`canPlayAv1`/codecs
   audio/HLS, `DirectPlayProfiles`/`CodecProfiles`/`TranscodingProfiles`) — exactement le type que
   PR91/PR92 posent comme **à ne plus exposer** côté contrat serveur (`docs/pr92-design-playback-api-and-diagnostics.md`
   §4 : « Aucun de ces types n'apparaît dans le contrat public »).
2. **Requête** — `getPlaybackInfo()` (`playbackmanager.js:526-639`) assemble une requête à plat
   (`EnableDirectPlay`/`EnableDirectStream`/`AllowVideoStreamCopy`/`AllowAudioStreamCopy`/
   `MaxStreamingBitrate`/`AudioStreamIndex`/`SubtitleStreamIndex`/
   `AlwaysBurnInSubtitleWhenTranscoding` + `DeviceProfile` inline) et l'envoie via
   `getMediaInfoApi(api).getPostedPlaybackInfo({ itemId, playbackInfoDto: query })`
   (`playbackmanager.js:634-637`) — c'est le SDK **`@jellyfin/sdk`** (upstream Jellyfin, pas
   `reefin-sdk`), qui pointe sur `POST /Items/{itemId}/PlaybackInfo`
   (`src/lib/reefin-sdk/generated/api/media-info-api.ts:181-184` confirme la même route côté
   client `reefin-sdk` généré, TypeScript miroir du contrat serveur — la route existe des deux
   côtés, seul le SDK réellement appelé diffère). C'est exactement le vocabulaire booléens-épars +
   `DeviceProfile` brut que PR92 §4.1 documente comme remplacé côté serveur par
   `ClientCapabilities`/`PlaybackConstraints` (PR112b) — **`reefin-web` n'a jamais suivi ce
   remplacement**, il parle encore le protocole point-1 pré-PR112.
3. **Réponse et démarrage** — `createStreamInfo()` (`playbackmanager.js:~3600-3736`) consomme
   `mediaSource.SupportsDirectPlay`/`SupportsDirectStream`/`SupportsTranscoding`/
   `TranscodingUrl`/`TranscodingSubProtocol`, construit l'URL de lecture réelle depuis
   `mediaSource.TranscodingUrl` (`playbackmanager.js:3678`, `apiClient.getUrl(mediaSource.TranscodingUrl)`)
   et extrait `playSessionId` en reparsant la query string de cette URL
   (`playbackmanager.js:3722`, `getParam('playSessionId', mediaUrl)`) plutôt que de le lire sur un
   champ structuré — le client dépend structurellement de la forme legacy `StreamInfo` sérialisée
   en query string, pas d'un DTO stable.

Côté serveur, cette requête arrive sur `MediaInfoController`
(`Reefin.Api/Controllers/MediaInfoController.cs:202`) → `MediaInfoHelper.SetDeviceSpecificData`
(`Reefin.Api/Helpers/MediaInfoHelper.cs:174-339`) → `PlaybackSessionManager.Create` → (depuis
PR115c) `MediaInfoHelper.ResolveServedStreamInfo`, qui consulte le canary
(`docs/pr115-design-canary-execution.md` §2) et peut servir une `StreamInfo` construite depuis un
plan v2 authoritaire — **transparent au client**, aucun champ de la réponse `PlaybackInfoResponse`
ne change de forme. C'est le point capital de la section suivante.

### 1.2 Ce que le serveur offre désormais (contrat v2 + canary)

Deux choses distinctes, à ne pas confondre — c'est l'erreur que ce document veut éviter de
reproduire :

**(A) Le canary (PR115a-d, fusionné) est déjà transparent aux clients legacy.** Un client qui
envoie un `DeviceProfile` brut via `PlaybackInfo` (comme `reefin-web` aujourd'hui) bénéficie déjà
d'une décision v2 quand le canary est authoritaire pour sa session — via le mappeur **permanent**
`ClientCapabilitiesMapper`/`DlnaPlaybackAdapter` (`src/Reefin.Playback.Dlna/ClientCapabilitiesMapper.cs`,
sens `DeviceProfile → ClientCapabilities`, à ne pas confondre avec le mappeur inverse **temporaire**,
voir §4.3), invoqué par `ShadowPlaybackSessionPlanner`
(seul appelant repo-wide de `DlnaPlaybackAdapter`, vérifié par grep). Migrer `reefin-web` **n'est
donc pas une condition pour que le canary serve du v2** — c'est déjà vrai aujourd'hui, sans aucun
changement client. Ce constat borne toute la suite : PR116 n'est pas un projet « activer v2 pour les
clients », c'est un projet « faire parler aux clients le protocole natif v2 » pour d'autres bénéfices
(§2).

**(B) Le contrat client v2 existe et est déjà exposé** :
`Reefin.Api/Controllers/PlaybackSessionsController.cs` — `POST`/`PUT {id}`/`DELETE {id}` sur
`Playback/Sessions`, `[Authorize]` sans élévation (donc bien le protocole client, pas
l'admin-only `PlaybackDiagnosticsSessionsController`). La requête
(`CreatePlaybackSessionRequest`/`ReplacePlaybackSessionRequest`,
`Reefin.Api/Models/PlaybackSessionDtos/`) prend `ClientCapabilities`/`PlaybackConstraints`
(`Reefin.Playback.Decision`, PR91) directement — plus de `DeviceProfile`. La réponse
(`PlaybackSessionResponse`) porte `DecisionVersion` : depuis PR115a, c'est le vrai
`PlaybackDecision.EngineVersion` quand un `V2PlanRecord` retenu est viable pour la session, sinon le
sentinel `LegacyDecisionVersion` (= `0`, `PlaybackSessionResponse.cs:65`) — **jamais un
`DecisionVersion` non nul sans qu'une vraie décision v2 l'ait produit** (garantie documentée dans
le commentaire XML de `PlaybackSessionResponse.DecisionVersion`).

**Point structurant, vérifié en lisant le DTO en entier** (`PlaybackSessionResponse.cs:43-53`) :
`Id`, `Kind`, `DecisionVersion`, `Method`, `Output`, `SelectedStreams`, `Transforms[]`, `Reasons[]`,
`CreatedAt`/`UpdatedAt`. **Aucun champ URL, aucun `TranscodingUrl`, aucun `PlaySessionId` autre que
l'`Id` de session lui-même.** PR92 §4.2 le dit en toutes lettres : « Pas de `StreamInfo`, pas d'URL
ffmpeg, pas de chemin. Le client obtient *ce qu'il aura* (méthode, sortie, streams), pas les
rouages internes. » Ce n'est donc pas une omission de PR112 à corriger : c'est la conception. La
conséquence pour PR116 est développée en §1.3 et §3.

### 1.3 L'écart

Le vrai gap n'est pas « `reefin-web` n'a pas encore de client v2 » (le SDK généré existe déjà,
§1.4) — il est double :

1. **Fidélité de déclaration.** `reefin-web` déclare ses capacités sous forme `DeviceProfile`
   DLNA, retraduites côté serveur vers le domaine (§1.2.A) par un mappeur qui projette
   fidèlement ce que le `DeviceProfile` *déclare*, mais **pas** ce que `ClientCapabilities` pourrait
   exprimer nativement de plus riche (le domaine PR91/PR102 sépare `Decode`/`OutputProfiles` avec un
   ordre de préférence explicite — un `DeviceProfile` DLNA porte une structure plus rigide,
   `TranscodingProfile` par conteneur, que le mappeur doit *interpréter*). Migrer `reefin-web` vers
   une déclaration native supprime un aller-retour de traduction, pas seulement un habillage.
2. **Le client n'a aujourd'hui aucune visibilité sur `DecisionVersion`.** Il consomme
   `PlaybackInfoResponse` (forme `MediaSourceInfo`/`StreamInfo`-adjacente), qui ne porte pas ce
   champ. Un client qui veut savoir « ma session a-t-elle été décidée par v2 ou par legacy ? » (pour
   du diagnostic, du support, ou conditionner un futur comportement) ne peut pas le savoir
   aujourd'hui sans appeler séparément `Playback/Sessions` (ce que la page diagnostics
   `apps/dashboard/features/playback` de `reefin-web` fait déjà, en lecture seule admin — voir
   `reefin-web/docs/reefin/design-web-playback-diagnostics.md`).

**Gap documentaire trouvé en cours de route** : `reefin-web/docs/reefin/design-web-playback-diagnostics.md:78-79`
affirme encore « `PlaybackSessionResponse.DecisionVersion` est toujours `LegacyDecisionVersion` tant
que PR115 (...) n'est pas livré » — **obsolète** : PR115a-d sont fusionnées sur `master` `reefin`
depuis cette rédaction. Ce n'est pas un bug de `reefin-web`, juste un doc non mis à jour ; à corriger
au passage d'une tranche PR116 (§3, PR116b).

### 1.4 Ce qui existe déjà côté `reefin-web` et change la donne

`reefin-web` a un précédent direct et récent pour ce type de migration :
`reefin-web/docs/reefin/design-reefin-api-layer.md` (2026-07-16) documente le remplacement progressif
de `@jellyfin/sdk` par un SDK **généré depuis l'OpenAPI de `reefin`**
(`src/lib/reefin-sdk/generated/`, `openapi-generator-cli` 7.11.0, script
`npm run generate:reefin-sdk`). Trois PR déjà livrées côté `reefin-web` (PR1-3 de ce document,
commits `ce75215`/`1bcd6c6` + PR3) :

- Le SDK généré **contient déjà** les endpoints `Playback/Sessions` typés — vérifié directement :
  `src/lib/reefin-sdk/generated/api/playback-api.ts` exporte
  `createPlaybackSession`/`replacePlaybackSession`/`deletePlaybackSession`, et les modèles
  `create-playback-session-request.ts`, `playback-session-response.ts`,
  `playback-decision-client-capabilities.ts`, `playback-decision-decode-capabilities.ts`,
  `playback-decision-playback-constraints.ts`, `playback-decision-playback-output-profile.ts`
  existent tous dans `src/lib/reefin-sdk/generated/models/`. **La génération n'est donc pas une
  tranche de PR116** — elle est déjà faite, en tant qu'effet de bord de la génération globale
  depuis l'OpenAPI serveur (299 routes, 395 schémas au moment du pin — `spec/version.json`).
- `useApi()` (`src/hooks/useApi.tsx`) expose déjà `reefinApi` en parallèle de l'`api` legacy
  (`@jellyfin/sdk`), même `DeviceId`, tenu à jour par mutation en place — **pas** une bascule
  complète (`design-reefin-api-layer.md` §9.3 documente explicitement pourquoi : `playbackmanager.js`
  est listé comme l'un des 15+ fichiers dépendant de `api.subscribe(...)` WebSocket, que `ReefinApi`
  ne porte pas encore).
- Un pattern de pont existant (`systemApiFor()`, `apps/dashboard/features/playback/api/playbackDiagnosticsApi.ts`)
  construit une classe générée à partir de la session `@jellyfin/sdk` existante plutôt que via
  `createReefinApi()` indépendant — évite un second `DeviceId` pour la même session. C'est le
  patron direct à réutiliser pour toute tranche PR116 qui appelle `Playback/Sessions` avant que
  `playbackmanager.js` (WebSocket-dépendant) ne puisse basculer entièrement sur `reefinApi`.
- **Contrainte d'encapsulation déjà posée et non résolue** :
  `reefin-web/docs/reefin/RFC-0001-vision-and-feasibility.md` §6.2 nomme `playbackmanager.js`
  (4342 lignes, 63 importeurs dont 12 dans `apps/modern/`) comme « cible n°1 d'encapsulation »,
  **non commencée** — et §7 place « le lecteur modernisé (première tranche qui attaque sérieusement
  l'encapsulation de `playbackmanager.js`) » en **phase 4** de la roadmap `reefin-web` (après
  administration simplifiée, avant fonctions intégrées). PR116 n'attend pas cette phase : les
  tranches §3 sont conçues pour ajouter un appel `Playback/Sessions` **à côté** du flux existant
  sans réécrire `playbackmanager.js`, précisément parce que l'encapsulation complète n'est pas un
  prérequis pour une lecture en ombre (shadow) — seulement pour une bascule complète du chemin de
  lecture (tranche bloquée, §3).
- **Piège de nommage à documenter dans le code, pas seulement ici** : `Reefin.Model.Session.ClientCapabilities`
  (`Reefin.Model/Session/ClientCapabilities.cs`, capacités de session — commandes générales,
  types de média jouables, sans rapport avec la décision de lecture) et
  `Reefin.Playback.Decision.ClientCapabilities` (le type PR91 pertinent ici) portent le même nom
  simple. Le SDK généré les distingue par préfixe de fichier
  (`playback-decision-client-capabilities.ts` vs un éventuel `client-capabilities-dto.ts` — ce
  dernier existe côté serveur, `Reefin.Model/Dto/ClientCapabilitiesDto.cs`, autre projection encore).
  Toute tranche PR116 doit importer le bon type — l'erreur ne casserait pas la compilation TS (les
  deux ont des champs différents mais le générateur ne les unifie pas), elle produirait un payload
  silencieusement invalide.

---

## 2. Cible

`reefin-web` déclare `ClientCapabilities`/`PlaybackConstraints` nativement (plus de construction
`DeviceProfile` pour ce usage), appelle `Playback/Sessions` (`POST`/`PUT`) via le `PlaybackApi`
généré de `reefin-sdk`, et lit `DecisionVersion` sur la réponse — au minimum à des fins de
diagnostic/télémétrie, et à terme (sous réserve du prérequis serveur §3, tranche bloquée) pour
piloter le démarrage effectif de la lecture. `ReverseDlnaAdapter`
(`src/Reefin.Playback.Dlna/ReverseDlnaAdapter.cs`) et le mappeur DLNA→domaine
(`ClientCapabilitiesMapper`/`DlnaPlaybackAdapter`) deviennent retirables **quand plus aucun client
de production n'a besoin d'eux** — condition explicitement distincte pour chacun (§4.3), et non
tenue par PR116 seul.

Non-cible explicite : ce document ne prescrit pas de réécrire `playbackmanager.js` ni de livrer
l'encapsulation RFC-0001 §6.2 — voir §5.

---

## 3. Tranches de migration

Toutes les tranches ci-dessous vivent dans **`reefin-web`**, sauf mention contraire. Elles suivent
la règle déjà en vigueur dans ce dépôt (`design-reefin-api-layer.md` §7) : bascule *all-or-nothing*
au niveau fichier/feature, jamais ligne à ligne à l'intérieur d'un même appel réseau, et **derrière
un flag** tant que le comportement de lecture réel n'est pas prouvé équivalent.

### PR116a — Constructeur natif `ClientCapabilities`/`PlaybackConstraints` (pas de réseau)

Nouveau module (`src/scripts/reefinPlaybackCapabilities.ts` ou équivalent), parallèle à
`browserDeviceProfile.js`, qui réutilise les primitives de détection déjà présentes
(`canPlayH264`/`canPlayHevc`/`canPlayAv1`/détection HLS/AC3/EAC3/DTS — toutes déjà dans
`browserDeviceProfile.js`, à extraire ou dupliquer sciemment le temps de la coexistence) mais
produit la forme domaine `DecodeCapabilities`/`PlaybackOutputProfile` (types générés
`playback-decision-decode-capabilities.ts`/`playback-decision-playback-output-profile.ts`) au lieu
d'un `DeviceProfile` DLNA. Aucun appel réseau. Pas de changement de comportement observable.

**Critères de sortie** : tests unitaires comparant, pour une matrice de navigateurs/appareils
connue (celle déjà couverte par les tests existants de `browserDeviceProfile.js` si présents, sinon
au moins Chrome desktop/Safari/tvOS-like), que le nouveau builder ne déclare rien que
`profileBuilder` ne déclare pas déjà (sous-ensemble strict acceptable au premier jet, sur-ensemble à
justifier). Aucun site d'appel modifié.

### PR116b — Appel shadow, lecture seule, sans impact sur la lecture réelle

Câble le `PlaybackApi` généré (déjà existant, §1.4) dans `playbackmanager.js` via le patron
`systemApiFor()`-like (réutilise `api.basePath`/`axiosInstance`/`authorizationHeader` de la session
`@jellyfin/sdk` existante — **pas** `createReefinApi()` indépendant, pour ne pas risquer un second
`DeviceId`, cf. §1.4). Derrière un flag (`appSettings` ou équivalent, défaut **off**) : après
l'appel `getPlaybackInfo()` existant (inchangé), déclenche un `POST Playback/Sessions`
**best-effort, fire-and-forget, jamais bloquant** avec les capacités du builder PR116a, puis logue
(console + éventuellement un point de télémétrie existant si `reefin-web` en a un) `DecisionVersion`
et `Method` de la réponse v2 à côté du `PlayMethod` que le flux legacy a réellement choisi. Aucune
branche du code de lecture réel ne lit cette réponse.

Corrige au passage la note obsolète de `design-web-playback-diagnostics.md` §1.3 (DecisionVersion
plus toujours 0 depuis PR115).

**Critères de sortie** : un appel shadow visible en dev tools réseau à chaque lecture (flag activé),
zéro régression sur la lecture réelle (le flux `getPlaybackInfo`/`createStreamInfo` existant est
totalement inchangé), l'échec de l'appel shadow (réseau, 400, 422) ne doit produire qu'un log, jamais
une exception remontée à l'appelant. Tests unitaires sur le point d'intégration (mock axios), pas de
nouveau test Playwright nécessaire (aucun comportement UI observable).

### PR116c — Comparaison visible en diagnostic admin

Étend `apps/dashboard/features/playback` (déjà le consommateur `reefin-sdk` existant côté admin,
§1.4/PR2 de `design-reefin-api-layer.md`) pour afficher, à côté de la comparaison legacy/v2 déjà
exposée par `PlaybackDiagnosticDetail.Comparison` (shadow serveur, PR113/PR98), une deuxième colonne
issue de l'appel shadow **client** PR116b (capacités natives déclarées vs capacités reconstruites
depuis le `DeviceProfile` DLNA par le serveur) — donne un signal direct sur l'écart de fidélité de
déclaration nommé en §1.3.1. Nécessite que l'appel PR116b transmette un identifiant de corrélation
exploitable (le plus simple : le `Playback/Sessions` shadow crée sa **propre** session distincte de
celle de `PlaybackInfo` — pas de fusion, deux sessions parallèles, acceptable puisque purement
observationnel).

**Critères de sortie** : page diagnostics existante affiche la comparaison sans régression de ses
tests existants (`playbackDiagnosticsApi.test.ts` et pairs) ; nouveau test pour le nouveau champ.

### PR116d — BLOQUÉE : bascule du démarrage de lecture sur la réponse v2

**Ne peut pas être livrée avec le contrat serveur actuel.** Nommée ici pour mémoire et parce que le
brief initial de ce document l'attendait comme tranche c/d — la conclusion de l'investigation (§1.2)
est qu'elle dépend d'un **prérequis serveur non designé** : `PlaybackSessionResponse` ne porte
aucune URL exécutable, et aucun endpoint compagnon ne permet de résoudre « donne-moi les octets pour
la session `Id` que `Playback/Sessions` vient de créer » — vérifié en lisant `StreamInfo.ToUrl()`
(`Reefin.Model/Dlna/StreamInfo.cs:877-1143`, jamais appelé pour une session créée via
`PlaybackSessionsController`) et en confirmant que `DynamicHlsController` ne consulte jamais
`IV2PlanStore` ni ne re-décide (`docs/pr115-design-canary-execution.md` §2, vérifié dans
`StreamingHelpers.GetStreamingState`). Voir §4.3 pour la forme que ce prérequis devrait probablement
prendre — hors périmètre design de ce document, à traiter dans son propre PR serveur (numéro non
alloué ici) avant que PR116d puisse être re-designée concrètement.

Retrait de `DeviceProfile`/`browserDeviceProfile.js` du chemin de démarrage de lecture (pas
seulement de la déclaration, §5) est conditionné à cette même tranche — pas de retrait partiel
prévu.

---

## 4. Risques et invariants

### 4.1 Décalage de version (skew)

- **Client ancien / serveur neuf** : sans objet pour PR116a-c — elles sont additives, un
  `reefin-web` non migré continue de fonctionner à l'identique (§1.2.A, le canary sert déjà v2 de
  façon transparente). Redevient pertinent pour une éventuelle PR116d future : un `reefin-web`
  migré vers un flux v2-only face à un serveur `reefin` plus ancien sans le prérequis §4.3 casserait
  la lecture — raison de plus pour ne pas livrer PR116d avant que ce prérequis existe et soit
  déployé.
- **Client neuf / serveur ancien (sans PR112b/PR115)** : le SDK généré (`reefin-sdk`) est pinné à un
  serveur `12.0.0` alors que `reefin-web` est en `13.0.0`
  (`reefin-web/src/lib/reefin-sdk/spec/version.json`, `versionSkewNote` déjà explicite dans le
  fichier committé, upgrade tracé pour W14.1 côté `reefin-web`). PR116b doit vérifier au moment de
  l'implémentation que le spec pinné inclut bien PR112b/PR115a (`DecisionVersion`,
  `ClientCapabilities`/`PlaybackConstraints` comme requête) — sinon régénérer (`npm run
  generate:reefin-sdk`) avant de démarrer PR116a, pas après.
- **`verify:reefin-sdk-fresh` n'est pas un gate CI actif** (`reefin-web/docs/reefin/design-reefin-api-layer.md`
  §9.4 : le job `reefin-sdk-contract-check.yml` n'a jamais été livré) — un désalignement spec/serveur
  ne serait pas détecté automatiquement pendant PR116. À exécuter manuellement
  (`npm run verify:reefin-sdk-fresh`, ou l'équivalent Docker documenté dans
  `reefin-web/src/lib/reefin-sdk/README.md`) avant chaque tranche PR116 qui touche au SDK généré.

### 4.2 Interaction avec le kill switch et le canary

**Vérifié avant d'écrire cette section** (une première version affirmait un risque de contamination
des métriques opérationnelles PR115d — infirmé par la lecture du code, corrigé ici) :
`PlaybackOperationalMetrics`/`PlaybackStopThresholdGuard` ne sont injectés que dans
`MediaInfoHelper` (`Reefin.Api/Helpers/MediaInfoHelper.cs:56-57,112-113`) et
`PlaybackDiagnosticsMetricsController` — jamais dans `PlaybackSessionsController` ni dans
`PlaybackSessionManager` pour la décision servi/repli elle-même (grep repo-wide confirmé, hors
tests). `PlaybackSessionManager` référence bien `PlaybackOperationalMetrics`
(`PlaybackSessionManager.cs:29,82,94,102`), mais uniquement pour `RecordTranscodeStart`
(`PlaybackSessionManager.cs:495,534`), déclenché par les événements réels
`ITranscodeManager.TranscodingJobStarted`/`TranscodingJobEnded` — c'est-à-dire seulement quand
ffmpeg démarre réellement pour une session, ce qui suppose qu'un client ait effectivement appelé
l'URL de streaming. Une session créée par un `POST Playback/Sessions` shadow (PR116b) n'a pas d'URL
(§1.2) et n'est donc **jamais streamée** : aucun `TranscodingJobStarted`/`Ended` ne peut s'y
rattacher.

**Conclusion, kill switch et garde-fou d'arrêt** : orthogonaux à PR116a-c. `PlaybackShadowOptions.Mode`
et `PlaybackStopThresholdGuard` opèrent exclusivement sur le chemin octets
(`MediaInfoController`/`UniversalAudioController`/`OpenMediaSource` → `ResolveServedStreamInfo`),
transparent au protocole de déclaration du client (§1.2.A). Migrer le protocole de déclaration ne
touche ni l'un ni l'autre — pas de risque de contamination des seuils d'arrêt PR115d par le shadow
client.

**Le vrai risque d'implémentation, plus étroit** : `CreatePlaybackSessionRequest.PlaySessionId` est
documenté « au plus une session par id — créer avec le même id la remplace »
(`CreatePlaybackSessionRequest.cs:15-18`). Un appel shadow PR116b qui réutiliserait le
`PlaySessionId` d'une vraie session en cours **écraserait** son `V2PlanRecord`/sa rétention shadow
retenus sous cet id — silencieusement, sans erreur. PR116b doit donc soit omettre `PlaySessionId`
(session anonyme, jamais réclamée par un vrai `PlaySessionId`), soit en générer un distinct dédié au
shadow, et nettoyer la session après coup (`DELETE Playback/Sessions/{id}`) pour ne pas accumuler
d'entrées mortes dans `IV2PlanStore`.

**Résiduel, attendu et sans danger** : si `Mode` est `Shadow`/`Canary`/`V2`, chaque `POST` shadow
fait tourner `ShadowPlaybackSessionPlanner` au même titre que n'importe quel appel à
`PlaybackSessionManager.Create` (PR115a : « un run autoritaire doit avoir lieu à chaque appel pour
lequel il est autoritaire ») — alimente donc `ShadowMetrics` (PR100, échantillonné/budgété,
purement observationnel) et `IV2PlanStore`, mais **pas** `PlaybackOperationalMetrics`/le garde-fou.
La session shadow suit la même règle de cohorte déterministe `CanaryCohort` (user+device) que
n'importe quelle autre — elle n'en crée pas de nouvelle catégorie, et n'a aucune influence sur
`CanaryPercentage`.

### 4.3 Retrait des adaptateurs temporaires — deux conditions distinctes, pas une

Le brief de ce document nomme « le mappeur DLNA inverse / les adaptateurs temporaires que le plan
veut retirer progressivement ». Il y en a **deux**, avec des conditions de retrait indépendantes —
les confondre serait une erreur de conception :

- **`ReverseDlnaAdapter`/`ReverseClientCapabilitiesMapper`/`ReverseConstraintsMapper`**
  (`src/Reefin.Playback.Dlna/ReverseDlnaAdapter.cs`, doc XML déjà « TEMPORARY ») — sens
  `ClientCapabilities → DeviceProfile`, appelé dans `PlaybackSessionsController.ResolveOptions`
  (`Reefin.Api/Controllers/PlaybackSessionsController.cs:168`) parce que
  `PlaybackSessionManager.Create` a **toujours** besoin d'un `DeviceProfile` pour faire tourner le
  legacy `StreamBuilder` comme plan de repli (canary ou pas). Retirable seulement quand
  `PlaybackSessionManager.Create` n'a plus besoin de ce plan de repli du tout — c'est-à-dire quand
  legacy cesse d'être consulté sur ce chemin, condition **serveur pure** (canary à 100 % + décision
  de retirer StreamBuilder de ce chemin), gouvernée par le runbook PR115d
  (`docs/pr115d-rollout-runbook.md`), **indépendante de l'adoption de `reefin-web`** — même si
  aucun client ne migre jamais vers `Playback/Sessions`, cette condition peut être remplie côté
  serveur seul (n'importe quel appelant de `Playback/Sessions` envoie déjà `ClientCapabilities`,
  c'est la forme du DTO).
- **`ClientCapabilitiesMapper`/`DlnaPlaybackAdapter`** (sens `DeviceProfile → ClientCapabilities`,
  §1.2.A) — appelé par `ShadowPlaybackSessionPlanner` pour tout client qui envoie encore un
  `DeviceProfile` brut, c'est-à-dire tout appel à `/Items/{itemId}/PlaybackInfo`. Celui-ci retire
  seulement quand **plus aucun client de production** (pas seulement `reefin-web` — tout client
  tiers Jellyfin-compatible visant `reefin`, hors périmètre RFC-0001 §5 de `reefin-web` mais pas
  hors périmètre du serveur `reefin`) n'appelle plus `PlaybackInfo` du tout. C'est un chantier
  **beaucoup plus large** que PR116 (dépréciation d'un endpoint public consommé potentiellement par
  des clients tiers non maintenus dans ce dépôt) — PR116 fait au mieux baisser le volume d'appels
  d'un client (`reefin-web`), il ne peut pas à lui seul satisfaire cette condition. À nommer
  explicitement hors périmètre de toute PR116x (§5).

### 4.4 Stratégie de test par tranche

- **PR116a** : unitaire pur (Vitest ou équivalent déjà en place dans `reefin-web` — voir tests
  existants `*.test.ts` cités en §1.4), aucune dépendance réseau, matrice de navigateurs simulée.
- **PR116b** : unitaire sur le point d'intégration (mock `axios`/mock `PlaybackApi`), plus une
  vérification manuelle en dev (flag activé, `npm run test:e2e` existant —
  `tests/e2e/home.spec.ts` — n'a pas besoin d'extension puisque aucun comportement UI ne change ;
  un nouveau test Playwright n'a de sens qu'à partir de PR116c où un écran change réellement).
- **PR116c** : étend les tests existants de `playbackDiagnosticsApi.ts`/`playbackDiagnosticsApi.test.ts`
  (déjà un précédent direct, §1.4) ; test Playwright optionnel si la page diagnostics a déjà une
  couverture e2e (non vérifié dans ce document — à confirmer avant PR116c, la seule spec e2e trouvée
  ici est `home.spec.ts`, périmètre différent).
- **PR116d** (bloquée) : sa stratégie de test dépend entièrement de la forme du prérequis serveur
  §4.3/ci-dessus, non designée ici.

---

## 5. Hors périmètre

- **Réécriture ou encapsulation de `playbackmanager.js`** (RFC-0001 §6.2, phase 4 de la roadmap
  `reefin-web`). PR116a-c s'ajoutent au flux existant sans y toucher structurellement ; PR116d, si
  elle devient designable, pourrait rouvrir cette question mais ne la préempte pas ici.
- **Conception du prérequis serveur « URL exécutable depuis une session v2 »** (§3 PR116d, §4.3).
  Nommé, pas designé — nécessite son propre document, potentiellement une extension de
  `PlaybackSessionResponse` ou un nouvel endpoint compagnon (`GET Playback/Sessions/{id}/Stream` ou
  similaire), avec ses propres implications sur l'invariant de parité exécutable PR115b
  (`docs/pr115-design-canary-execution.md`, section normative) — un nouveau point de sérialisation
  d'URL rouvrirait potentiellement cet invariant s'il ne réutilise pas `PlaybackExecutionPlanAdapter.ToStreamInfo`
  tel quel.
- **Retrait de `ClientCapabilitiesMapper`/`DlnaPlaybackAdapter`** (§4.3, deuxième condition) —
  dépend de clients hors `reefin-web`, hors périmètre de ce document et du dépôt `reefin-web`.
- **Renommage de l'identité client** (`REEFIN_CLIENT_IDENTITY` = `'Jellyfin Web'` aujourd'hui,
  `reefin-web/docs/reefin/branding-audit.md` catégorie 1) — orthogonal, nécessite sa propre
  coordination de migration de session.
- **WebSocket sur `reefinApi`/`ReefinApi`** — bloquant pour une bascule complète de
  `playbackmanager.js` vers `reefinApi` (`design-reefin-api-layer.md` §9.3/§9.5), mais PR116a-c ne
  le requièrent pas (appels REST ponctuels via le patron `systemApiFor()`-like, pas une bascule de
  session entière).
- **Clients autres que `reefin-web`** (mobile, TV, Kodi, ...) — RFC-0001 §5 les exclut déjà du
  périmètre direct de `reefin-web` ; ce document ne les traite pas non plus, au-delà de la mention
  §4.3 sur pourquoi ils bloquent le retrait du mappeur DLNA→domaine.
- **CI contract-check `reefin-sdk-contract-check.yml`** (`design-reefin-api-layer.md` §9.4, dette
  déjà nommée côté `reefin-web`, non résolue ici) — recommandé avant PR116b en usage manuel (§4.1),
  pas livré par PR116.

---

## 6. Critères de sortie de PR116 (ce document)

- [x] État des lieux du flux de lecture `reefin-web` actuel, avec fichiers/lignes réels.
- [x] Constat vérifié que le canary serveur sert déjà v2 aux clients legacy, sans changement client
      (§1.2.A) — reformule l'objectif de PR116 en conséquence.
- [x] Constat vérifié et bloquant : `PlaybackSessionResponse` ne porte pas d'URL exécutable, aucune
      tranche PR116 ne peut donc consommer la réponse v2 pour démarrer une vraie lecture sans un
      prérequis serveur non designé ici (§1.2, §3 PR116d, §4.3).
- [x] Tranches PR116a-c concrètes, additives, testables, sans dépendance à l'encapsulation
      `playbackmanager.js`.
- [x] Deux conditions de retrait distinctes identifiées pour les deux adaptateurs temporaires
      (§4.3), l'une gouvernée côté serveur seul, l'autre hors périmètre `reefin-web`.
- [x] Risque néfaste concret nommé sur l'interaction shadow client / garde-fou opérationnel PR115d
      (§4.2), non résolu — signalé comme prérequis d'implémentation de PR116b, pas ignoré.
- [ ] Implémentation : PR116a (builder natif), PR116b (appel shadow), PR116c (diagnostic admin) —
      toutes côté `reefin-web`, hors périmètre de ce document.
