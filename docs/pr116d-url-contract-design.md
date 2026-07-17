# Design — PR117, le contrat de livraison d'URL pour les clients v2 (prérequis serveur de PR116d)

- **PR** : PR117 (design uniquement, aucun code de production) — numéro non alloué avant ce document ;
  `docs/pr116-client-migration-design.md` §3/§4.3/§5 le nommait « prérequis serveur non designé »
  sans lui donner de numéro, précisément pour ne pas préempter ce document
- **Statut** : proposé
- **Dépend de** : PR112/PR112b (contrat client v2, `Playback/Sessions`), PR115a-d (canary, fusionné,
  `master` `reefin` à `4c20bf00a6`), PR113/PR113a/PR113b (discipline DTO filtrée admin), PR116
  (`docs/pr116-client-migration-design.md`, fusionné en doc — nomme le manque que ce document comble)
- **Précède** : PR116d (`reefin-web`, actuellement **bloquée** — voir `docs/pr116-client-migration-design.md`
  §3), qui consomme le contrat posé ici pour piloter le démarrage réel de la lecture
- **Dépôt concerné** : `reefin` uniquement (ce document). Le client `reefin-web` n'est pas modifié ici —
  voir §6 pour la tranche PR116d qui le sera.

Ce document fige le **contrat serveur** par lequel un client parlant le protocole v2
(`Playback/Sessions`, PR112) obtient une URL exécutable pour récupérer les octets d'une session qu'il
vient de faire planifier — le chaînon manquant identifié par `docs/pr116-client-migration-design.md`
§1.2/§1.3 : « `PlaybackSessionResponse` ne porte aucune URL exécutable ». Aucun câblage client, aucun
changement de comportement pour les clients legacy — au même sens que PR92/PR115b avant lui. La
conclusion structurante (§2) est que la solution n'est **pas** d'ajouter l'URL à `PlaybackSessionResponse`
mais un **endpoint compagnon dédié** : une inspection du code (§1.4) montre que `PlaybackSessionResponse`
est aujourd'hui enveloppé **tel quel**, par composition, dans une projection consommée par une surface
admin élevée mais **cross-utilisateur** (`PlaybackSessionListItem`, `System/PlaybackDiagnostics/Sessions`)
— y ajouter une URL porteuse d'un jeton d'accès (`StreamInfo.ToUrl()` sérialise `&ApiKey=` en clair,
`Reefin.Model/Dlna/StreamInfo.cs:1032-1036`) rouvrirait exactement la fuite `TranscodingUrl`/jeton que
PR113 a fermée (`PlaybackDiagnosticDetail.cs:17` : « never a file path, transcoding URL, session token,
or API key »). Ce constat, pas une préférence de style, tranche entre les options évaluées en §2.

---

## 1. État des lieux

### 1.1 Ce que `Playback/Sessions` fait déjà — et ce qu'il ne fait pas

`PlaybackSessionsController.CreatePlaybackSession`/`ReplacePlaybackSession`
(`Reefin.Api/Controllers/PlaybackSessionsController.cs:80-131`) planifient une session via
`ResolveOptions` (`:150-186`), qui construit un `MediaOptions` complet — `Profile` (via
`ReverseDlnaAdapter.ToDeviceProfile(request.Capabilities)`, `:168`), `DeviceId` (`User.GetDeviceId()`,
`:172`), `MediaSources` (toutes les sources candidates, `:164`) — puis appelle
`_playbackSessionManager.Create(new PlaybackSessionRequest(kind, options), request.PlaySessionId)`
(`:86`). Ce `Create` est **le même appel**, sur le **même** `IPlaybackSessionManager`, que celui que
`MediaInfoHelper.SetDeviceSpecificData` invoque pour le flux legacy (`Reefin.Api/Helpers/MediaInfoHelper.cs:310`)
— même magasin de sessions (`PlaybackSessionManager.StoreOrReplace`,
`Reefin.MediaEncoding/Playback/PlaybackSessionManager.cs:309-332`), même planificateur legacy
(`StreamBuilder` via `Plan(request)`), même capture v2 (`IV2PlanStore`/`ShadowPlaybackSessionPlanner`
si le mode effectif l'autorise).

Ce que `PlaybackSessionsController` ne fait **jamais**, contrairement à `MediaInfoHelper.SetDeviceSpecificData` :
il n'appelle jamais `ResolveServedStreamInfo` (`MediaInfoHelper.cs:432-520`, privée) ni
`StreamInfo.ToUrl()` (`Reefin.Model/Dlna/StreamInfo.cs:877-1143`). Le `StreamInfo` legacy que la
planification produit (`session.Plan.StreamInfo`) existe bien en mémoire côté serveur — c'est
exactement ce que `MediaInfoHelper` transforme en URL — mais rien ne le sérialise pour un appelant de
`Playback/Sessions`. C'est la vérification de terrain qui referme la question ouverte par
`docs/pr116-client-migration-design.md` §1.2/§3 : le manque n'est pas conceptuel (« il faudrait
resoudre l'URL quelque part »), il est mécanique et localisé (« il faut appeler la même logique que
`ResolveServedStreamInfo` depuis ce contrôleur, avec les mêmes garanties »).

