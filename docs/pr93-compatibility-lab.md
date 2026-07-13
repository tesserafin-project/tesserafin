# Design + squelette — Laboratoire de compatibilité playback

- **PR** : PR93 (design + squelette de données ; aucun code de production, aucun moteur)
- **Statut** : proposé
- **Dépend de** : PR91 (domaine), PR92 (contrats)
- **Précède** : PR94 (domaine implémenté), PR96–PR97 (moteur), PR98 (shadow mode)

Le labo fournit un **format de fixture versionné** et un jeu de cas obligatoires. Il n'exécute encore rien : ni le domaine (PR94) ni les moteurs (legacy projeté / v2, PR96–PR98) n'existent. PR93 pose les **données et la méthode de comparaison** ; le runner C# arrive avec le moteur.

Emplacement : `tests/PlaybackCompat/`
- `schema/fixture.schema.json` — JSON Schema du format v1.
- `fixtures/*.json` — cas, un fichier par cas.
- `fixtures/MANIFEST.md` — liste des catégories obligatoires et leur statut.
- `README.md` — comment ajouter/exécuter un cas (le runner viendra plus tard).

---

## 1. Objectif

Comparer, pour une même entrée, la décision du **moteur legacy** (projeté en `PlayMethod` + `TranscodeReason` + streams) et celle du **moteur v2** (`PlaybackDecision`, PR91), **par catégories** — pas par égalité brute de `StreamInfo`. L'égalité brute de `StreamInfo` échouerait sur des différences non pertinentes (ordre de champs, valeurs internes) et masquerait les vraies divergences.

---

## 2. Format de fixture (v2, PR102)

Champ `fixtureVersion: 2`. Structure alignée sur les objets PR91, mise à jour par PR102 pour
séparer capacités de décodage et cibles d'encodage sur `capabilities` (bump depuis le v1 de PR93 :
`capabilities` était un objet plat `ClientCapabilities`, remplacé par `decode` +
`outputProfiles`).

```jsonc
{
  "fixtureVersion": 2,
  "id": "video-h264-aac-mp4-directplay",   // stable, = nom de fichier
  "category": "direct-play",                // voir MANIFEST
  "engineVersion": 3,                        // version de moteur v2 attendue
  "description": "H.264/AAC en MP4, client compatible → direct play",

  "input": {
    "context":      { /* PlaybackRequestContext (sans PII) */ },
    "capabilities": {
      "decode":         { /* DecodeCapabilities: ce que le client sait lire (PR102) */ },
      "outputProfiles": [ /* PlaybackOutputProfile[], ordre = préférence client (PR102) */ ]
    },
    "source":       { /* MediaSourceSnapshot (PR91 §3.3) */ },
    "constraints":  { /* PlaybackConstraints (PR91 §3.4) */ }
  },

  "expected": {
    "method":         "DirectPlay | Remux | Transcode",
    "selectedStreams": { "video": 0, "audio": 1, "subtitle": null },
    "output":         { "container": "mp4", "videoCodec": "h264", "audioCodec": "aac" },
    "transforms":     [],                     // vocabulaire fermé PR91 §3.6
    "reasonCodes":    ["MethodChosen"],       // ReasonCode PR91 §5.1
    "isViable":       true
  }
}
```

Le champ `expected` décrit la **décision v2**. La projection legacy est calculée à l'exécution (PR98) et comparée par catégories (§4).

`outputProfiles` vide signifie que le client ne déclare aucune cible de transcodage ; le moteur
retombe alors sur un défaut legacy nommé (h264/aac, conteneur choisi parmi `decode.containers`),
tracé par le `ReasonCode.OutputProfileFallbackUsed` dédié.

---

## 3. Cas obligatoires (voir `fixtures/MANIFEST.md`)

