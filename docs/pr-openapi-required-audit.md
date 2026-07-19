# Audit — issue #51 : schémas OpenAPI sans tableau `required`

Statut : **audit et prototype seulement**. Aucune correction large n'est appliquée par ce
document. Le seul code livré est la régression
`tests/Reefin.Server.Integration.Tests/OpenApiRequiredContractTests.cs`, qui **échoue**
volontairement contre le contrat actuel.

Base : `openapi/openapi.json` commité (401 schémas), master `5163e58`. Toutes les mesures
ci-dessous sont produites en générant réellement le document (host-side `dotnet test`,
`OpenApiSpecTests` écrit le spec dans `bin/`), pas estimées. Le dump de référence est
byte-identique au fichier commité, ce qui valide le banc de mesure.

---

## 1. Le symptôme, prouvé

`Reefin.Playback.Decision.VideoCodecCapability` (`src/Reefin.Playback.Decision/VideoCodecCapability.cs`)
est un `record` positionnel :

```csharp
public sealed record VideoCodecCapability(
    string Codec,
    IReadOnlyList<string> Profiles,      // non-nullable, aucune valeur par défaut
    double? MaxLevel,
    int? MaxBitDepth,
    IReadOnlyList<string> VideoRangeTypes, // non-nullable, aucune valeur par défaut
    Resolution? MaxResolution,
    int? MaxBitrate);
```

Le schéma publié `PlaybackDecisionVideoCodecCapability` n'a **aucun** tableau `required` : le
contrat déclare `Profiles` et `VideoRangeTypes` optionnels.

Chaîne d'atteignabilité depuis un corps de requête (calculée par fermeture transitive sur
`$ref`) :

```
POST /Playback/Sessions      CreatePlaybackSessionRequest  -> PlaybackDecisionClientCapabilities
PUT  /Playback/Sessions/{id} ReplacePlaybackSessionRequest -> PlaybackDecisionClientCapabilities
                                 -> PlaybackDecisionDecodeCapabilities
                                 -> PlaybackDecisionVideoCodecCapability
```

Sonde exécutée contre le serveur réel (`POST Playback/Sessions`, `VideoCodecs[0]` omettant les
deux membres — exactement ce que le contrat autorise aujourd'hui) :

```
STATUS=400
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
 "title":"One or more validation errors occurred.","status":400,
 "errors":{"Capabilities.Decode.VideoCodecs[0].Profiles":["The Profiles field is required."],
           "Capabilities.Decode.VideoCodecs[0].VideoRangeTypes":["The VideoRangeTypes field is required."]}}
```

C'est la contrainte serveur réelle, pas une déduction depuis le type.

### Ce que la validation ASP.NET applique réellement

`Reefin.Server/Extensions/ApiServiceCollectionExtensions.cs` (`AddMvc`) ne touche pas à
`MvcOptions.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes`, qui reste donc à sa
valeur par défaut `false`. `<Nullable>enable</Nullable>` est actif pour tout le dépôt
(`Directory.Build.props`). Conséquence, vérifiée par la sonde ci-dessus :

- membre **type référence non-nullable** dont la valeur liée est `null` → `[Required]` implicite
  → 400 avec `errors`. C'est le cas des paramètres de constructeur primaire sans valeur par
  défaut : `System.Text.Json` passe `null` au constructeur quand le membre JSON est absent.
- membre **type valeur** (`bool`, `int`, `enum`) → absent donne le défaut du type, jamais `null`,
  le `[Required]` implicite ne se déclenche pas. Contre-sonde : la même requête omettant
  `SupportsHls`/`SupportsDash` ne produit **aucune** erreur de validation.
- membre **type référence non-nullable avec initialiseur** (`= Array.Empty<Guid>()`,
  affectation dans le constructeur) → absent donne la valeur initialisée, non-`null`, le
  `[Required]` implicite ne se déclenche pas non plus.

Swashbuckle ne voit rien de tout cela : `SupportNonNullableReferenceTypes()` est déjà activé
(ligne 249) et produit correctement `nullable: true/false`, mais il n'émet jamais `required` —
`required` ne sort aujourd'hui que d'un attribut `[Required]` explicite.

---

## 2. Inventaire

### 2.1 Par atteignabilité

| | schémas |
|---|---|
| schémas totaux | 401 |
| schémas objet (`type: object` ou `properties`) | 295 |
| — **sans** tableau `required` | **268** |
| — avec tableau `required` | 27 |

Les 27 exceptions viennent toutes d'un `[Required]` explicite en C# (`CreateUserByName.Name`,
`UploadSubtitleDto.*`, `StartupRemoteAccessDto.EnableRemoteAccess`, …).