### 1.2 Ce qu'il faut pour reproduire `ResolveServedStreamInfo` depuis `Playback/Sessions`

Vérification champ par champ des paramètres de `ResolveServedStreamInfo`/`PlaybackExecutionContext`
contre ce que `PlaybackSessionsController` a déjà en main après `Create` :

| Paramètre nécessaire | Source côté `MediaInfoHelper` (legacy) | Source équivalente côté `Playback/Sessions` |
|---|---|---|
| `sessionId` | `session.Id` | `session.Id` — identique |
| `legacyStreamInfo` | `session.Plan.StreamInfo` | `session.Plan.StreamInfo` — identique |
| `mediaSource` | passé par l'appelant (déjà choisi en amont) | `session.Plan.StreamInfo.MediaSource` — le legacy `StreamBuilder` a déjà tranché laquelle des `MediaOptions.MediaSources` (toutes envoyées par `ResolveOptions`, `:164`) il sert ; réutiliser cette source évite toute nouvelle logique de sélection |
| `profile` | passé par l'appelant (`DeviceProfile` du protocole legacy) | `session.Request.Options.Profile` — retenu sur `PlaybackSession.Request` (`PlaybackSessionRequest.Options`, `Reefin.Controller/MediaEncoding/PlaybackSessionRequest.cs:10`), c'est le `DeviceProfile` que `ReverseDlnaAdapter.ToDeviceProfile` a construit à la création |
| `itemId` | passé par l'appelant | `session.Request.Options.ItemId` |
| `deviceId` | passé par l'appelant | `session.Request.Options.DeviceId` |
| `playSessionId` | passé par l'appelant (string legacy) | `session.PlaySessionId` — **peut être `null`** (paramètre optionnel de `CreatePlaybackSessionRequest.PlaySessionId`, `:16-18`) ; voir §2.3 pour la garde nécessaire |
| `alwaysBurnInSubtitleWhenTranscoding` | passé par l'appelant | `session.Request.Options.AlwaysBurnInSubtitleWhenTranscoding` (`Reefin.Model/Dlna/MediaOptions.cs:55`, alimenté par `ReverseDlnaAdapter.ApplyConstraints`) |
| `startTimeTicks` | passé par l'appelant | **absent de `MediaOptions`** — `PlaybackConstraints.StartTimeTicks` (`src/Reefin.Playback.Decision/PlaybackConstraints.cs`, dernier champ) existe côté requête v2 mais `ResolveOptions` ne le recopie nulle part de persistant sur la session aujourd'hui ; voir §2.3, à trancher en implémentation |

Sept champs sur huit sont déjà disponibles sans aucun changement de forme des DTOs existants — seul
`startTimeTicks` demande une décision d'implémentation mineure (§2.3). C'est la preuve que le contrat
v2 (PR112b) a, dès sa conception, capturé tout ce qu'il fallait pour cette tranche ; il manquait
seulement le point d'appel.

### 1.3 L'invariant de parité exécutable s'applique sans changement

`docs/pr115-design-canary-execution.md`, section normative (« Invariant de parité exécutable ») :
« Aucun plan v2 ne peut alimenter le chemin live si tous les champs influençant `StreamInfo.ToUrl()`
ne sont pas reproduits avec parité ; sinon, fallback legacy explicite. » Ce document ne rouvre pas cet
invariant : la solution retenue en §2 **réutilise verbatim** `ResolveServedStreamInfo`/
`PlaybackExecutionPlanAdapter.ToStreamInfo`/`StreamInfo.ToUrl()`, sans nouvelle voie de sérialisation
d'URL — exactement la mise en garde de `docs/pr116-client-migration-design.md` §5 : « un nouveau point
de sérialisation d'URL rouvrirait potentiellement cet invariant s'il ne réutilise pas
`PlaybackExecutionPlanAdapter.ToStreamInfo` tel quel ». Ce document choisit explicitement de ne pas le
rouvrir.

### 1.4 La fuite qui élimine l'option la plus simple

`PlaybackDiagnosticsSessionsController.GetPlaybackSessions()`
(`Reefin.Api/Controllers/PlaybackDiagnosticsSessionsController.cs:59-70`), route
`System/PlaybackDiagnostics/Sessions`, `[Authorize(Policy = Policies.RequiresElevation)]` (admin,
**pas** scopée à l'utilisateur courant — elle liste **toutes** les sessions actuellement suivies,
tous utilisateurs confondus, `_playbackSessionManager.GetAll()`) construit chaque élément ainsi :

