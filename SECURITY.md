# Security policy — Tesserafin Server

This policy covers the **`tesserafin-project/tesserafin`** repository: the Tesserafin
server. Tesserafin Web, the browser client, has its own policy with the same contract
and a different scope — see
[`tesserafin-project/tesserafin-web/SECURITY.md`](https://github.com/tesserafin-project/tesserafin-web/blob/main/SECURITY.md).

## 1. Supported versions

**Tesserafin has not published its first Stable release yet.** Public Tesserafin
SemVer begins at `1.0.0`; the `12.x` server and `13.x` web numbers inherited from
upstream history describe a lineage, not a Tesserafin release history. This is
recorded authoritatively in
[`docs/versioning-policy.md`](./docs/versioning-policy.md).

Consequently:

* Reports concerning the **current release candidate and the current default branch
  (`master`)** are accepted.
* Reports concerning the pre-release development container images are accepted, but
  those images are internal, unsupported development artifacts and carry no
  compatibility or support promise.
* **Once public releases begin, the latest Stable release is the supported public
  line.** This section will be updated at that point.

There are no beta, nightly or long-term-support channels. Do not assume one exists.

## 2. Confidential reporting

**Use the repository's Security tab and select "Report a vulnerability" to submit a
private vulnerability report. Do not open a public issue for a suspected
vulnerability.**

That form opens a private security advisory readable only by you and the Tesserafin
maintainers. Never report a suspected vulnerability through a public issue, a public
discussion, a pasted log, an ordinary pull request, or any other public channel:
doing so discloses the problem to everyone before a fix exists.

**Ordinary bugs belong in public issues.** A crash, a rendering defect, a broken
playback path, or any other defect with no security impact is reported the normal
way, in this repository's public issue tracker — not through the private advisory
form. If you are unsure which one applies, use the private form; a maintainer will
move it to a public issue if it turns out to carry no security impact.

**No Tesserafin email address is currently advertised.** The private advisory form
above is the only confidential intake channel this project operates, and this policy
will not publish an invented or personal address in place of one. If you genuinely
cannot use the GitHub form, open a public issue that says only that — with **no**
vulnerability detail of any kind — and a maintainer will arrange another route.

Tesserafin is a fork of Jellyfin. Tesserafin issues are not upstream issues: do not
route Tesserafin vulnerability reports to the Jellyfin project's security or support
channels, and do not assume any upstream security promise, response time or contact
applies here.

## 3. Response expectations

These are **targets, not guarantees**. Tesserafin is maintained by volunteers and
makes no contractual commitment of anyone's time.

| Stage | Target |
| --- | --- |
| Acknowledgement that the report was received | within **7 calendar days** |
| Initial assessment, or a request for more information | within **14 calendar days** |
| Coordinated updates while investigation or remediation continues | at a cadence agreed with the reporter |
| Disclosure timing | agreed with the reporter when practical |

If a target is missed, the report is not dismissed — it is late. Nothing in this
section creates an entitlement to a fix, a timeline, or a specific outcome.

## 4. What to include in a report

* The **affected repository and component** — for example the API, the playback
  pipeline, the container runtime, a diagnostics endpoint, or packaging.
* The **version or exact commit**, and where relevant the container image digest, so
  the report can be reproduced against the same artifact.
* **Reproducible steps**, in the smallest form that still triggers the problem.
* The **impact**: what an attacker gains, and what precondition or privilege level
  they need to start from.
* **Minimal proof** — the least evidence that demonstrates the issue.

Do **not** include live credentials, API keys, session tokens, access tokens,
production data, or personal data belonging to anyone other than yourself. If a
report cannot be made without such a value, say so in the private advisory and wait
for a maintainer to arrange a safe transfer — never send it through a public one.

## 5. Coordinated disclosure and researcher conduct

* Allow the maintainers **reasonable time to investigate and patch** before publishing
  any detail. Timing is agreed with the reporter where practical.
* Test only against instances you own or are explicitly authorised to test.
* **Avoid destructive testing**: no data deletion or corruption, no persistence or
  backdoors, no denial of service or other service disruption, no lateral movement,
  and no access to data belonging to unrelated users.
* Stop at the point where the vulnerability is demonstrated. Do not extract more data
  than the minimum needed to prove it.
* **There is no bug-bounty programme and no reward, payment or recognition is
  promised**, unless a future written programme published by the project says
  otherwise.

## 6. Diagnostics and privacy

Tesserafin's diagnostics posture is a published decision, not an implicit one. Read
it before attaching diagnostic output to a report:

* **Policy decision:**
  [#80 — historical diagnostic surfaces and the scope of the #75 closure test](https://github.com/tesserafin-project/tesserafin/issues/80)
  (closed as completed; the decision and its per-surface rulings are in its closure
  comments).
* **Implementation:**
  [PR #85 — slice 75a, closed contract-mapping diagnostic behind the existing shadow gate](https://github.com/tesserafin-project/tesserafin/pull/85)
  and
  [PR #86 — slice 75b, bounded single-pass structural scan of the request body](https://github.com/tesserafin-project/tesserafin/pull/86).
* **Open follow-ups:**
  [#82 — strict UUID type for `PlaybackAttemptId` (future contract)](https://github.com/tesserafin-project/tesserafin/issues/82),
  [#83 — structured `DivergenceSummary` codes (future, conditional)](https://github.com/tesserafin-project/tesserafin/issues/83),
  [#84 — shareable redacted fixture export (future)](https://github.com/tesserafin-project/tesserafin/issues/84).

What that means in practice:

* **Diagnostic collection is bounded and structured.** Shadow diagnostic records are
  captured only when the shadow mode actually ran, are held in memory, are evicted on
  the session lifecycle, and introduce no separate persistence. Divergence summaries
  are server-generated; no client string is interpolated into them.
* **The elevated fixture export must not be assumed safe for public sharing.**
  `GET .../{id}/Fixture` is a deliberate pull by an elevated administrator, never
  produced or uploaded automatically, and it contains diagnostic capabilities and
  identifiers. Treat its output as sensitive by default.
* **#84's shareable redacted export is future work, not a delivered feature.** There
  is currently no redaction pass you can rely on. Do not treat any existing export as
  pre-redacted.
* **Reporters must redact before sharing evidence**: remove tokens, API keys, session
  identifiers, user and device identifiers, absolute filesystem paths, internal
  hostnames and network addresses, and any media metadata you do not intend to
  disclose. Log retention on a deployed instance is the operator's responsibility.

## 7. Scope of this policy

**In scope for this repository:**

* the HTTP API and its authentication and authorisation surfaces;
* playback — session lifecycle, stream and transcode paths, media delivery;
* the container image and its runtime, including startup, permissions and volume
  handling;
* server-side diagnostics, including the administrative diagnostics endpoints and the
  fixture export;
* packaging and distribution of the server artifacts.

**Out of scope here** (report against the correct repository instead):

* the browser client, its session handling and its bundled assets — see
  [Tesserafin Web's policy](https://github.com/tesserafin-project/tesserafin-web/blob/main/SECURITY.md);
* upstream Jellyfin code as shipped by upstream, and upstream infrastructure;
* findings that require a privilege level the attacker is already assumed to hold
  legitimately, unless the report shows a privilege boundary being crossed;
* missing hardening with no demonstrated impact, and automated scanner output with no
  reproduction.

Automated analysis is not a substitute for this policy, and this policy is not a
substitute for it. Since 2026-08-01, CodeQL runs the `security-extended` suite on this
repository automatically — on every pull request, on every push to `master`, and
weekly — and `master` is a protected branch whose required status checks include it.

What is **not** finished is the finding inventory those analyses produced. Alerts
remain open, they are classified by owner rather than dismissed, and some of them need
a product-level security decision this project has not yet taken. That work is tracked
in [#94](https://github.com/tesserafin-project/tesserafin/issues/94) and
[#185](https://github.com/tesserafin-project/tesserafin/issues/185). Do not read a
green pipeline as "no known issues".