L'issue annonce 263 ; le comptage exact ici est **268**. L'écart n'a pas été expliqué (voir §6).

Répartition des 268 :

| catégorie | schémas |
|---|---|
| atteignables depuis un `requestBody` / des `parameters` | **111** |
| atteignables **uniquement** depuis une réponse | 94 |
| atteignables depuis aucune opération (types orphelins) | 63 |

C'est la ligne à 111 qui porte le risque : sur-déclarer `required` dans un schéma de réponse
gêne le client sans le casser côté serveur, alors qu'un schéma de requête faux fait échouer des
requêtes légitimes ou en laisse passer d'illégitimes.

### 2.2 Par comportement de désérialisation

Mesuré en générant trois fois le document complet avec une règle différente, puis en diffant les
tableaux `required` contre la référence. Les trois ensembles sont emboîtés (C ⊂ B ⊂ A, vérifié).

| catégorie de membre | entrées `required` | schémas | req. | rép. seule | orphelins |
|---|---|---|---|---|---|
| **A** — tout membre non-nullable (référence *et* valeur) | 935 | 242 | 441 | 293 | 201 |
| **B** — membres **type référence** non-nullables | 231 | 100 | 91 | 109 | 31 |
| **C** — paramètre de constructeur primaire, type référence non-nullable, **sans** valeur par défaut | **29** | **13** | 18 | 11 | 0 |
| A − B — membres **type valeur** non-nullables (`bool`, `int`, `enum`) | 704 | 213 | 350 | 184 | 170 |
| B − C — type référence non-nullable **avec initialiseur** ou non lié par constructeur | 202 | 87 | 73 | 98 | 31 |

Seul l'ensemble **C** correspond à ce que le serveur exige réellement. A − B et B − C sont des
sur-déclarations : le serveur accepte ces membres absents.

**Collections initialisées par défaut en C#** — sous-ensemble de B − C, exemples réels :

| schéma | membres que B ajoute à tort | C# |
|---|---|---|
| `UserConfiguration` | `GroupedFolders`, `LatestItemsExcludes`, `MyMediaExcludes`, `OrderedViews` | `= Array.Empty<Guid>()` dans le constructeur |
| `LibraryOptions` | 12 membres (`DisabledSubtitleFetchers`, `PathInfos`, `TypeOptions`, …) | initialisés |
| `ServerConfiguration` | 17 membres (`CorsHosts`, `MetadataPath`, `UICulture`, …) | initialisés |

**Booléens et types valeur avec défaut** — sous-ensemble de A − B, exemples réels :

| schéma | membres que A ajoute à tort | comportement réel |
|---|---|---|
| `PlaybackDecisionDecodeCapabilities` | `SupportsDash`, `SupportsHls` | `bool` positionnel, absent → `false`, accepté (contre-sonde) |
| `UserPolicy` | 30 membres dont `EnableAllChannels` | `EnableAllChannels = true` dans le constructeur : rendre le membre requis **changerait le comportement** du client qui ne l'envoyait pas |
| `UserConfiguration` | `SubtitleMode`, `DisplayMissingEpisodes`, … | `enum`/`bool`, absent → défaut, accepté |

**Membres nullables devant néanmoins être présents** : **0 trouvé**. Tous les membres marqués
`nullable: true` de la vocabulaire `Reefin.Playback.Decision` sont des paramètres de constructeur
`T?` sans défaut ; absent et `null` produisent tous deux `null` et sont tous deux acceptés. Voir
§6 : le contrat n'a de toute façon aucun moyen de distinguer les deux.

---

## 3. Approches essayées et mesurées

Le SDK TypeScript de reefin-web est généré par `openapi-generator` : un membre absent de
`required` sort en `'X'?: T`, un membre présent sort en `'X': T`. « nouveaux champs TS
non-optionnels » = nombre d'entrées `required` ajoutées.

### Approche A — `c.NonNullableReferenceTypesAsRequired()`

Le bouton existe bien dans Swashbuckle 10.2.3 (vérifié dans
`Swashbuckle.AspNetCore.SwaggerGen.xml`) et n'est pas appelé aujourd'hui.

- entrées `required` ajoutées : **935**
- modèles SDK modifiés : **242**
- nouveaux champs TS non-optionnels : **935** (441 sur des schémas de requête)
- callsites clients cassés : non chiffré — la portée (`UserPolicy`, `ServerConfiguration`,
  `LibraryOptions`, `BaseItemDto`, `UserConfiguration`) touche la quasi-totalité du dashboard
