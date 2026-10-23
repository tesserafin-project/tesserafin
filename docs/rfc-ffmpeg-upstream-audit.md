# RFC — Audit de compatibilité FFmpeg upstream (n9.0.2) pour Tesserafin

- **Issue** : #340 (parente #335)
- **Statut** : brouillon. Faits : inventaire mécanique (phase 1), preuve de compilation linux-x64 (§3.6). Restent : inventaire des scénarios, linux-arm64, audit Windows, prototype.
- **Date** : 2026-10-23
- **Nature** : design et preuves uniquement, aucun changement de runtime, de pins ou d'`EncodingHelper`.

Ce document ne tranche pas encore entre les options. Il fixe ce qui est mesuré, ce qui ne l'est pas, et ce qui change déjà la discussion.

## 1. Options comparées

| Option | Description |
|---|---|
| A1 | Rester sur jellyfin-ffmpeg 7.1.4-3 (état actuel) |
| A2 | Passer à jellyfin-ffmpeg 8.1.3-1 |
| B | FFmpeg upstream n9.0.2 sans patch, serveur adapté |
| C | n9.0.2 + correctifs maintenus par Tesserafin (branche de commits rebasée par release) |

FFmpeg 9.1 n'est pas sortie (seul `n9.1-dev` existe). jellyfin-ffmpeg n'a pas de série 9.x : sa branche `jellyfin` est en 8.1.3-2.

## 2. Méthode et limites

**Entrées épinglées**

| Entrée | Commit |
|---|---|
| jellyfin-ffmpeg `v7.1.4-3` (= `components.json`) | `d4590e12452f94d40e413caecb34b08de608353b` |
| jellyfin-ffmpeg `v8.1.3-1` | `253db2a7b0a8045c54ce68ce33d7f601229b1822` |
| FFmpeg `n7.1.4` | `9abe92e3af7fa7becc8f7f742b1457b4c28220a6` |
| FFmpeg `n8.1.3` | `1041abdc962f4cc4f394aa8de9dc5236c0c3b9e7` |
| FFmpeg `n9.0.2` | `946fcce07b6dcd0331c8cc609192aeff5e1924f8` |
| Tesserafin `master` | `19b2d77bd7` |

**Instrument.** Chaque patch de la série est testé seul contre un arbre FFmpeg vierge avec `patch --dry-run -F2`, dans les deux sens. Un hunk est « OK » s'il s'applique, « déjà présent » s'il s'applique à l'envers. La série est ensuite appliquée **en séquence** (fuzz 2, rejets écartés), ce qui reflète un vrai portage. Script en annexe B.

**Témoins.**
- Série 7.1.4-3 appliquée en séquence sur n7.1.4 : 0 échec. Les entrées sont cohérentes.
- Patch 0001 sur n7.1.4 vierge : 3/3 OK, 0 déjà présent. Après application : 0 OK, 3/3 déjà présents. L'instrument distingue les deux états.
- Calibrage du test isolé sur la base d'origine : 8.1.3-1 sur n8.1.3 donne 909/939 hunks OK (97 %), 7.1.4-3 sur n7.1.4 donne 791/805 (98 %). Un patch testé seul échoue donc sur ~2-3 % de hunks à cause de ses dépendances dans la série, sans aucun écart upstream.

**Ce que l'instrument ne prouve pas.**
- Un hunk qui s'applique n'est pas un code qui compile, ni un code correct. La compilation est traitée en §3.6.
- « Déjà présent » peut être une coïncidence de contexte sur de petits hunks. Les cas qui décident sont à relire à la main.
- L'inventaire côté serveur (§3.2) lit les littéraux de chaîne du code non-test. Un nom composé à l'exécution lui échappe. Un seul cas trouvé : `"tonemap_{0}=…"` dans `EncodingHelper.cs`, qui couvre `tonemap_cuda` et `tonemap_opencl`.

## 3. Résultats

### 3.1 Trois FFmpeg différents sont livrés aujourd'hui

| Livraison | Source du binaire | Accélérations compilées |
|---|---|---|
| Docker (amd64/arm64) | `.deb` jellyfin-ffmpeg 7.1.4-3 (`Dockerfile`) | VAAPI, QSV (libvpl), CUDA/NVENC, AMF, OpenCL, Vulkan + libplacebo, RKMPP/RKRGA |
| Natif Linux | `ci/ffmpeg` (`ffmpeg-configure*.txt`) | VAAPI, CUDA/NVENC, OpenCL ; x64 : + libvpl, AMF. Pas de Vulkan ni libplacebo, pas de RKMPP |
| Windows | `ci/windows/ffmpeg` | D3D11VA/D3D12VA/DXVA2, AMF, libvpl, CUDA/NVENC. Pas de VAAPI, OpenCL ni Vulkan |

Conséquences :
- Une capacité validée sous Docker ne l'est pas pour le natif, et inversement. Le contrat de capacités doit être défini par livraison tant que ces binaires diffèrent.
- **Licence, à traiter hors de cet RFC.** Le FFmpeg de l'image Docker est configuré avec `--enable-gpl --enable-libfdk-aac` (constaté par `ffmpeg -buildconf` dans `tesserafin-local:master-8f6ce4e601` et dans l'image `ghcr.io/tesserafin-project/tesserafin-server` `ac038fbe9d86`) et expose l'encodeur `libfdk_aac`. `EncodingHelper.cs:800` le préfère dès qu'il est présent. C'est la combinaison que le patch 0029 rend possible et que `fork-patches.json` classe `unsafe` pour le runtime natif.

### 3.2 Ce que le serveur exige d'absent d'upstream n9.0.2

199 composants FFmpeg (filtres, codecs, BSF) sont nommés par le code serveur. Ceux qui existent chez jellyfin-ffmpeg 8.1.3-1 et pas dans n9.0.2 :

