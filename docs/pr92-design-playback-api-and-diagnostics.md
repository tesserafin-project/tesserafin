# Design — API Playback v2 et UX de diagnostic

- **PR** : PR92 (design uniquement, aucun code de production)
- **Statut** : proposé
- **Dépend de** : PR91 (`docs/pr91-rfc-playback-decision-v2.md`)
- **Précède** : PR112 (implémentation DTO + séparation endpoints), PR113 (diagnostic backend), PR114 (UI, dépôt web)
- **Numéros re-basés le 2026-07-14 (PR105b)** : la tranche PR105–PR111 est occupée par le démêlage DI (`docs/rfc-di-query-user-views-v2.md`) ; les anciens numéros PR105/106/107/108 de ce document correspondent désormais à PR112/113/114/115

Ce document fige les **contrats réseau** et le **flux de diagnostic**. Les wireframes sont basse-fidélité et volontairement non stylés : le haute-fidélité attend la stabilisation des DTO. L'implémentation UI appartient au dépôt web, pas à ce serveur.

---

## 1. Problème actuel (`PlaybackSessionsController`)

Une seule route `System/PlaybackSessions` mélange deux publics et fuit les types internes :

| Symptôme | Preuve (code actuel) |
| --- | --- |
| GET admin et POST/PATCH/DELETE client partagent contrôleur + route | `[Route("System/PlaybackSessions")]`, GET protégé `RequiresElevation` (L61-64) au milieu des verbes client |
| Le PATCH n'est pas un patch | `PatchPlaybackSession` reçoit un `CreatePlaybackSessionRequest` **complet** et reconstruit toutes les options (L105-113, `ResolveOptions`) — c'est un remplacement |
| Types internes/DLNA dans le contrat | réponse = `PlaybackSession` interne (L64, L89, L112) ; requête = `DeviceProfile` brut (L35) |

---

## 2. Séparer les surfaces

Deux contrôleurs, deux publics, deux politiques d'autorisation.

```text
/Playback/Sessions                          (client, [Authorize])
  POST   /Playback/Sessions                 créer une session, renvoie la décision
  PUT    /Playback/Sessions/{id}            remplacer entièrement le plan d'une session
  DELETE /Playback/Sessions/{id}            terminer une session

/System/PlaybackDiagnostics/Sessions        (admin, [Authorize(RequiresElevation)])
  GET    /System/PlaybackDiagnostics/Sessions        liste (résumés)
  GET    /System/PlaybackDiagnostics/Sessions/{id}   détail diagnostic complet
```

Le GET global quitte le contrôleur client : il n'a jamais eu la même autorisation ni le même public. Le nom `PlaybackDiagnostics` acte que c'est un outil d'observation administrateur, pas l'API de lecture.

---

## 3. Corriger les verbes

`PATCH` recevant une requête de création complète disparaît. Décision v1 : **`PUT` = remplacement complet**.

- `POST /Playback/Sessions` : crée. Idempotence conservée par `PlaySessionId` (au plus une session par id — comportement actuel, L18-20 du DTO).
- `PUT /Playback/Sessions/{id}` : re-planifie intégralement avec un corps complet. Sémantique honnête : « voici l'état complet voulu ».
- `PATCH` : **réservé** pour un vrai DTO partiel (ex. changer seulement `AudioStreamIndex`), introduit seulement quand un besoin réel existe. Pas de PATCH tant qu'il n'y a pas de DTO partiel dédié.

---

## 4. DTO stables

**Aucun** de ces types n'apparaît dans le contrat public :

- `DeviceProfile` (`Reefin.Model.Dlna`)
- `MediaOptions` (`Reefin.Model.Dlna`)
- `StreamInfo` (`Reefin.Model.Dlna`)
- `PlaybackSession` interne (`Reefin.Controller.MediaEncoding`)

### 4.1 Requête client

`CreatePlaybackSessionRequest.DeviceProfile` est remplacé par le `ClientCapabilities` de PR91 (snapshot inline). Les booléens d'autorisation deviennent `PlaybackConstraints`.

```text
PlaybackSessionCreateRequest        (POST)
  ItemId
  MediaSourceId?
  Capabilities        : ClientCapabilities        (PR91, remplace DeviceProfile)
  Constraints         : PlaybackConstraints        (PR91, remplace les booléens epars)
  PlaySessionId?

PlaybackSessionReplaceRequest       (PUT)  — même forme, sans PlaySessionId (l'id de route fait foi)
```

### 4.2 Réponse client — décision versionnée

La réponse client porte une **représentation versionnée de la décision**, pas l'enregistrement interne.