- désaccord résiduel contrat/serveur : **704 membres type valeur + 202 membres initialisés sont
  déclarés requis alors que le serveur les accepte absents**

Corrige bien la cible (`Codec`, `Profiles`, `VideoRangeTypes`), mais au prix d'un contrat faux
dans l'autre sens sur 906 membres. Malgré son nom, le bouton marque aussi les types valeur.
**Rejetée.**

### Approche B — `ISchemaFilter` limité aux types référence non-nullables

- entrées `required` ajoutées : **231**
- modèles SDK modifiés : **100**
- nouveaux champs TS non-optionnels : **231** (91 sur des schémas de requête)
- callsites clients cassés : non chiffré
- désaccord résiduel : **202 membres à initialiseur déclarés requis à tort** (`ServerConfiguration.UICulture`,
  `UserConfiguration.GroupedFolders`, …)

Corrige la cible et élimine le faux positif des booléens, mais reste faux sur les initialiseurs,
que la réflexion ne peut pas voir. **Rejetée.**

### Approche C — `ISchemaFilter` limité à ce qui est prouvable

Règle : membre lié par un paramètre de constructeur primaire, de type **référence**, annoté
non-nullable, et `!ParameterInfo.HasDefaultValue`. C'est exactement l'ensemble des membres pour
lesquels « absent ⇒ `null` ⇒ `[Required]` implicite ⇒ 400 » est démontrable depuis les
métadonnées. Prototypée en la restreignant au namespace `Reefin.Playback.Decision`.

- entrées `required` ajoutées : **29**
- modèles SDK modifiés : **13** (les 13 existent déjà comme modèles TS générés)
- nouveaux champs TS non-optionnels : **29** (18 sur des schémas de requête, 11 réponse seule)
- callsites clients cassés : **1**
- désaccord résiduel : aucun sur les 13 schémas couverts ; **inchangé sur les 255 autres schémas
  sans `required`** (voir §5)

Détail des 29 entrées :

```
PlaybackDecisionAudioCodecCapability     Codec
PlaybackDecisionAudioStreamSnapshot      Codec
PlaybackDecisionClientCapabilities       Decode, OutputProfiles
PlaybackDecisionDecodeCapabilities       AudioCodecs, DirectPlayProfiles, SubtitleDelivery, VideoCodecs
PlaybackDecisionDecodeProfile            AudioCodecs, Containers, VideoCodecs
PlaybackDecisionMediaSourceSnapshot      AudioStreams, Container, MediaSourceId, Protocol, SubtitleStreams, VideoStreams
PlaybackDecisionPlaybackConstraints      PreferredSubtitleLanguages
PlaybackDecisionPlaybackOutputProfile    AudioCodecs, Container, VideoCodecs
PlaybackDecisionReasonNode               Children, Subject
PlaybackDecisionSubtitleCapability       Format
PlaybackDecisionSubtitleStreamSnapshot   Format
PlaybackDecisionVideoCodecCapability     Codec, Profiles, VideoRangeTypes
PlaybackDecisionVideoStreamSnapshot      Codec
```

`SupportsHls`/`SupportsDash` sont correctement exclus.

### Le callsite client cassé par C

`reefin-web/src/scripts/reefinPlaybackCapabilities.ts:291-293` :

```ts
function videoCodecCapability(codec: string): VideoCodecCapability {
    return { Codec: codec };
}
```

C'est **le bug lui-même**. Le commentaire de tête du fichier (lignes 40-41) documente que les
entrées « laissent `MaxLevel`/`MaxBitDepth`/`VideoRangeTypes`/`MaxResolution`/`MaxBitrate` non
renseignés », ce que le contrat autorisait. Sous C, ce callsite devient une erreur de compilation
TypeScript au lieu d'un 400 à l'exécution : c'est le résultat recherché, pas un dommage
collatéral.

Tous les autres constructeurs vérifiés fournissent déjà les membres concernés :
`decodeProfile()`, `outputProfile()`, `buildDecodeCapabilities()`, `buildClientCapabilities()`,
`buildPlaybackConstraints()`, les littéraux `SubtitleCapability`, et le fixture
`compareClientCapabilities.test.ts`. Les 11 entrées « réponse seule » (snapshots de diagnostic)
ne cassent aucun consommateur : elles rendent des champs lus non-optionnels, jamais construits
côté client hors fixtures déjà complets.

---

## 4. Recommandation

Appliquer **l'approche C**, en deux temps et hors de ce lot :

