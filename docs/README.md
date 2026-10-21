# Tesserafin documentation

Start with the task you want to complete. Installation and operation use published
artifacts; contributor builds and historical acceptance records have a separate
role. [Versioning policy](versioning-policy.md) and [release contents](../CHANGELOG.md)
define what is published.

## Install and operate

| Task | Guide |
| --- | --- |
| Choose Docker/NAS, native Linux or Windows | [Install Tesserafin](install.md) |
| Set up a Docker/NAS installation | [A3 guided install](container/A3-guided-install.md) |
| Check the native Linux platform/filesystem contract | [L0 Linux packages](distribution/L0-linux-packages.md) |
| Install the published Windows ZIP | [Windows installation](distribution/install-windows.md) |
| Complete onboarding, browse or play | [User guide](user-guide.md) |
| Operate an installed server | [Administrator guide](admin-guide.md) |
| Understand Docker state, back up or restore | [A2 persistent state](container/A2-persistent-state.md) |
| Inspect an image, pin it or plan an upgrade | [A6 versioning and upgrades](container/A6-versioning-and-upgrades.md) |
| Read health and logs | [A5 observability](container/A5-observability.md) |
| Configure container hardware acceleration | [A4 hardware acceleration](container/A4-hardware-acceleration.md) |
| Configure provider credentials | [Provider keys](metadata-provider-keys.md), [secret configuration](secret-configuration.md) |
| Configure XMLTV input | [XMLTV path](xmltv-listings-path.md) |
| Report an ordinary problem | [Support](support.md) |
| Report a suspected vulnerability privately | [Security policy](../SECURITY.md) |

The Linux guide distinguishes architecture-native package lifecycle evidence
from GPU evidence and independent NAS reports. The Windows guide distinguishes
the downloadable unsigned ZIP from an MSI form that is not published. The
Docker upgrade guide retains historical build evidence without treating it as a
published-image forward-migration proof.

## Develop and verify

| Task | Reference |
| --- | --- |
| Build, test and format the server | [BUILDING](../BUILDING.md) |
| Run the local merge gate and understand CI | [Local CI](local-ci.md) |
| Change/generate the canonical API contract | [OpenAPI contract](openapi-contract.md); use its current-status notes when reading historical sections |
| Verify the paired generated Web SDK | [SDK provenance gate](../ci/verify-sdk-provenance.sh), [pair lock](../ci/web-pair.lock.json) |
| Understand content-pack semantics | [Content-pack contract](content-pack-contract.md) |
| Understand playback credentials | [Playback credential contract](playback-credential-server-contract.md) |
| Run real-server integration evidence | [End-to-end guide](e2e-real-server.md) |

## Engineering records

The [metadata access proposal](metadata-provider-decision.md) records dated
primary-source findings and unresolved commercial/distribution questions for
TMDB, TheAudioDB and OMDb. It is pending owner decision and changes no credentials
or provider defaults.


The [documentation inventory](documentation-inventory.md) records the selected
cleanup, which entry points remain authoritative, and which material is retained
as evidence. No archive or removal is implied by a filename.

- [Container A1](container/A1-implementation-note.md) and
  [A7](container/A7-server-web-release-pair.md) record implementation/release-pair
  work; start an installation with A3 instead.
- [Distribution records](distribution/) include F0 and W0–W5 build, lifecycle and
  acceptance packets. Their commit IDs, run IDs and exclusions identify what was
  actually measured; they are not a catalogue of downloadable artifacts.
- The `pr*.md`, `issue*.md`, RFC and design documents describe specific changes
  or decisions. Consult their own status and accepted decision, rather than
  treating every historical plan as current product behavior.

For current work and sequencing use
[the roadmap](https://github.com/tesserafin-project/tesserafin/issues/129), not
an old acceptance packet. No new platform, release, migration compatibility or
hardware support is established by this index.
