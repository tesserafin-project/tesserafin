# CI locale (Docker) — porte de merge obligatoire

> ## ⚠ Mise à jour du 2026-07-30 — `ci-compat.yml` est armé (#94, tranche ABI)
>
> **`ci-compat.yml` n'est plus en garage.** Il s'exécute automatiquement sur
> `pull_request` vers `master` ; son `workflow_dispatch` a été retiré, car
> chaque job est relatif à une pull request et un dispatch ne pouvait
> produire aucun verdict. La ligne `ci-compat.yml` du tableau 2026-07-27
> ci-dessous, comme celle du tableau « Workflows mis en pause », ne décrit
> plus l'état courant. Trois workflows serveur sont désormais armés :
> `ci-tests.yml`, `ci-format.yml` et `ci-compat.yml`.
>
> Les deux défauts cités dans ce tableau sont corrigés, et trois autres, qui
> auraient laissé la porte verte sans rien prouver, avec eux :
>
> * les quatre assemblages d'avant le renommage (`MediaBrowser.Common.dll`,
>   `MediaBrowser.Controller.dll`, `MediaBrowser.Model.dll`,
>   `Emby.Naming.dll`) sont remplacés par `Tesserafin.Common.dll`,
>   `Tesserafin.Controller.dll`, `Tesserafin.Model.dll` et
>   `Tesserafin.Naming.dll` ;
> * `secrets.JF_BOT_TOKEN` a disparu : le commentaire de PR est supprimé, le
>   rapport vit dans le résumé de job et dans un artefact ;
> * la boucle `apicompat … || true` — qui transformait fichier manquant,
>   plantage de l'outil et vraie rupture d'ABI en simple texte de rapport
>   dans un step qui sortait `0` — est remplacée par `ci/abi-compat.sh`,
>   fail-closed de bout en bout ;
> * `Microsoft.DotNet.ApiCompat.Tool` est épinglé à `10.0.302` dans
>   `.config/dotnet-tools.json` au lieu d'être installé en `latest` ;
> * le périmètre ABI est `ci/abi-assemblies.txt` (8 assemblages), donnée de
>   contrat dont le nombre d'entrées est vérifié contre une constante de
>   `ci/abi-compat.sh` : le réduire exige deux modifications visibles.
>
> Les contrôles déterministes (`./ci/tests/abi-compat.test.sh`, 33
> assertions) tournent dans le workflow lui-même sur des bibliothèques
> synthétiques jetables : ils prouvent à chaque run que la porte rougit sur
> un membre public supprimé, une signature incompatible, un assemblage
> manquant d'un côté ou de l'autre, un manifeste vide, dupliqué, non trié ou
> rétréci, et un échec d'ApiCompat.
>
> **Rien de tout cela ne rend un check obligatoire.** Les *required status
> checks* et CodeQL restent indisponibles pour la même raison externe
> qu'en dessous, et la porte locale (`./ci/run.sh`) reste la porte de merge
> faisant autorité. #94 reste ouverte.

