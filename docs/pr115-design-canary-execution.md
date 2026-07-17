# Design — PR115b, le contexte d'exécution du canary

- **PR** : PR115b (design uniquement, aucun code de production)
- **Statut** : proposé
- **Dépend de** : PR114a (`docs/major-rewrite-plan-v13.md` §PR114a — couche d'exécution v2), PR115a (fusionné, PR #23 — autorité/cohorte canary)
- **Précède** : PR115c (branchement live), PR115d (gate opérationnel)

Ce document fige la **forme** du contexte d'exécution qui manque à `PlaybackExecutionPlan` pour que PR115c puisse brancher le moteur v2 sur le vrai chemin de streaming sans re-décider quoi que ce soit. Aucun câblage d'endpoint, aucun changement de comportement — PR115b reste dormant, au même sens que PR114a/PR115a. Ce document fige aussi, comme exigence normative, l'**invariant de parité exécutable** qui conditionne toute bascule vers le chemin live — voir la section dédiée juste après ce rappel.

**Avertissement de méthode** : une première version de ce document classait la quasi-totalité des champs « laissés à leur défaut » par `PlaybackExecutionPlanAdapter` comme hors périmètre, en observant leur absence de la signature de `MediaInfoHelper.SetDeviceSpecificData`. C'était une erreur de couche : ce n'est pas ce site d'appel qui construit le `StreamInfo` legacy (il ne fait que le muter après coup), c'est `StreamBuilder.BuildVideoItem`/`BuildStreamVideoItem`. Une relecture de ces méthodes (§3) montre que legacy peuple activement une partie de ces champs, depuis deux sources différentes de la décision v2 : la source média sélectionnée, et le profil d'appareil. Une seconde correction, après lecture du corps de `StreamInfo.ToUrl` (`Reefin.Model/Dlna/StreamInfo.cs:877-1143`) plutôt que de sa seule contrepartie côté décodage (`StreamingHelpers.ParseParams`), affine encore le périmètre exact de ce que l'URL client sérialise réellement — voir §3.B/§3.C. La classification ci-dessous reflète cette double correction.

---

## Invariant de parité exécutable (normatif)

**MUST — cette section prime sur toute autre partie du document en cas de conflit apparent.** Une relecture de risque a jugé inacceptable la première version de ce document, qui proposait de laisser `RequireAvc`/`RequireNonAnamorphic` non résolus jusqu'à PR115c en s'appuyant sur la seule discipline « ne pas augmenter `CanaryPercentage` » (PR115d) comme garde-fou. Ce n'est pas une protection : rien dans le code n'empêche un run local, un test manuel, un environnement de staging, ou une régression future de PR115d de faire passer une session transcodée par le chemin v2 avant que ce champ soit réellement fermé — une note de documentation ne borne aucun comportement runtime. L'invariant suivant remplace cette approche et est désormais une exigence dure :

> Aucun plan v2 ne peut alimenter le chemin live si tous les champs influençant `StreamInfo.ToUrl()` ne sont pas reproduits avec parité ; sinon, fallback legacy explicite.

Concrètement :

1. **Champ couvert = tout champ qui apparaît dans le corps de `StreamInfo.ToUrl()`** (`Reefin.Model/Dlna/StreamInfo.cs:877-1143`), sans exception de catégorie. §3 identifie neuf champs sérialisés uniquement pour `PlayMethod == Transcode` (`RequireNonAnamorphic`, `TranscodingMaxAudioChannels`, `EnableSubtitlesInManifest`, `EnableMpegtsM2TsMode`, `EstimateContentLength`, `TranscodeSeekInfo`, `CopyTimestamps`, `RequireAvc`, `EnableAudioVbrEncoding`) — `RequireAvc`/`RequireNonAnamorphic` en font partie et ne bénéficient d'aucun régime d'exception.

2. **Option retenue : résoudre l'intégralité des champs influençant `ToUrl()` dans PR115b, avant que PR115c ne branche le chemin live.** `RequireAvc`/`RequireNonAnamorphic` inclus, pas seulement le sous-groupe `TranscodingProfile` de §3.C. §3.C, §4.2, §7 #1 et §8 sont amendés en conséquence pour l'exiger comme critère de sortie de PR115b — voir ces sections.

3. **Option rejetée, conservée pour mémoire** : restreindre le canary live aux méthodes `DirectPlay`/`Remux` (`IsDirectStream`) et forcer un fallback legacy explicite pour toute session qui résoudrait en `Transcode` — ce qui rendrait `RequireAvc`/`RequireNonAnamorphic` sans objet puisque non sérialisés hors transcodage (§3.C). Rejetée parce qu'elle amputerait durablement la couverture du canary sur son cas majeur (le transcodage n'est pas marginal) pour éviter un travail borné et déjà scopé dans PR115b. Reste documentée comme filet de dernier recours si l'extraction du moteur `ApplyConditions` (§7 #1) s'avérait bloquante en pratique — mais n'est pas le plan retenu.

4. **L'oracle de parité (§6) doit comparer la query string complète de `ToUrl()`**, pas un sous-ensemble choisi à la main. Aucun allow-list n'est permis pour une divergence de **valeur** sur une clé présente des deux côtés capable de changer l'exécution côté client (méthode de lecture, codec, conteneur, conditions de compatibilité comme `RequireAvc`/`RequireNonAnamorphic`) — seules restent tolérées les divergences déjà prouvées neutres pour l'exécution : absence de clé des deux côtés dans un cas où §3.C documente que la clé n'est structurellement pas sérialisée (hors `PlayMethod.Transcode`, hors `SubProtocol==hls`), ou l'ordre de sérialisation des paramètres.