| Composant | Où le serveur le nomme | Patch 8.1.3-1 | Livraison qui le compile aujourd'hui |
|---|---|---|---|
| `tonemapx` | `EncodingHelper`, `MediaEncoder`, `EncoderValidator` | 0053 | Docker, natif Linux, Windows |
| `alphasrc` | `EncodingHelper` (12), `EncoderValidator` | 0023 | toutes |
| `hwupload_vaapi` | `EncodingHelper` (6), `EncoderValidator` | 0014 | Docker, natif Linux |
| `scale_opencl` | `EncodingHelper`, `EncoderValidator` | 0006 | Docker, natif Linux |
| `yadif_opencl`, `bwdif_opencl` | `EncodingHelper`, `EncoderValidator` | 0069 | Docker, natif Linux |
| `tonemap_cuda` | `EncoderValidator`, `"tonemap_{0}"` | 0004 | toutes |
| décodeur `ac4` | `EncoderValidator` | 0054 | toutes |
| options `remove_dovi` / `remove_hdr10plus` de `hevc_metadata` et `av1_metadata` | `EncoderValidator` | 0061 | toutes |
| `*_rkmpp`, `*_rkrga` | `EncodingHelper`, `EncoderValidator` | 0042 | Docker arm64 seulement |
| `*_videotoolbox` (overlay, tonemap, bwdif, encodeur mjpeg) | `EncodingHelper`, `EncoderValidator` | 0044, 0046, 0047, 0056 | aucune (pas de plateforme macOS déclarée) |

Options sondées par `EncoderValidator` et déjà présentes upstream : le framesync (« Action to take when encountering EOF ») d'`overlay_vaapi`, `overlay_opencl`, `overlay_vulkan` et `overlay_cuda`, et `format` de `scale_cuda`. Absentes upstream :
- `alpha_format` d'`overlay_opencl` et `overlay_cuda` (0008, 0031), passé par `EncodingHelper.cs:4227,4451` pour l'incrustation de sous-titres ;
- `bt2390` de `tonemap_opencl` (0007 ; upstream ne le propose que dans `libplacebo`) ;
- le demi-tour de `transpose_opencl` (0051). Upstream l'a pour `transpose_cuda`, `transpose_vaapi`, `transpose_vt` et `vpp_qsv`. `fork-patches.json` classe ce patch `useful` (« le serveur ne le nomme pas »), alors que `EncoderValidator` sonde `TransposeOpenclReversal` : premier exemple de classement à revoir.

Retraits upstream en 9.0 (NPP, OMX, `libcelt`, `sonic`, `v308`/`v408`/`v410`) : le serveur n'en nomme aucun.

**Lecture.** L'option B suppose de remplacer ou supprimer tous les usages de la première partie du tableau. Les chemins VideoToolbox ne sont compilés par aucune livraison, et RKMPP seulement par Docker. Ils peuvent sortir du périmètre sans attendre la décision sur le fork.

### 3.3 La série jellyfin 8.1.3-1 se porte en grande partie sur n9.0.2

| Série → base | Hunks | OK seuls | Patches intacts en séquence | Hunks en échec en séquence |
|---|---:|---:|---:|---:|
| 7.1.4-3 → n9.0.2 | 805 | 548 (68 %) | — | — |
| 8.1.3-1 → n9.0.2 | 939 | 811 (86 %) | 78 / 100 | 108 |
| 8.1.3-1 → n8.1.3 (témoin) | 939 | 909 (97 %) | — | — |

Sur les 22 patches non intacts :
- **Absorbés upstream** (tous les hunks déjà présents) : 0016 (NVDEC > 32 surfaces), 0084 (détection AC-4 en MPEG-TS), 0090 (`sw_pix_fmt` dans codecpar).
- **Dérive de contexte de 1 à 3 hunks** : 0002, 0003, 0004, 0006, 0022 (sub2video), 0027 (side data DoVi dans hlsenc/mpegtsenc), 0042, 0044, 0045, 0055, 0061 (BSF DoVi/HDR10+), 0066, 0092, 0094. Un portage manuel court est probable, à confirmer à la compilation.
- **Portage réel** : 0005 AMF (19 hunks en échec, dont 13 déjà présents), 0078 backports Vulkan (35, dont 29 déjà présents), 0082 filtres Vulkan (13), 0083 backports CUDA (10, dont 8 déjà présents).
- **Remplacé upstream** : 0050 `transpose_cuda` (5 hunks en échec) crée un filtre qu'upstream fournit désormais (`vf_transpose_cuda.c`, demi-tour compris). À comparer, pas à porter.

Ces chiffres sont à lire avec §2 : environ 3 % des échecs viennent de l'instrument lui-même.

Tableau complet en annexe A. L'arbre porté est conservé pour la compilation (§6).

### 3.4 Patches « required » abandonnés par Jellyfin en 8.1

21 patches de 7.1.4-3 n'existent plus dans 8.1.3-1. Pour ceux classés `required`, état dans n9.0.2 :

| Patch 7.1.4-3 | Hunks déjà présents dans n9.0.2 | Lecture |
|---|---|---|
| 0017 backports VAAPI | 7 / 7 | absorbé |
| 0045 fMP4 x265 sur Safari | 1 / 1 | absorbé |
| 0065 yuv420p → p01x | 12 / 14 | absorbé en grande partie |
| 0093 écarts n7.1.4 / release/7.1 | 9 / 12 | absorbé en grande partie |
| 0078 race CSC VAAPI | 3 / 4 | absorbé en grande partie |
| 0087 padding RPU DoVi | 4 / 5 | absorbé en grande partie |
| 0088 retrait de l'EL au strip DoVi | 1 / 2 | partiel, à relire |
| 0015 `alpha_format` d'`overlay_vaapi` | 0 / 4 | **absent**. Remplacé en 8.1 par 0015 « check alpha mode for vaapi overlay ». Le serveur ne passe `alpha_format` qu'à `overlay_cuda` et `overlay_opencl` : la justification `required` est à revoir |
| 0023 décodage HEVC HDR dans le BSF | 0 / 3 | **absent**, à vérifier |
| 0027 encodage HEVC VAAPI sur Tiger Lake | 0 / 1 | **absent**, à vérifier |
| 0089 codecpar quand le décodeur active des BSF | 0 / 1 | **absent**, à vérifier |

