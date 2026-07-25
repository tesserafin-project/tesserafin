# A4 — Hardware acceleration and the software fallback

Issue [#90]. This document is the operator-facing half of A4: what the container
does about GPUs on its own, how to give it one when you have one, and how to read
back what it decided.

The short version: **you do not have to do anything.** The default stack has no
GPU access and needs none. It probes for hardware at every start, finds none,
and transcodes in software. Everything below is optional.

---

## 1. What happens by default

`docker compose up -d` starts a container with no device mappings at all. On
every start, before serving anything, the server:

1. checks whether hardware encoding is enabled (it is, by default);
2. walks the hardware backend catalog in priority order, skipping any backend
   that is not plausible on this host — wrong operating system, missing ffmpeg
   build support, or no device node;
3. runs a **real one-second trial encode** for each remaining candidate;
4. selects the first candidate whose trial encode actually succeeded;
5. falls back to software if none did.

A backend is never selected because configuration said so, because a device file
exists, or because ffmpeg reports it as compiled in. It is selected because it
encoded a real frame on this machine, on this start. A wrong guess anywhere in
the catalog can therefore only ever produce "not selected" — never "selected and
broken".

On a host with no GPU there is nothing to select, and the server transcodes with
`libx264`. That path is covered end to end by `docker/hwa-smoke.sh`, which forces
a real item through the encoder in a running production container and checks that
actual media bytes come back.

---

## 2. Reading the startup decision

Every start logs exactly one conclusive decision event:

```
[INF] Tesserafin.MediaEncoding.Encoder.MediaEncoder: Hardware acceleration decision:
      Mode=software Backend=none Reason=NoApplicableBackend ConfiguredBackend=none
      CandidatesConsidered=[] CandidatesProbed=[] ProbeFailureCategories=[]
```

Find it with:

```bash
docker logs tesserafin 2>&1 | grep 'Hardware acceleration decision'
```

The fields are structured logging parameters, not prose, so they stay stable and
machine-readable. (Rendering the whole log as JSON is [#91], not this change.)

| Field | Meaning |
| --- | --- |
| `Mode` | `hardware` or `software` — what this run will actually encode with |
| `Backend` | The effective backend; always `none` when `Mode=software` |
| `Reason` | Which rule produced this outcome (table below) |
| `ConfiguredBackend` | The preferred backend that was configured before probing |
| `CandidatesConsidered` | Backends that were applicable on this host |
| `CandidatesProbed` | Backends whose trial encode was actually run |
| `ProbeFailureCategories` | Why probes failed, coarsely classified |

`Reason` is a fixed set:

| Reason | What it tells you |
| --- | --- |
| `HardwareDisabled` | Hardware encoding is switched off. Nothing was probed. |
| `PreferredBackendVerified` | Your configured backend was probed and works. |
| `AutoSelectedBackendVerified` | A backend was picked automatically and works. |
| `NoApplicableBackend` | Nothing was even worth probing. **This is the normal no-GPU container result.** |
| `AllProbesFailed` | A device was present but nothing could actually encode with it. Worth investigating — see the failure categories. |

The distinction between the last two matters when troubleshooting.
`NoApplicableBackend` usually means you did not pass a device through.
`AllProbesFailed` means you did, and it did not work — wrong group id, a driver
the image cannot use, or a GPU without an encode engine.

---

## 3. Enabling VAAPI (Linux only)

VAAPI covers Intel and AMD integrated and discrete GPUs on Linux.

### 3.1 Check that you have a render node

```bash
ls -l /dev/dri/renderD*
```

Expect something like:

```
crw-rw---- 1 root render 226, 128 Jul 25 09:11 /dev/dri/renderD128
```

If there is no `renderD*` entry, this host has no usable VAAPI device and there
is nothing to enable. `card0`/`card1` are display nodes and are **not** what
transcoding uses; they are deliberately not mapped.

### 3.2 Find the numeric render group id

```bash
stat -c '%g %G' /dev/dri/renderD128
```

```
992 render
```

The **number** on the left is what you need. It is commonly `992`, `993` or
`104`, but it genuinely varies between distributions and between installs — do
not copy a value from a guide, read it from your own host.

The container runs as the fixed non-root identity `10000:10000`, which is not a
member of your host's render group. The node is mode `0660`, owned by
`root:render`, so the container has to be given that group as a supplementary
group in order to open it. This is why the group id matters, and it is what
avoids the two bad alternatives: running the container as root, or loosening the
device's permissions on the host.

### 3.3 Configure and start

In `.env`:

```ini
TESSERAFIN_RENDER_DEVICE=/dev/dri/renderD128
TESSERAFIN_RENDER_GID=992
```

Then start with the override layered on top of the default file:

```bash
docker compose -f docker-compose.yml -f docker-compose.vaapi.yml up -d
```

`docker-compose.vaapi.yml` is an override, not a replacement. Both files are
needed, in that order.

### 3.4 Confirm it worked

```bash
docker logs tesserafin 2>&1 | grep 'Hardware acceleration decision'
```

```
Mode=hardware Backend=vaapi Reason=AutoSelectedBackendVerified ConfiguredBackend=none
CandidatesConsidered=[vaapi] CandidatesProbed=[vaapi] ProbeFailureCategories=[]
```

If it instead says `Mode=software Backend=none Reason=AllProbesFailed`, the
device was mapped but could not encode. The usual cause is a wrong
`TESSERAFIN_RENDER_GID`. Check with:

```bash
docker exec tesserafin sh -c 'ls -l /dev/dri/renderD128 && test -w /dev/dri/renderD128 && echo writable'
```

If the override is enabled but the device does not exist on the host,
`docker compose up` fails immediately and says which device it could not find.
That is deliberate: asking for hardware you do not have should be a visible
error, not a container that quietly runs in software while you believe it is not.

---

## 4. Forcing software mode

Set **`EnableHardwareEncoding=false`** in the server's encoding configuration
(Dashboard → Playback → Transcoding, or `encoding.xml`). That is the only switch
that means "never use hardware". It short-circuits before any probe runs, so it
also costs no startup time and never spins the GPU.

It does **not** erase your preferred backend, so switching hardware encoding back
on reconsiders that backend first.

Setting the acceleration type to `none` is *not* how you force software. `none`
means "no preference — select automatically", which is the fresh-install default.

---

## 5. Moving between hosts

A configured backend is a **preference**, never proof that it still works. It is
re-verified by a real trial encode on every start, including when it was already
in the configuration from a previous run.

The practical consequence: a `/config` volume created on a machine with a GPU can
be moved to a machine without one and the server will boot, select software, and
keep transcoding. It will not try to run VAAPI commands against hardware that is
not there. This is covered by step 7 of `docker/hwa-vaapi.sh`, which takes the
persisted state from a verified VAAPI run, restarts it with the device removed,
and requires both a `Mode=software` decision and a real completed transcode.

---

## 6. Security posture

The VAAPI override grants exactly one additional capability: access to one render
node. Everything else is identical to the default stack.

| | Default | With VAAPI override |
| --- | --- | --- |
| Runs as | `10000:10000` (non-root) | `10000:10000` (non-root) |
| `no-new-privileges` | yes | yes |
| Privileged mode | no | no |
| Docker socket | no | no |
| Host networking | no | no |
| Media mount | read-only | read-only |
| Devices | none | one render node, `rw` (not `mknod`) |
| Supplementary groups | none | the host render gid |

Note what is *not* used: `privileged: true`, mapping all of `/dev/dri`, or adding
the `card*` display node. None of them is needed for transcoding.

---

## 7. Hardware validation coverage — what is actually proven

Be precise about this, because "supports hardware acceleration" is easy to claim
and hard to verify.

| Backend | Status |
| --- | --- |
| **VAAPI** (Linux) | **Validated on real hardware.** An AMD Radeon (Renoir/Barcelo, `radeonsi`, Mesa Gallium) render node, driven end to end through a production container: real startup trial encode, real `h264_vaapi` media transcode, real bytes returned, and a real fallback to software after the device was removed. |
| Software (`libx264`) | **Validated on real hardware.** Proven by `docker/hwa-smoke.sh` on a host with no render node. |
| QSV, NVENC, AMF, VideoToolbox, RKMPP, V4L2M2M | **Probe-gated, not hardware-validated.** No corresponding hardware was available to test against. |

The second row of that last cell is the important one. Those six backends are in
the catalog and will be probed where they are plausible, but **no claim is made
that they have been verified against real hardware**, because they have not been.
The probe-gating invariant is what makes shipping them anyway safe: an unverified
or even wrong trial-encode argument line can only cause that backend to fail its
probe and not be selected. It cannot cause a broken backend to be chosen.

If you run Tesserafin on one of those, the decision log tells you exactly what
happened, and a failed probe costs you a fallback to software rather than a
broken player.

### Not in scope here

Recovering from a hardware failure that happens **during an already-running
transcode** is a different problem from choosing a backend at startup, and it is
not part of A4. `TranscodeFallbackPlanner` exists in the codebase but is not
wired to anything and does not perform live fallback. That work is tracked
separately.

MJPEG VAAPI transcoding quality ([#76]) is also out of scope and unchanged;
`mjpeg_vaapi` remains unavailable for transcode jobs. VAAPI image extraction is
unchanged.

[#90]: https://github.com/tesserafin-project/tesserafin/issues/90
[#76]: https://github.com/tesserafin-project/tesserafin/issues/76
[#91]: https://github.com/tesserafin-project/tesserafin/issues/91