> ## ⚠ Mise à jour du 2026-07-30 — `ci-format.yml` est armé (#94, tranche #156)
>
> Deux workflows serveur sont désormais armés et s'exécutent
> **automatiquement** sur les pull requests et les push `master` :
> `ci-tests.yml` (armé le 2026-07-27, tranche C1.1 / #151) et
> `ci-format.yml` (cette tranche). La phrase « ce qui a été ré-armé côté
> serveur : rien » du bloc 2026-07-27 ci-dessous ne décrit donc plus l'état
> courant, pas plus que la ligne `ci-format.yml` de son tableau.
>
> La dette qui retenait `ci-format.yml` est purgée : les 75 diagnostics
> StyleCop de documentation XML (47 `SA1615`, 26 `SA1611`, 1 `SA1629`,
> 1 `SA1618`) répartis sur **16** fichiers de `tests/` — et non 22, chiffre
> erroné à la source, relu dans le log du run `30230606255` lui-même — ont été
> corrigés en écrivant la documentation manquante. Aucune règle n'a été
> supprimée, désactivée ni rétrogradée.
>
> **Rien de tout cela ne rend un check obligatoire.** Les *required status
> checks* et CodeQL restent indisponibles pour la même raison externe qu'en
> dessous, et la porte locale (`./ci/run.sh`) reste la porte de merge
> faisant autorité. #94 reste ouverte.

> ## ⚠ Mise à jour du 2026-07-27 — la CI hébergée est revenue (#94)
>
> **Ce qui a changé.** GitHub réalloue des runners hébergés pour ce dépôt. La
> cause 1 décrite ci-dessous (refus d'allocation avant le premier step) est
> **révolue**. Preuve : run `30229812748` (« ABI Compatibility », dispatch
> manuel sur `master`), job `ABI - HEAD` **completed/success**, **8 steps
> exécutés**, runner `GitHub Actions 1000000000`, label `ubuntu-latest` —
> l'exact opposé de la signature de panne (échec en 3-4 s, zéro step, aucun
> runner). Le dépôt appartient désormais à l'organisation
> `tesserafin-project` ; l'allocation personnelle `all3f0r1` citée plus bas
> n'est plus le pool facturé. **Tout le diagnostic de juillet ci-dessous est
> conservé comme archive, il ne décrit plus l'état courant.**
>
> **Ce qui a été ré-armé côté serveur : rien.** Chaque workflow a été
> *réellement exécuté* sur la PR #132 avant d'être re-garé ; les preuves sont
> dans son en-tête. Côté web en revanche, `pull_request.yml` et `push.yml`
> sont armés et **verts** (run `30231042125` sur PR, run `30231202773` sur
> `main`).
>
> | Workflow | Preuve | Cause du maintien en garage |
> | --- | --- | --- |
> | `ci-tests.yml` | run `30231076754` | l'ajout de `ffmpeg`/`libfontconfig1` et du filtre `Category!=Smoke` corrige 13 des 14 échecs. Reste `OpenApiContractTests.CommittedContract_MatchesRunningServer` : le serveur hébergé produit `fea08c38…` là où le contrat commité vaut `0700633b…`. Le contrat n'est pas périmé — le même test passe en local. SDK identique (10.0.302) et `Release` rejoué en conteneur (3/3) : les deux explications évidentes sont **exclues**. Divergence d'environnement non isolée. |
> | `ci-codeql-analysis.yml` | run `30230774481` (web, même échec) | code scanning exige **GitHub Advanced Security**, absent en dépôt privé sur plan `free`. L'analyse tourne, l'upload est refusé. **Perte de couverture réelle et non compensée.** |
> | `ci-format.yml` | run `30230606255` | `dotnet format --verify-no-changes` : 75 violations StyleCop sur 22 fichiers (47 SA1615, 26 SA1611, 1 SA1629, 1 SA1618), toutes des commentaires XML sous `tests/`. Dette pré-existante. |
> | `ci-compat.yml` | run `30230606251` | `ABI - Difference` cherche `MediaBrowser.Common.dll`, `Emby.Naming.dll`… — noms d'assemblages d'avant le renommage ; et le step de rapport exige `secrets.JF_BOT_TOKEN`, absent. |
> | `openapi-pull-request.yml` | run `30230606338` | `openapi-diff:2.1.6` lève `java.lang.StackOverflowError` sur cette spec. |
> | `openapi-workflow-run.yml` | — | poste un commentaire avec le même `secrets.JF_BOT_TOKEN` absent. |
> | `openapi-merge.yml` | — | publie la spec ; C1 ne doit rien publier. |
> | `local-ci.yml` | `total_count: 0` | aucun runner self-hosted ; ré-armer créerait un check `queued` éternel. |
>
> **Économie de minutes** déjà écrite dans `ci-tests.yml` pour le jour où il
> sera armé : `ubuntu-latest` uniquement en automatique (macOS ×10 et Windows
> ×2 restent sur `workflow_dispatch`), `timeout-minutes: 30`,
> `fail-fast: true`, PR en brouillon ignorées, `paths-ignore` sur `**/*.md` et
> `docs/**` (sûr **uniquement** parce que ce workflow ne porte aucune analyse
> de sécurité), cache NuGet, couverture de code sur les push `master`
> seulement, et `concurrency` qui annule les runs supplantés.
>
> **Ce qui n'est TOUJOURS pas résolu — et c'est pour ça que #94 reste
> ouverte.** Les *required status checks* restent indisponibles :
> `GET .../branches/master/protection` et `GET .../rulesets` renvoient
> toujours `403 "Upgrade to GitHub Pro or make this repository public to
> enable this feature"` (dépôt privé, organisation sur le plan `free`). Les
> checks s'exécutent et rapportent un statut, mais **aucun ne peut être rendu
> obligatoire**. La porte locale garde donc une autorité conventionnelle, pas
> mécanique.
>
> **La porte locale reste obligatoire** tant que ce point n'est pas levé, et
> depuis #94 elle purge `bin/`/`obj/` elle-même (voir « Porte de référence »).


## Pourquoi — cause exacte (issue #62)

Ce n'est **pas** un défaut de configuration du dépôt. Deux causes distinctes,
toutes deux confirmées.

### Cause 1 — refus d'allocation GitHub *avant* démarrage du job

- le compte `all3f0r1` est sur le plan **`free`** et `all3f0r1/reefin` est un
  dépôt **privé** : les minutes Actions sont donc comptées ;
- l'allocation gratuite est de **2000 minutes/mois à l'échelle du COMPTE**,
  pas du dépôt ;
- consommation juillet 2026 : Linux 634 min (×1), Windows 266 min (×2 = 532),
  macOS 148 min (×10 = 1480) → **≈ 2646 minutes pondérées > 2000**.
  Le dépassement est avéré même en ignorant totalement Windows
  (634 + 1480 = 2114 > 2000) ;
- le pool est drainé **majoritairement par un autre dépôt du compte**
  (`youtube-chapter-splitter`, runs macOS et Windows). C'est pour cette raison
  que les jobs pourtant bon marché de `reefin` sont refusés ;
- `netAmount` = 0 partout alors que `grossAmount` > 0 : la **limite de dépense
  est à 0 $**, donc le dépassement est *refusé* et non facturé.

Signature technique cohérente : les jobs `ubuntu-latest` (`ABI - HEAD`,
`ABI - BASE`, `Head Artifact`, `Common Ancestor`, `OpenAPI Check`) échouaient
en **3-4 secondes, avec zéro step exécuté et aucune annotation**, alors que
`GET /repos/all3f0r1/reefin/actions/permissions` renvoie
`enabled: true, allowed_actions: all` et que tous les `runs-on` sont valides.
Un job qui échoue *avant son premier step* n'est pas un job cassé : c'est un
job à qui on n'a jamais donné de machine.

### Corollaire — la protection de branche est indisponible

Vérifié :

```console
$ gh api repos/all3f0r1/reefin/branches/master/protection
{"message":"Upgrade to GitHub Pro or make this repository public to enable
this feature.","status":"403"}
$ gh api repos/all3f0r1/reefin/rulesets
{"message":"Upgrade to GitHub Pro or make this repository public to enable
this feature.","status":"403"}
```

Deux conséquences :

1. **Bonne nouvelle** : aucun *required status check* ne peut être configuré
   sur `master`. Basculer un workflow en `workflow_dispatch` ne risque donc
   pas de transformer un rouge en « Expected — Waiting for status to be
   reported » éternel. Le bruit est bien supprimé, pas seulement recoloré.
2. **Mauvaise nouvelle** : la condition 3 du retour à la normale (« la porte
   obligatoire a une autorité explicite ») est **mécaniquement inatteignable**
   tant que le dépôt est privé sur le plan `free`. On ne peut rendre aucune
   porte obligatoire — la porte locale n'a aujourd'hui qu'une autorité
   conventionnelle (ce document), pas mécanique.

C'est aussi une confirmation indépendante du diagnostic : ce 403 n'est pas un
problème de droits, c'est GitHub qui confirme `free` + privé.

### Cause 2 — aucun runner self-hosted enregistré

`.github/workflows/local-ci.yml` exige
`runs-on: [self-hosted, linux, x64, reefin]`, mais
`GET /repos/all3f0r1/reefin/actions/runners` renvoie `total_count: 0`.
Sans runner portant ces labels, le job ne tombe pas en échec : il reste
**`queued` indéfiniment**, ce qui laisse un check *pending* éternel sur chaque
pull request. C'est par construction, pas un bug.

### Ce qui a été fait dans le dépôt — et ce qui ne l'a pas été

Les workflows concernés ont été basculés en **`workflow_dispatch` (manuel
uniquement)**. C'est tout. Aucun workflow n'a été réécrit pour *faire croire*
à une réparation : la cause est extérieure au dépôt (facturation /
allocation), on se contente d'arrêter la pollution de chaque PR et de
documenter. Voir le tableau « Workflows mis en pause » plus bas.

## Porte de référence pendant la panne

Tant que la cause 1 ou la cause 2 subsiste, **la seule porte de merge qui
fasse autorité est la CI locale Docker**. Avant de merger quoi que ce soit,
sur le serveur :

```bash
./ci/run.sh
```

**Une seule commande, et aucun prérequis à ne pas oublier.** Depuis #94, la
purge de tous les `bin/`/`obj/` est intrinsèque à `ci/run.sh` : elle a lieu
après le build de l'image et *avant* la première compilation, elle couvre les
artefacts root-owned laissés par une exécution précédente (elle s'exécute dans
le conteneur, en root, sur le bind-mount), et elle est suivie d'une assertion
côté hôte qui échoue la porte si le moindre répertoire a survécu. Il n'existe
pas d'option pour la désactiver. Contrat et tests :
`ci/lib/clean-artifacts.sh` et `ci/tests/clean-artifacts.test.sh`.

La purge n'est pas décorative. Le dépôt est bind-monté dans le conteneur,
donc `obj/` survit d'une exécution à l'autre *et* aux builds côté hôte
(notamment `./ci/openapi-generate.sh`). Avec un `obj/` chaud, MSBuild
considère un projet à jour et le saute — et sauter un projet saute ses
analyseurs Roslyn : la porte affiche alors **PASS pour un arbre qui échoue
depuis un checkout propre**. Ce n'est pas hypothétique (PR #46 et #45 ont été
mergées sur un PASS obtenu ainsi, et master a ensuite échoué CA1034 au premier
checkout propre). `ci/run.sh` passe aussi `--no-incremental` pour la même
raison ; c'est la ceinture qui accompagne les bretelles, pas l'inverse.