Les lignes « absent » sont soit devenues inutiles avec le code upstream 8.x, soit perdues par Jellyfin. Chacune doit être rattachée à un scénario (§6) avant une décision.

### 3.5 Dépendances de build : n9.0.2 exige le SDK AMF ≥ 1.5.2

Premier essai de compilation de l'arbre porté (§6) dans le builder `ci/ffmpeg`, configuration linux-x64 de F0 : le configure s'arrête sur `ERROR: amf requested but not found`. `ffbuild/config.log` montre la condition qui échoue :

```
check_cpp_condition amf AMF/core/Version.h (AMF_VERSION_MAJOR << 48 | AMF_VERSION_MINOR << 32 | AMF_VERSION_RELEASE << 16 | AMF_VERSION_BUILD_NUM) >= 0x1000500020000
```

Version minimale du SDK AMF exigée par le `configure` FFmpeg :

| FFmpeg | Minimum AMF |
|---|---|
| n7.1.4 | 1.4.33 |
| n8.1.3 | 1.4.36 (la série jellyfin 8.1.3-1 le relève à 1.5.0, patch 0005) |
| n9.0.2 | **1.5.2** |

`components.json` épingle `amf-headers` en **v1.5.0** (`afed28d37aca1938da2eedc50599bb3535a987ec`), choisi comme minimum pour la série 7.1.4-3 (v1.4.35 n'a pas `AMF_VIDEO_ENCODER_AV1_B_PIC_PATTERN`). Les options B et C imposent donc de monter ce pin, au moins en v1.5.2 (`eadd00804d5f7e5cd8c85d540073198312870776`) ; la dernière étiquette est v1.5.3 (`57ee0cfb0e1a08d9041f77435653794c1b012be0`).

Conséquences :
- Monter le pin touche la chaîne F0 (`components.json`, empreintes acceptées, SBOM, reproductibilité) sur linux-x64 et win-x64, les deux plateformes qui compilent AMF. linux-arm64 n'est pas concernée (AMF n'y est pas compilé).
- Les en-têtes ne suffisent pas : le pilote AMD installé doit fournir un runtime AMF compatible. Aucun banc ne le vérifie (§4.2).
- C'est le premier coût de dépendance mesuré du passage en 9.x. D'autres peuvent apparaître une fois le configure passé.

La suite de l'essai (§3.6) substitue les en-têtes AMF v1.5.3 dans une copie du cache, sans toucher au pin du dépôt.

### 3.6 Preuve de compilation linux-x64

**Montage.** Le vrai pipeline F0 (`fetch-sources.sh`, dépendances épinglées, image `ci/ffmpeg/builder` figée, `ffmpeg-configure.txt` + `ffmpeg-configure.linux-x64.txt`, `--network none`), avec une seule substitution : la source FFmpeg est l'arbre porté, placé dans une copie du cache à la place de `git/jellyfin-ffmpeg`. Le dépôt n'est pas modifié. Les contrôles du builder s'exécutent sur l'arbre porté : `tonemapx` et `alphasrc` présents, `libfdk_aac` toujours dans la liste nonfree. Le patch fdk-aac de la série 8.1 (0026) est retiré, conformément à F0. Build incrémental de développement : il ne vaut pas preuve de reproductibilité.

**Référence.** Le F0 actuel (jellyfin-ffmpeg 7.1.4-3) est reconstruit avec les mêmes dépendances. Écart à noter : il utilise lui aussi les en-têtes AMF v1.5.3 au lieu du pin v1.5.0.

**Résultat.** Le build aboutit et produit `ffmpeg version 9.0.2-tesserafin.1`, configuré `--disable-libfdk-aac`, sans encodeur `libfdk_aac`, avec pour seules bibliothèques partagées non système `libva` et `libva-drm`, comme F0. Il a fallu :

| Correction manuelle | Patch d'origine | Cause |
|---|---|---|
| 7 lignes `*_deps` de `configure` (`tonemap_cuda` ×2, `scale_opencl`, `tonemap_d3d11`, `overlay_videotoolbox`, décodeur `mpeg4_rkmpp` ×2) | 0004, 0006, 0042, 0044, 0094 | Hunks rejetés. Sans ces lignes, le composant s'active sans condition : `tonemap_d3d11` cassait la compilation Linux (`windows.h`) |
| 4 enregistrements (`ff_vf_scale_opencl` et son `OBJS-`, `OBJS-` de `mpeg4_rkmpp`, `ff_vf_transpose_d3d11`) | 0006, 0042, 0092 | Hunks rejetés. Sans erreur de compilation : `scale_opencl` manquait silencieusement au binaire jusqu'au contrôle de contrat |
| Doublon de `saturate_rintf` retiré de `cuda/vector_helpers.cuh` | 0003 | Upstream 9.0 a intégré l'helper de Jellyfin ; le patch l'ajoutait une seconde fois |
| `#include "libavcodec/itut35.h"` dans `bsf/av1_metadata.c` et `bsf/h265_metadata.c` | 0061 | Hunk rejeté |
| `amfenc_av1.c` : `ff_amf_pix_fmts` → `ff_amf_pix_fmts_10b_8b` | 0005 | Upstream a une liste unique, Jellyfin l'a scindée ; le hunk AV1 n'est pas passé |

Diff total : 8 fichiers, +14 / −24 lignes (annexe C).

**Contrat.** L'inventaire de §3.2 relève 202 noms de composants dans le code serveur (filtres, codecs, BSF ; quelques faux positifs, comme `ulti`, fragment d'une regex de `VideoListResolver`). Comparaison du binaire porté au F0 de référence :
- **Aucun composant nommé par le serveur n'est perdu.** Ceux qui manquent aux deux binaires relèvent de backends que F0 ne compile pas (RKMPP, VideoToolbox, Vulkan, libplacebo) ou d'exclusions voulues (`libfdk_aac`, `aac_at`).
- Les 14 sondes d'option d'`EncoderValidator` (§3.2), rejouées par `ffmpeg -h filter=…` / `-h bsf=…`, donnent des résultats **identiques** : 13 positives, plus `overlay_vulkan` négative sur les deux binaires (Vulkan non compilé). Cette réponse négative montre aussi que la sonde sait dire non.
- Pertes, toutes dues à des retraits upstream et non nommées par le serveur : codecs `sonic`, `sonicls`, `v308`, `v408`, `v410` et filtre `pp` (retrait de libpostproc en FFmpeg 8).
- Gains : notamment `vpp_amf`, `sr_amf`, `vp9_amf`, `pad_cuda`, BSF `dovi_split`.
- `EncoderValidator` lit la version (`^ffmpeg version n?((?:[0-9]+\.?)+)` donne 9.0.2). Sa table `_ffmpegMinimumLibraryVersions` cite encore `libpostproc`, qui n'existe plus. Elle ne sert que si la version est illisible, mais il faudra la nettoyer.

