# Share an installation report

Have you followed a Tesserafin installation guide on your own machine? Share
what worked and where you got stuck. Successful, partial and failed runs all
help improve the instructions. Native ARM64 hardware, Unraid and Synology DSM
Container Manager are particularly useful gaps to cover.

[Open an installation report](https://github.com/tesserafin-project/tesserafin/issues/new?template=installation_report.yml).
The reports are collected under [#117](https://github.com/tesserafin-project/tesserafin/issues/117).
Search that tracker and existing reports first; add another run to an existing
report if it is the same host, artifact and procedure.

Start from the [installation guide](install.md) and its published artifacts.
Record development builds separately if that is what you used. A report is
evidence for that particular environment and artifact, not a promise of support
for every NAS model, GPU or future release.

## What to record

The form asks for:

- Host/CPU model, OS or NAS version, host and container architectures, whether
  execution is native, virtualized or emulated, and tool versions. An ARM64
  image run through QEMU on x64 is not a native ARM64 hardware report.
- The exact artifact: requested image tag/digest and locally pulled digest(s),
  or release URL and native package/archive filename with a checksum if
  available. Include the server version shown by the application. A version
  alone does not identify the bytes installed.
- Whether the installation began with fresh state on a new host, fresh isolated
  state on a previously used host, or existing/imported/restored state.
- The guide URL/revision and the steps actually followed, including deviations,
  permissions and named volumes versus host bind mounts.
- Startup/health, the browser Web UI, first-run wizard, sign-in and library
  browsing. Say **passed**, **failed** or **not tested** for each. If an existing
  installation skips the wizard, record that; API reachability alone does not
  prove onboarding. First playback is optional and is not a GPU acceptance run.
- Persistence after restart and, for containers, recreation using the same
  artifact and persistent state. Record these separately; a restart does not
  replace a container.

For Docker, these read-only commands help identify the artifact. Replace
`tesserafin` with your container name if different:

```sh
docker version --format '{{.Server.Version}}'
docker compose version
docker inspect --format '{{.Config.Image}} {{.Image}}' tesserafin
```

The last command prints the requested reference and the running image's local
ID. Use that image ID in the following command:

```sh
docker image inspect --format '{{json .RepoDigests}} {{.Os}}/{{.Architecture}}' IMAGE_ID
```

Copy only those fields, not an unfiltered inspect dump. `RepoDigests` may be
empty for a local build. Record that limitation rather than substituting a
digest copied from a guide. An image ID is not a registry manifest digest;
record both when available. If a NAS UI exposes only part of this information,
say what is missing; SSH is not required just to submit a report.

## Check persistence on an isolated installation

Use a disposable test installation or an existing run you already observed.
An incomplete report is useful; no one needs to disrupt their main server to
complete the form. Keep source media read-only and use separate state directories
or volumes for a new test installation.

1. After onboarding, note the account, library and a harmless saved preference
   you can recognize later, such as a sorting preference.
2. Restart using the installation's normal controls. Check that you can sign in
   and still see the same library and preference.
3. For a container test, recreate the container at the **same image digest**,
   keeping the same state volumes/bind mounts and configuration. The canonical
   Compose path can use `docker compose up -d --force-recreate` from its install
   directory with the image pinned by digest. Record the requested reference and
   running image ID before and after. For Unraid/DSM, record the container
   manager action actually used and the retained mappings.
4. Check the same account, library and preference again. Record failures or
   untested stages explicitly. Native installations can mark container recreation
   as not applicable.

Do not remove state volumes or use `docker compose down -v`. Recreating a
container at the same digest tests state persistence, not a version upgrade or
backup restoration. Those procedures have their own
[backup and recovery guide](backup-recovery.md).

## Before submitting

Reports are public. Review excerpts and screenshots for passwords, API tokens,
private addresses, personal paths and media names. A complete configuration,
`.env`, database or backup is not needed. Short error excerpts are enough when
something failed; logs are optional when it worked.

Use the repository's **Security** tab → **Report a vulnerability** for suspected
security issues, following [SECURITY.md](../SECURITY.md). Other problems follow
the [support guide](support.md); the installation form adds no new support
channel or response-time promise.

## Collection target and maintainer triage

This bounded collection pass targets **one independently submitted, usable
report for each row**, rather than an endless request for more hardware:

| Path to cover | Evidence needed |
| --- | --- |
| Canonical Docker Compose on native x64 | Published artifact, fresh-state onboarding, restart and recreation observations |
| Container on native ARM64 hardware | Actual host/container architecture, published artifact, onboarding and persistence observations |
| Unraid template | Unraid version, actual template/import steps, mappings and persistence observations |
| Synology DSM Container Manager | DSM/manager versions, actual creation steps, permissions and persistence observations |

A single report may cover more than one row when it establishes both, such as
an ARM64 DSM installation. Reports are independent when submitted by someone
outside the maintainer/automated acceptance runs. A failed run counts as useful
coverage when its artifact, steps and failure are reproducible and any blocked
stages are marked; it never counts as a successful installation proof. Partial
reports remain useful while missing evidence is collected. Native Linux and
Windows reports are welcome and tracked separately from these four targets.

For each report, link it from #117, record the covered row(s), artifact, outcome
and missing evidence, and open or link a concrete bug/documentation follow-up
for each confirmed problem. Link back to the report so a failure remains
traceable even if the report itself is closed after triage.

Close #117 only after the entry point is published, the four rows have usable
independent reports (or an explicit maintainer disposition of a missing row),
and confirmed defects have linked follow-ups. Keep successful coverage separate
from failures and gaps in the closing record. This collection target adds no
new release gate and does not reopen the existing installation acceptance.
Publishing this guide or merging its PR supplies no hardware evidence itself.
