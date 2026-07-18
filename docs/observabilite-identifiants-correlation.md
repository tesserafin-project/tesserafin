# Identifiants de corrélation — les trois portées

> Statut : `RequestId` implémenté par #42, `PlaybackAttemptId` par #43. Deux PR, deux champs.

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

## Implémentation (#43)

- **Généré par le client**, une seule fois au début d'une tentative, et renvoyé tel quel sur toutes
  les requêtes de cette tentative — retries compris. Le serveur ne le fabrique jamais : il le valide,
  le stocke, l'écho. Un retry conserve la valeur ; une nouvelle tentative en génère une nouvelle.
- **Surface (additive et optionnelle partout)** : `PlaybackInfo` en requête (`PlaybackInfoDto`) et en
  réponse (`PlaybackInfoResponse`) ; `POST` et `PUT Playback/Sessions` (`PlaybackPlanRequestBase`,
  donc les deux corps) ; `PlaybackSession` ; `PlaybackSessionResponse` ; diagnostics admin
  (`PlaybackDiagnosticDetail`, et la liste via sa `PlaybackSessionResponse` imbriquée).
  `GET .../Stream` et `DELETE` n'en ont pas besoin : à ce stade la session le porte déjà.
- **Opaque.** Aucune structure imposée — ni GUID, ni hexadécimal, ni préfixe. Seules deux choses sont
  vérifiées, et uniquement parce qu'une valeur non bornée ou non imprimable serait une nuisance dans
  un fichier de log plutôt qu'une aide à la corrélation : un plafond de longueur (128 caractères) et
  l'absence de caractères de contrôle. Rien n'est jamais extrait ni interprété de la valeur.
- **Rejet.** Fourni-et-malformé ⇒ `ArgumentException` ⇒ 400, par le même validateur sur les deux
  endpoints v2 et sur `PlaybackInfo` — une valeur acceptée ici et refusée deux requêtes plus loin
  serait pire que pas de validation. Absent ⇒ `null`, valide partout : un client tiers qui ignore le
  champ n'est jamais affecté. Une chaîne vide ou blanche est refusée plutôt que traitée comme
  « absente » : l'accepter fabriquerait un seau de tentative fusionnant toutes les tentatives sans
  rapport ayant elles aussi envoyé du vide.
- **`null` n'efface pas.** Un `PUT` de la même tentative qui omet le champ conserve la valeur
  enregistrée à la création. « Pas envoyé » n'est pas « oublie ».
- **Jamais une clé d'autorisation** : aucune décision d'accès n'en est dérivée, il ne sert jamais à
  retrouver une session, et il ne remplace aucun contrôle existant.
- **Log structuré** : publié sous la propriété `PlaybackAttemptId`, dans un scope **imbriqué** dans
  celui de #42. Une ligne porte donc les deux — et sur un retry, `PlaybackAttemptId` est identique
  pendant que `RequestId` diffère. C'est exactement la paire qui rend les portées lisibles.

### Hors périmètre de #43, explicitement

- **La génération côté client** vit dans `reefin-web`, un autre dépôt. Cette PR serveur ne peut pas
  la livrer ; elle rend le champ acceptable de bout en bout côté serveur.
- **`openapi/openapi.json` et `contract.lock.json`** n'existent pas sur `master` : le mécanisme de
  régénération déterministe est la PR #40, non fusionnée (cf. #44 §6). `OpenApiSpecTests` est
  structurel (il récupère la spec servie, sans comparaison à un instantané commité), donc le champ
  apparaît dans la spec générée sans qu'aucun fichier ne soit à régénérer ici.
