# Getting help with Tesserafin

Where to bring a problem, what to include, and what this project does and does not promise in
return.

**Tesserafin has not published a release yet.** Everything below describes the channels that
exist today; none of it is a support contract, and none of it is a commitment on anyone's time.

## The channels

There are exactly two, and they are not interchangeable.

| What you have | Where it goes |
| --- | --- |
| A suspected **security vulnerability** | The repository's **Security** tab → *Report a vulnerability*. Never a public issue. See [`SECURITY.md`](../SECURITY.md). |
| Anything else — a crash, a rendering defect, a broken playback path, a documentation error, a feature request | A **public issue** in the repository the problem belongs to |

Server, container, packaging, API: [tesserafin issues](https://github.com/tesserafin-project/tesserafin/issues).
Browser client and UI: [tesserafin-web issues](https://github.com/tesserafin-project/tesserafin-web/issues).

If you are unsure whether something has security impact, **use the private form**. A maintainer
will move it to a public issue if it turns out not to.

**There is no other channel.** No support email, no chat server, no forum, no ticketing system,
no phone number. Discussions are not enabled on either repository. If you find a Tesserafin
"support" channel somewhere else, it is not operated by this project.

**Do not route Tesserafin problems to Jellyfin.** Tesserafin is a fork, but it is an independent
project with no product or protocol compatibility claim. Jellyfin's issue tracker, forums and
security contacts are not Tesserafin's, and no upstream response time or support promise applies
here.

## What to include in a bug report

Most reports that stall do so because one of these is missing:

* **The exact image you are running.** A digest or an immutable version tag, not "latest" —
  `docker compose ps --format '{{.Image}}'`. See
  [the admin guide](./admin-guide.md#1-know-exactly-what-you-are-running).
* **What `/health` says** — `curl -fsS http://127.0.0.1:8096/health`. The three fields
  distinguish "still starting" from "the database is not answering" from "startup failed".
* **The relevant logs**, not the whole file. For playback and transcoding problems the decisive
  line is usually the hardware-acceleration decision:
  `docker logs tesserafin 2>&1 | grep 'Hardware acceleration decision'`.
* **What you did, what happened, what you expected.**
* **Whether it reproduces from a clean start**, if you can tell.

**Redact before you paste.** Logs and diagnostics can carry paths, file names and identifiers
from your own library. The project's diagnostics posture is a published decision — see
[#80](https://github.com/tesserafin-project/tesserafin/issues/80) and its trackers
[#82](https://github.com/tesserafin-project/tesserafin/issues/82),
[#83](https://github.com/tesserafin-project/tesserafin/issues/83),
[#84](https://github.com/tesserafin-project/tesserafin/issues/84) — and nothing in it obliges
you to share more than you want to.

## What to expect

* **Security reports** have stated targets in [`SECURITY.md`](../SECURITY.md): 7 days to
  acknowledge, 14 days to an initial assessment, coordinated updates, and disclosure timing
  agreed with the reporter. Those are **targets, not guarantees**.
* **Everything else is best-effort.** Tesserafin is maintained by volunteers and makes no
  contractual commitment of anyone's time. There is no SLA, no triage rota and no
  guaranteed response.
* **There is no supported release to fall back to.** Until a public Stable release exists, every
  artefact is a development build — see [`docs/versioning-policy.md`](./versioning-policy.md)
  and [`CHANGELOG.md`](../CHANGELOG.md).

## Before reporting, check whether it is a known limit

Several behaviours are documented as limits rather than defects, and reporting them again does
not move them. The current list is in the *Known limitations at the first release* section of
[`CHANGELOG.md`](../CHANGELOG.md) and §7 of [the admin guide](./admin-guide.md) — unvalidated
acceleration backends, no live mid-session transcode retry, the unproven forward-migration
boundary, no Jellyfin client compatibility, and Linux-container-only support.