---

## 1. Rappel — les 4 tranches et ce qui existe déjà

| Tranche | Rôle | État |
| --- | --- | --- |
| PR115a | Le moteur v2 devient l'**autorité** d'exécution pour un sous-ensemble de sessions live (cohorte déterministe `CanaryCohort`), indépendamment de l'observabilité shadow. `IV2PlanStore`/`V2PlanRecord` retiennent la décision + le plan, keyés par `PlaybackSessionId`. `PlaybackExecutionPlanResolver` lit ce store. | **Fusionné** (PR #23) |
| PR115b | **Ce document.** Définit le contexte d'exécution — les faits que `PlaybackExecutionPlan` ne porte pas et dont le chemin de streaming réel a besoin pour transformer un plan en `StreamInfo` exploitable — et résout l'intégralité des champs influençant `StreamInfo.ToUrl()`, `RequireAvc`/`RequireNonAnamorphic` inclus, conformément à l'invariant de parité exécutable (section dédiée ci-dessus). | Design (ce PR) |
| PR115c | Branche le vrai chemin de streaming : pour une session canary-autoritaire avec un plan exécutable, construit le `StreamInfo` via l'adaptateur + le contexte au lieu du `StreamInfo` legacy ; fallback legacy sinon. | À venir |
| PR115d | Gate opérationnel avant ouverture du cohort en production (`CanaryPercentage` > 0 en environnement réel). | À venir |

Ce qui existe déjà côté exécution (PR114a, `src/Reefin.Playback.Execution`, `src/Reefin.Playback.Dlna`) :

- `PlaybackExecutionPlan` (`src/Reefin.Playback.Execution/PlaybackExecutionPlan.cs`) : record figé, copie verbatim d'une `PlaybackDecision` viable — méthode, source id, conteneur/codecs/bitrates/résolution/plage vidéo cibles, indexes vidéo/audio/sous-titre, méthode de livraison + format sous-titre, transforms. Ne référence que `Reefin.Playback.Decision`.
- `PlaybackExecutionPlanBuilder` (même projet) : projection pure `PlaybackDecision → PlaybackExecutionPlan`, refuse (`TryBuild`/`Build`) plutôt que de deviner.
- `PlaybackExecutionPlanAdapter` (`src/Reefin.Playback.Dlna/PlaybackExecutionPlanAdapter.cs:83-176`) : remplit un `StreamInfo` legacy depuis un plan. Signature actuelle :
  ```csharp
  public static StreamInfo ToStreamInfo(
      PlaybackExecutionPlan plan,
      MediaSourceInfo mediaSource,
      DeviceProfile deviceProfile,
      Guid itemId,
      string? deviceId = null,
      string? deviceProfileId = null,
      string? playSessionId = null)
  ```
  Sa propre doc XML (lignes 54-82) énumère ce qu'elle laisse à sa valeur par défaut faute d'équivalent v2 direct — c'est la liste que ce PR doit trancher champ par champ (§3), et la relecture de `StreamBuilder` montre que « pas d'équivalent v2 » ne veut pas dire « legacy ne le peuple pas non plus ».
- `IPlaybackExecutionPlanResolver`/`PlaybackExecutionPlanResolver` (`Reefin.MediaEncoding/Playback`) : résout le plan d'une session par `PlaybackSessionId` — lookup dans `IV2PlanStore`, ne throw jamais, `null` uniforme que la session soit non-canary, hors-cohorte, ou refusée par le builder. Enregistré en DI, dormant. C'est le point d'entrée que PR115c doit utiliser (§4.4).
- Gate de parité `ExecutionPlanParityTests` (`tests/Reefin.Playback.Shadow.Tests/ExecutionPlanParityTests.cs`) : compare, sur les 9 cas oracle, le `StreamInfo` produit par l'adaptateur au VRAI `StreamInfo` de `StreamBuilder`, sur les champs **exécutables** (`PlayMethod`/`Container`/`SubProtocol`/codecs cibles/bitrate vidéo/résolution/indexes/méthode de livraison sous-titre). Ne compare **pas** les champs de ce document — c'est le trou que §6 comble.

---

## 2. Le vrai chemin de streaming live — tracé depuis le code

Le point d'entrée client-facing qui a le mot « décision » dans son nom (`PlaybackSessionsController`, `POST/PUT /Playback/Sessions`) n'est **pas** celui qui sert les octets aujourd'hui. Il expose le contrat v2 (PR91/PR92) et reflète déjà, depuis PR115a, la décision v2 quand une session est canary-autoritaire — mais c'est une **projection de lecture**, pas la source d'une URL exécutable.

La bascule décision → octets passe par un site d'appel bien plus modeste, appelé depuis trois contrôleurs de production :

- `Reefin.Api/Controllers/MediaInfoController.cs:202`
- `Reefin.Api/Controllers/UniversalAudioController.cs:150`
- `Reefin.Api/Helpers/MediaInfoHelper.cs:436` (`OpenMediaSource`, chemin live stream)

Les trois appellent `MediaInfoHelper.SetDeviceSpecificData` (`Reefin.Api/Helpers/MediaInfoHelper.cs:174-339`). Séquence exacte :