1. `direct-play` — H.264/AAC/MP4 direct play.
2. `remux` — MKV → MP4 (streams copiés).
3. `audio-transcode` — audio DTS → AAC.
4. `video-codec-incompatible` — codec vidéo non supporté.
5. `bitrate-resolution-limit` — limite de bitrate ou résolution.
6. `hdr-tonemap` — HDR avec tonemapping.
7. `subtitle-burn-in` — PGS/ASS nécessitant burn-in.
8. `subtitle-external` — SRT externe supporté.
9. `downmix` — downmix multicanal.
10. `live-tv` — Live TV.
11. `alternate-versions` — versions alternatives.
12. `no-viable-plan` — aucun plan viable (`isViable: false`).

PR93 fournit des fixtures **exemplaires** pour un sous-ensemble représentatif des mécaniques distinctes (direct play, remux, transcode audio, downmix, aucun plan). Les catégories restantes sont listées `à compléter` dans le manifeste et seront ajoutées avec le runner (PR96+).

---

## 4. Comparaison par catégories (pas d'égalité brute de `StreamInfo`)

Le runner (PR98) réduit chaque décision à un **vecteur de catégories** et compare ces vecteurs :

| Axe | Legacy (projeté) | v2 | Règle |
| --- | --- | --- | --- |
| Méthode | `PlayMethod` | `PlaybackDecision.Method` | égalité (DirectPlay/Remux(=DirectStream)/Transcode) |
| Streams sélectionnés | indices | indices | égalité des indices vidéo/audio/sous-titre |
| Classe de transformation | dérivée de `TranscodeReason` + méthode | `Transforms[]` normalisé | égalité d'ensembles (remux, transcode-vidéo, transcode-audio, downmix, tonemap, burn-in) |
| Catégories de raison | bits `TranscodeReason` groupés | `ReasonCode` groupés | égalité d'ensembles de catégories (§4.1) |
| Sortie | conteneur/codecs | conteneur/codecs | égalité conteneur + codecs de sortie |

### 4.1 Groupes de raisons

Les ~30 bits `TranscodeReason` et les `ReasonCode` v2 sont repliés en catégories comparables :

- `container` ← ContainerNotSupported, ContainerBitrateExceedsLimit
- `video-codec` ← VideoCodecNotSupported, VideoProfileNotSupported, VideoLevelNotSupported, VideoCodecTagNotSupported
- `video-range` ← VideoRangeTypeNotSupported (HDR)
- `video-dims` ← VideoResolutionNotSupported, VideoBitDepthNotSupported, VideoFramerateNotSupported, AnamorphicVideoNotSupported, InterlacedVideoNotSupported
- `audio-codec` ← AudioCodecNotSupported, AudioProfileNotSupported
- `audio-channels` ← AudioChannelsNotSupported
- `audio-rate` ← AudioSampleRateNotSupported, AudioBitDepthNotSupported
- `bitrate` ← VideoBitrateNotSupported, AudioBitrateNotSupported, ContainerBitrateExceedsLimit
- `subtitle` ← SubtitleCodecNotSupported
- `stream-count` ← SecondaryAudioNotSupported, StreamCountExceedsLimit
- `error` ← UnknownVideoStreamInfo, UnknownAudioStreamInfo, DirectPlayError

### 4.2 Classes de divergence (PR98)

Chaque écart de vecteur est étiqueté :

- `equivalent` — vecteurs identiques.
- `expected-improvement` — v2 fait mieux (ex. direct play là où legacy transcodait sans raison).
- `known-v2-limitation` — v2 moins capable, documenté.
- `potential-regression` — v2 pire, non expliqué → à investiguer.
- `unexplained` — différence sans classification → bloque la promotion (PR108).

---

## 5. Critères de sortie de la PR93

- [x] Format de fixture versionné (`schema/fixture.schema.json`).
- [x] Fixtures exemplaires pour les mécaniques distinctes.
- [x] Manifeste des 12 catégories obligatoires avec statut.
- [x] Méthode de comparaison par catégories définie (mapping raisons inclus).
- [ ] Runner C# legacy-vs-v2 : PR98 (nécessite le moteur PR96–PR97).
