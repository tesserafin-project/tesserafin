# W2-A0 — acquiring the Windows Web payload daemonlessly, by digest

Tracks [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Authorised from master `aac506ed751af520cc7ba459341cd8abf22be6cf`.

The Windows server distribution has to ship the same Tesserafin Web bundle the
Linux packages ship. On Linux that is one line of
`ci/package/assemble-payload.sh`: `docker pull` by digest, then `docker cp` out
of a throwaway container. A hosted `windows-latest` runner has no Linux
container runtime, and installing one to copy 33 MB of static files would put a
container engine inside the trust boundary of a release artifact.

W2-A0 is the proof that none of that is necessary. It obtains the accepted
payload over the OCI distribution protocol, verifies it against the same
canonical digest the Debian and RPM packages assert, and hands it over — with no
daemon, no container, no Actions artifact, and no write permission anywhere.

W2-A0 acquires and verifies. It does not build a ZIP, install a service,
publish anything, or touch any package. Those are later W2 work.

## The accepted contract

| Input | Value |
| --- | --- |
| Image | `ghcr.io/tesserafin-project/tesserafin-web-assets@sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c` |
| Canonical tree digest | `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f` |
| Web revision | `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a` |
| Payload root in the image | `/web` |
| Provenance document | `/metadata/web-revision.json` |
| Clamp epoch | `1785852822` (`sourceDateEpoch`, read from the payload) |

The tree digest and the revision are not new numbers. They are
`WEB_PAYLOAD_SHA256` in `ci/package/pins.env` and `WEB_VCS_REF` in the
`Dockerfile`, which is what makes "the Windows ZIP ships the same web bundle as
the .deb" a checkable statement rather than an assurance.

## What the pinned descriptor actually is

Read from the registry read-only before any of this was written, because a
consumer designed against an assumed layout is a consumer that fails on the
first real image:

| Descriptor | Media type | Digest | Size |
| --- | --- | --- | --- |
| Manifest | `application/vnd.oci.image.manifest.v1+json` | `sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c` | 482 |
| Config | `application/vnd.oci.image.config.v1+json` | `sha256:401ff9335bc8b6cfff8fbf7e929c8c898d47f0f63e262a10ee2111d4bebece93` | 2087 |
| Layer 0 | `application/vnd.oci.image.layer.v1.tar+gzip` | `sha256:7259a5e99d5bb6a5f09ef23a8d9c8ec4c766518bd69112af4712e9fc4b860a17` | 34616141 |

* It is an image **manifest**, not an index — there is no platform to select.
* One layer, gzip-compressed, `diff_id`
  `sha256:dcb9f6b4f9994ed596913b764cfeb4f9d831f596ff8c82c4866a185752fbde86`.
* The tar inside is plain POSIX `ustar`: 2339 entries, 20 directories and 2319
  regular files, and **nothing else**. No PAX or GNU long-name records, no
  symbolic links, hard links, device nodes or FIFOs, no whiteouts, no absolute
  or traversing names, no name needing the `prefix` field.
* Roots are `licenses/`, `metadata/` and `web/`. The config labels the payload
  root: `org.tesserafin.web.assets.path=/web`.
* No name collides case-insensitively, none is a Windows reserved device name,
  the longest is 78 characters and the deepest is four levels.

So no overlay semantics are required to materialise this image. The consumer
still implements layer ordering and plain whiteouts, because the contract it
enforces has to be about what an OCI image may contain, not about what this one
happens to contain today — but it refuses the forms whose meaning is ambiguous
rather than guessing.

## The chain

`ci/windows/w2/consume-web-payload.ps1` fails closed unless every link holds:

1. The reference is an immutable `sha256:` + 64 lowercase hex digits. A tag is
   refused outright.
2. Registry, repository, reference, tree digest and revision are the five
   accepted values above. Anything else needs `-Fixture`, which is additionally
   restricted to a loopback registry so it can only ever drive a disposable one.
3. The manifest bytes are hashed **before they are parsed**, and must equal the
   requested digest.
4. The manifest's media type is a supported image manifest. An image index is
   named and refused: a consumer that silently picks a platform is a consumer
   whose output is no longer decided by the pinned digest alone.
5. Config and every layer are downloaded to a private file, and each must match
   both its declared size and its declared digest.
6. Each layer is decompressed and its `diff_id` must equal the config's
   `rootfs.diff_ids[i]`, in order, with the counts equal. This closes the gap
   between "the compressed bytes were verified" and "what was extracted was".
7. Layers are applied in manifest order into one accumulating root.
8. Every entry must be a regular file or a directory. Symbolic links, hard
   links, character and block devices, FIFOs and extended-attribute records are
   refused, not skipped.
9. Every name is refused if it is absolute, traverses with `..`, contains a
   backslash or a control character, uses `< > : " | ? *` (which covers drive
   letters and NTFS alternate-data-stream syntax), is a reserved device name,
   ends a component in a dot or a space, exceeds 220 characters, or resolves
   outside the extraction root. Two entries that differ only in case are refused
   too: NTFS would silently merge them and the tree digest would move.
10. `.wh.<name>` is applied as an overlay whiteout. `.wh..wh..opq` and the other
    `.wh..wh.` forms are refused rather than approximated.
11. The payload's own `metadata/web-revision.json` must record the pinned web
    commit, and supplies the clamp epoch.
12. The extracted `web/` tree must hash to the pinned canonical
    `pkg_tree_digest`.
13. Only then is the tree handed over, by a single directory rename out of a
    private staging directory that is a **sibling** of the destination — so the
    move is a rename on one volume and a half-verified tree is never visible
    under the accepted path. On any failure the staging directory is removed and
    the destination is guaranteed absent.

### The canonical digest on a runner with no GNU tar

`ci/windows/w2/pkg-tree-digest.py` re-expresses `ci/package/lib.sh`'s
`pkg_tree_digest` — GNU-format tar, names sorted, owner and group 0 numeric,
every mtime clamped, hashed as a file — against Python's `tarfile`. The archives
are byte-identical, not merely equivalent; `--check-oracle` asserts that against
a real `tar(1)` wherever one exists, and control C19 asserts it on Windows
against a digest recorded from GNU tar 1.35.

Two deliberate differences, both of which make it a faithful expression of what
`assemble-payload.sh` hashes rather than of what `tar` would do to an arbitrary
directory:

* Modes are normalised to `0755`/`0644` rather than read from disk, because
  `assemble-payload.sh` normalises them immediately before hashing and Windows
  has no POSIX mode to read back.
* `--exclude-vcs` is not reimplemented. A name on that list is a hard error
  instead: silently dropping a file is the one failure a digest cannot report,
  and the accepted payload contains none.

## Authentication boundary

The only credential is the job-scoped `GITHUB_TOKEN` with `packages: read`. No
PAT, no repository secret, no `docker login` equivalent, and no anonymous
fallback — a missing credential is a refusal, not a downgrade.

The token is read from an environment variable and never from a parameter: a
parameter is visible in the process table to every other process on the machine.
It is exchanged for a registry bearer over the standard token endpoint using an
`Authorization` header, never a query parameter.

The token, its base64 basic form and the exchanged bearer are all registered
with a redaction pass, and every string the script emits — log lines, denials,
exception text and the evidence document — goes through it first. Actions masks
the `GITHUB_TOKEN` itself but not the derived credentials, so relying on that
masking alone would leak exactly the two values that are as disclosing as the
original.

## Hostile controls

`ci/windows/w2/web-payload-controls.py` builds disposable OCI images in a
temporary directory, serves them from a loopback registry that speaks just
enough of the distribution API, and drives the real consumer against them. Every
negative control asserts the **named** refusal, not merely a non-zero exit: a
control that passes because the script failed for an unrelated reason would keep
passing after the property it names was deleted.

| Control | Property |
| --- | --- |
| C01 | A tag-only reference is refused |
| C02 / C02b | A short digest, and an uppercase one, are refused |
| C03 | A manifest that is neither the requested digest nor valid JSON is refused **on the digest** — proving the bytes are authenticated before they are parsed |
| C04 | A layer whose bytes were substituted at the same length is refused |
| C05 | A layer descriptor that lies about its size is refused |
| C06 / C06b | An unsupported layer media type, and an image index, are refused |
| C07 / C07b | No credential, and a credential the registry rejects, both fail — with neither the token, its basic form, nor the bearer anywhere in the output |
| C08 | A `..` traversal entry is refused |
| C09.1–C09.6 | Absolute, drive-letter, UNC, alternate-data-stream, reserved-device-name and trailing-dot paths are refused |
| C10.1–C10.3 | Symbolic link, hard link and FIFO entries are refused |
| C11a / C11b | A plain whiteout is applied and changes the tree; an opaque whiteout is refused |
| C12 | A payload that is not the pinned tree is refused |
| C13 | A payload recording a different web commit is refused |
| C14 / C14b | A second layer that fails after the first has written files leaves no output and no staging directory |
| C15 | Layers served in reverse order produce a different tree and are refused |
| C16 | The acquisition path invokes no container executable, daemon or engine — verified with a planted dependency the scanner must find |
| C17 | No artifact upload/download and no cache can carry the payload between jobs |
| C18 | A correct three-layer image with an override and a whiteout is accepted, and the evidence describes all three verified layers |
| C19 | The canonical digest implementation agrees with GNU tar |
| C20 | Two clean consumptions are byte-identical and hash to the same tree |
| C21 | The consumer's own accepted constants are still the ruling's five values |
| C22 | The workflow declares only `contents: read` + `packages: read`, triggers only on `pull_request`, pins every action to a commit SHA, persists no credentials, and names no mutable image |

C16, C17 and C22 each run their audit over a deliberately bad planted file
first: an audit that cannot find a planted violation is reported INERT rather
than PASS. The suite exits non-zero on any RED **or** any INERT.

## Hosted proof

`.github/workflows/w2-windows-web-payload.yml` runs one `windows-latest` job on
`pull_request` — including for a draft. It runs the full control suite against
local fixtures, then consumes the real accepted image twice into two directories
that have never held anything, requires both to hash to the pinned tree digest,
and compares them file by file. Permissions are `contents: read` and
`packages: read`; there is no `pull_request_target`, no `workflow_dispatch`, no
artifact step, no cache, and no container engine.

## What this does not establish

* **Nothing about the ZIP.** No archive is built, no layout is decided, no
  service is installed and nothing is published. W2-A0 stops at a verified
  directory.
* **`licenses/` and `metadata/` are not handed over.** The accepted output is
  the `web/` tree, which is what the canonical digest covers. The distribution's
  licence obligations are a later step's problem and will need them.
* **The image is a single-layer OCI manifest today.** The multi-layer,
  whiteout-bearing and Docker-media-type paths are exercised only by local
  fixtures. They exist so a re-published web image cannot silently change what
  the consumer accepts — not because they have been met in production.
* **An image index would be refused.** If the web image ever becomes
  multi-platform, this consumer must be extended deliberately, with a documented
  platform-selection rule.
* **The path budget is 220 characters.** The accepted payload's longest name is
  78, but a future bundle with deeper hashed chunk paths would be refused rather
  than truncated.