1. **Construit `MediaOptions`** depuis les paramètres de la requête : `DeviceId` (`claimsPrincipal.GetDeviceId()`), `ItemId`, `UserId`, `Profile` (device profile), `Context = EncodingContext.Streaming` (constante à ce site d'appel — aucune des trois routes ne fait varier ce champ), `MaxAudioChannels`, `AllowAudioStreamCopy`/`AllowVideoStreamCopy`, `AlwaysBurnInSubtitleWhenTranscoding`, `MediaSourceId`/`AudioStreamIndex`/`SubtitleStreamIndex` (si la source correspond), `MaxBitrate`.
2. **Planifie** (`MediaInfoHelper.cs:260`) :
   ```csharp
   var session = _playbackSessionManager.Create(new PlaybackSessionRequest(kind, options), playSessionId);
   var streamInfo = session?.Plan.StreamInfo;
   ```
   `session.Plan.StreamInfo` est construit par `PlaybackSessionPlanner.PlanVideo`/`PlanAudio` (`Reefin.MediaEncoding/Playback/PlaybackSessionPlanner.cs`), un simple appel à `StreamBuilder.GetOptimalVideoStream`/`GetOptimalAudioStream` → `BuildVideoItem` (§3). C'est le **même** `PlaybackSessionManager.Create` que PR115a a instrumenté : `_v2PlanStore` publie/attache déjà un `V2PlanRecord` sous `session.Id` (`Reefin.MediaEncoding/Playback/PlaybackSessionManager.cs:79-129`) — `session.Id` est donc connu et disponible à ce point d'appel précis, avant toute construction d'URL. **Aucune corrélation supplémentaire n'est nécessaire ici** : PR115c pourra résoudre le plan v2 immédiatement après cette ligne via `IPlaybackExecutionPlanResolver.Resolve(session.Id)`.
3. **Mute `streamInfo` avec des faits request-scoped que la décision ne porte pas** (`MediaInfoHelper.cs:265-266`) :
   ```csharp
   streamInfo.PlaySessionId = playSessionId;
   streamInfo.StartPositionTicks = startTimeTicks;
   ```
4. **Dérive les `Supports*`** de `mediaSource` depuis `streamInfo.PlayMethod` + permissions utilisateur — logique de politique orthogonale à la décision, lit uniquement `PlayMethod`/`MediaType`/`Container`, tous déjà portés par `PlaybackExecutionPlan`.
5. **Construit l'URL exécutable** (`MediaInfoHelper.cs:303, 316`) :
   ```csharp
   mediaSource.TranscodingUrl = streamInfo.ToUrl(null, claimsPrincipal.GetToken(), extraParams);
   ```
   `ToUrl` (`Reefin.Model/Dlna/StreamInfo.cs:877`) sérialise **tout** l'état du `StreamInfo` en query string — décision, champs request-scoped posés à l'étape 3, et tous les champs peuplés à l'étape 2 par `StreamBuilder` (§3), y compris ceux que `SetDeviceSpecificData` ne touche jamais lui-même.

Le client appelle ensuite cette URL contre `DynamicHlsController` (`Reefin.Api/Controllers/DynamicHlsController.cs`). Vérifié dans `StreamingHelpers.GetStreamingState` (`Reefin.Api/Helpers/StreamingHelpers.cs:45-266`) : ce chemin **ne rappelle jamais `StreamBuilder`**, **ne consulte jamais `IV2PlanStore`**, et **ne re-décide rien** — il rejoue directement les query params déjà présents dans l'URL (`ParseParams` en décode une partie encodée positionnellement, `Reefin.Api/Helpers/StreamingHelpers.cs:397-602`), complétés par `EncodingHelper.AttachMediaSourceInfo` qui lit la source, pas la décision.

**Conséquence pour la conception** : la bascule v2 est un **point de branchement unique et déjà localisé** — `MediaInfoHelper.SetDeviceSpecificData`, ligne 261 — *pour les trois sites d'appel vérifiés ci-dessus*. Ce document ne prétend pas avoir grep le dépôt entier à la recherche d'un autre producteur de `MediaSource.TranscodingUrl`/`StreamInfo.ToUrl` (ex. un chemin `VideosController`/LiveTV distinct) ; si un tel site existe, son mode d'échec est un repli silencieux sur legacy (pas une casse), donc sans risque pour la définition du contexte — mais PR115c doit confirmer l'exhaustivité avant d'annoncer le canary comme pleinement actif. `DynamicHlsController` n'a besoin d'aucune modification. Mais cette conséquence a un corollaire strict que la §1 (v1 de ce document) avait sous-estimé : puisque `ToUrl()` sérialise **tout** l'état du `StreamInfo`, et que `DynamicHlsController` rejoue cet état sans jamais le corriger, **tout champ que `StreamBuilder` peuple aujourd'hui et que l'adaptateur laisse à son défaut produit une URL différente pour un client canary** — un risque fonctionnel réel, pas seulement une lacune de test. §3 identifie précisément ces champs.

---

## 3. Ce qu'il manque à `PlaybackExecutionPlan` — trois catégories, pas une

`PlaybackExecutionPlanAdapter.ToStreamInfo` produit déjà tous les champs **exécutables** que le gate de parité PR114a vérifie. Le reste — les champs que sa doc XML liste comme « laissés à leur défaut » — se répartit en **trois** catégories de nature très différente, pas une opposition binaire « contexte vs hors périmètre ».

### 3.A — Faits HTTP-scoped (candidats naturels au contexte)

| Champ `StreamInfo` | Origine legacy réelle | Preuve |
| --- | --- | --- |
| `PlaySessionId` | Paramètre `playSessionId` de la requête | `MediaInfoHelper.cs:265` |
| `StartPositionTicks` | Paramètre `startTimeTicks` de la requête | `MediaInfoHelper.cs:266` |
| `AlwaysBurnInSubtitleWhenTranscoding` | `MediaOptions.AlwaysBurnInSubtitleWhenTranscoding`, posé depuis la requête, copié tel quel dans `StreamInfo` par `BuildVideoItem` (`StreamBuilder.cs:662`), relu ensuite par `SetDeviceSpecificData` pour suffixer l'URL | `StreamBuilder.cs:649-663`, `MediaInfoHelper.cs:306,328` — préférence client forçant le burn-in, distincte de la décision `SubtitleDelivery=Burn` que porte déjà `PlaybackExecutionPlan.SubtitleDelivery` |
| `Context` | `EncodingContext.Streaming`, constante fixée par `SetDeviceSpecificData` (jamais paramétrée à ce site d'appel) | `MediaInfoHelper.cs:198` |
| `ItemId`, `DeviceId`, `DeviceProfileId` | Déjà des paramètres de l'adaptateur, regroupés ici pour cohérence | — |

Ces champs n'existent nulle part dans `PlaybackDecision`/`PlaybackExecutionPlan` et ne devraient jamais y exister : ce sont des faits de la requête HTTP, pas de la décision de lecture.

### 3.B — Faits source-scoped (l'adaptateur doit les lire sur `mediaSource`, pas les recevoir)

| Champ `StreamInfo` | Origine legacy réelle | Preuve | Dans l'URL (`ToUrl`) ? |
| --- | --- | --- | --- |
| `MaxFramerate` | `videoStream?.ReferenceFrameRate` — le flux vidéo **sélectionné sur la source**, posé pour Transcode ET DirectStream (« applies to transcode and direct-stream ») | `StreamBuilder.cs:960` | Oui, si renseigné (`StreamInfo.cs:979-983`) |
| `AudioSampleRate` | `audioStream.SampleRate` — le flux audio **sélectionné sur la source**, quand l'audio n'est pas transcodé | `StreamBuilder.cs:1033` | Oui, si renseigné (`StreamInfo.cs:973-977`) |
| `RunTimeTicks` | `item.RunTimeTicks` où `item` est le `MediaSourceInfo` | `StreamBuilder.cs:658` (`BuildVideoItem`) | **Non** — absent du corps de `ToUrl` (`StreamInfo.cs:877-1143` relu en entier) |

Ni requête, ni profil, ni décision v2 : ce sont des propriétés du `MediaStream` sélectionné sur la source. L'adaptateur reçoit déjà `mediaSource` et connaît déjà, via le plan, quel index vidéo/audio a été sélectionné (`plan.VideoStreamIndex`/`plan.AudioStreamIndex`) — il peut résoudre `MaxFramerate`/`AudioSampleRate` lui-même (`mediaSource.GetMediaStream(...)`) sans qu'ils transitent par le contexte. Ces deux-là sont matériels pour la parité d'URL (§6) ; `RunTimeTicks`, lui, n'apparaît jamais dans `ToUrl` — le laisser à l'adaptateur reste une bonne pratique pour la fidélité de la valeur `StreamInfo` en mémoire (un futur consommateur pourrait le lire), mais ce n'est **pas** requis par l'argument de sécurité de ce document (§2 : « tout champ que l'URL sérialise »), donc pas un blocage de sortie de PR115b si jamais il était oublié.

### 3.C — Faits dérivés du profil d'appareil (résolus intégralement par ce PR, invariant de parité exécutable oblige)

`StreamBuilder` peuple un bloc de champs supplémentaires via `SetStreamInfoOptionsFromTranscodingProfile`/`SetStreamInfoOptionsFromDirectPlayProfile` (`StreamBuilder.cs:595-632`), appelées **inconditionnellement pour Transcode (`StreamBuilder.cs:805`) et pour DirectStream (`StreamBuilder.cs:772`)** — seul le DirectPlay pur les laisse à défaut. Le profil retenu est trouvé en filtrant `options.Profile.TranscodingProfiles` par `(Type == playlistItem.MediaType, Context == options.Context, container compatible)` (`StreamBuilder.cs:165-174`) :

| Champ `StreamInfo` | Source de la valeur |
| --- | --- |
| `CopyTimestamps`, `TranscodingMaxAudioChannels`, `EstimateContentLength`, `EnableSubtitlesInManifest`, `EnableMpegtsM2TsMode`, `EnableAudioVbrEncoding`, `MinSegments`, `SegmentLength`, `TranscodeSeekInfo` | `TranscodingProfile` apparié depuis `deviceProfile.TranscodingProfiles` par (type, contexte, conteneur) |
| `RequireAvc`, `RequireNonAnamorphic` | Évaluation des conditions `IsAvc`/`IsAnamorphic` d'un `CodecProfile` du profil d'appareil, appliquées au codec cible retenu (`StreamBuilder.cs:1838-1880`, dans le moteur générique `ApplyConditions`) |

Ce ne sont **ni** des faits requête (ils ne varient pas avec `playSessionId`/`startTimeTicks`), **ni** des faits source (ils varient avec `deviceProfile`, pas avec `mediaSource`), **ni** des faits que `PlaybackExecutionPlan` pourrait raisonnablement porter (v2 ne modélise pas la notion legacy de « profil de transcodage apparié » ni de conditions `CodecProfile` — les ajouter au domaine serait importer un concept DLNA dans une décision qui s'en affranchit délibérément, RFC PR91 §8). Ce sont des **faits de résolution de profil**, une troisième forme de travail que ni le contexte (3.A) ni la lecture de source (3.B) ne couvrent.

**Portée réelle dans l'URL, confirmée en lisant le corps de `ToUrl` (`Reefin.Model/Dlna/StreamInfo.cs:1045-1094`)** : ce bloc entier de neuf champs (`RequireNonAnamorphic`, `TranscodingMaxAudioChannels`, `EnableSubtitlesInManifest`, `EnableMpegtsM2TsMode`, `EstimateContentLength`, `TranscodeSeekInfo`, `CopyTimestamps`, `RequireAvc`, `EnableAudioVbrEncoding`) n'est sérialisé dans l'URL que quand `!IsDirectStream` — et `StreamInfo.IsDirectStream` (`StreamInfo.cs:282-283`) se réduit, hors DVD/Blu-ray, à `PlayMethod is DirectStream or DirectPlay`. Autrement dit : **ces neuf champs n'affectent l'URL que pour une session dont `plan.Method == PlaybackMethod.Transcode`** — une session canary en DirectPlay ou Remux (DirectStream) n'a rien à en craindre, quelle que soit la résolution retenue pour 3.C. Deux de ces neuf (`RequireAvc`, `EnableAudioVbrEncoding`, lignes 1089-1093) sont en plus sérialisés **sans condition de valeur** (contrairement aux autres, gardés par un `if`) — un `RequireAvc` laissé à `false` alors que legacy aurait résolu `true` produit une divergence d'URL immédiate et silencieuse, pas une simple omission de clé. `SegmentContainer`/`SegmentLength`/`MinSegments` (HLS uniquement, `StreamInfo.cs:997-1016`) suivent une porte différente (`SubProtocol == hls`, indépendante de `IsDirectStream`) et restent donc pertinents même hors transcodage pur si le sous-protocole est HLS.

- Le sous-groupe `TranscodingProfile` (9 champs) a une clé d'appariement simple — `(MediaType, Context, Container)` — entièrement dérivable de `plan.Container`, du type de média déjà inféré par l'adaptateur, et de `Context = Streaming` (3.A). Deux implémentations possibles : (i) extraire `SetStreamInfoOptionsFromTranscodingProfile`/la recherche de profil de `StreamBuilder` en méthode `internal` réutilisable par l'adaptateur (zéro duplication, mais couple l'adaptateur — TEMPORAIRE par nature — à `StreamBuilder` — permanent), ou (ii) réimplémenter la même recherche dans `Reefin.Playback.Dlna` (risque de divergence si la règle legacy change). **Recommandation : (i)**, avec le gate de parité §6 comme filet si l'extraction dérive.
- Le sous-groupe `RequireAvc`/`RequireNonAnamorphic` dépend du moteur de conditions `ApplyConditions`, plus large que la seule recherche de profil (il évalue des `ProfileCondition` arbitraires, dont `IsAvc`/`IsAnamorphic` ne sont que deux cas parmi d'autres déjà utilisés pour choisir le codec cible lui-même). L'invariant de parité exécutable (section dédiée en tête de document) impose que ce PR **tranche** cette partie, pas seulement le sous-groupe `TranscodingProfile` : implémentation retenue = extraire (ou réutiliser directement) `ApplyConditions`/la sous-partie `IsAvc`/`IsAnamorphic` de `StreamBuilder`, appliquée au codec cible que le plan a déjà retenu, avec le gate `ToUrl` (§6) comme preuve de parité — voir §4.2 et §7 #1.

---

## 4. Forme concrète proposée

### 4.1 Type

Nouveau record dans `src/Reefin.Playback.Execution/PlaybackExecutionContext.cs` — uniquement des primitives (§3.A), donc aucune violation de la convention « un projet, un souci » (`Reefin.Playback.Execution` ne référence toujours que `Reefin.Playback.Decision`) :

```csharp
namespace Reefin.Playback.Execution;

/// <summary>
/// Request-scoped facts the execution machinery needs alongside a <see cref="PlaybackExecutionPlan"/>,
/// but that the v2 decision engine never sees and never decides — carried verbatim from the calling
/// request, not derived, not re-decided. Deliberately does NOT carry source-scoped facts (resolved by
/// the adapter directly from the caller-supplied media source, e.g. RunTimeTicks/MaxFramerate/
/// AudioSampleRate) or device-profile-scoped facts (resolved by the adapter from the caller-supplied
/// device profile, e.g. the matched TranscodingProfile's knobs) — see PR115b's design doc §3 for why
/// those two are not context.
/// </summary>
public sealed record PlaybackExecutionContext(
    Guid ItemId,
    string? PlaySessionId,
    string? DeviceId,
    string? DeviceProfileId,
    long StartPositionTicks,
    bool AlwaysBurnInSubtitleWhenTranscoding);
```

`Context = EncodingContext.Streaming` n'est pas un champ du record : `EncodingContext` est un type `Reefin.Model.Dlna`, et l'ajouter romprait la contrainte « aucune référence à `Reefin.Model` » de `Reefin.Playback.Execution`. Comme c'est une constante à chaque site d'appel réel observé (§2), l'adaptateur (qui référence déjà `Reefin.Model.Dlna`) la fixe en dur pour la résolution de profil de 3.C, avec un commentaire renvoyant à ce document si un futur appelant a besoin d'un autre contexte d'encodage.

### 4.2 Adaptateur

`PlaybackExecutionPlanAdapter.ToStreamInfo` (`src/Reefin.Playback.Dlna`) change de signature : les paramètres optionnels épars deviennent un seul `PlaybackExecutionContext`, et le corps gagne trois responsabilités supplémentaires (une par catégorie du §3) :

```csharp
public static StreamInfo ToStreamInfo(
    PlaybackExecutionPlan plan,
    PlaybackExecutionContext context,
    MediaSourceInfo mediaSource,
    DeviceProfile deviceProfile)
```

- **3.A** : affecte `PlaySessionId`/`StartPositionTicks`/`AlwaysBurnInSubtitleWhenTranscoding`/`ItemId`/`DeviceId`/`DeviceProfileId` depuis `context`, comme avant.
- **3.B** : résout `RunTimeTicks`/`MaxFramerate`/`AudioSampleRate` en lisant `mediaSource.GetMediaStream(...)` aux indexes que `plan.VideoStreamIndex`/`plan.AudioStreamIndex` désignent déjà.
- **3.C (sous-groupe `TranscodingProfile`)** : recherche dans `deviceProfile.TranscodingProfiles` par `(plan`-déduit `MediaType, EncodingContext.Streaming, plan.Container)`, réutilisant si possible la logique extraite de `StreamBuilder` (§3.C), pour peupler les neuf champs listés.
- **3.C (`RequireAvc`/`RequireNonAnamorphic`)** : **résolu par ce PR**, comme l'exige l'invariant de parité exécutable — extraction/réutilisation du sous-ensemble `IsAvc`/`IsAnamorphic` du moteur `ApplyConditions` de `StreamBuilder`, appliqué au codec cible déjà retenu par `plan`, gardé par le gate `ToUrl` (§6) sur les 9 cas oracle, sans allow-list de valeur possible sur ces deux champs (§7 #1).

C'est un changement de signature *breaking* mais sans risque de production : l'adaptateur est un type TEMPORAIRE non consommé en dehors des tests (`ExecutionPlanParityTests`, `PlaybackExecutionPlanAdapterTests`) — PR115b met à jour les deux, travail mécanique.

### 4.3 DI

Aucun nouveau service. `PlaybackExecutionContext` est une valeur construite à l'appel, pas une dépendance résolue. Le wiring PR115a existant (`IV2PlanStore`, `IPlaybackExecutionPlanResolver`, `Reefin.Server.Core/ApplicationHost.cs:645-687`) reste inchangé.

### 4.4 Comment une requête se résoudra (PR115c, pour situer où PR115b s'arrête)

Ce PR ne câble rien, mais documente la cible pour que PR115c soit mécanique. Le point important, corrigé depuis une première ébauche de ce document : PR115c doit consommer `IPlaybackExecutionPlanResolver` — le type que PR114a/PR115a ont explicitement construit et câblé pour ce rôle (« the entry point a canary can later switch on ») — plutôt que d'appeler `IV2PlanStore.TryGet` directement, ce qui laisserait le résolveur inutilisé après deux PR consacrées à le préparer :

```
MediaInfoHelper.SetDeviceSpecificData(...)
  session = _playbackSessionManager.Create(request, playSessionId)   // inchangé
  plan = _executionPlanResolver.Resolve(session.Id)                  // null uniforme : hors cohorte, mode
                                                                       // Legacy/Shadow, ou refus du builder
  if plan is not null:
      context  = new PlaybackExecutionContext(item.Id, playSessionId, deviceId, deviceProfileId,
                                               startTimeTicks, alwaysBurnInSubtitleWhenTranscoding)
      streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, profile)
  else:
      streamInfo = session?.Plan.StreamInfo                          // fallback legacy, inchangé
  // tout ce qui suit (Supports*, ToUrl, ...) est déjà aveugle à la provenance de streamInfo
```

---

## 5. Non-objectifs explicites de PR115b

- **Aucun branchement live.** `MediaInfoHelper`, `MediaInfoController`, `UniversalAudioController` ne changent pas de comportement — le `if`/`else` du §4.4 est la cible de PR115c, pas de ce PR.
- **Aucun changement de comportement d'endpoint**, aucun changement de réponse HTTP.
- **Dormant, comme PR115a/PR114a** : le nouveau `PlaybackExecutionContext`, la résolution 3.B/3.C de l'adaptateur (y compris `RequireAvc`/`RequireNonAnamorphic`), ne sont exercés que par les tests.
- **`deviceProfileId` n'est pas retiré du contexte** malgré son statut `[ParameterObsolete]` côté `DynamicHlsController` — le garder préserve la fidélité au site d'appel réel ; sa dépréciation est une décision de nettoyage séparée.
- **Pas de nouveau mécanisme de corrélation `playSessionId → PlaybackSessionId`** : le §2 démontre qu'il n'est pas nécessaire, `session.Id` étant déjà en main au point d'appel.

**Amendement** : une version antérieure de cette liste comptait « `RequireAvc`/`RequireNonAnamorphic` restent non résolus » parmi les non-objectifs de PR115b. L'invariant de parité exécutable (section dédiée en tête de document) interdit ce report : ces deux champs sont désormais dans le périmètre **obligatoire** de PR115b, pas un non-objectif — voir §3.C, §4.2, §7 #1, §8.

---

## 6. Stratégie de test

`ExecutionPlanParityTests` (`tests/Reefin.Playback.Shadow.Tests/ExecutionPlanParityTests.cs`) compare déjà, sur les 9 cas oracle, le `StreamInfo` de l'adaptateur au vrai `StreamInfo` de `StreamBuilder`, mais uniquement sur les champs exécutables — et construit déjà les deux objets `StreamInfo` en mémoire (ligne 50 pour legacy, ligne 62 pour v2). Le test le plus fort n'est pas une énumération de champs supplémentaires à comparer un par un (fragile : un champ oublié dans la liste manuelle repasse inaperçu), mais une comparaison au niveau où §2 a montré que la fidélité compte réellement — **l'URL exécutable elle-même** :

1. **Parité `ToUrl`** : pour chaque cas oracle, stamper les **mêmes** valeurs 3.A (play session id, start position ticks, burn-in) sur `legacyStreamInfo` et sur le `context` passé à l'adaptateur avant d'appeler `ToUrl` des deux côtés — sans ça, `PlaySessionId`/`StartTimeTicks` divergent trivialement (le fixture oracle ne les pose jamais sur `legacyStreamInfo`, `SetDeviceSpecificData` les pose *après* `BuildVideoItem`, pas `StreamBuilder` lui-même) et noient le vrai signal. Puis appeler `legacyStreamInfo.ToUrl(baseUrl, token, extra)` et `adapterStreamInfo.ToUrl(baseUrl, token, extra)` avec les mêmes `baseUrl`/`token`/`extra`, parser les deux query strings **complètes** en dictionnaires, et comparer clé par clé — avec le même allow-listing que le gate existant applique déjà (bitrate vidéo ignoré quand la vidéo est copiée, cas Dolby Vision/HEVC Firefox déjà documentés), plus une note explicite que `StartTimeTicks` n'apparaît dans l'URL que hors HLS (`StreamInfo.cs:1017-1024`) et que le bloc `RequireNonAnamorphic`/…/`EnableAudioVbrEncoding` n'apparaît que pour `plan.Method == Transcode` (§3.C) — deux cas où « clé absente des deux côtés » est le résultat attendu, pas un signal. Toute clé présente d'un côté et absente (ou différente) de l'autre reste un vrai signal : soit un champ 3.B/3.C mal résolu, soit un champ 3.A mal câblé dans le test, soit une divergence légitime à documenter explicitement — jamais un oubli silencieux. Conformément à l'invariant de parité exécutable (section dédiée en tête de document), cet allow-listing est strictement limité aux divergences déjà prouvées neutres pour l'exécution (clé absente des deux côtés, dans les cas structurels ci-dessus) ; aucune divergence de **valeur** sur une clé présente des deux côtés n'est allow-listable — `RequireAvc`/`RequireNonAnamorphic` inclus, sans exception ni régime particulier : une valeur différente y est toujours un échec du gate, jamais un allow-list accepté.
2. **Non-régression de l'existant** : `PlaybackExecutionPlanAdapterTests` (`tests/Reefin.Playback.Dlna.Tests/PlaybackExecutionPlanAdapterTests.cs`) mis à jour pour la nouvelle signature.
3. **Ce que ce PR ne peut PAS prouver** : que le `if`/`else` de PR115c bascule correctement en conditions réelles — ça n'existe pas encore. Le critère de sortie de PR115b est « pour les 9 cas oracle, l'URL produite par le contexte + l'adaptateur est identique à celle que `SetDeviceSpecificData` aurait produite — `RequireAvc`/`RequireNonAnamorphic` inclus, sans aucune divergence de valeur allow-listée, seules les divergences structurellement neutres du point 1 restant tolérées » — une preuve statique, pas une preuve d'intégration bout-en-bout (celle-ci est le critère de sortie de PR115c).

---

## 7. Risques / questions ouvertes

| # | Question | Recommandation |
| --- | --- | --- |
| 1 | **Amendé suite à l'invariant de parité exécutable** (section dédiée en tête de document). `RequireAvc`/`RequireNonAnamorphic` (§3.C) dépendent du moteur `ApplyConditions` de `StreamBuilder`, plus complexe qu'une simple recherche de profil. `ToUrl` ne les sérialise que pour `plan.Method == Transcode` (§3.C), donc seules les sessions canary transcodées sont concernées — un DirectPlay/Remux canary n'y est pas sensible ; mais pour une session transcodée, `RequireAvc` en particulier est sérialisé **sans condition de valeur** (`StreamInfo.cs:1089-1090`) : un `false` par défaut côté adaptateur alors que legacy aurait résolu `true` produit une URL `RequireAvc=false` littéralement fausse pour le client, pas une simple absence de clé — et rien dans le code n'empêcherait un tel plan de servir une session avant que PR115d n'ouvre formellement le cohort en production. « Ne pas augmenter `CanaryPercentage` » est une pratique opérationnelle non vérifiable par le code, pas un invariant exécutable : ce n'est **pas** une protection acceptable pour ce risque. Le risque résiduel n'est donc plus « champ non résolu » mais « effort d'extraction du moteur `ApplyConditions` sous-estimé ». | **Résoudre dans PR115b, avant PR115c** (option retenue) : extraire `ApplyConditions` (ou la sous-partie `IsAvc`/`IsAnamorphic`) de `StreamBuilder` pour réutilisation par l'adaptateur, appliquée au codec cible que `plan` a déjà retenu ; le gate `ToUrl` (§6) sur les 9 cas oracle, sans allow-list de valeur sur ces deux champs, est la preuve de sortie exigée par §8. **Option rejetée, conservée pour mémoire** : restreindre le canary live aux méthodes `DirectPlay`/`Remux` et forcer un fallback legacy explicite pour toute session `Transcode` — rejetée pour ne pas amputer durablement la couverture du canary sur son cas majeur ; retenue seulement comme filet de dernier recours si l'extraction s'avère bloquante en pratique. |
| 2 | Le sous-groupe `TranscodingProfile` (§3.C) — extraire `SetStreamInfoOptionsFromTranscodingProfile`/la recherche de profil de `StreamBuilder`, ou réimplémenter dans `Reefin.Playback.Dlna` ? | Extraire (option (i) du §3.C) : zéro risque de divergence, coût = une méthode `internal`/`public` supplémentaire sur un type déjà volumineux. Le gate `ToUrl` (§6) attrape une dérive dans les deux cas, mais l'extraction l'empêche structurellement. |
| 3 | Le changement de signature de `ToStreamInfo` casse `PlaybackExecutionPlanAdapterTests`/`ExecutionPlanParityTests` existants — acceptable ? | Oui : l'adaptateur est un type TEMPORAIRE (PR114a le dit explicitement) sans consommateur de production. Coût purement mécanique. |
| 4 | `deviceProfileId` est `[ParameterObsolete]` sur `DynamicHlsController` — faut-il le sortir du contexte maintenant ? | Non, pas dans ce PR : le garder préserve la fidélité au site d'appel réel. Sa dépréciation est une décision de nettoyage orthogonale. |
| 5 | Les règles `Supports*`/`ForceRemoteSourceTranscoding` (`MediaInfoHelper.cs:268-310`) ne lisent que `PlayMethod`/`Container`/`SubProtocol`/`AlwaysBurnInSubtitleWhenTranscoding` — indifférentes à l'origine (legacy/v2) du `StreamInfo`, à condition que 3.A/3.B/3.C soient correctement résolus. | PR115c doit ajouter un test d'intégration qui exerce ce chemin avec un `StreamInfo` construit par l'adaptateur, pour confirmer qu'aucune branche de permission ne suppose implicitement une provenance `StreamBuilder`. |
| 6 | `IPlaybackSessionManager` n'expose aucun lookup `playSessionId → PlaybackSessionId` — un manque pour PR115c ? | Non : le seul point de branchement (`SetDeviceSpecificData`) a déjà `session.Id` en main au moment utile (§2). Ne devient utile que pour un besoin hors périmètre canary (ex. un endpoint diagnostic qui ne repasse pas par `Create`/`Patch`). |

---

## 8. Critères de sortie de PR115b

- `PlaybackExecutionContext` (`src/Reefin.Playback.Execution`) introduit, sans référence à `Reefin.Model`/`Reefin.Controller`.
- `PlaybackExecutionPlanAdapter.ToStreamInfo` prend le contexte, résout 3.A depuis `context`, 3.B depuis `mediaSource`, et **l'intégralité** de 3.C depuis `deviceProfile` — le sous-groupe `TranscodingProfile` **et** `RequireAvc`/`RequireNonAnamorphic` (§7 #1) : aucun champ influençant `StreamInfo.ToUrl()` ne reste hors périmètre, conformément à l'invariant de parité exécutable (section dédiée en tête de document).
- Gate `ToUrl` (§6) ajouté à `ExecutionPlanParityTests` (ou test miroir dédié), vert sur les 9 cas oracle, comparant la query string **complète**, sans allow-list de valeur sur `RequireAvc`/`RequireNonAnamorphic` ni sur aucun champ capable de changer l'exécution — seul l'allow-listing structurel déjà documenté (§6 point 1) reste toléré.
- `PlaybackExecutionPlanAdapterTests` mis à jour pour la nouvelle signature, aucune assertion existante affaiblie.
- Zéro changement dans `MediaInfoHelper`, `MediaInfoController`, `UniversalAudioController`, `DynamicHlsController`, `ApplicationHost.cs` — dormant, comme annoncé.
- **Invariant de parité exécutable respecté** : à l'issue de PR115b, aucun champ influençant `StreamInfo.ToUrl()` n'est laissé non résolu. Si ce critère ne pouvait pas être atteint pour un champ donné, la sortie de PR115b devrait documenter explicitement quel fallback legacy explicite protège ce champ avant toute ouverture de PR115c/PR115d — ce document ne prévoit aujourd'hui aucun champ dans ce cas.
- `dotnet build Reefin.sln` / `dotnet test Reefin.sln` verts, sans nouvelle régression au-delà des 2 échecs environnementaux `ParseNetworkTests` déjà documentés.
