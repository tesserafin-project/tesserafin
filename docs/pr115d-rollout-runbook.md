# Runbook — PR115d, ouverture progressive du cohort canary v2 en production

- **PR** : PR115d (gate opérationnel)
- **Statut** : implémenté
- **Dépend de** : PR115a (autorité/cohorte, fusionné), PR115b (contexte d'exécution, fusionné), PR115c (branchement live, fusionné)
- **Public visé** : l'opérateur qui augmente `PlaybackShadow.CanaryPercentage` en production

Ce document est le complément opérationnel de `docs/pr115-design-canary-execution.md` (qui fige la conception) : il décrit comment ouvrir le cohort canary par paliers, ce qu'il faut surveiller à chaque palier, comment lire l'endpoint de diagnostics, la procédure de coupe-circuit manuelle, et le comportement du garde-fou automatique introduit par PR115d.

---

## 1. Ce que PR115d ajoute — résumé

Avant PR115d, le seul filet de sécurité contre une régression du chemin live v2 était humain : un opérateur doit remarquer un problème (logs, rapports utilisateurs) et repasser `PlaybackShadow.Mode` à `Legacy`/`Shadow` à la main. PR115d ajoute trois choses, sans changer le comportement du chemin de décision lui-même (§4.4 du document PR115b reste la source de vérité sur l'ordre des vérifications) :

1. **Métriques agrégées** (`PlaybackOperationalMetrics`, `Reefin.MediaEncoding/Playback`) : compteurs cumulatifs servi-par-v2 / servi-par-legacy / repli-par-raison, plus (best-effort) le taux d'échec de démarrage ffmpeg pour les sessions servies par v2.
2. **Garde-fou automatique** (`PlaybackStopThresholdGuard`) : force legacy pour toute requête dès que le taux d'erreur `AdapterError` ou le taux d'échec de démarrage transcodage dépasse un seuil configurable, sans attendre qu'un humain agisse.
3. **Endpoint de diagnostics** : `GET /System/PlaybackDiagnostics/Metrics` (admin, élévation requise) expose ces compteurs et l'état courant du garde-fou.

**Rien de ceci ne remplace le kill switch existant** (`PlaybackShadow.Mode`) — le garde-fou produit le même effet observable (repli legacy pour toute requête) mais se déclenche automatiquement sur signal d'erreur plutôt que sur décision d'opérateur. Les deux mécanismes coexistent ; le kill switch reste vérifié en premier dans `MediaInfoHelper.ResolveServedStreamInfo` (voir §5.2 de ce document pour l'ordre exact).

---

## 2. Rappel — l'exclusion Dolby Vision reste inconditionnelle

**Ce point n'est PAS un réglage de rollout.** PR115c a fermé, en dur et sans exception, la classe de risque identifiée par le constat de sortie de PR115b : une source Dolby Vision/HDR dont le codec figure dans la CSV de codecs candidats legacy (`legacyStreamInfo.VideoCodecs`) est exclue du chemin live v2 avec repli explicite (`PlaybackLiveFallbackReason.DolbyVisionExclusion`), quel que soit `CanaryPercentage`. Aucun palier de ce runbook, aucune valeur de `CanaryPercentage`, ne réactive cette classe de session sur v2 — c'est un comportement figé en code (`MediaInfoHelper.IsDolbyVisionExcluded`), pas une discipline opérationnelle que ce runbook aurait à faire respecter. Un opérateur qui observe un volume anormal de `DolbyVisionExclusion` dans les métriques (§4) n'a rien à faire d'autre que le constater — ce n'est ni une anomalie, ni un signal d'arrêt : c'est le comportement voulu tant que `EncodingHelper.CanStreamCopyVideo` n'a pas été investigué (voir le document PR115b, section « Constat de sortie PR115b » #2, pour le détail du risque sous-jacent).

---

## 3. Paliers de rollout

`PlaybackShadow.CanaryPercentage` (0-100, clampé au `set`) contrôle la taille du cohort. La cohorte est déterministe (hash utilisateur+device, `CanaryCohort`, PR115a) : augmenter le pourcentage n'évince jamais une paire déjà dans le cohort — un rollout progressif est donc cohérent, une paire qui a basculé sur v2 à 5 % y reste à 25 %.

Progression recommandée : **0 % → 1 % → 5 % → 25 % → 100 %**. Chaque palier doit rester actif un temps suffisant pour accumuler un échantillon significatif avant le suivant (au minimum le `MinimumSampleSize` du garde-fou, par défaut 20 tentatives v2 — voir §5.1 ; en pratique, plusieurs heures à un volume de trafic réel plutôt qu'un minimum théorique).

### Palier 0 % — préparation, aucun trafic canary

But : vérifier que l'infrastructure est en place avant d'exposer le moindre utilisateur réel.

- [ ] `PlaybackShadow.Mode = Canary` (ou `V2`), `CanaryPercentage = 0`.
- [ ] `GET /System/PlaybackDiagnostics/Metrics` répond 200, tous les compteurs à 0, `stopThresholdGuardEnabled = true`, `stopThresholdGuardTripped = false`.
- [ ] Confirmer que `PlaybackShadow.StopThresholds.Enabled` n'a pas été désactivé par erreur dans la configuration de l'environnement cible (défaut : activé — voir §5).
- [ ] Un smoke test manuel avec un compte de test explicitement mis dans le cohort (`CanaryPercentage = 100` temporairement sur un environnement de pré-production, ou un calcul manuel de `CanaryCohort.IsInCohort`) confirme un `ServedByV2Count` qui s'incrémente et une lecture HLS fonctionnelle.

### Palier 1 % — premier trafic réel

But : détecter une régression grossière sur un échantillon minimal avant d'exposer davantage d'utilisateurs.

- [ ] `CanaryPercentage = 1`.
- [ ] Surveiller `GET /System/PlaybackDiagnostics/Metrics` à intervalle rapproché (toutes les 15-30 minutes les premières heures).
- [ ] `adapterErrorRate` doit rester proche de 0 % — l'adaptateur est censé réussir sur la quasi-totalité des plans éligibles (invariant de parité exécutable, PR115b). Un taux non nul mais sous le seuil configuré (défaut 10 %, §5.1) mérite investigation avant de passer au palier suivant, même sans déclenchement du garde-fou.
- [ ] `transcodeStartFailureRate` : comparer visuellement au taux d'échec habituel du chemin legacy (hors scope de cet endpoint — à corréler avec les logs ffmpeg existants) plutôt qu'à une valeur absolue, ce signal ayant des causes largement indépendantes du choix v2/legacy (voir la remarque XML de `PlaybackOperationalMetrics` sur la définition exacte d'un « échec de démarrage »).
- [ ] `stopThresholdGuardTripped` doit rester `false`. S'il passe à `true` : voir §6 (procédure de coupe-circuit) — ne PAS augmenter le pourcentage tant que la cause n'est pas comprise, même après un retour à `false`.
- [ ] Vérifier qu'aucune session `AdapterError` inattendue n'apparaît dans `fallbackReasonCounts` pour une classe de contenu qui ne devrait pas y être exposée (croiser avec les logs `MediaInfoHelper` — chaque décision de repli est loguée avec l'id de session).

### Palier 5 %

But : confirmer que les tendances du palier 1 % tiennent sur un échantillon plus large et une diversité de profils client plus grande.

- [ ] `CanaryPercentage = 5`.
- [ ] Même checklist que le palier 1 %, en comparant les taux au palier précédent plutôt qu'en absolu — une dégradation relative (même sous le seuil du garde-fou) est un signal d'alerte pour retarder le palier suivant.
- [ ] Vérifier la distribution des raisons de repli (`fallbackReasonCounts`) : `DolbyVisionExclusion` doit rester cohérent avec la proportion de contenu HDR/Dolby Vision de la bibliothèque (voir §2) ; toute autre raison en croissance disproportionnée (`SourceIdMismatch`, `PlanNotExecutable`) mérite investigation avant d'ouvrir davantage.

### Palier 25 %

But : dernier palier avant l'ouverture complète — le volume doit être suffisant pour exposer des cas limites rares (profils d'appareil peu courants, sources exotiques).

- [ ] `CanaryPercentage = 25`.
- [ ] Même checklist. Porter une attention particulière au `transcodeStartFailureRate` : à ce volume, une régression systémique du plan v2 (par exemple des arguments ffmpeg mal formés pour une classe de profil d'appareil) doit être détectable avant l'ouverture à 100 %.
- [ ] Confirmer qu'aucun ticket/rapport utilisateur ne corrèle avec les fenêtres où `CanaryPercentage` a été relevé.

### Palier 100 %

But : ouverture complète — tout le trafic éligible (hors exclusions dures, §2) passe par v2 quand `Mode = Canary`/`V2`.

- [ ] `CanaryPercentage = 100`.
- [ ] Surveillance identique aux paliers précédents, à fréquence réduite une fois la stabilité confirmée (par exemple quotidienne plutôt que toutes les 30 minutes).
- [ ] Documenter la date d'ouverture complète dans le journal (`docs/major-rewrite-plan-v13.md` §journal).

**Rétrogradation entre paliers** : `CanaryPercentage` peut être baissé à tout moment sans effet de bord — la cohorte étant déterministe et monotone par construction (PR115a), baisser le pourcentage réduit simplement le sous-ensemble éligible ; aucune session déjà en cours n'est interrompue (le repli, comme l'entrée dans le cohort, est évalué par requête, `MediaInfoHelper.ResolveServedStreamInfo` étant appelé à chaque construction d'URL de streaming, pas une fois par session).

---

## 4. Lire l'endpoint de diagnostics

`GET /System/PlaybackDiagnostics/Metrics` (élévation admin requise, `Policies.RequiresElevation`) :

```json
{
  "servedByV2Count": 1423,
  "servedByLegacyCount": 87,
  "fallbackReasonCounts": {
    "NoAuthoritativeRecord": 12,
    "PlanNotExecutable": 3,
    "SourceIdMismatch": 0,
    "DolbyVisionExclusion": 41,
    "KillSwitch": 0,
    "AdapterError": 2,
    "StopThresholdTripped": 29
  },
  "adapterAttempts": 1425,
  "adapterErrorRate": 0.0014,
  "transcodeStartAttemptsV2": 980,
  "transcodeStartFailuresV2": 4,
  "transcodeStartFailureRate": 0.0041,
  "stopThresholdGuardEnabled": true,
  "stopThresholdGuardTripped": false,
  "generatedAt": "2026-07-17T10:32:00Z"
}
```

Points de lecture :

- **`servedByV2Count`/`servedByLegacyCount`** : compteurs cumulatifs depuis le démarrage du processus (pas de fenêtre glissante ni de remise à zéro périodique — même convention que `ShadowMetrics`, `src/Reefin.Playback.Shadow/ShadowMetrics.cs`). Un redémarrage du serveur remet tout à zéro.
- **`fallbackReasonCounts`** : une entrée par valeur de `PlaybackLiveFallbackReason` (`Reefin.MediaEncoding/Playback/PlaybackLiveFallbackReason.cs`), y compris les raisons dont le compteur est 0. `StopThresholdTripped` non nul dans l'historique n'implique PAS que le garde-fou est tripped MAINTENANT — voir `stopThresholdGuardTripped` pour l'état courant.
- **`adapterAttempts`/`adapterErrorRate`** : le dénominateur est *uniquement* les requêtes ayant atteint l'adaptateur (`servedByV2Count + fallbackReasonCounts.AdapterError`) — les autres raisons de repli (cohort, kill switch, exclusion Dolby Vision, etc.) n'ont jamais atteint l'adaptateur et ne diluent donc pas ce taux.
- **`transcodeStartAttemptsV2`/`transcodeStartFailuresV2`/`transcodeStartFailureRate`** : signal *best-effort* — voir la remarque XML de `PlaybackOperationalMetrics` pour la définition précise d'un « échec de démarrage » (un job `TranscodingJobEnded` sans `TranscodingJobStarted` préalable) et ses limites (une session dont le job ne peut pas être corrélé à une session suivie n'est simplement pas comptée, silencieusement).
- **`stopThresholdGuardEnabled`** : reflète `PlaybackShadow.StopThresholds.Enabled` lu en direct sur la configuration serveur au moment de l'appel — pas une valeur mise en cache.
- **`stopThresholdGuardTripped`** : l'état courant du garde-fou, recalculé à chaque appel (aucun état persisté séparément — voir §5.2). `true` signifie que TOUTE requête live est actuellement repliée sur legacy, indépendamment de `CanaryPercentage`.

---

## 5. Seuils d'arrêt automatiques (`PlaybackStopThresholdOptions`)

### 5.1 Configuration et défauts

`PlaybackShadow.StopThresholds` (`Reefin.Model/Configuration/PlaybackStopThresholdOptions.cs`) :

| Champ | Défaut | Rôle |
| --- | --- | --- |
| `Enabled` | `true` | Le garde-fou protège par défaut — un opérateur doit désactiver explicitement, jamais l'inverse. |
| `AdapterErrorRateThreshold` | `0.10` (10 %) | Taux de `AdapterError` parmi les tentatives v2 (`servedByV2Count + AdapterError`) au-delà duquel le garde-fou se déclenche. |
| `TranscodeStartFailureRateThreshold` | `0.20` (20 %) | Taux d'échec de démarrage ffmpeg parmi les sessions servies par v2, au-delà duquel le garde-fou se déclenche. Seuil plus large que le précédent : les causes d'échec de démarrage sont largement indépendantes du choix v2/legacy (espace disque, contention matérielle, source malformée). |
| `MinimumSampleSize` | `20` | Nombre minimal de tentatives avant qu'un taux ne soit fiable — évite qu'une seule requête malchanceuse (« 100 % d'échec sur 1 tentative ») ne déclenche le garde-fou dès le palier 1 %. |

Tous les champs numériques sont clampés au `set` (même convention PR104 que `PlaybackShadowOptions`) : pas d'exception possible depuis une valeur de configuration invalide.

### 5.2 Sémantique du déclenchement — à lire avant de dépendre de ce mécanisme

`PlaybackStopThresholdGuard.Evaluate()` (`Reefin.MediaEncoding/Playback/PlaybackStopThresholdGuard.cs`) est **sans état persisté** : il recalcule sa réponse à chaque appel, à partir de la configuration lue en direct et des compteurs cumulatifs de `PlaybackOperationalMetrics`. Deux conséquences opérationnelles importantes :

1. **Le garde-fou est collant (« sticky ») en pratique une fois déclenché.** Un déclenchement force legacy pour toute requête suivante → plus aucune tentative v2 → les compteurs qui ont produit le déclenchement (tentatives v2, erreurs adaptateur, échecs de démarrage) cessent d'évoluer → le taux calculé reste figé à sa valeur de déclenchement. Ce n'est pas un bug : c'est le comportement voulu d'un coupe-circuit — il ne doit pas se relever tout seul juste parce que le trafic v2 s'est arrêté.
2. **La seule façon de lever un déclenchement est un changement de configuration**, pris en compte immédiatement (pas de redémarrage requis — même mécanisme que le kill switch `PlaybackShadow.Mode`, lu en direct à chaque requête). Trois leviers, du plus radical au plus ciblé :
   - `StopThresholds.Enabled = false` — désactive le garde-fou entièrement (à utiliser avec prudence : supprime aussi la protection contre une récidive immédiate de la même cause).
   - Relever le seuil concerné (`AdapterErrorRateThreshold`/`TranscodeStartFailureRateThreshold`) au-dessus du taux figé.
   - Relever `MinimumSampleSize` au-dessus du nombre de tentatives déjà accumulées (force le garde-fou à considérer l'échantillon actuel comme insuffisant).

**Un déclenchement doit toujours être investigué avant d'être levé.** Le garde-fou existe précisément parce qu'un opérateur peut ne pas surveiller l'endpoint de diagnostics en continu — lever la protection sans comprendre la cause revient à annuler l'intérêt du mécanisme.

### 5.3 Observabilité d'un déclenchement

- **Log** : `PlaybackStopThresholdGuard` émet un log de niveau `Critical` au moment précis de la transition faux→vrai (jamais à chaque requête tant que l'état reste tripped — pas de spam). Le message inclut le détail des deux conditions évaluées (taux adaptateur et taux de démarrage, avec numérateur/dénominateur) même si une seule a effectivement déclenché.
- **Diagnostics** : `stopThresholdGuardTripped = true` sur `GET /System/PlaybackDiagnostics/Metrics` (§4) tant que la condition de déclenchement n'a pas été levée par un changement de configuration (§5.2).
- **Effet observable** : chaque requête repliée pendant que le garde-fou est tripped est comptée sous `fallbackReasonCounts.StopThresholdTripped` et loguée en `Information` par `MediaInfoHelper.FallbackToLegacy`, comme tout autre repli — aucune requête n'échoue silencieusement, le comportement observable pour le client est identique à un repli legacy ordinaire (URL de streaming valide, juste servie par le chemin legacy).

---

## 6. Procédure de coupe-circuit manuelle (kill switch)

Indépendamment du garde-fou automatique, un opérateur peut à tout moment forcer un repli complet sur legacy :

1. Repasser `PlaybackShadow.Mode` à `Legacy` ou `Shadow` (n'importe laquelle des deux force legacy pour le chemin live — `Shadow` a l'avantage de conserver l'observabilité shadow existante pendant l'investigation).
2. Effet immédiat, sans redémarrage — `MediaInfoHelper.ResolveServedStreamInfo` lit `PlaybackShadow.GetEffectiveMode()` en direct sur la configuration serveur à chaque requête, avant même de tenter de résoudre un plan v2 (vérifié en premier, avant le garde-fou automatique lui-même — voir l'ordre dans `MediaInfoHelper.cs`).
3. Chaque requête ainsi repliée est comptée sous `fallbackReasonCounts.KillSwitch` (§4) et loguée.
4. `CanaryPercentage` n'a pas besoin d'être remis à 0 pour que le kill switch soit efficace — `Mode` prime. Le remettre à 0 reste recommandé avant de réactiver `Mode = Canary`, pour rouvrir progressivement plutôt que de retomber directement sur le pourcentage précédent.

Le garde-fou automatique (§5) et le kill switch manuel produisent le même effet observable pour le client (repli legacy) mais sont deux mécanismes indépendants : désactiver l'un n'affecte pas l'autre, et les deux sont vérifiés dans le même ordre à chaque requête (kill switch d'abord, garde-fou ensuite, avant toute résolution de plan).

---

## 7. Ce que ce runbook ne couvre pas

- Le comportement du chemin de décision lui-même (quels champs `StreamInfo` sont résolus par l'adaptateur, l'invariant de parité exécutable) — voir `docs/pr115-design-canary-execution.md`.
- Les tests de fumée ffmpeg/HLS en local (Docker) — voir `ci/smoke.sh` et sa documentation en tête de fichier.
- La procédure de rollback d'une version de code (ce document couvre un réglage de configuration, pas un déploiement).
