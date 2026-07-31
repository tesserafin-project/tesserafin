# Metadata provider credentials — operator guide and disposition

Tesserafin ships **no third-party metadata provider credential of its own**. Where a
provider requires an API key, that key is operator-supplied configuration. A provider
with no key configured is inert: it fetches nothing, and the rest of the server is
unaffected.

This document records the operator setup and the disposition that produced it. It is
the TMDb half of the [C3] secrets slice (#96, #172); it does not by itself satisfy
that slice, which additionally requires an enforced secret-scanning gate.

## TheMovieDb (TMDb)

### Getting a key

1. Create a free account at <https://www.themoviedb.org/signup>.
2. Request an API key from **Settings → API**. The v3 API key (a 32-character
   string) is the one Tesserafin uses.
3. In the Tesserafin dashboard, open **Plugins → TMDb** and paste the key into
   **TheMovieDb API key**, then **Save**.
4. **Restart the server.** The key is read once, when the TMDb client is first
   resolved; it does not take effect in a running process.

Your key is stored in `/config/plugins/configurations/Tesserafin.Plugin.Tmdb.xml`,
inside the `/config` volume — so it is covered by the A2 backup and it is *not* part
of any image. Treat that file as sensitive: it carries a credential bound to your
TMDb account.

### What happens without a key

- The server starts normally and every non-TMDb feature is unaffected.
- The TMDb provider returns no results: no movie/series/season/episode/person
  metadata, no TMDb images, no TMDb-sourced similar items.
- A single warning is logged at startup of the TMDb client naming the setting to fill in.
- The TMDb plugin configuration page still loads; the image-size selectors fall back
  to the stored values rather than TMDb's live configuration.

Other metadata providers (local NFO, embedded tags, and any provider that needs no
credential) keep working.

## Why there is no built-in key

Tesserafin is a fork of Jellyfin, and the fork inherited Jellyfin's own TheMovieDb
API key as a compiled-in `const` used whenever an operator had not configured one.
That value was byte-identical to upstream's: it belongs to the Jellyfin project's
TMDb account, so Tesserafin could neither rotate it nor take responsibility for it,
and it was baked into every published image inside `Tesserafin.Providers.dll`.

Shipping another project's credential is not a defensible default at any scale, and
it made the [C3] gate unachievable as written — a fail-closed secret scan would be
red on `master` from the moment it landed. The disposition taken is the one that
removes the problem rather than annotating it: **no built-in default, operator
configuration only.**

The trade-off is deliberate and stated plainly: out of the box, TMDb metadata does
not work until the operator supplies a key. That is the cost of not shipping someone
else's credential.

### What this does not undo

- The value remains in this repository's **git history**, at its pre-rename path.
  History was not rewritten.
- The value remains in **already-published image digests**, which are immutable.
  Those digests are superseded, not altered; do not run them.

Neither is recoverable. The guarantee this disposition provides is forward-looking:
no future build carries the credential.
