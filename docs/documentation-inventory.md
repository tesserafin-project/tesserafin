# Installation and administration documentation inventory

Scope: the entry points requested by
[#199](https://github.com/tesserafin-project/tesserafin/issues/199), at server
baseline `5e1cc8515cafcb61ecdbc48ab66cfb67211b2ea4`. This is a selected navigation
and contradiction cleanup, not a rewrite of all engineering documentation.

## Entry points and disposition

| Area | Existing entry points | Disposition in this cleanup |
| --- | --- | --- |
| Repository landing page | [README](../README.md) | Replace the repeated installation walkthrough with the platform selector; retain project/release status and contributor anchors. Correct the obsolete statement that no native repository exists. |
| Installation selection | Previously repeated in README, A3, L0 and Windows prose | [One selector](install.md), routing to detailed guides with published-download limits. |
| Container/NAS installation | [A3](container/A3-guided-install.md), [Compose](../docker-compose.yml), [environment example](../.env.example), [Unraid template](../deployment/unraid/docker-templates/tesserafin.xml) | Keep paths and implementation. Correct A3's contradictory private/login-required and development-tag instructions; retain superseded-image warnings and historical evidence. Do not claim a Community Applications listing. |
| Native Linux | [L0](distribution/L0-linux-packages.md), [service environment](../packaging/linux/tesserafin.conf) | Retain the artifact/lifecycle contract; add a route to actual downloads and installation. Separate produced provenance sidecars from assets actually published. |
| Windows | [Installation](distribution/install-windows.md), [W5 accepted ZIP pin](../ci/windows/w5/accepted-unsigned-zip.json) | Retain as the detailed operator guide. Selector explicitly states ZIP availability, absent MSI/arm64 assets and unsigned/GPU limits. |
| Daily use | [User guide](user-guide.md) | Retain as the canonical onboarding/browse/play guide; index it. |
| Administration | [Admin guide](admin-guide.md) | Retain as the operational router; index it. Do not duplicate its procedures. |
| Backup/recovery | [A2](container/A2-persistent-state.md), [backup script](../docker/backup.sh), [restore script](../docker/restore.sh), [round trip](../docker/state-roundtrip.sh), [server backup API](../Tesserafin.Api/Controllers/BackupController.cs) | Retain. The separate recovery PR below inventories archive formats and proof gaps. Do not silently promote Docker evidence to native recovery. |
| Upgrades/versioning | [A6](container/A6-versioning-and-upgrades.md), [version policy](versioning-policy.md), [CHANGELOG](../CHANGELOG.md) | Remove obsolete public-channel/install advice from A6's operator sections; update inspection examples to the release package. Retain its historical audit/rehearsal records and the #127 proof limitation. Policy remains authoritative. |
| Health, logging, hardware | [A5](container/A5-observability.md), [A4](container/A4-hardware-acceleration.md), L0, Windows guide | Index the guides; retain platform-specific evidence and limitations. No new hardware acceptance. |
| Credentials/metadata | [Secrets](secret-configuration.md), [provider keys](metadata-provider-keys.md), [provider audit](provider-auth-audit.md) | Route operators to configuration guides; preserve the audit as evidence. Do not change provider licensing/access decisions. |
| Support/security | [Support](support.md), [SECURITY](../SECURITY.md) | Retain the distinction between public bugs and private vulnerability reports; index it. |
| Contributor setup | [BUILDING](../BUILDING.md), [README development](../README.md#development), [local CI](local-ci.md) | Retain build/CI procedures and existing anchors; route BUILDING's installation reference to the selector. |
| Contracts/verification | [OpenAPI](openapi-contract.md), [SDK gate](../ci/verify-sdk-provenance.sh), [content packs](content-pack-contract.md), [playback credentials](playback-credential-server-contract.md) | Index as engineering references, not installation steps. Preserve their current-status notes, accepted constraints and historical context. |
| Engineering acceptance | [Container records](container/), [distribution records](distribution/), root `pr*.md` / RFC / design documents | Preserve paths, commit/run identities, accepted decisions and links. Their individual scope/status controls how to interpret them. No blanket deletion/archive classification. |

## Selected move/archive/removal set

**No files move or are deleted.** There is no verified obsolete duplicate whose
removal is needed to make the entry points usable. The bounded changes are the
README router, BUILDING's installation link, A3's contradictory instructions,
A6's current operator sections, L0's download distinction, and the new selector,
index and this inventory. Historical acceptance sections stay at their linked
paths; git retains the previous operator text.

The separate diagnostics [PR #323](https://github.com/tesserafin-project/tesserafin/pull/323)
and recovery [PR #324](https://github.com/tesserafin-project/tesserafin/pull/324)
add their own references through the administrator guide and A2. This cleanup
does not copy their changes or require their files to exist before it merges.
Their review/acceptance remains separate.

## Publication facts used

The `1.0.0` and `v1.1.0` GitHub release metadata was read when preparing this
change. `v1.1.0` lists the six Linux packages/archives, x64 checksum list, FFmpeg
source/runtime sidecars and one Windows x64 ZIP. It lists no MSI or arm64
checksum-list asset. The Windows ZIP digest agrees with the accepted ZIP pin.
The current container reference is checked against Compose, `.env.example`,
A3 and the version policy. This does not repeat an anonymous registry pull,
package install, device test or artifact-based restore.

Before changing the supported download matrix later, check publication metadata
again. A build workflow or an old acceptance packet alone does not establish
that a file can be downloaded.

## Verification boundary

Verify local file links and section anchors from the changed entry points,
Markdown fences, the shared container digest, release-asset filenames and the
absence of runtime/packaging changes. Hosted repository checks still apply.
No new installation, signing, hardware, forward-migration or disaster-recovery
acceptance is claimed by this documentation-only diff.