1. un `ISchemaFilter` implémentant la règle « paramètre de constructeur primaire, type référence
   non-nullable, sans valeur par défaut », d'abord restreint à `Reefin.Playback.Decision` ;
2. le correctif d'appel dans reefin-web (`videoCodecCapability` doit renseigner `Profiles: []` et
   `VideoRangeTypes: []`), à livrer **avant** la régénération du contrat pour ne pas casser le
   build web.

Ne **pas** appliquer A ni B.

### Conditions d'arrêt

- « le changement minimal correct est large ou casse massivement le SDK » — **non déclenchée pour
  C** (29 entrées, 13 modèles, 1 callsite), **déclenchée pour A** (935 entrées, 242 modèles) et
  **déclenchée pour B** (231 entrées, 100 modèles, 202 sur-déclarations).
- « impossible de distinguer absent / null / défaut » — **non déclenchée pour C**, qui ne
  s'applique qu'aux membres où la distinction est démontrable. **Déclenchée pour toute règle
  s'appuyant sur les initialiseurs** : ils ne sont pas dans les métadonnées, donc pour les 202
  membres de B − C on ne peut pas décider depuis le type. Voir aussi §6.
- « corriger exigerait d'affaiblir une contrainte serveur réelle » — **non déclenchée**. C ne
  change aucun comportement serveur ; il aligne le contrat sur une contrainte déjà appliquée.

Conformément au périmètre du lot, **aucune de ces corrections n'est appliquée ici**.

---

## 5. Ce qui reste faux après C

L'approche C ne traite que 13 des 268 schémas (dont 8 atteignables depuis une requête). Les 255
autres restent sans `required`, dont **103 atteignables depuis une requête**. Pour ceux-là, la question « ce membre est-il réellement
exigé ? » ne se décide pas depuis les métadonnées : il faut savoir si la propriété a un
initialiseur. Les traiter demande soit un attribut explicite `[Required]` au cas par cas, soit
une analyse de source. Hors périmètre.

---

## 6. Ce qui n'a pas pu être prouvé

- **L'écart 263 (issue) / 268 (mesuré)**. Le critère de comptage de l'issue est inconnu ; les 268
  sont les schémas ayant `type: object` ou `properties` et pas de `required` dans le fichier
  commité. Non réconcilié.
- **Absent vs `null` vs défaut**. OpenAPI 3.0 ne distingue pas un membre absent d'un membre à
  `null` autrement que par `required` + `nullable`. Aucun membre du dépôt n'a besoin de cette
  distinction (§2.2), mais le contrat ne pourrait pas l'exprimer si le besoin apparaissait.
- **Le nombre de callsites cassés par A et par B** n'a pas été chiffré : cela demanderait un
  typecheck de reefin-web contre un SDK régénéré, donc une écriture dans un dépôt en lecture
  seule pour ce lot. Seul C, assez étroit pour une revue manuelle exhaustive, est chiffré — et ce
  « 1 callsite » vient d'une **inspection manuelle** des 6 fichiers de reefin-web référençant ces
  types, pas d'un `tsc` contre un SDK régénéré.
- **L'approche C n'a été mesurée que restreinte au namespace `Reefin.Playback.Decision`.** La même
  règle « paramètre de constructeur primaire, type référence non-nullable, sans défaut » appliquée
  à tout le document n'a pas été chiffrée ; elle ajouterait nécessairement plus que 29 entrées.
  La recommandation §4 dit « d'abord restreint » : l'élargissement demande sa propre mesure.
- **La contre-sonde booléens** renvoie 400 (« Error processing request. », échec de résolution de
  l'item, `ItemId` inexistant) et non 200. Ce qui est prouvé est l'**absence d'erreur de
  validation** pour `SupportsHls`/`SupportsDash` — MVC renvoyant toutes les erreurs de validation
  d'un coup, comme dans la première sonde qui en listait deux. Le trajet complet jusqu'à 200 n'a
  pas été exécuté.
- **Le spec miroir de reefin-web est en retard sur master** : 395 schémas contre 401, 117 schémas
  divergents, 6 absents (`PlaybackSessionStreamDescriptor`, `PlaybackOperationalMetricsResponse`,
  …). `PlaybackDecisionVideoCodecCapability` y est identique. Cette dérive est antérieure et hors
  périmètre.
- **Docker interdit sur ce lot** : `./ci/run.sh` et `./ci/openapi-generate.sh` n'ont pas été
  exécutés. Le contrat commité n'a pas été régénéré. La suite complète n'a pas été passée ; seuls
  les tests cités ici ont tourné.
