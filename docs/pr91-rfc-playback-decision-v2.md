# RFC — Playback Decision v2 (domaine de décision non-DLNA)

- **PR** : PR91 (design uniquement, aucun code de production)
- **Statut** : proposé
- **Dépend de** : point 1-2 de `docs/major-rewrite-plan-v13.md` (« Protocole DLNA comme cœur de décision »)
- **Précède** : PR92 (design API/UX), PR93 (labo de compatibilité), PR94 (implémentation domaine), PR95 (adaptateur legacy), PR96–PR97 (moteur), PR98 (shadow mode)

Ce document ne définit que des **contrats**. Il n'introduit aucun type dans le code de production ; les esquisses C# sont normatives sur la *forme* attendue, pas encore compilables.

---

## 1. Problème

Le lifecycle des sessions de lecture est passé en v2 (create/patch/delete, listing diagnostic), mais le **contrat de décision** reste couplé au modèle DLNA. Chaînes de couplage vérifiées dans le code :

| Frontière | Type exposé | Espace de nom | Emplacement |
| --- | --- | --- | --- |
| Entrée planner | `MediaOptions` | `Reefin.Model.Dlna` | `IPlaybackSessionPlanner.PlanAudio/PlanVideo` |
| Requête publique | `DeviceProfile` | `Reefin.Model.Dlna` | `CreatePlaybackSessionRequest.DeviceProfile` (L35) |
| Requête interne | `MediaOptions` | `Reefin.Model.Dlna` | `PlaybackSessionRequest.Options` |
| Plan | `StreamInfo?` | `Reefin.Model.Dlna` | `PlaybackPlan.StreamInfo` (L38) |
| Raisons | `TranscodeReason` | `Reefin.Model.Session` | `PlaybackPlan.TranscodeReasons` |
| Réponse réseau | `PlaybackSession` interne | `Reefin.Controller.MediaEncoding` | renvoyé tel quel par le contrôleur (L64, L89, L112) |

Conséquences :