**Exécution (pas une preuve de fidélité).** Sur le VAAPI AMD de l'hôte (`/dev/dri/renderD128`) : `h264_vaapi` via `hwupload_vaapi` et `hevc_vaapi` en HLS fMP4 fonctionnent. En logiciel, `tonemapx` et `alphasrc` + `overlay` fonctionnent avec la syntaxe d'`EncodingHelper`. Mes premières commandes échouaient de la même façon sur la référence 7.1.4 : l'erreur venait de la ligne de commande, pas du portage.

**Écart `tonemapx`.** Sur une même source, la sortie brute de `tonemapx` (BT.2390, sans encodeur) diffère entre 9.0.2 et la référence 7.1.4 : PSNR 38,3 dB, SSIM 0,997. Source et conversion 10 bits sont identiques au bit près. Le code `tonemapx` de l'arbre porté est identique octet pour octet à celui de jellyfin-ffmpeg 8.1.3-1, et le patch `tonemapx` a changé entre 7.1.4-3 et 8.1.3-1. L'écart vient donc vraisemblablement de la série 8.1, et toucherait aussi l'option A2, plutôt que du passage en 9.0. Ce n'est pas prouvé au pixel : aucun binaire jellyfin 8.1.3 n'a été construit. Ce point relève du prototype HDR10 → SDR (§6.4), avec une référence externe.

**Ce que la compilation n'établit pas** : la correction des chemins CUDA, QSV et AMF (pas de banc), la fidélité des couleurs, le comportement Dolby Vision, linux-arm64 (le builder exige un hôte arm64 natif) et win-x64.

**Leçon pour tout portage de la série.** Un test hunk par hunk ne suffit pas. Il faut aussi contrôler que chaque ligne `configure` et chaque enregistrement (`allfilters.c`, `allcodecs.c`, `Makefile`) ajoutés par la série sont présents, puis comparer `-filters`, `-encoders`, `-decoders` et `-bsfs` à une référence. La perte de `scale_opencl` n'a été détectée qu'à ce dernier contrôle.

## 4. Ce qui change déjà la discussion

1. **Le coût de l'option C est plus bas que redouté** si on part de la série 8.1.3-1 et non de 7.1.4-3 : 78 patches sur 100 passent tels quels sur n9.0.2, et l'arbre porté compile en linux-x64 avec 8 fichiers retouchés (+14 / −24 lignes), sans perte sur le contrat serveur (§3.6). Reste à prouver : comportement, autres plateformes, et coût des rebases futurs.
2. **Le périmètre des backends décide de la taille du fork.** Les patches lourds à porter correspondent à des backends (AMF, Vulkan, CUDA) qu'aucun banc ne valide aujourd'hui. Seul AMD VAAPI est testable.
3. **Les écarts entre livraisons sont un problème indépendant du fork.** Docker et natif n'exposent pas les mêmes capacités, et l'image Docker embarque `libfdk_aac` avec `--enable-gpl`.

## 5. Questions ouvertes

- Seuil chiffré de fidélité des couleurs (ΔE/SSIM) et profils DV testés (5, 7, 8.1 proposés). À fixer avant les mesures.
- Périmètre des backends par plateforme (§4.2).
- Traitement de la question `libfdk_aac` de l'image Docker (§3.1), à porter dans une issue dédiée.

## 6. Étapes suivantes

