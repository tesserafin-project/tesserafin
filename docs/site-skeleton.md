# Official site — content skeleton

E2 (#102) asks for a *"placeholder / skeleton for a future official site (content-only, no
infra this wave)"*. This is that skeleton.

**What this is.** A page inventory: what the site will say, and which in-repo document is the
single source of truth for each statement. It is content planning, not a site.

**What this is deliberately not.** No domain, no host, no static-site generator, no theme, no
CI job, no analytics, no asset pipeline, no copy written out in full. Choosing any of those is
a later wave. Nothing here should be read as a commitment to publish a site at all.

---

## 1. The rule that governs every page

**No page may claim something the repository does not already support.** The site is the most
visible surface this project has, and it is the easiest place to accidentally promise a
release, a channel, a client or a support commitment that does not exist. Concretely, at the
time of writing:

* **There is no public release.** No page may present a download, a "get started" button or a
  version number as if a Stable release exists. `docs/versioning-policy.md` is authoritative:
  public SemVer begins at `1.0.0` and nothing has been published.
* **The GHCR packages are private.** Anonymous pulls do not work. No page may print a
  `docker pull` line that a visitor cannot run.
* **Only Stable is planned as a public channel.** No beta, preview or nightly channel may be
  implied, and no mutable tag (`latest`, `stable`, `1`, `1.0`) may appear in an example.
* **Tesserafin is not a Jellyfin drop-in**, and it is neither endorsed by nor affiliated with
  the Jellyfin project. Fork attribution belongs on the page, not in a footnote — see `NOTICE`.
* **No support commitment may be invented.** `SECURITY.md` states response *targets, not
  guarantees*, and the private advisory form is the only confidential intake channel that
  exists. A "contact us" page must not imply an address or an SLA that is not real.
* **Native mobile and TV clients are roadmap items**, not shipped software, and no repository
  for them exists yet.

A page that cannot be written without breaking one of these is a page that is not ready.

## 2. Page inventory

| Page | Purpose | Source of truth in-repo | Ready to write? |
| --- | --- | --- | --- |
| Home | What Tesserafin is, who it is for, the honest project status | `README.md` §What Tesserafin is, §Project status | yes |
| Install | The one supported path: prebuilt container | `docs/container/A3-guided-install.md` | **blocked** — packages are private; see §3 |
| Administer | Running it: health, backup, upgrade, transcoding | `docs/admin-guide.md` | yes |
| Documentation hub | Index into the container docs, versioning policy, architecture | `docs/`, `ARCHITECTURE.md` | yes |
| Changelog / Releases | What each release contains, and what it does not | `CHANGELOG.md` | yes, as `[Unreleased]` only |
| Security | How to report a vulnerability, what to expect | `SECURITY.md` | yes |
| Principles | Free-software core, no mandatory hosted service, client commercial boundary | `README.md` §Product principles | yes |
| Lineage & licence | Fork attribution, GPL-2.0-or-later | `NOTICE`, `LICENSE`, `README.md` §Licence and lineage | yes |
| Roadmap | What is coming and what is explicitly not promised | #129, #142, #141, #144, #146 | yes, if framed as intent |
| Contribute | Build from source, the required checks | `BUILDING.md` | yes |

Each page **links to** its source of truth rather than restating it. Copy that is duplicated
between the site and the repository will drift, and the repository is the side that gate
evidence points at.

## 3. The install page is blocked, and by what

The install page is the one visitors will actually use, and it cannot be written truthfully
today: the GHCR packages are private, so every command on it would fail for a visitor. Making
the packages publicly pullable is an owner decision that has not been taken. Until it is, the
install page either does not exist or says plainly that installation is not yet open to the
public — it must not print a command that only a maintainer can run.

## 4. Sequencing

1. **Now** — this inventory, kept in step with the documents it points at.
2. **When a first release exists** — Home, Changelog, Security, Principles, Lineage,
   Documentation hub, Contribute can be written from material that already exists.
3. **When the packages are public** — the Install page becomes writable.
4. **A later wave** — generator, hosting, domain, theme, and the copy itself.

## 5. Out of scope for E2

Everything in step 4. E2's gate asks only that this material be *captured as a tracked
skeleton*; it does not ask for a site, and building one would exceed the release scope.