1. **Le format réseau est le modèle interne.** Le contrôleur `PlaybackSessionsController` renvoie directement l'enregistrement interne `PlaybackSession`, qui contient `PlaybackSessionRequest` (fondé sur `MediaOptions`) et `PlaybackPlan` (pouvant porter `StreamInfo`). Impossible de faire évoluer le moteur sans casser le contrat client.
2. **La décision n'est pas expliquable.** `TranscodeReason` est un `[Flags] enum` de ~30 bits (`ContainerNotSupported`, `VideoCodecNotSupported`, `AudioCodecNotSupported`, … `DirectPlayError`). Il dit *quels* murs ont été touchés, jamais *pourquoi* la méthode retenue en découle, ni quel stream/source a été choisi et écarté.
3. **DLNA est le modèle métier.** `DeviceProfile`/`MediaOptions`/`StreamInfo` imposent la sémantique DLNA (profils d'appareil, conditions, transcoding profiles) à toute décision, y compris pour des clients qui ne parlent pas DLNA.

Objectif de la série : **abandonner DLNA comme modèle métier de la décision**, en gardant DLNA seulement comme *un* adaptateur d'entrée (PR95) tant que les clients existants l'utilisent.

---

## 2. Objectifs / non-objectifs

**Objectifs**

- Définir cinq objets de domaine immuables, **sans aucune dépendance** vers `Reefin.Model.Dlna`.
- Rendre la décision **reproductible** (snapshot d'entrée complet) et **expliquable** (trace structurée).
- Versionner la décision et le moteur.
- Permettre un shadow mode (PR98) : comparer legacy vs v2 par catégories, pas par égalité brute de `StreamInfo`.

**Non-objectifs (cette PR)**

- Implémenter le moteur ou le domaine (PR94, PR96–PR97).
- Concevoir les DTO réseau et les routes (PR92).
- Toucher au contrôleur ou aux clients (PR105+).
- Retirer `MediaOptions`/`DeviceProfile` : ils survivent comme entrée de l'adaptateur (PR95).

---

## 3. Les cinq objets de domaine

Emplacement cible proposé : nouvel assembly/namespace `Reefin.Playback.Decision` (nom à confirmer en PR94), **interdit d'importer `Reefin.Model.Dlna`** (contrainte vérifiée par un test d'architecture en PR94).

Tous immuables (`record`/`readonly`), sérialisables (System.Text.Json), sans accès repository/session/ffmpeg.

### 3.1 `PlaybackRequestContext`

Le *qui/quoi/quand* de la requête, indépendant du transport.

```text
PlaybackRequestContext
  RequestId            identifiant unique de la décision (corrélation logs/diagnostic)
  ItemId               média demandé
  MediaSourceId?       version alternative éventuelle
  UserId               utilisateur (pour politiques/quotas, pas pour la sélection technique)
  MediaKind            Audio | Video
  RequestedAt          horodatage
  EngineVersion        version du moteur ayant produit la décision (voir 3.5)
```

### 3.2 `ClientCapabilities`

Ce que le client **peut lire**, exprimé sans vocabulaire DLNA. Snapshot **inline et immuable** (voir §4).

```text
ClientCapabilities
  Containers[]              conteneurs acceptés (mux)
  VideoCodecs[]             { Codec, Profiles[], MaxLevel?, MaxBitDepth?, VideoRangeTypes[] }
  AudioCodecs[]             { Codec, MaxChannels?, MaxSampleRate?, MaxBitDepth? }
  SubtitleDelivery[]        { Format, Method: Embed | External | Burn | Hls }
  MaxResolution?            largeur × hauteur
  MaxVideoBitrate?
  MaxAudioBitrate?
  SupportsHls / SupportsDash
  SupportedProtocols[]      Http | Hls | ...
```

Note : ceci est la *cible sémantique*, pas un mapping 1-1 de `DeviceProfile`. L'adaptateur PR95 projette `DeviceProfile` → `ClientCapabilities` à sens unique.

### 3.3 `MediaSourceSnapshot`

Un instantané figé de la source, découplé de `MediaSourceInfo`/`StreamInfo`.

```text
MediaSourceSnapshot
  MediaSourceId
  Container
  Protocol
  Bitrate?
  RunTimeTicks?
  VideoStreams[]     { Index, Codec, Profile?, Level?, Width?, Height?, BitDepth?, VideoRange?, Framerate?, Bitrate?, IsAnamorphic?, IsInterlaced? }
  AudioStreams[]     { Index, Codec, Channels?, SampleRate?, BitDepth?, Bitrate?, Language?, IsDefault }
  SubtitleStreams[]  { Index, Format, IsExternal, IsForced, IsDefault, Language? }
  SupportsDirectPlay / SupportsDirectStream / SupportsTranscoding
```

### 3.4 `PlaybackConstraints`

Les **overrides et interdits** de la requête (issus aujourd'hui des booléens de `CreatePlaybackSessionRequest` et des champs de `MediaOptions`).

```text
PlaybackConstraints
  AllowDirectPlay / AllowDirectStream / AllowTranscoding
  AllowVideoStreamCopy / AllowAudioStreamCopy
  MaxBitrate?
  MaxAudioChannels?
  PreferredAudioStreamIndex?
  PreferredSubtitleStreamIndex?
  AlwaysBurnInSubtitleWhenTranscoding
  StartTimeTicks
```

### 3.5 `PlaybackDecision`

La sortie. Contient **au minimum** :

```text
PlaybackDecision
  Method                DirectPlay | Remux (direct stream) | Transcode
  SelectedSource        MediaSourceId retenu
  SelectedVideoStream?  index
  SelectedAudioStream?  index
  SelectedSubtitle?     { index, delivery: Embed | External | Burn | Hls }
  Output                { Container, VideoCodec?, AudioCodec?, Resolution?, VideoRange?, Bitrate?, AudioChannels? }
  Transforms[]          transformations requises (voir 3.6)
  Reasoning             trace structurée (voir §5)
  EngineVersion         version du moteur (aligne RequestContext.EngineVersion)
  IsViable              false ⇒ aucun plan possible ; Reasoning explique l'échec
```

### 3.6 Transformations (`Transforms[]`)

Vocabulaire fermé, orthogonal à la méthode, pour dire *ce que fera le pipeline* :

```text
RemuxContainer         (conteneur source → conteneur sortie, streams copiés)
TranscodeVideo         (codec/résolution/range de sortie)
TranscodeAudio         (codec/canaux de sortie)
CopyVideo / CopyAudio
Downmix                (N → M canaux)
Tonemap                (HDR → SDR)
BurnInSubtitle
ExtractSubtitle        (external/embed → livraison)
```

Un `DirectPlay` a `Transforms = []`. Un remux MKV→MP4 = `[RemuxContainer, CopyVideo, CopyAudio]`. Le cas « conteneur MKV refusé, vidéo copiable, audio DTS non décodable » = `[RemuxContainer, CopyVideo, TranscodeAudio(AAC)]`.

---

## 4. Capacités : inline snapshot vs `CapabilityProfileId`

Deux options :

- **A — snapshot inline immuable** : chaque requête porte l'intégralité de `ClientCapabilities`. La décision est autonome et reproductible : rejouer une décision = fournir le même snapshot.
- **B — `CapabilityProfileId`** : le client enregistre ses capacités une fois, puis référence un id. Déduplication réseau, mais introduit un cycle de vie (invalidation, versionnement du profil) et rend le rejeu dépendant d'un store.

**Décision v1 : option A (snapshot inline immuable).** Justification :

- Reproductibilité triviale pour le labo de compatibilité (PR93) et le shadow mode (PR98) : une fixture porte son snapshot complet.
- Débogage direct : le diagnostic administrateur (PR92/PR106) affiche exactement ce que le client a annoncé, sans jointure sur un store.
- Pas de gestion de cache/invalidation dans le chemin critique.

La déduplication par `CapabilityProfileId` reste possible **plus tard** comme optimisation de transport, sans changer le domaine : l'id se résoudrait en snapshot avant d'entrer dans le moteur.

---

## 5. Modèle de raisons — trace structurée, pas un enum de flags

`TranscodeReason` (`[Flags]`, ~30 bits) est conservé **uniquement** comme code de raison feuille stable et sérialisable (mapping direct pour le legacy/adaptateur). Il ne suffit pas à *expliquer* une décision : il liste des murs sans arbre de causalité.

Le domaine v2 ajoute une **trace arborescente** :

```text
ReasonNode
  Code        code stable sérialisable (ReasonCode, voir 5.1)
  Outcome     Rejected | Accepted | Chosen
  Subject     Container | VideoStream(i) | AudioStream(i) | Subtitle(i) | Source(id) | Method
  Detail?     valeurs observées vs attendues (ex: "DTS" vs [AAC, AC3])
  Children[]  sous-raisons
```

Exemple normatif (le cas cible du plan) :

```text
Direct play refusé                         [Rejected, Method=DirectPlay]
└─ conteneur MKV non accepté               [Rejected, Container, got=mkv want=[mp4,ts]]
   ├─ vidéo H.264 copiable                 [Accepted, VideoStream#0 → CopyVideo]
   └─ audio DTS non décodable              [Rejected, AudioStream#2, got=dts want=[aac,ac3]]
      └─ décision : remux + transcode AAC  [Chosen, Method=Remux, Transforms=[RemuxContainer, CopyVideo, TranscodeAudio(AAC)]]
```

### 5.1 `ReasonCode`

Enum **fermé, versionné, stable en sérialisation** (string ou entier documenté — décision en PR94), aligné 1-1 sur les catégories de `TranscodeReason` plus des codes d'acceptation/choix que l'enum flags n'a pas :

- Contraintes (miroir de `TranscodeReason`) : `ContainerNotSupported`, `VideoCodecNotSupported`, `AudioCodecNotSupported`, `SubtitleCodecNotSupported`, `AudioChannelsNotSupported`, `VideoResolutionNotSupported`, `VideoRangeTypeNotSupported`, `ContainerBitrateExceedsLimit`, … (couverture complète en PR94).
- Positifs (nouveaux) : `StreamCopyable`, `SourceSelected`, `MethodChosen`, `SubtitleBurnInRequired`, `DownmixRequired`, `TonemapRequired`.
- Échec global : `NoViablePlan`.

Contrainte de compatibilité : tout `ReasonCode` de contrainte doit se projeter vers un bit `TranscodeReason` (et réciproquement) pour le shadow mode.

---

## 6. Versionnement du moteur

`EngineVersion` est un entier monotone (ex. `2` pour la v2 initiale). Porté à la fois par `PlaybackRequestContext` (entrée) et `PlaybackDecision` (sortie). Le shadow mode (PR98) étiquette chaque diff avec `(legacyEngine, v2Engine)`. Un changement de règles de décision incrémente `EngineVersion` ; les fixtures du labo (PR93) épinglent la version attendue.

---

## 7. Frontière avec le legacy (aperçu, détaillé en PR95)

Adaptation **à sens unique**, le domaine v2 ne référence jamais DLNA :

```text
DeviceProfile / MediaOptions   (Reefin.Model.Dlna)
        │  adaptateur PR95 (sens unique)
        ▼
ClientCapabilities / PlaybackConstraints / MediaSourceSnapshot   (Reefin.Playback.Decision)
        │  moteur PR96–PR97
        ▼
PlaybackDecision
        │  projection legacy PR98 (pour comparaison shadow)
        ▼
PlayMethod + TranscodeReason + StreamInfo   (pour parité)
```

---

## 8. Questions ouvertes (à trancher en PR94)

1. Sérialisation de `ReasonCode` : string stable vs entier documenté. *Recommandation : string* (diagnostic lisible, robuste au réordonnancement).
2. Nom de l'assembly/namespace du domaine (`Reefin.Playback.Decision` ?).
3. Représentation des codecs : enum fermé vs string normalisé. *Recommandation : string normalisé* (extensible sans recompiler).
4. `MediaSourceSnapshot` doit-il porter les tailles de fichiers/chemins ? *Non* : le domaine reste sans I/O ; les chemins locaux appartiennent au diagnostic administrateur filtré (PR92/PR106).

---

## 9. Critères de sortie de la PR91

- [x] Cinq objets définis au niveau champ, sans dépendance DLNA.
- [x] Modèle de raisons arborescent spécifié, avec l'exemple normatif.
- [x] Décision capacités inline vs `CapabilityProfileId` tranchée (inline).
- [x] Versionnement du moteur défini.
- [x] Frontière legacy esquissée (renvoi PR95).
- [ ] Implémentation : hors périmètre (PR94).
