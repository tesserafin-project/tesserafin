# Identifiants de corrélation — les trois portées

> Statut : le `RequestId` décrit ici est implémenté (issue #42). Le `PlaybackAttemptId` est spécifié
> ici pour fixer la hiérarchie, et implémenté séparément (issue #43).

L'issue #34 confondait deux identifiants sous un seul nom. Elle a été scindée en #42 et #43 pour une
raison dirimante : **un même identifiant ne peut pas à la fois changer à chaque requête HTTP et
rester stable sur toute une tentative de lecture.** Les deux propriétés sont mutuellement
exclusives. Il faut donc deux champs, et ce document dit lequel répond à quelle question.

## Tableau des portées

| Portée | Identifiant | Origine | Durée de vie | Change quand ? |
|---|---|---|---|---|
| Une requête HTTP | `RequestId` / `TraceId` | **serveur** — `Activity.TraceId`, sinon `HttpContext.TraceIdentifier` | le round-trip | **à chaque requête**, y compris deux requêtes de la même tentative |
| Une tentative de lecture | `PlaybackAttemptId` | **client** — opaque pour le serveur | de `PlaybackInfo` jusqu'au `DELETE` ou à l'abandon | à chaque **nouvelle** tentative ; **inchangé** sur un retry |
| Une session serveur | `PlaySessionId` / `PlaybackSessionId` | client (`PlaySessionId`) / serveur (`PlaybackSessionId`) | la session de transcodage/lecture, potentiellement des heures | à chaque nouvelle session |

Les portées sont emboîtées : une session agrège plusieurs tentatives, une tentative agrège plusieurs
requêtes.

```
PlaySessionId / PlaybackSessionId  ─────────────────────────────────────────►  (heures)
  └─ PlaybackAttemptId  ────────────────────►      └─ PlaybackAttemptId  ────►
       └─ RequestId ─►  └─ RequestId ─►  └─ RequestId ─►   └─ RequestId ─►
       (PlaybackInfo)   (POST Sessions)  (retry)           (PUT Sessions)
```

## Ce que `RequestId` est, et n'est pas

**Est** : la clé qui répond à « quelles lignes de log viennent d'un seul et même aller-retour HTTP ».

**N'est pas** :

- **Pas un identifiant de tentative.** Il change entre le `POST` et le `PUT` d'une même lecture.
  Corréler une tentative avec lui est impossible par construction — c'est le rôle de
  `PlaybackAttemptId`.
- **Pas un identifiant de session.** `PlaySessionId` reste inchangé (contrat client v2, PR112b) ;
  #42 n'ajoute que du log.
- **Pas une clé d'autorisation.** Aucune décision d'accès n'en est dérivée, aucun contrôle existant
  n'est remplacé.

## Implémentation (#42)

- `RequestCorrelationMiddleware` (`Reefin.Api/Middleware/`) — enregistré **avant**
  `ExceptionMiddleware`, pour qu'une requête qui finit en erreur porte quand même son identifiant.
  Il dérive la valeur, la range dans `HttpContext.Items`, ouvre un `ILogger.BeginScope` qui la
  publie sous la propriété structurée `RequestId` pour toute la durée de la requête, et l'écho sur
  l'en-tête de réponse `X-Request-Id`.
- **`traceparent` entrant** : géré sans que ce middleware ne lise l'en-tête. La couche d'hébergement
  ASP.NET Core l'a déjà transformé en `Activity` ambiante ; `RequestCorrelation.Derive` préfère
  `Activity.TraceId`, donc la trace de l'appelant est **rejointe**, pas redémarrée. `X-Request-Id`
  renvoie toujours l'identifiant retenu.
- `IRequestCorrelationAccessor` (`Reefin.Controller/Diagnostics/`) — l'abstraction qui laisse
  `Reefin.MediaEncoding` lire l'identifiant sans dépendre d'ASP.NET Core. Implémentation hébergée :
  `HttpRequestCorrelationAccessor`. Hors requête (timer, tâche planifiée, démarrage) elle renvoie
  `null`, et **`null` est la bonne réponse** : aucune requête n'a causé cette ligne. Les diagnostics
  n'inventent jamais de corrélation.
- Points de log alimentés : `TranscodeManager` (`PingTranscodingJob`, kill par timer,
  `KillTranscodingJob`) et `PlaybackSessionManager.RecordLifecycleEvent`, où l'identifiant est
  estampillé **par événement** — en plus du `PlaybackSessionId` qui, lui, reste la clé de
  regroupement. Il est ensuite exposé sur `DiagnosticTimelineEntry.RequestId`.

Tout est additif : chaque paramètre ajouté est optionnel et vaut `null` par défaut, donc aucun
appelant antérieur ne change de comportement.