```text
PlaybackSessionResponse
  Id
  Kind                Audio | Video
  DecisionVersion     entier (EngineVersion, PR91)
  Method              DirectPlay | Remux | Transcode
  Output              { Container, VideoCodec?, AudioCodec?, Resolution?, VideoRange?, AudioChannels?, Bitrate? }
  SelectedStreams     { Video?, Audio?, Subtitle? { index, delivery } }
  Transforms[]        vocabulaire fermé PR91 (RemuxContainer, TranscodeAudio, ...)
  Reasons[]           résumé plat des ReasonCode (sans détails techniques sensibles)
  CreatedAt / UpdatedAt
```

Pas de `StreamInfo`, pas d'URL ffmpeg, pas de chemin. Le client obtient *ce qu'il aura* (méthode, sortie, streams), pas les rouages internes.

### 4.3 Réponse diagnostic administrateur — plus riche, filtrée

Le détail admin peut porter la trace complète mais **filtre** chemins locaux, secrets, tokens, arguments ffmpeg bruts.

```text
PlaybackDiagnosticDetail
  ...tout PlaybackSessionResponse, plus :
  RequestContext      : PlaybackRequestContext (PR91) — sans PII au-delà de l'UserId
  Capabilities        : ClientCapabilities annoncées
  SourceSnapshot      : MediaSourceSnapshot (codecs/streams, PAS les chemins fichiers)
  Reasoning           : arbre ReasonNode complet (PR91 §5)
  Comparison?         : { LegacyMethod, LegacyReasons[], DivergenceClass }   (shadow mode, PR98)
  Timeline[]          : { Stage, At }  — Créée → planifiée → ffmpeg lancé → lecture
```

Règle de filtrage (à tester en PR113) : jamais de `Path`/`TranscodingUrl`/token de session/clé API dans une réponse diagnostic. Le snapshot source expose les *caractéristiques* des streams, pas leur localisation.

---

## 5. Wireframe administrateur (basse fidélité)

```text
Sessions actives
────────────────────────────────────────────────────────────
Utilisateur | Appareil | Média | Méthode   | État  | Âge
Alex        | Web TV    | Dune  | Transcode | Actif | 03:12

Détail
────────────────────────────────────────────────────────────
Décision       Transcode vidéo + audio
Source         4K HDR / HEVC / TrueHD
Sortie         1080p SDR / H.264 / AAC
Raisons        Codec vidéo, HDR, codec audio
Streams        Vidéo #0 · Audio #2 · Sous-titre #4
Timeline       Créée → planifiée → ffmpeg lancé → lecture
Comparaison    Legacy / moteur v2
Actions        Copier le diagnostic · Exporter le cas de test
```

- **Copier le diagnostic** : JSON filtré (§4.3), pour ticket/support.
- **Exporter le cas de test** : produit une fixture au format PR93 (source + capacités + contraintes + décision attendue), alimentant directement le labo de compatibilité.

---

## 6. Flux

```text
Client                    Serveur                         Admin
  │ POST /Playback/Sessions                                 │
  │  (Capabilities + Constraints inline)                    │
  │────────────▶ adaptateur? non : capacités déjà v2        │
  │              moteur v2 (shadow) + legacy (vérité)        │
  │◀──────────── PlaybackSessionResponse (décision v2)       │
  │                                                          │
  │              [session trackée]                           │
  │                            GET /System/PlaybackDiagnostics/Sessions
  │                            ◀──────────────────────────────│ liste
  │                            GET .../Sessions/{id}          │
  │                            ◀──────────────────────────────│ détail + comparaison legacy/v2
```

Tant que le shadow mode (PR98) tourne, la **vérité reste le moteur legacy** ; la réponse client peut continuer à refléter le legacy pendant que le diagnostic expose la divergence v2. La bascule vers v2 comme vérité est PR115 (feature flag/canary), gated par le labo étendu (PR104+) et la clôture DI (PR111).

---

## 7. Décisions

1. Deux contrôleurs séparés (client vs `PlaybackDiagnostics` admin). **Retenu.**
2. `PUT` = remplacement complet ; `PATCH` réservé à un futur DTO partiel. **Retenu.**
3. Contrat public sans `DeviceProfile`/`MediaOptions`/`StreamInfo`/`PlaybackSession` interne ; réponse = décision versionnée. **Retenu.**
4. Diagnostic admin plus riche mais filtré (jamais chemins/secrets/tokens/args ffmpeg). **Retenu.**
5. Haute-fidélité UI + implémentation : reportées (UI = dépôt web, PR114).

---

## 8. Critères de sortie de la PR92

- [x] Surfaces client/admin séparées et routées.
- [x] Verbes corrigés (PUT remplace, PATCH réservé).
- [x] DTO stables définis, sans type DLNA ni interne.
- [x] Règle de filtrage diagnostic posée.
- [x] Wireframes basse-fidélité + flux.
- [ ] Implémentation DTO/endpoints : PR112 ; diagnostic backend : PR113 ; UI : PR114.