Historique : jusqu'à #94, cette purge était une commande à lancer *à la main*
avant la porte. Une porte dont la validité dépend de la mémoire de
l'appelant n'est pas une porte — d'où le déplacement dans le script.

> Note : `ci/run.sh` ne prend **aucun argument**. Un éventuel
> `./ci/run.sh local` serait silencieusement ignoré — la commande exacte est
> `./ci/run.sh` tout court.

### Preuve de référence de la porte

Exécution probante sur `master` non modifié, au moment du parking :

| | |
| --- | --- |
| Date | 2026-07-19 |
| cwd | `/home/alex/Repos/.wt/it13-rig` (worktree propre) |
| SHA | `756cea19519e8c395ad763e117abb0602c087108` |
| Commande | purge `bin/`+`obj/` manuelle, puis `./ci/run.sh` (avant #94 ; la purge est désormais intégrée) |
| Exit code | **0** |
| Durée | 5 min 53 s (354 s de build+test) |
| Résultat | `RESULT: PASS` — 21 assemblies, **4020 passés, 0 échec, 11 skipped** |

Les 11 skips sont structurels et non des skips de complaisance : 4
`ManagedFileSystemTests` Windows-only, 2 `MediaEncoding`, 2 `Controller`,
3 `Integration`.

C'est le vert de référence auquel comparer toute exécution ultérieure. Un
`PASS` avec un total de tests **inférieur** à 4020 doit être traité comme un
faux vert (typiquement un `obj/` non purgé) et non comme un succès.

### ⚠ `./ci/run.sh` ne suffit pas à lui seul

`ci/run.sh` lance `dotnet test --filter 'Category!=Smoke'` (ligne 78). Il
**exclut donc tous les tests marqués `Category=Smoke`**, et avec eux le
smoke et l'E2E. Un `RESULT: PASS` de `run.sh` ne dit **rien** de ces
suites-là.

C'est un piège de confiance réel : une régression prouvée par un test E2E
peut passer un `run.sh` vert. Le cas a été rencontré sur l'issue #59, dont la
reproduction — un ré-encodage réellement servi malgré `AllowTranscoding:false`
— n'est visible que côté E2E.

La porte de merge complète est donc **trois scripts**, pas un :

```bash
# 1. build + suite unitaire (exclut Category=Smoke) — purge bin/+obj/ incluse
./ci/run.sh

# 2. les suites que run.sh a délibérément écartées
./ci/smoke.sh
./ci/smoke-e2e.sh
```

Références mesurées le 2026-07-19 sur le correctif #59 (SHA `48bee212`) :

| Commande | Exit | Durée | Résultat |
| --- | --- | --- | --- |
| `./ci/run.sh` | 0 | 482 s | 4031 passés / 0 échec / 11 skipped |
| `./ci/smoke.sh` | 0 | 110 s | 32/32 |
| `./ci/smoke-e2e.sh` | 0 | 48 s | 11/11 |

Ne conclure « porte verte » qu'après les **trois** exit 0.

## Comment lancer

```bash
./ci/run.sh
```

C'est le seul point d'entrée. Il :

1. build l'image `tesserafin-ci` depuis `Dockerfile.ci` (racine du dépôt),
2. **purge tous les `bin/`/`obj/` du checkout** via cette image (donc y compris
   les artefacts root-owned), puis vérifie côté hôte qu'il n'en reste aucun ;
   un échec ici arrête la porte **avant** toute compilation,
3. lance un conteneur qui exécute, dans l'ordre et en échouant vite au
   premier problème :
   - `dotnet restore Tesserafin.sln`
   - `dotnet build Tesserafin.sln` (0 erreur exigée)
   - `dotnet test Tesserafin.sln` (suite complète)
4. affiche un résumé PASS/FAIL avec le temps total en fin d'exécution.

Le dépôt est monté en bind-mount (pas copié) dans le conteneur : le script
teste donc toujours l'état **actuel** du répertoire de travail — la branche
sortie, y compris les modifications non commitées. C'est ce qui permet au
même script de servir de porte pour n'importe quelle branche.

Le restore NuGet est mis en cache dans un volume Docker nommé
(`tesserafin-nuget`), donc les exécutions suivantes sont nettement plus rapides
que la première (qui télécharge tout).

### Ce que ça couvre

- Build complet de `Tesserafin.sln` (tous les projets).
- Suite de tests complète de `Tesserafin.sln` (tous les projets `tests/*`), y
  compris `Reefin.Server.Integration.Tests`.
- **Contrat OpenAPI** (issue #36) : `OpenApiContractTests` fait partie de cette
  suite et vérifie que `openapi/openapi.json` commité correspond bien à ce que
  le serveur produit. Si ce n'est pas le cas, ce gate échoue et indique la
  commande à lancer (`./ci/openapi-generate.sh`). C'est volontairement un test
  et non une étape séparée de `ci/run.sh` : il réutilise le démarrage serveur
  déjà payé par la suite. Voir `docs/openapi-contract.md`.
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
une régression de code. Dans le conteneur `tesserafin-ci`, la résolution réseau
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

## Workflows mis en pause (issue #62)

Basculés en `workflow_dispatch` (déclenchement manuel uniquement). Chaque
fichier porte en tête un commentaire rappelant son déclencheur d'origine, à
restaurer tel quel au retour à la normale.

| Workflow | Avant | Après | Pourquoi |
| --- | --- | --- | --- |
| `ci-compat.yml` | `pull_request` | `workflow_dispatch` | Rouge sur chaque PR avant le premier step (`ABI - HEAD`, `ABI - BASE`). |
| `openapi-pull-request.yml` | `pull_request` | `workflow_dispatch` | Idem (`Common Ancestor`, `Head Artifact`, `Base Artifact`). Le contrat OpenAPI reste gardé par `OpenApiContractTests` dans la porte locale. |
| `openapi-merge.yml` | `push` (master + tags `v*`) | `workflow_dispatch` | Même refus d'allocation ; rouge permanent sur master. Conséquence assumée : la spec publiée n'est plus mise à jour automatiquement. |
| `ci-tests.yml` | `push` (master) + `pull_request` | `workflow_dispatch` | Rouge sur chaque PR. Redondant pendant la panne : la porte locale exécute le même `dotnet build` + `dotnet test`. |
| `ci-format.yml` | `push` (master) + `pull_request` | `workflow_dispatch` | Rouge sur chaque PR. Couverture partiellement reprise par warnings-as-errors ; l'écart avec `dotnet format` est assumé, pas masqué. |
| `ci-codeql-analysis.yml` | `push` + `pull_request` + `schedule` | `workflow_dispatch` | Rouge sur chaque PR **et** un run cron en échec chaque semaine. **Perte réelle** : plus d'analyse de sécurité statique pendant la panne. |
| `local-ci.yml` | `pull_request` + `push` (master) | `workflow_dispatch` | Aucun runner correspondant (`total_count: 0`) : restait `queued` indéfiniment, laissant un check *pending* éternel. |
| `issue-stale.yml` | `schedule` + `workflow_dispatch` | `workflow_dispatch` | Sans garde de dépôt : un run en échec par jour, avant le premier step. |
| `pull-request-stale.yaml` | `schedule` + `workflow_dispatch` | `workflow_dispatch` | Idem, deux runs en échec par jour. |
| `issue-template-check.yml` | `issues` (`opened`) | `workflow_dispatch` | Sans garde de dépôt : un run en échec par issue ouverte. Ne pollue aucune PR et ne conditionne aucun merge, mais échoue de la même façon que les crons — mis en pause pour la même raison. |

Workflows **laissés intacts**, et pourquoi :

- `openapi-generate.yml` — `workflow_call` seul, aucun déclencheur propre ;
  il ne part que si un appelant (désormais manuel) l'invoque.
- `openapi-workflow-run.yml` — `workflow_run` en aval d'« OpenAPI Check » ;
  ce n'est pas un check de PR, il ne partira plus tant qu'« OpenAPI Check »
  n'est pas dispatché.
- `commands.yml`, `project-automation.yml`, `pull-request-conflict.yml` —
  déclenchés sur PR, mais leurs jobs sont gardés par
  `if: github.repository == 'jellyfin/jellyfin'` ou par une condition sur un
  commentaire `@jellyfin-bot`. Dans ce fork ces conditions sont fausses, donc
  les jobs sont **skipped** : ils ne demandent aucun runner et ne produisent
  aucun rouge. Les toucher n'apporterait rien.
- `release-bump-version.yaml` — `release: published` + `workflow_dispatch` ;
  ne se déclenche pas dans le flux courant.

## Retour à la normale

Le parking supprime le bruit ; **il ne répare pas GitHub Actions**. L'issue #62
ne pourra être close que lorsque les cinq conditions suivantes sont vraies
*simultanément* :

1. quota / facturation, ou allocation Actions, rétabli ;
2. déclencheurs d'origine restaurés (tableau ci-dessus, rappelés en tête de
   chaque fichier) ;
3. runner self-hosted disponible, **ou** `local-ci.yml` retiré proprement ;
4. ABI, OpenAPI, tests et CodeQL exécutent réellement leurs steps ;
5. un run complet vert observé, **CodeQL inclus**.

Corollaires des critères historiques, conservés car ils restent exigibles :
aucun check ne doit échouer avant son premier step, aucun check optionnel ne
doit rester `pending` indéfiniment, et la porte obligatoire doit avoir une
autorité explicite.

> **Dette de sécurité ouverte et assumée.** Tant que ce parking dure, **aucune
> analyse CodeQL distante n'est produite**. Ce n'est pas une protection
> prétendument active : c'est une absence, visible et temporaire. La porte
> locale ne remplace pas CodeQL. La condition 5 exige explicitement un run
> CodeQL vert avant clôture.

Aucune de ces conditions n'est atteignable depuis le dépôt. Il faut **une
action humaine de facturation, ou l'enregistrement d'un runner**. Et la
condition 3 exige en plus de lever le blocage sur la protection de branche
(dépôt public **ou** plan GitHub Pro) — voir le corollaire plus haut. Options,
avec leur conséquence réelle :

- **Relever la limite de dépense GitHub Actions** (Settings → Billing →
  Spending limits). Débloque immédiatement les runners hébergés — mais les
  minutes au-delà du quota deviennent **payantes**.
- **Rendre `all3f0r1/reefin` public**. Les minutes Actions redeviennent
  **gratuites et illimitées** — mais **le code devient public**.
- **Réduire la consommation des autres dépôts du compte**, en particulier les
  runs macOS de `youtube-chapter-splitter` (facturés **×10**). Gratuit, mais
  agit seulement au prochain cycle de facturation et ne garantit rien.
- **Enregistrer un runner self-hosted** avec les labels
  `self-hosted, linux, x64, reefin` (voir ci-dessous). Débloque `local-ci.yml`
  **sans consommer une seule minute hébergée**, et donne à la porte locale
  l'autorité explicite exigée par la condition 3. Ne débloque pas les autres
  workflows.

Une fois l'une de ces options en place, restaurer les déclencheurs d'origine
listés dans le tableau ci-dessus (ils sont rappelés en tête de chaque fichier
de workflow).

## Runner self-hosted (optionnel)

`.github/workflows/local-ci.yml` existe déjà et cible
`runs-on: [self-hosted, linux, x64, reefin]`. Aucun runner portant ces labels
n'est enregistré sur ce dépôt (`total_count: 0`), et son déclenchement
automatique a été **retiré** : sans runner, un run automatique ne tombe pas en
échec, il reste `queued` pour toujours et laisse un check *pending* éternel
sur chaque PR — pire qu'un rouge. Il reste déclenchable à la main et prêt à
re-servir.

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

5. Vérifier que le runner apparaît bien en ligne :
   `gh api repos/all3f0r1/reefin/actions/runners` doit renvoyer
   `total_count: 1` (et non `0`) avec les 4 labels attendus.
6. Alors seulement, ré-armer le déclenchement automatique en remettant dans
   `.github/workflows/local-ci.yml` :

   ```yaml
   on:
     pull_request:
     push:
       branches: [master]
     workflow_dispatch:
   ```

L'ordre compte : ré-armer *avant* que le runner soit en ligne recrée
exactement le check `queued` éternel décrit plus haut.

Le workflow appelle `./ci/run.sh` — exactement le même script que celui
documenté ci-dessus, donc pas de divergence entre CI locale manuelle et CI
self-hosted automatique. Depuis #94 la purge `bin/`/`obj/` est faite par
`ci/run.sh` lui-même, donc un runner self-hosted au workspace persistant est
couvert sans step préalable : le script nettoie l'espace de travail hérité de
la run précédente avant de compiler, et refuse de compiler s'il n'y arrive
pas.