1. **Compilation.** linux-x64 : faite (§3.6). Restent linux-arm64, qui demande un hôte arm64 natif (le builder refuse la cross-compilation et l'émulation), et win-x64 (étape 3).
2. **Inventaire des scénarios** : rattacher chaque patch conservé à un scénario de l'issue (« quel scénario échoue sans lui ? »), dépendances comprises.
3. **Audit Windows séparé**, puisque `win-x64` applique toute la série.
4. **Prototype** des cinq cas de l'issue, avec les critères chiffrés une fois fixés.

## Annexe A — Série 8.1.3-1 sur n9.0.2, patch par patch

« Seul » : patch testé isolément sur n9.0.2 vierge. « Séquence » : hunks en échec quand la série est appliquée dans l'ordre. Classement 7.1.4-3 : décision actuelle de `fork-patches.json` (numéro 7.1.4-3 entre parenthèses), rapprochée par le nom du patch.

| Patch (8.1.3-1) | Hunks | Seul sur n9.0.2 : OK / déjà présent | Échecs en séquence | Statut mécanique | Classement 7.1.4-3 | Composant nommé par le serveur |
|---|---:|---|---:|---|---|---|
| `0001-add-fixes-for-segment-muxer` | 3 | 3 / 0 | 0 | intact | required (0001) |  |
| `0002-update-cuda-func-header` | 5 | 4 / 0 | 1 | à porter | required (0002) |  |
| `0003-add-enhanced-cuda-pixfmt-converter-impl` | 50 | 49 / 6 | 1 | absorbé en partie | required (0003) |  |
| `0004-add-cuda-tonemap-impl` | 22 | 21 / 0 | 1 | à porter | required (0004) | tonemap_cuda |
| `0005-add-amf-fixes-from-upstream-and-custom-tunings` | 34 | 15 / 13 | 19 | à porter | required (0005) |  |
| `0006-add-opencl-scaler-and-pixfmt-converter-impl` | 6 | 3 / 0 | 3 | à porter | required (0006) | scale_opencl |
| `0007-add-bt2390-eetf-and-code-refactor-to-opencl-tonemap` | 28 | 28 / 1 | 0 | intact | required (0007) |  |
| `0008-add-pgs-support-to-opencl-overlay` | 16 | 16 / 0 | 0 | intact | required (0008) | alpha_format |
| `0009-add-d3d11-opencl-interop-for-amd` | 17 | 17 / 1 | 0 | intact | irrelevant (0009) |  |
| `0010-add-a-hack-for-opencl-reverse-mapping` | 13 | 13 / 0 | 0 | intact | required (0010) |  |
| `0011-add-fixes-for-mapping-from-qsv-source-device` | 11 | 11 / 1 | 0 | intact | required (0011) |  |
| `0012-add-d3d11-opencl-interop-for-qsv` | 6 | 5 / 0 | 0 | intact | irrelevant (0012) |  |
| `0013-add-vendor-id-option-for-d3d11` | 1 | 1 / 0 | 0 | intact | irrelevant (0013) |  |
| `0014-add-vaapi-hwupload-filter` | 4 | 4 / 0 | 0 | intact | required (0014) | hwupload_vaapi |
| `0015-check-alpha-mode-for-vaapi-overlay-filter` | 4 | 4 / 1 | 0 | intact | nouveau en 8.1 |  |
| `0016-add-fixes-for-nvdec-exceed-32-surfaces-error` | 3 | 0 / 3 | 3 | absorbé | required (0016) |  |
| `0017-backport-fixes-for-qsv-from-upstream` | 5 | 5 / 0 | 0 | intact | required (0018) |  |
| `0018-backport-fixes-for-qsv-from-cartwheel` | 16 | 16 / 0 | 0 | intact | required (0019) |  |
| `0019-add-fixes-for-qsv-vpp-filters` | 24 | 24 / 3 | 0 | intact | required (0020) |  |
| `0020-add-external-frame-support-for-qsv-enc` | 6 | 6 / 0 | 0 | intact | required (0021) |  |
| `0021-suppress-overlay-warning-logs` | 1 | 1 / 0 | 0 | intact | useful (0022) |  |
| `0022-add-sub2video-option-to-subtitles-filter` | 20 | 19 / 0 | 1 | à porter | required (0024) |  |
| `0023-add-alphasrc-source-video-filter` | 3 | 3 / 0 | 0 | intact | required (0025) | alphasrc |
| `0024-increase-the-default-async-depth-for-vaapi-encode` | 1 | 1 / 0 | 0 | intact | useful (0026) |  |
| `0025-add-multiple-values-tags-and-webp-support-for-id3v2` | 1 | 1 / 0 | 0 | intact | useful (0028) |  |
| `0026-remove-fdk-aac-from-nonfree` | 2 | 2 / 0 | 0 | intact | unsafe (0029) |  |
| `0027-pass-dovi-sidedata-to-hlsenc-and-mpegtsenc` | 12 | 11 / 0 | 1 | à porter | required (0030) |  |
| `0028-add-pause-support-for-ffmpeg-cli` | 9 | 9 / 0 | 0 | intact | irrelevant (0031) |  |
| `0029-enable-gcc-vectorization-and-fix-cpuflags` | 5 | 5 / 0 | 0 | intact | useful (0032) |  |
| `0030-tune-dxva-align-for-intel-to-avoid-copy-on-qsv` | 5 | 5 / 0 | 0 | intact | irrelevant (0033) |  |
| `0031-add-10bit-support-for-cuda-overlay` | 14 | 14 / 1 | 0 | intact | required (0034) | alpha_format |
| `0032-add-hdr-metadata-for-nvenc-hevc-encoder` | 2 | 2 / 0 | 0 | intact | required (0035) |  |
| `0033-add-d3d11va-dxva2-hevc-422-444-decoding-on-intel` | 21 | 21 / 0 | 0 | intact | irrelevant (0036) |  |
| `0034-default-vaapi-scaler-algorithm-to-fast` | 1 | 1 / 0 | 0 | intact | useful (0037) |  |
| `0035-dont-fail-on-vaapi-to-drm-export-vasync` | 1 | 1 / 0 | 0 | intact | required (0038) |  |
| `0036-skip-loading-plugins-on-vpl-runtime` | 1 | 1 / 0 | 0 | intact | required (0039) |  |
| `0037-tune-the-default-bf-for-qsv-enc` | 3 | 3 / 0 | 0 | intact | useful (0040) |  |
| `0038-allow-vaapi-import-drm-prime2-planar-formats` | 5 | 5 / 1 | 0 | intact | required (0041) |  |
| `0039-add-vui-info-to-the-seq-header-of-hevc-vaapi-encoder` | 2 | 2 / 0 | 0 | intact | required (0042) |  |
| `0040-sync-intel-d3d11va-textures-before-mapping-to-opencl` | 13 | 11 / 0 | 0 | intact | irrelevant (0043) |  |
| `0041-add-icon-for-windows-version-ffmpeg` | 2 | 2 / 0 | 0 | intact | irrelevant (0044) |  |
| `0042-add-full-hwa-pipeline-for-rockchip-rk3588-platform` | 80 | 76 / 0 | 3 | à porter | irrelevant (0046) | av1_rkmpp, mjpeg_rkmpp, mpeg1_rkmpp, mpeg2_rkmpp, mpeg4_rkmpp, overlay_rkrga, scale_rkrga, vpp_rkrga |
| `0043-add-format-option-to-vt-scale-filter` | 4 | 4 / 0 | 0 | intact | irrelevant (0047) |  |
| `0044-add-vf-overlay-videotoolbox-filter` | 7 | 6 / 0 | 1 | à porter | irrelevant (0048) | overlay_videotoolbox |
| `0045-add-coreimage-based-vf-transpose-vt-filter` | 3 | 2 / 0 | 1 | à porter | irrelevant (0049) |  |
| `0046-add-vf-tonemap-videotoolbox-filter` | 5 | 5 / 0 | 0 | intact | irrelevant (0050) | tonemap_videotoolbox |
| `0047-add-mjpeg-videotoolbox-encoder` | 9 | 9 / 0 | 0 | intact | irrelevant (0051) | mjpeg_videotoolbox |
| `0048-add-vt-low-priority-keyframe-decoding` | 3 | 3 / 0 | 0 | intact | irrelevant (0052) |  |
| `0049-add-dummy-device-derive-to-videotoolbox-hwcontext` | 2 | 2 / 0 | 0 | intact | irrelevant (0053) |  |
| `0050-add-cuda-transpose-filter-impl` | 5 | 0 / 1 | 5 | remplacé upstream | useful (0054) |  |
| `0051-add-flip-feat-to-opencl-transpose-filter` | 5 | 5 / 0 | 0 | intact | useful (0055) |  |
| `0052-opus-allow-5point1-side-inputs` | 1 | 1 / 0 | 0 | intact | required (0056) |  |
| `0053-add-simd-optimized-tonemapx-filter` | 20 | 18 / 0 | 0 | intact | required (0057) | tonemapx |
| `0054-add-ac4-decoder-for-atsc-3-0` | 17 | 17 / 1 | 0 | intact | required (0058) | ac4 |
| `0055-allow-vt-sw-decoder-for-every-codec` | 1 | 0 / 0 | 1 | à porter | irrelevant (0066) |  |
| `0056-add-bwdif-videotoolbox-filter` | 10 | 10 / 0 | 0 | intact | irrelevant (0067) | bwdif_videotoolbox |
| `0057-add-12bit-decoding-on-videotoolbox` | 2 | 2 / 0 | 0 | intact | irrelevant (0068) |  |
| `0058-fix-the-sub2video-perf-regressions` | 2 | 2 / 0 | 0 | intact | required (0069) |  |
| `0059-allow-vpl-qsv-to-init-with-the-legacy-msdk-path` | 14 | 14 / 0 | 0 | intact | required (0070) |  |
| `0060-alway-set-videotoolboxenc-pixel-buffer-info` | 2 | 2 / 0 | 0 | intact | irrelevant (0071) |  |
| `0061-add-remove-dovi-hdr10plus-bsf` | 17 | 15 / 0 | 2 | à porter | required (0072) | remove_dovi/remove_hdr10plus |
| `0062-fix-atenc-layout-samplerate` | 4 | 4 / 0 | 0 | intact | irrelevant (0073) |  |
| `0063-videotoolbox-remove-opengl-compatability` | 1 | 1 / 0 | 0 | intact | irrelevant (0074) |  |
| `0064-use-dynamic-pool-for-vpl-qsv-hwupload` | 3 | 3 / 0 | 0 | intact | required (0075) |  |
| `0065-fix-ass-incorrect-null-copy` | 1 | 1 / 0 | 0 | intact | required (0077) |  |
| `0066-add-first-vframe-only-to-ffprobe` | 8 | 7 / 0 | 1 | à porter | useful (0079) |  |
| `0067-fix-auto-inserting-sw-color-conv-filters-between-hw-fmts` | 1 | 1 / 0 | 0 | intact | required (0080) |  |
| `0068-fix-dummy-hw-frame-missing-hw-frames-ctx` | 1 | 1 / 0 | 0 | intact | required (0081) |  |
| `0069-add-yadif-and-bwdif-opencl-filter-impl` | 18 | 15 / 0 | 0 | intact | required (0082) | bwdif_opencl, yadif_opencl |
| `0070-allow-vpp-tonemap-without-hdr10-metadata` | 8 | 8 / 1 | 0 | intact | required (0083) |  |
| `0071-allow-qsvenc-to-bail-out-from-unsupported-low-power-mode` | 1 | 1 / 0 | 0 | intact | required (0084) |  |
| `0072-add-array-of-textures-support-for-d3d11va-hwaccel` | 11 | 8 / 0 | 0 | intact | irrelevant (0086) |  |
| `0073-dont-return-ext-error-for-invalid-vt-frame` | 1 | 1 / 0 | 0 | intact | irrelevant (0090) |  |
| `0074-fix-mapped-hwframe-to-swframe-swscale-conversion` | 2 | 2 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0075-fix-hwupload-filter-cannot-use-devices-created-by-hwaccel` | 3 | 3 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0076-fix-seeking-h264-open-gop-videos-with-d3d11va-on-amd` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0077-suppress-the-resumed-reading-at-pts-with-rate-warning` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0078-backport-fixes-for-vulkan-from-upstream` | 67 | 32 / 29 | 35 | à porter | useful (0060) |  |
| `0079-add-fixes-for-vaapi-drm-prime-vulkan-interop` | 37 | 37 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0080-prefer-vulkan-device-with-higher-api-version` | 2 | 2 / 0 | 0 | intact | useful (0063) |  |
| `0081-signal-vulkan-semaphores-in-drm-prime-reverse-mapping` | 4 | 4 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0082-add-fixes-and-enhancements-for-vulkan-filters` | 23 | 10 / 0 | 13 | à porter | nouveau en 8.1 |  |
| `0083-backport-fixes-for-cuda-from-upstream` | 19 | 9 / 8 | 10 | à porter | nouveau en 8.1 |  |
| `0084-backport-fixes-for-detecting-ac4-streams-in-mpegts` | 3 | 0 / 3 | 3 | absorbé | nouveau en 8.1 |  |
| `0085-fix-hdr10-sidedata-not-removed-in-libplacebo-outlink` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0086-fix-amd-hevc-vaapi-encoder-extradata-with-overrides` | 7 | 7 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0087-add-dtsx-detect-for-dts-hd-hra` | 8 | 8 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0088-add-webp-to-matroskadec-image-mime-types` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0089-relax-to-allow-safe-filenames-in-mkv-attachments` | 3 | 3 / 0 | 0 | intact | required (0095) |  |
| `0090-backport-a-fix-to-use-sw-pix-fmt-in-codec-par-if-set` | 1 | 0 / 1 | 1 | absorbé | nouveau en 8.1 |  |
| `0091-add-d3d11-deinterlace-filter` | 13 | 13 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0092-add-d3d11-video-processor-overlay-transpose-filters` | 12 | 9 / 0 | 1 | à porter | nouveau en 8.1 |  |
| `0093-fix-d3d11-scale-visible-source-rect` | 2 | 2 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0094-add-d3d11-tonemap-filter` | 7 | 3 / 0 | 1 | à porter | nouveau en 8.1 |  |
| `0095-add-scale-d3d11-p010-shader-fallback` | 16 | 13 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0096-fix-full-range-input-for-mjpeg-vaapi-encoder` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0097-add-misc-enhancements-to-mfenc-hw-encoder` | 30 | 30 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0098-backport-a-fix-to-not-stall-sub2video-on-stream-eof` | 2 | 2 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0099-fix-qsv-av1-decoder-exporting-hdr-side-data` | 4 | 4 / 1 | 0 | intact | nouveau en 8.1 |  |
| `0100-backport-trim-bitstream-filter` | 4 | 4 / 0 | 0 | intact | nouveau en 8.1 |  |

## Annexe B — Instrument

```bash
#!/usr/bin/env bash
# hunks.sh <tree> <patchdir> <out.tsv>
# Per-patch hunk status of each patch of <patchdir>/series against a pristine <tree>,
# each patch tested alone: ok (applies forward, fuzz allowed), rev (already present),
# else neither. Output: patch, hunks, ok, rev, files.
set -eu  # no pipefail: grep finding nothing is a normal count of 0
tree=$1 dir=$2 out=$3
: > "$out"
cd "$tree"
grep -v '^#' "$dir/series" | while read -r p; do
  [ -z "$p" ] && continue
  total=$(grep -c '^@@ ' "$dir/$p" || true)
  fwd=$(patch -p1 -N --dry-run -F2 < "$dir/$p" 2>&1 || true)
  revo=$(patch -p1 -R -f --dry-run -F2 < "$dir/$p" 2>&1 || true)
  # forward: a hunk is ok unless FAILED/ignored; a new file that already exists counts as failed
  # patch prints "N out of M hunk(s) FAILED|ignored" per file; sum N
  ffail=$(grep -oE '^[0-9]+ out of [0-9]+ hunks? (FAILED|ignored)' <<<"$fwd" | awk '{s+=$1} END{print s+0}')
  rfail=$(grep -oE '^[0-9]+ out of [0-9]+ hunks? (FAILED|ignored)' <<<"$revo" | awk '{s+=$1} END{print s+0}')
  ok=$(( total - ffail < 0 ? 0 : total - ffail ))
  rev=$(( total - rfail < 0 ? 0 : total - rfail ))
  files=$(sed -nE 's#^\+\+\+ [^/]+/([^[:space:]]+).*#\1#p' "$dir/$p" | sort -u | paste -sd, -)
  printf '%s\t%s\t%s\t%s\t%s\n' "$p" "$total" "$ok" "$rev" "$files" >> "$out"
done
```

Usage : `hunks.sh <arbre FFmpeg vierge> <dossier debian/patches> <sortie.tsv>`. Les dossiers de patches sont extraits par `git archive <tag> debian/patches` du dépôt jellyfin-ffmpeg.

## Annexe C — Corrections manuelles du portage (§3.6)

Appliquées après la série 8.1.3-1 en séquence (fuzz 2, rejets écartés) sur n9.0.2, puis le retrait de 0026. L'arbre porté vit dans le rig d'audit, il n'est pas publié.

```diff
diff --git a/configure b/configure
index e46cd5a8bf..5ef591f65a 100755
--- a/configure
+++ b/configure
@@ -3744,6 +3744,8 @@ vp9_mediacodec_decoder_deps="mediacodec"
 vp9_mediacodec_encoder_deps="mediacodec"
 vp9_qsv_decoder_select="qsvdec"
 vp9_rkmpp_decoder_deps="rkmpp"
+mpeg4_rkmpp_decoder_deps="rkmpp"
+mpeg4_rkmpp_decoder_select="mpeg4_unpack_bframes_bsf dump_extradata_bsf"
 vp9_vaapi_encoder_deps="VAEncPictureParameterBufferVP9"
 vp9_vaapi_encoder_select="vaapi_encode"
 vp9_qsv_encoder_deps="libmfx MFX_CODEC_VP9"
@@ -4315,6 +4317,11 @@ tonemapx_filter_deps="const_nan"
 tonemap_vaapi_filter_deps="vaapi VAProcFilterParameterBufferHDRToneMapping"
 tonemap_videotoolbox_filter_deps="metal corevideo videotoolbox const_nan"
 tonemap_opencl_filter_deps="opencl const_nan"
+tonemap_cuda_filter_deps="ffnvcodec const_nan"
+tonemap_cuda_filter_deps_any="cuda_nvcc cuda_llvm"
+scale_opencl_filter_deps="opencl const_nan"
+tonemap_d3d11_filter_deps="d3d11va const_nan d3dcompiler_h"
+overlay_videotoolbox_filter_deps="metal corevideo coreimage videotoolbox"
 transpose_opencl_filter_deps="opencl"
 transpose_vaapi_filter_deps="vaapi VAProcPipelineCaps_rotation_flags"
 transpose_vt_filter_deps="videotoolbox VTPixelRotationSessionCreate"
diff --git a/libavcodec/Makefile b/libavcodec/Makefile
index 525c560843..1255d91f24 100644
--- a/libavcodec/Makefile
+++ b/libavcodec/Makefile
@@ -1099,6 +1099,7 @@ OBJS-$(CONFIG_MPEG4_NVDEC_HWACCEL)        += nvdec_mpeg4.o
 OBJS-$(CONFIG_MPEG4_VAAPI_HWACCEL)        += vaapi_mpeg4.o
 OBJS-$(CONFIG_MPEG4_VDPAU_HWACCEL)        += vdpau_mpeg4.o
 OBJS-$(CONFIG_MPEG4_VIDEOTOOLBOX_HWACCEL) += videotoolbox.o
+OBJS-$(CONFIG_MPEG4_RKMPP_DECODER)     += rkmppdec.o
 OBJS-$(CONFIG_VC1_D3D11VA_HWACCEL)        += dxva2_vc1.o
 OBJS-$(CONFIG_VC1_DXVA2_HWACCEL)          += dxva2_vc1.o
 OBJS-$(CONFIG_VC1_D3D12VA_HWACCEL)        += dxva2_vc1.o d3d12va_vc1.o
diff --git a/libavcodec/amfenc_av1.c b/libavcodec/amfenc_av1.c
index 47326a1499..91da4b2178 100644
--- a/libavcodec/amfenc_av1.c
+++ b/libavcodec/amfenc_av1.c
@@ -751,7 +751,7 @@ const FFCodec ff_av1_amf_encoder = {
     .p.capabilities   = AV_CODEC_CAP_DELAY | AV_CODEC_CAP_HARDWARE |
                       AV_CODEC_CAP_DR1,
     .caps_internal  = FF_CODEC_CAP_INIT_CLEANUP,
-    CODEC_PIXFMTS_ARRAY(ff_amf_pix_fmts),
+    CODEC_PIXFMTS_ARRAY(ff_amf_pix_fmts_10b_8b),
     .color_ranges   = AVCOL_RANGE_MPEG | AVCOL_RANGE_JPEG,
     .p.wrapper_name   = "amf",
     .hw_configs     = ff_amfenc_hw_configs,
diff --git a/libavcodec/bsf/av1_metadata.c b/libavcodec/bsf/av1_metadata.c
index 22274b2988..9fd8333792 100644
--- a/libavcodec/bsf/av1_metadata.c
+++ b/libavcodec/bsf/av1_metadata.c
@@ -24,6 +24,7 @@
 #include "libavcodec/bsf_internal.h"
 #include "libavcodec/cbs.h"
 #include "libavcodec/cbs_bsf.h"
+#include "libavcodec/itut35.h"
 #include "libavcodec/cbs_av1.h"
 
 typedef struct AV1MetadataContext {
diff --git a/libavcodec/bsf/h265_metadata.c b/libavcodec/bsf/h265_metadata.c
index 9590097e72..08887a6c0b 100644
--- a/libavcodec/bsf/h265_metadata.c
+++ b/libavcodec/bsf/h265_metadata.c
@@ -23,6 +23,7 @@
 #include "libavcodec/bsf_internal.h"
 #include "libavcodec/cbs.h"
 #include "libavcodec/cbs_bsf.h"
+#include "libavcodec/itut35.h"
 #include "libavcodec/cbs_h265.h"
 #include "libavcodec/h2645data.h"
 #include "libavcodec/h265_profile_level.h"
diff --git a/libavfilter/Makefile b/libavfilter/Makefile
index 10d0feb6df..25e25d8246 100644
--- a/libavfilter/Makefile
+++ b/libavfilter/Makefile
@@ -489,6 +489,7 @@ OBJS-$(CONFIG_SCALE_D3D11_FILTER)            += vf_scale_d3d11.o scale_eval.o d3
 OBJS-$(CONFIG_SCALE_D3D12_FILTER)            += vf_scale_d3d12.o scale_eval.o
 OBJS-$(CONFIG_SCALE_CUDA_FILTER)             += vf_scale_cuda.o scale_eval.o \
                                                 vf_scale_cuda.ptx.o cuda/load_helper.o
+OBJS-$(CONFIG_SCALE_OPENCL_FILTER)           += vf_scale_opencl.o opencl.o opencl/scale.o scale_eval.o
 OBJS-$(CONFIG_SCALE_QSV_FILTER)              += vf_vpp_qsv.o
 OBJS-$(CONFIG_SCALE_RKRGA_FILTER)            += vf_vpp_rkrga.o scale_eval.o
 OBJS-$(CONFIG_SCALE_VAAPI_FILTER)            += vf_scale_vaapi.o scale_eval.o vaapi_vpp.o
diff --git a/libavfilter/allfilters.c b/libavfilter/allfilters.c
index 3685c3ffdc..73d1a0f97e 100644
--- a/libavfilter/allfilters.c
+++ b/libavfilter/allfilters.c
@@ -452,6 +452,7 @@ extern const FFFilter ff_vf_vpp_amf;
 extern const FFFilter ff_vf_sr_amf;
 extern const FFFilter ff_vf_frc_amf;
 extern const FFFilter ff_vf_scale_cuda;
+extern const FFFilter ff_vf_scale_opencl;
 extern const FFFilter ff_vf_scale_d3d11;
 extern const FFFilter ff_vf_scale_d3d12;
 extern const FFFilter ff_vf_scale_qsv;
@@ -525,6 +526,7 @@ extern const FFFilter ff_vf_tpad;
 extern const FFFilter ff_vf_transpose;
 extern const FFFilter ff_vf_transpose_cuda;
 extern const FFFilter ff_vf_transpose_opencl;
+extern const FFFilter ff_vf_transpose_d3d11;
 extern const FFFilter ff_vf_transpose_vaapi;
 extern const FFFilter ff_vf_transpose_vt;
 extern const FFFilter ff_vf_transpose_vulkan;
diff --git a/libavfilter/cuda/vector_helpers.cuh b/libavfilter/cuda/vector_helpers.cuh
index e4a9353ff1..ca8246e5a0 100644
--- a/libavfilter/cuda/vector_helpers.cuh
+++ b/libavfilter/cuda/vector_helpers.cuh
@@ -132,27 +132,4 @@ inline __device__ float4 saturate_rintf<float4>(float4 a, float factor) {
     );
 }
 
-template<typename T>
-inline __device__ T saturate_rintf(T a, float factor) {
-    return rintf(__saturatef(a) * factor);
-}
-
-template<>
-inline __device__ float2 saturate_rintf<float2>(float2 a, float factor) {
-    return make_float2(
-        saturate_rintf(a.x, factor),
-        saturate_rintf(a.y, factor)
-    );
-}
-
-template<>
-inline __device__ float4 saturate_rintf<float4>(float4 a, float factor) {
-    return make_float4(
-        saturate_rintf(a.x, factor),
-        saturate_rintf(a.y, factor),
-        saturate_rintf(a.z, factor),
-        saturate_rintf(a.w, factor)
-    );
-}
-
 #endif
```
