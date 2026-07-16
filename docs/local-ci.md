# CI locale (Docker) — porte de merge obligatoire

## Pourquoi

Le quota GitHub Actions hébergé est épuisé depuis le ~2026-07-06. Tant qu'il
n'est pas reconstitué (ou qu'un runner self-hosted n'est pas enregistré, voir
plus bas), il n'y a **aucune CI hébergée fonctionnelle** sur ce dépôt.

`ci/run.sh` remplace cette CI hébergée comme porte de merge obligatoire :
avant de merger une branche, on exige un `./ci/run.sh` vert en local (ou sur
un runner self-hosted), au même titre qu'une CI GitHub Actions verte
l'aurait exigé auparavant.

## Comment lancer

```bash
./ci/run.sh
```

C'est le seul point d'entrée. Il :

1. build l'image `reefin-ci` depuis `Dockerfile.ci` (racine du dépôt),
2. lance un conteneur qui exécute, dans l'ordre et en échouant vite au
   premier problème :
   - `dotnet restore Reefin.sln`
   - `dotnet build Reefin.sln` (0 erreur exigée)
   - `dotnet test Reefin.sln` (suite complète)
3. affiche un résumé PASS/FAIL avec le temps total en fin d'exécution.

Le dépôt est monté en bind-mount (pas copié) dans le conteneur : le script
teste donc toujours l'état **actuel** du répertoire de travail — la branche
sortie, y compris les modifications non commitées. C'est ce qui permet au
même script de servir de porte pour n'importe quelle branche.

Le restore NuGet est mis en cache dans un volume Docker nommé
(`reefin-nuget`), donc les exécutions suivantes sont nettement plus rapides
que la première (qui télécharge tout).

### Ce que ça couvre

- Build complet de `Reefin.sln` (tous les projets).
- Suite de tests complète de `Reefin.sln` (tous les projets `tests/*`), y
  compris `Reefin.Server.Integration.Tests` (dont `OpenApiSpecTests`, qui
  fait donc office de vérification du contrat OpenAPI — pas d'étape séparée
  nécessaire).
- Dépendances natives nécessaires à l'exécution des tests : `ffmpeg`
  (`Reefin.MediaEncoding`), `libfontconfig1` (SkiaSharp.NativeAssets.Linux,
  utilisé par `src/Reefin.Drawing.Skia`). `SQLitePCLRaw` n'a besoin de rien
  de plus : sa lib native est embarquée dans le paquet NuGet.

### Ce que ça ne couvre pas

- Pas de lint/format check dédié (le build échoue déjà sur warnings-as-errors
  via `Directory.Build.props` pour un usage dev normal ; ce gate CI ne
  relitige pas cette politique et ne fait que constater succès/échec du
  build et des tests).
- Pas de packaging/déploiement.

## Particularité constatée : tests statiques partagés

En construisant cette CI, une régression pré-existante et non liée à
l'infrastructure a été mise au jour puis corrigée : certains tests de
`Reefin.Controller.Tests` (`BaseItemTests`) dépendaient de l'ordre
d'exécution parce qu'ils lisaient des statics process-wide
(`Video.RecordingsManager`, `BaseItem.MediaSourceManager`) sans les
initialiser eux-mêmes, comptant implicitement sur ce qu'un autre test avait
laissé en place. Corrigé en initialisant explicitement ces statics dans
chaque test qui les touche (voir `BaseItemStaticStateFixture` pour le
contexte plus général sur cette classe de statics partagés).

## Particularité environnementale : `ParseNetworkTests`

Sur certaines machines hôtes (résolution `localhost` sans mapping IPv6
`::1`), 2 tests de `Reefin.Server.Tests.ParseNetworkTests.TestNetworks`
échouent en dehors de Docker — c'est un problème d'environnement hôte, pas
une régression de code. Dans le conteneur `reefin-ci`, la résolution réseau
par défaut de Docker inclut `::1 localhost`, donc ces 2 tests passent sans
configuration supplémentaire. Si jamais ils réapparaissaient en échec dans
le conteneur, la correction attendue est d'assurer le mapping `::1
localhost` (option `--add-host` ou entrée `/etc/hosts` du conteneur), jamais
de les filtrer silencieusement.

## Root-owned bin/obj

Le conteneur tourne en `root` (image SDK par défaut) et le dépôt est monté
en lecture-écriture, donc `bin/` et `obj/` écrits pendant le build/test
appartiennent à `root` sur l'hôte. Ces répertoires sont ignorés par git
(`[Bb]in/`, `[Oo]bj/` dans `.gitignore`) donc ça n'affecte jamais
`git status`/les commits, mais ça peut bloquer un `dotnet build` hôte
ultérieur avec une erreur "Access denied". `ci/run.sh` corrige
automatiquement la propriété du répertoire de travail vers l'utilisateur
invoquant à la fin de chaque exécution (succès ou échec) — c'est ce qui
rend le script sûr à relancer plusieurs fois de suite sur le même
checkout.

## Runner self-hosted (optionnel)

`.github/workflows/local-ci.yml` existe déjà et cible
`runs-on: [self-hosted, linux, x64, reefin]`. Il est **inerte** tant
qu'aucun runner portant ces labels n'est enregistré sur ce dépôt — sans
runner, GitHub Actions n'a personne à qui donner le job, et ça ne consomme
donc pas le quota hébergé (épuisé de toute façon).

Pour l'activer, enregistrer un runner self-hosted sur une machine qui a
Docker :

1. Repo GitHub → Settings → Actions → Runners → New self-hosted runner.
2. Suivre les instructions d'installation affichées (téléchargement de
   l'agent `actions-runner`, `./config.sh --url ... --token ...`).
3. À l'étape de configuration des labels, ajouter explicitement :
   `self-hosted, linux, x64, reefin` (le label `reefin` en plus des 3
   labels par défaut est ce qui matche le `runs-on` du workflow).
4. Démarrer le runner (`./run.sh`, ou l'installer comme service avec
   `./svc.sh install && ./svc.sh start` pour qu'il survive aux reboots).

Une fois le runner en ligne avec ces labels, le workflow s'active
automatiquement sur chaque push vers `master` et chaque pull request, et
appelle `./ci/run.sh` — exactement le même script que celui documenté
ci-dessus, donc pas de divergence entre CI locale manuelle et CI
self-hosted automatique.
