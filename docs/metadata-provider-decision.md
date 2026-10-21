# Metadata provider access — decision proposal

Status: **proposed, owner decision pending**. Evidence checked 2026-10-21.
Refs [#175](https://github.com/tesserafin-project/tesserafin/issues/175).
This records public-source findings and an engineering recommendation; it does
not grant usage rights or change provider configuration.

## Proposed disposition

Keep the three providers operator-configured for now. Do not ship a shared
project credential or add a metadata proxy. This preserves independent household
servers and avoids making a provider outage a playback dependency. It does **not**
establish that an operator key clears the licensing of paid Tesserafin clients.
API access, content rights and client distribution need separate answers.

| Provider | Proposed immediate position | Before a project credential or paid-client claim |
| --- | --- | --- |
| TMDB | Keep optional operator key; prioritize commercial clarification | Obtain terms covering the free server plus paid official apps, direct household access and cached/displayed content |
| TheAudioDB | Keep optional operator key; evaluate a business arrangement | Confirm app-store use, third-party household installations, aggregate quotas and artwork attribution/rights |
| OMDb | Keep optional operator key; exclude from any commercial-use promise | Obtain express commercial/content permission; a Patreon subscription alone is insufficient evidence |

These are recommendations for review, not accepted product decisions. The current
[operator guide](metadata-provider-keys.md) remains the configuration reference.
No API was called, account created, subscription purchased, credential requested,
terms accepted on the project's behalf, or provider contacted during this review.

## TMDB

The [developer FAQ][tmdb-faq] describes free access for non-commercial use with
attribution and a separate commercial service. It identifies revenue generation
as the commercial-project criterion. Attribution requires an approved TMDB logo,
less prominent than the application's branding, and an About/Credits notice that
disclaims endorsement. The FAQ's wording and the legal terms' wording differ;
verify the applicable notice before a client ships.

The [API terms][tmdb-terms] were available through indexed primary-source text,
but direct retrieval was blocked by robots during this review. The indexed text
includes fees for integrated applications among commercial examples and requires
a separate written commercial agreement. It also describes a non-transferable,
non-sublicensable API licence, caching restrictions and termination cleanup.
**The indexed copy is older evidence, not a freshly fetched contract.** Re-read
and retain the applicable complete terms before deciding credential distribution
or a cache policy; do not implement a retention period from this snapshot.

The [rate-limit guide][tmdb-rates] withdraws the old 40-per-10-seconds rule and
mentions changeable protection near 40 requests/second, with HTTP 429 respected.
This is not an allocated quota, service-level commitment or proof of the scope
of a shared key's limit.

**Inference for Tesserafin:** splitting a free server from paid clients does not
justify assuming the commercial question disappears when requests originate on
the household server. Ask for explicit coverage of that topology.

## TheAudioDB

The [terms, dated 01/07/2025][audiodb-terms], permit free API development but
require a paid subscription for app-store publication. Paid projects must stay
within limits and credit custom artwork's data source. Reselling requires specific
permission; recipients of third-party API access must comply with the terms.
Artwork may carry separate rights: the terms point to `strCreativeCommons` and
require permission or another legal basis for third-party content. An API
subscription is not a blanket artwork clearance.

The [API guide][audiodb-api] publishes a public development key and premium keys;
this does not authorize restoring a built-in fallback in Tesserafin. V1 uses URL
path authentication; V2 uses a header and is premium-only. Published limits are
30/minute free, 100/minute premium and 120/minute business, with HTTP 429 and a
minute's wait after exceeding the limit. The page does not establish whether
limits aggregate by credential, source address or deployment.

The [pricing page][audiodb-pricing] lists Single Developer at EUR 8/month and
Small Business at EUR 20/month with a private key and 120 requests/minute. These
are dated public listings, not a quote for Tesserafin's distributed topology.

**Inference for Tesserafin:** ask for a business/deployment agreement before
including AudioDB-backed metadata in paid app-store claims. Retain artist/artwork
attribution information where supplied; identify actual display and storage gaps
in a separately scoped implementation review.

## OMDb

The [key page][omdb-key] lists free access at 1,000 requests/day and a Patreon
option. It does not specify a shared-installation entitlement or a verified
paid-tier quota. The [home page][omdb-home] labels content CC BY-NC 4.0 and restricts
the separate Poster API to patrons. The [licence summary][cc-by-nc] requires credit,
a licence link and modification disclosure, and excludes commercial purposes;
other rights may still apply.

The [legal page][omdb-legal], last updated March 12, 2015 but still published,
limits contributions to personal, non-commercial use and includes additional
restrictions on redistribution, indexing and derivatives. Its relationship to
the site's Creative Commons notice is not resolved by this review.

**Inference for Tesserafin:** paying for API capacity or poster access does not
establish commercial content rights. Request written clarification of both
API access and content/display/cache permissions before promising integration
with paid apps. Keep OMDb optional while that answer is outstanding.

## Questions ready for owner review

Use the following topology in each separately authorized inquiry: a free,
self-hosted server makes direct metadata requests; independent households store
metadata and artwork; paid native Android/TV and later Apple clients browse the
household server. No mandatory Tesserafin metadata relay is proposed.

1. Does the offered permission cover this whole topology and paid client sales,
   including when households supply their own API keys?
2. May one project credential be distributed to independent installations? If so,
   how are limits aggregated, installations identified and abuse/revocation handled?
3. What rights cover metadata, artwork, resizing, local caching, backups and display
   to household users? What expires or must be purged on termination?
4. What exact attribution, approved logo, links and third-party notices must appear
   in Web and each native client? Which rights are not included?
5. What fee, quota, support/availability commitment and credential-rotation process
   apply? Is an operator-owned credential an explicitly supported alternative?

Record the provider's answer and the owner-selected option **per provider** in
#175. Do not place credentials, confidential contracts or personal account data
in that issue or a PR. Public findings can link to a redacted decision record.

## Implementation after a decision

Keep playback, local NFO/embedded metadata and server administration usable
without any of these providers. Treat provider activation and missing permissions
as explicit setup states; do not imply that creating a free key clears all uses.
A future approved project key must remain operator-overridable and revocable,
with no literal credential in source, images, logs or public evidence.

Before an access-policy implementation, audit the actual request throttling,
backoff, metadata provenance, attribution and cache/backup paths against the
selected agreement. This document has not verified those runtime properties.
Use local NFO/embedded tags as the existing independent fallback. An alternative
online provider requires its own coverage/licensing review; no unreviewed service
is nominated as a drop-in replacement.

## Evidence and limits

Primary pages below were read on 2026-10-21, except the TMDB legal page whose
indexed text was readable but direct access was blocked. Prices and published
limits are observations at that date. This is a documentation-only investigation;
no runtime, browser attribution or provider-request test is claimed. Refresh
sources at the owner decision and again before any commercial release.

[tmdb-faq]: https://developer.themoviedb.org/docs/faq
[tmdb-terms]: https://www.themoviedb.org/api-terms-of-use?language=en-CA
[tmdb-rates]: https://developer.themoviedb.org/docs/rate-limiting
[audiodb-terms]: https://www.theaudiodb.com/docs_terms_of_use.php
[audiodb-api]: https://www.theaudiodb.com/free_music_api
[audiodb-pricing]: https://www.theaudiodb.com/pricing
[omdb-key]: https://www.omdbapi.com/apikey.aspx
[omdb-home]: https://www.omdbapi.com/
[omdb-legal]: https://www.omdbapi.com/legal.htm
[cc-by-nc]: https://creativecommons.org/licenses/by-nc/4.0/