```csharp
new PlaybackSessionListItem(
    PlaybackSessionResponseMapper.Map(session),   // <- PlaybackSessionResponse entier, par composition
    _diagnosticsStore.TryGet(session.Id, out _),
    session.Request?.Options.ItemId,
    session.Request?.Options.DeviceId)
```

`PlaybackSessionListItem` (`Reefin.Api/Models/PlaybackSessionDtos/PlaybackSessionListItem.cs:29`) est
`sealed record PlaybackSessionListItem(PlaybackSessionResponse Session, bool HasDiagnostic, Guid? ItemId, string? DeviceId)`
— il **enveloppe** `PlaybackSessionResponse`, il ne recopie pas ses champs un à un. Tout champ ajouté
à `PlaybackSessionResponse` traverse donc automatiquement cette route admin, pour la session de
**n'importe quel utilisateur**, sans qu'aucun mainteneur n'ait à y penser. C'est structurellement
différent de `PlaybackDiagnosticDetailMapper.Map` (`:68-96`), qui reconstruit
`PlaybackDiagnosticDetail` champ par champ, positionnellement, depuis
`PlaybackSessionResponseMapper.Map(session)` (l'overload **sans** `v2Record`) — celui-ci ne fuiterait
pas un nouveau champ tant que le mapper n'est pas changé pour le recopier explicitement, mais
`PlaybackSessionListItem` n'offre aucune protection de ce genre : c'est une fuite automatique, pas une
fuite qu'il faudrait introduire par erreur.

Or `StreamInfo.ToUrl()` sérialise le jeton d'accès de l'appelant en clair dans la query string
(`&ApiKey=`, `Reefin.Model/Dlna/StreamInfo.cs:1032-1036`, valeur = `claimsPrincipal.GetToken()` côté
`MediaInfoHelper.cs:372,385`) — c'est un jeton porteur, pas un identifiant. Ajouter un champ URL à
`PlaybackSessionResponse` livrerait donc, à tout administrateur consultant
`System/PlaybackDiagnostics/Sessions`, le jeton d'authentification de **n'importe quel** utilisateur
ayant une session active — exactement la classe de fuite que PR113 a fermée (« closing the
`MediaSourceInfo.Path`/`OpenToken`/`TranscodingUrl` leak », commentaire XML du contrôleur diagnostics,
`PlaybackDiagnosticsSessionsController.cs:21`) et que `docs/major-rewrite-plan-v13.md` documente comme
tel dans son historique PR113. Ce n'est pas un risque théorique nécessitant une discipline de test à
maintenir sans faille : c'est une conséquence directe et automatique de la forme `record ... (Session,
...)`.

---

## 2. Le contrat

### 2.1 Options évaluées

**(a) Champ(s) URL ajoutés à `PlaybackSessionResponse`.** Rejetée : §1.4 le prouve, cette forme fuite
mécaniquement vers `System/PlaybackDiagnostics/Sessions` sans qu'aucune discipline de test ne puisse
la neutraliser sans restructurer `PlaybackSessionListItem` — ce qui reviendrait, de fait, à construire
une projection distincte (donc à choisir l'option (b) en pire) ou à casser la promesse « chaque route
admin est une projection filtrée, jamais l'objet interne » que PR113 a posée pour `PlaybackSession`
lui-même et que ce document doit étendre à `PlaybackSessionResponse`, pas y faire exception.

**(b) Endpoint compagnon dédié.** Retenue. `GET Playback/Sessions/{id}/Stream`
(`PlaybackSessionsController`, même contrôleur, même `[Authorize]` sans élévation — voir §4 pour le
contrôle de propriété additionnel requis), réponse `PlaybackSessionStreamDescriptor` — un DTO
**séparé** de `PlaybackSessionResponse`, jamais enveloppé par `PlaybackSessionListItem` ni par
`PlaybackDiagnosticDetail`, donc structurellement hors d'atteinte de la fuite §1.4. Détail en §2.2.

**(c) Construction de l'URL côté client, à partir des champs de décision.** Rejetée, pour deux
raisons independantes. Primo, `StreamInfo.ToUrl()` sérialise des champs que `PlaybackSessionResponse`
ne porte délibérément pas et que PR91/PR92 excluent nommément du contrat public — neuf champs
réservés à `PlayMethod.Transcode` (`RequireNonAnamorphic`, `RequireAvc`,
`TranscodingMaxAudioChannels`, `EnableSubtitlesInManifest`, `EnableMpegtsM2TsMode`,
`EstimateContentLength`, `TranscodeSeekInfo`, `CopyTimestamps`, `EnableAudioVbrEncoding`,
`docs/pr115-design-canary-execution.md` §Invariant point 1), plus `DeviceProfileId`,
`RequireAvc`/`RequireNonAnamorphic` — un client qui reconstruirait l'URL devrait soit deviner ces
valeurs (risque d'exécution divergente du flux réellement décidé), soit recevoir un DTO aussi riche
que `StreamInfo` lui-même, ce qui annule exactement la promesse « pas de `StreamInfo`, pas d'URL
ffmpeg, pas de chemin » de PR92 §4.2. Secundo, et plus fondamental : la décision « servi par v2 ou
replié sur legacy » dépend d'état serveur non exposable sans fuite (interrupteur d'urgence, garde-fou
de seuils d'arrêt PR115d, correspondance stricte de `SourceId`, exclusion Dolby Vision) — un client ne
peut pas reproduire cette résolution, et il ne doit pas avoir à le faire : c'est précisément la
logique que PR115c a centralisée côté serveur pour ne jamais la dupliquer.

### 2.2 Forme concrète du contrat retenu

```
GET Playback/Sessions/{id}/Stream?startTimeTicks={ticks}
[Authorize]  (même scope que le reste du contrôleur — pas d'élévation)
```

Réponse `200 OK`, nouveau DTO `PlaybackSessionStreamDescriptor` (à créer,
`Reefin.Api/Models/PlaybackSessionDtos/`) :

| Champ | Type | Rôle |
|---|---|---|
| `Url` | `string` | L'URL exécutable — sortie directe de `StreamInfo.ToUrl(null, accessToken, ...)`, chemin relatif (le client connaît déjà son `baseUrl`, comme pour `TranscodingUrl` legacy) |
| `Protocol` | même enum que `PlaybackSessionResponse.Output.Protocol` (`Hls`/`Http`) | Redondant avec `Output.Protocol` de la réponse POST/PUT d'origine mais **réévalué à cet instant** — voir §3 pour pourquoi cette redondance est nécessaire, pas accidentelle |
| `ServedBy` | `DecisionVersion`-shaped `int` (même sentinel `LegacyDecisionVersion = 0`) | La version du moteur qui a **réellement produit cette URL**, résolue au moment de l'appel — voir §3, distincte du `DecisionVersion` retourné par le POST/PUT initial |
| `FallbackReason` | `PlaybackLiveFallbackReason?` (nullable, même enum que `Reefin.MediaEncoding/Playback/PlaybackLiveFallbackReason.cs`, déjà utilisé en diagnostics admin — projection publique restreinte, voir §4) | `null` quand `ServedBy` est une vraie version v2 ; sinon la raison typée du repli, pour que le client puisse le journaliser/l'afficher sans avoir à deviner |
| `SubtitleUrl` | `string?` | Présent uniquement quand `PlaybackSessionResponse.SelectedStreams.Subtitle?.Method == External` — l'URL de livraison externe que `MediaInfoHelper.SetDeviceSpecificSubtitleInfo` pose aujourd'hui sur `mediaSource.MediaStreams[i].DeliveryUrl` (`MediaInfoHelper.cs:694-717`), absente de `PlaybackSessionResponse.SelectedStreams` (qui ne porte qu'un index + une méthode, jamais une URL, par design PR91) |

**Pourquoi `GET` et pas `POST`** : l'appel ne planifie rien de nouveau — il **projette** une décision
déjà prise (`session.Plan`/`session.Request` existent déjà, posés par le `POST`/`PUT` précédent) vers
une URL. Aucun effet de bord observable côté client (pas de nouvelle session, pas de nouveau plan) ;
la même session interrogée deux fois de suite sans changement d'état serveur entre les deux produit la
même URL (à l'exception du TOCTOU documenté en §3). C'est la même posture que
`PlaybackSessionResponseMapper.Map(session, v2Record)` : une lecture, pas une décision.

**Pourquoi `startTimeTicks` en paramètre de requête, pas repris de la session** : §1.2 documente que
`MediaOptions` ne porte pas ce champ aujourd'hui — `PlaybackConstraints.StartTimeTicks` existe côté
requête v2 mais n'est nulle part persisté sur `PlaybackSession`. Deux implémentations possibles,
tranchées au niveau de la tranche PR117 (§6), pas ici : (i) accepter `startTimeTicks` en paramètre de
requête optionnel sur `GET .../Stream`, valeur par défaut `0` si absent — le point de départ de la
lecture est une propriété du moment où on demande à lire, pas de la décision de compatibilité
elle-même, donc plutôt naturel comme paramètre de cet appel ; (ii) étendre `MediaOptions`/
`PlaybackSession` pour retenir `Constraints.StartTimeTicks` dès la création. (i) est préféré par ce
document — moins de surface d'état à faire vivre, cohérent avec le fait qu'une reprise de lecture
(retour sur un item en pause) doit pouvoir redemander une URL avec un nouveau point de départ sans
recréer toute la session.

### 2.3 Garde `PlaySessionId` obligatoire

`CreatePlaybackSessionRequest.PlaySessionId` est optionnel (`:16-18`). Or `StreamInfo.PlaySessionId`
alimente à la fois la query string (`&PlaySessionId=`, dédoublonnage/traçage côté lecteurs) et la
corrélation `ITranscodeManager.TranscodingJobStarted`/`Ended` que `PlaybackSessionManager` utilise pour
ses propres métriques (`docs/pr116-client-migration-design.md` §4.2, `PlaybackSessionManager.cs:495,534`).
Émettre une URL sans `PlaySessionId` casserait cette corrélation silencieusement. `GET .../Stream` doit
donc renvoyer **`409 Conflict`** (pas `400` — la requête de création était valide, c'est son absence de
`PlaySessionId` qui rend *cette* opération impossible) quand `session.PlaySessionId` est `null`, avec un
message dirigeant le client vers un `PUT {id}` (ou un nouveau `POST`) fournissant explicitement
`PlaySessionId`. Alternative rejetée : générer un `PlaySessionId` à la volée dans ce `GET` et muter la
session — un endpoint de lecture ne doit pas avoir d'effet de bord observable (§2.2), et cela romprait
la garantie « au plus une session par `PlaySessionId` » (`CreatePlaybackSessionRequest.cs:15-18`) sans
que l'appelant l'ait demandé.

---

## 3. Cohérence avec le canary

### 3.1 Le point exact où POST et GET peuvent diverger

Le `POST`/`PUT` initial (`PlaybackSessionResponse.DecisionVersion`) reflète la décision au moment de la
**planification** (`_v2PlanStore.TryGet(session.Id, out var v2Record)`,
`PlaybackSessionsController.cs:94,129`) — c'est un instantané. `GET .../Stream`, pour respecter §1.3
(réutilisation verbatim de `ResolveServedStreamInfo`), doit **réévaluer** l'interrupteur d'urgence, le
garde-fou de seuils d'arrêt PR115d, et la résolution du plan **au moment de l'appel** — exactement
comme `MediaInfoHelper.ResolveServedStreamInfo` le fait déjà à chaque requête `PlaybackInfo`, et
exactement le sens de la remarque documentée sur l'interrupteur : « takes effect on the very next
request, no restart required » (`MediaInfoHelper.cs:450-453`). Mettre en cache la résolution au moment
du `POST` casserait cette garantie opérationnelle pour toute session déjà créée avant qu'un opérateur
bascule l'interrupteur — un korrectif partiel, pas un vrai kill switch.

Conséquence acceptée, explicitement documentée pour le client (PR116d, §6) : entre l'instant T0 du
`POST`/`PUT` (`DecisionVersion` annoncé) et l'instant T1 du `GET .../Stream` (`ServedBy` réellement
résolu), l'un des six chemins de repli de `PlaybackLiveFallbackReason`
(`Reefin.MediaEncoding/Playback/PlaybackLiveFallbackReason.cs:12-74` — `NoAuthoritativeRecord`,
`PlanNotExecutable`, `SourceIdMismatch`, `DolbyVisionExclusion`, `KillSwitch`, `AdapterError`, plus
`StopThresholdTripped` PR115d) peut s'être déclenché entretemps. **Le contrat explicite : `ServedBy`/
`FallbackReason` du `GET .../Stream` font foi pour ce qui sera effectivement livré ; `DecisionVersion`
du `POST`/`PUT` est un signal de planification, pas une promesse d'exécution.** C'est la même tolérance
TOCTOU que le flux legacy accepte déjà aujourd'hui (une URL `TranscodingUrl` legacy, une fois émise par
un appel `PlaybackInfo`, encode une décision figée que `DynamicHlsController` exécute telle quelle sans
jamais revérifier l'état courant du canary — voir §3.2) ; ce document ne l'introduit pas, il l'étend
au nouveau point d'entrée avec la même discipline.

### 3.2 Ce qui NE change pas après l'émission de l'URL

`docs/pr116-client-migration-design.md` §3 PR116d cite déjà la vérification : `DynamicHlsController`
ne consulte jamais `IV2PlanStore` ni ne re-décide (`StreamingHelpers.GetStreamingState`,
`Reefin.Api/Helpers/StreamingHelpers.cs:45`) — une fois l'URL construite (par
`PlaybackExecutionPlanAdapter.ToStreamInfo` si `ServedBy` est v2, ou par le `StreamInfo` legacy sinon),
tous les paramètres de décision (codec cible, conteneur, `RequireAvc`/`RequireNonAnamorphic`, etc.)
sont **gelés dans la query string** — l'exécution reproduit l'invariant de parité (§1.3), elle ne
redécide rien. Le seul TOCTOU réel est donc celui de §3.1, borné à la fenêtre entre le `POST`/`PUT` et
le `GET .../Stream` — jamais entre l'émission de l'URL et sa lecture effective par le client.

### 3.3 Extraction requise, pas duplication

`ResolveServedStreamInfo` est aujourd'hui `private` sur `MediaInfoHelper` (`:432`). La tranche PR117
(§6) doit l'extraire vers un composant partagé et injectable (proposition :
`IPlaybackLiveStreamResolver`, `Reefin.MediaEncoding/Playback/` ou `Reefin.Api/Helpers/`, signature
proche de l'actuelle mais sans dépendance à `ClaimsPrincipal`/`IPAddress` — ces deux-là restent
propres à l'appelant), consommé à la fois par `MediaInfoHelper.SetDeviceSpecificData` (comportement
inchangé) et par le nouveau point d'entrée `Playback/Sessions/{id}/Stream`. **Dupliquer** la logique
(kill switch, garde-fou, résolution de plan, vérification `SourceId`, exclusion Dolby Vision, capture
`PlaybackLiveWiringOutcome`/`PlaybackOperationalMetrics`) plutôt que la partager serait la manière la
plus probable de rouvrir silencieusement l'invariant de parité — deux implémentations qui doivent
rester identiques divergent tôt ou tard. Effet de bord utile : les métriques opérationnelles PR115d
(`PlaybackOperationalMetrics.RecordServed`/`RecordFallback`) et les diagnostics live-wiring
(`IPlaybackLiveWiringDiagnosticsStore`) captent alors **aussi** les résolutions déclenchées par des
appels `GET .../Stream` v2 natifs — cohérent avec l'esprit de PR115d (mesurer tout le trafic réel qui
traverse cette décision), à condition de le nommer explicitement dans les tests de non-régression de
PR115d (le volume mesuré par le runbook `docs/pr115d-rollout-runbook.md` va légèrement augmenter une
fois PR116d déployée — pas un changement de sémantique, juste de volume).

---

## 4. Sécurité

### 4.1 Aucune nouvelle surface d'authentification

`GET .../Stream` reste `[Authorize]`, même scope que `POST`/`PUT`/`DELETE` du même contrôleur.
`accessToken` = `claimsPrincipal.GetToken()`, exactement la valeur que `MediaInfoHelper.cs:372,385`
utilise déjà pour peupler `&ApiKey=` dans `TranscodingUrl` — c'est le jeton de la session
d'authentification **courante** de l'appelant, jamais un jeton nouvellement émis. Le paramètre
`ApiKey=` en query string sur les endpoints de streaming (`DynamicHlsController`,
`[Authorize]`, `Reefin.Api/Controllers/DynamicHlsController.cs:39-40`) est un mécanisme déjà en place
côté `CustomAuthenticationHandler` (`Reefin.Api/Auth/CustomAuthenticationHandler.cs`) — ce document ne
crée aucun nouveau mode d'authentification, il réutilise le seul qui existe déjà pour ce type
d'endpoint.

### 4.2 Contrôle de propriété — exigence nouvelle sur ce endpoint précis

Constat vérifié en lisant `PlaybackSessionsController` en entier (§1.1) : `ReplacePlaybackSession` et
`DeletePlaybackSession` ne comparent **jamais** aujourd'hui l'utilisateur appelant à
`session.Request?.Options.UserId` — n'importe quel utilisateur authentifié non-admin peut déjà, en
devinant/observant un GUID de session, `PUT`/`DELETE` la session d'un autre utilisateur. C'est un gap
pré-existant, de sévérité limitée (au pire un déni de service sur une session, pas une fuite de
donnée). **Émettre une URL porteuse de jeton (§1.4) sur cette même absence de contrôle changerait la
sévérité de classe** : un utilisateur authentifié quelconque pourrait alors dérober le jeton
d'authentification et le flux d'un autre utilisateur simplement en énumérant des identifiants de
session. `GET .../Stream` doit donc, **en plus** de l'authentification existante, vérifier que
l'appelant est soit `session.Request?.Options.UserId` (comparaison directe, le champ existe déjà sur
`MediaOptions`), soit administrateur (même règle que `RequestHelpers.GetUserId`,
`Reefin.Api/Helpers/RequestHelpers.cs:67-83`, déjà utilisée à la création) — `403 Forbidden` sinon.
**Hors périmètre de ce document, mais nommé pour mémoire** : étendre la même vérification à `PUT`/
`DELETE` serait un durcissement raisonnable, indépendant de PR117 — à traiter dans son propre correctif
si jugé prioritaire, ne pas le laisser se dissoudre dans cette tranche.

### 4.3 `PlaybackSessionResponse`/`PlaybackDiagnosticDetail`/`PlaybackSessionListItem` restent inchangés

Aucun champ ajouté à `PlaybackSessionResponse` (§2.1(a) rejetée) : `PlaybackSessionResponseMapperTests`
(`tests/Reefin.Api.Tests/Models/PlaybackSessionDtos/PlaybackSessionResponseMapperTests.cs`) et
`PlaybackDiagnosticDetailMapperTests`
(`tests/Reefin.Api.Tests/Models/PlaybackSessionDtos/PlaybackDiagnosticDetailMapperTests.cs`) n'ont
besoin d'aucune extension pour rester probants sur ce point — c'est un avantage direct de l'option (b)
retenue, pas seulement un choix défensif. Exigence de test **nouvelle**, néanmoins : un test
d'intégration explicite affirmant que la réponse JSON de `System/PlaybackDiagnostics/Sessions` et de
`System/PlaybackDiagnostics/Sessions/{id}` ne contient jamais les chaînes `"Url"`/`"SubtitleUrl"` au
niveau attendu par `PlaybackSessionStreamDescriptor` — un garde-fou structurel, pas seulement une
confiance dans la séparation de types, pour la même raison que §1.4 documente : une fuite de ce type
serait silencieuse sans un test dédié à la détecter.

### 4.4 Aucune régression PII

Le descripteur ne porte aucune donnée nouvelle par rapport à ce que `TranscodingUrl` legacy expose
déjà au navigateur du même utilisateur (ids de session/device/source, jeton de sa propre session,
paramètres de codec) — la classe de donnée est identique, seule la route qui la sert change. Le
sous-titre externe (`SubtitleUrl`) suit la même règle de filtrage que `mediaSource.MediaStreams[].DeliveryUrl`
aujourd'hui (déjà servi au client legitimate propriétaire de la session).

---

## 5. Compat & rollout

- **Additif, sans changement de forme des DTOs existants.** Nouvelle route, nouveau DTO
  (`PlaybackSessionStreamDescriptor`), aucune modification de `PlaybackSessionResponse`,
  `CreatePlaybackSessionRequest`, `ReplacePlaybackSessionRequest`, `PlaybackDiagnosticDetail`,
  `PlaybackSessionListItem`. Les clients legacy (`PlaybackInfo`) ne voient rigoureusement aucun
  changement — `MediaInfoHelper.SetDeviceSpecificData` continue d'appeler la même logique
  (extraite mais comportementalement identique, §3.3), pas une nouvelle.
- **Interaction avec PR116b (appel shadow client).** `docs/pr116-client-migration-design.md` §3
  PR116b décrit un `POST Playback/Sessions` best-effort, fire-and-forget, **jamais suivi d'un appel de
  lecture réel**. Ce document ajoute une règle explicite : PR116b **ne doit jamais** appeler
  `GET .../Stream` sur sa session shadow — le faire déclencherait potentiellement un vrai job de
  transcodage ffmpeg (`ResolveServedStreamInfo` → `PlaybackExecutionPlanAdapter.ToStreamInfo`, puis un
  client qui suivrait cette URL démarrerait effectivement `TranscodingJobStarted`) pour une requête
  purement observationnelle — contredirait directement §4.2 de ce même document
  (« Résiduel, attendu et sans danger » n'y couvre que la planification, jamais l'exécution). Seule
  PR116d (bascule réelle) est autorisée à appeler ce endpoint.
- **Interrupteur d'urgence et garde-fou d'arrêt (PR115d).** §3.1/§3.3 : la réévaluation à chaque appel
  `GET .../Stream` signifie que `PlaybackShadowOptions.Mode`/`PlaybackStopThresholdGuard` s'appliquent
  à ce nouveau point d'entrée avec exactement la même immédiateté qu'au flux legacy — aucune fenêtre
  supplémentaire où un kill switch actionné resterait sans effet pour les nouveaux appels.
- **Flag côté client.** PR116d (`reefin-web`) doit vivre derrière son propre flag `appSettings`
  (distinct du flag shadow PR116b, défaut off) — `docs/pr116-client-migration-design.md` §3 PR116d
  document déjà cette tranche comme bloquée dans l'attente de ce prérequis ; ce document ne prescrit
  pas la bascule du flag lui-même, seulement le contrat qu'elle consommera (§6 la nomme comme tranche
  suivante).
- **Rollback.** Retirer la route `Playback/Sessions/{id}/Stream` (ou la faire répondre `404`) est
  suffisant pour désactiver ce prérequis sans toucher au reste du contrat v2 — aucune migration de
  données, `IV2PlanStore`/`PlaybackSession` ne changent pas de forme.

---

## 6. Tranches d'implémentation

### PR117 — serveur (`reefin`), ce document

1. Extraire `MediaInfoHelper.ResolveServedStreamInfo` (privée) vers un composant partagé et injectable
   (§3.3), sans changement de comportement pour `SetDeviceSpecificData` — critère de sortie : suite de
   tests existante `MediaInfoHelperTests`/tests d'intégration streaming, verte sans modification.
2. Nouveau `GET Playback/Sessions/{id}/Stream` sur `PlaybackSessionsController`, DTO
   `PlaybackSessionStreamDescriptor` (§2.2), garde `PlaySessionId` (§2.3, `409`), contrôle de
   propriété (§4.2, `403`), `SubtitleUrl` conditionnel (§2.2).
3. Trancher et implémenter le traitement de `startTimeTicks` (§2.2, option (i) recommandée).
4. Tests : unitaires sur le nouveau mapper/contrôleur (cas `ServedBy` v2, cas repli avec chacune des
   sept `PlaybackLiveFallbackReason`, cas `409`/`403`) ; test d'intégration de non-fuite (§4.3) ;
   extension de l'oracle de parité existant
   (`tests/Reefin.Playback.Shadow.Tests/ExecutionPlanParityTests.cs`) pour couvrir le nouveau point
   d'appel s'il s'avère que l'extraction §3.3 introduit un chemin de construction de `StreamInfo`
   distinct de celui déjà couvert — à confirmer en implémentation, pas garanti nécessaire si
   l'extraction est une pure relocalisation sans branchement nouveau.
5. **Critère de sortie** : un client v2 (test d'intégration simulé, sans `reefin-web`) peut `POST
   Playback/Sessions` puis `GET .../Stream` et recevoir une URL que `DynamicHlsController`/l'endpoint
   de stream statique sert effectivement, pour au moins un cas `DirectPlay`, un cas `Transcode` HLS, et
   un cas de repli legacy explicite (interrupteur forcé en test).

### PR116d — client (`reefin-web`), suite de `docs/pr116-client-migration-design.md` §3

Débloquée par PR117. Portée reprise du document parent : bascule du démarrage de lecture réel sur la
réponse v2, retrait de `DeviceProfile`/`browserDeviceProfile.js` du chemin de démarrage (pas seulement
de la déclaration). Nouveauté apportée par ce document : le point d'intégration précis est
`POST Playback/Sessions` (déjà PR116b) suivi de `GET Playback/Sessions/{id}/Stream` (nouveau), dont la
réponse (`Url`, `ServedBy`, `FallbackReason`) remplace `mediaSource.TranscodingUrl`/
`createStreamInfo()` (`playbackmanager.js:~3600-3736`) comme source de l'URL de lecture réelle.
Critères de sortie et stratégie de test : à détailler dans une révision dédiée de
`docs/pr116-client-migration-design.md` §3/§4.4 une fois PR117 fusionnée — non refaits ici pour éviter
la dérive déjà nommée dans ce même document (« pas absorbé silencieusement dans une tranche qui
prétendrait le résoudre »).

---

## 7. Hors périmètre

- **URLs de pièces jointes** (`MediaAttachment.DeliveryUrl`, `MediaInfoHelper.cs:410-418`) — non
  incluses dans `PlaybackSessionStreamDescriptor`. Basse priorité (polices de sous-titres, rarement
  bloquant pour démarrer une lecture) ; à ajouter dans une extension ultérieure du même DTO si un
  besoin client se confirme.
- **Durcissement du contrôle de propriété sur `PUT`/`DELETE`** (§4.2) — gap pré-existant nommé, pas
  corrigé ici.
- **Retrait de `ReverseDlnaAdapter`/`ClientCapabilitiesMapper`** — conditions déjà posées et
  inchangées par `docs/pr116-client-migration-design.md` §4.3 ; ce document ne les modifie pas (il
  s'appuie sur `ReverseDlnaAdapter.ToDeviceProfile` tel quel, §1.2).
- **Flux `OpenMediaSource`/live TV** (`MediaInfoHelper.OpenMediaSource`, `:629-681`) — hors périmètre,
  ce document couvre uniquement le chemin `Playback/Sessions` créé par un client v2 pour un item de
  bibliothèque standard.
- **WebSocket / bascule complète de `playbackmanager.js`** — déjà hors périmètre de
  `docs/pr116-client-migration-design.md` §5, inchangé ici.
- **Numérotation finale de la tranche PR116d révisée** — ce document ne renumérote pas
  `docs/pr116-client-migration-design.md` ; PR116d y reste nommée telle quelle, PR117 est un
  prérequis serveur distinct qui la précède.

---

## 8. Critères de sortie de PR117 (ce document)

- [x] Vérification champ par champ que `PlaybackSessionsController` dispose déjà de tout ce dont
      `ResolveServedStreamInfo` a besoin, sauf `startTimeTicks` (§1.2).
- [x] Constat vérifié et décisif : `PlaybackSessionResponse` ne peut pas porter l'URL sans fuite
      automatique vers `System/PlaybackDiagnostics/Sessions` via `PlaybackSessionListItem` (§1.4) —
      élimine l'option la plus simple sur preuve de code, pas sur préférence.
- [x] Contrat retenu (endpoint compagnon, §2), avec forme de DTO concrète et justification de chaque
      champ.
- [x] Cohérence canary explicitée : `GET .../Stream` réévalue à chaque appel (pas de cache depuis le
      `POST`), fenêtre TOCTOU documentée et bornée, invariant de parité exécutable préservé par
      réutilisation verbatim (§3).
- [x] Sécurité : aucune nouvelle surface d'authentification, contrôle de propriété nouveau et requis
      identifié avec sa justification, non-régression du filtrage PR113 vérifiée structurellement
      (§4).
- [x] Compat/rollout additifs, interaction avec PR116b/PR115d explicitée, rollback trivial (§5).
- [x] Tranches PR117 (serveur) puis PR116d (client) avec critères de sortie (§6).
- [ ] Implémentation : PR117, hors périmètre de ce document.
