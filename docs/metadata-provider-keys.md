# Metadata provider credentials — operator guide and disposition

Tesserafin ships **no third-party metadata provider credential of its own**. Where a
provider requires an API key, that key is operator-supplied configuration. A provider
with no key configured is inert: it fetches nothing, and the rest of the server is
unaffected.

This document records the operator setup and the disposition that produced it. It
covers the [C3] secrets slice's *credential-removal* half (#96, #172, #174); it does
not by itself satisfy that slice, which additionally requires an enforced
secret-scanning gate.

Three providers are affected. Each is independent: configuring one does nothing for
the others, and leaving all three unconfigured is a supported state.

| Provider | Setting | Configuration file under `/config/plugins/configurations/` |
|---|---|---|
| TheMovieDb | **TheMovieDb API key** (Plugins → TMDb) | `Tesserafin.Plugin.Tmdb.xml` |
| TheAudioDB | **TheAudioDB API key** (Plugins → AudioDB) | `Tesserafin.Plugin.AudioDb.xml` |
| OMDb | **OMDb API key** (Plugins → OMDb) | `Tesserafin.Plugin.Omdb.xml` |

Every one of those files lives inside the `/config` volume, so all three are covered
by the A2 backup and none is part of any image. Treat them as sensitive: each carries
a credential bound to your own account with that provider.

All three keys are read once, when the provider is first resolved. **Changing a key
requires a server restart** to take effect.

## TheMovieDb (TMDb)

### Getting a key

1. Create a free account at <https://www.themoviedb.org/signup>.
2. Request an API key from **Settings → API**. The v3 API key (a 32-character
   string) is the one Tesserafin uses.
3. In the Tesserafin dashboard, open **Plugins → TMDb** and paste the key into
   **TheMovieDb API key**, then **Save**.
4. **Restart the server.**

### What happens without a key

- The server starts normally and every non-TMDb feature is unaffected.
- The TMDb provider returns no results: no movie/series/season/episode/person
  metadata, no TMDb images, no TMDb-sourced similar items.
- A single warning is logged at startup of the TMDb client naming the setting to fill in.
- The TMDb plugin configuration page still loads; the image-size selectors fall back
  to the stored values rather than TMDb's live configuration.

## TheAudioDB

### Getting a key

1. Create an account at <https://www.theaudiodb.com/> and obtain an API key from your
   account page.
2. In the Tesserafin dashboard, open **Plugins → AudioDB** and paste the key into
   **TheAudioDB API key**, then **Save**.
3. **Restart the server.**

The field is masked and is never pre-filled with the stored value. Leaving it empty
and saving **keeps** the stored key; to remove a key, tick **Remove the stored API
key when saving**. That is deliberate — a blank field must never silently delete a
working credential.

### What happens without a key

- The server starts normally and every non-AudioDB feature is unaffected.
- No request is made to TheAudioDB at all. TheAudioDB carries its credential as a URL
  *path segment*, so there is no anonymous form of the call to fall back to.
- Artist and album lookups return an empty result, and TheAudioDB artist and album
  images are not offered.
- One warning is logged, the first time a lookup would have needed the key, naming
  the setting to fill in.

## OMDb

### Getting a key

1. Request a key at <https://www.omdbapi.com/apikey.aspx> and activate it from the
   confirmation email.
2. In the Tesserafin dashboard, open **Plugins → OMDb** and paste the key into
   **OMDb API key**, then **Save**.
3. **Restart the server.**

The field behaves exactly like TheAudioDB's: masked, never pre-filled, empty means
"keep the stored key", and an explicit checkbox removes it.

### What happens without a key

- The server starts normally and every non-OMDb feature is unaffected.
- No request is made to OMDb at all. OMDb has no anonymous tier, so an unconfigured
  lookup has nothing to ask for.
- Movie, series and episode searches through OMDb return no results, and OMDb images
  are not offered.
- One warning is logged, the first time a lookup would have needed the key, naming
  the setting to fill in.

## The product effect, stated plainly

With none of the three keys configured — the state every fresh install starts in —
**those three optional providers return no metadata and no images.** Local NFO files,
embedded tags, and every provider that needs no credential keep working, as do
playback, transcoding, users, libraries and the rest of the server.

That is the deliberate outcome, not a defect. Supplying a key for a provider you care
about is a two-minute task; shipping someone else's credential is not something a
restart can undo.

## Why there is no built-in key

Tesserafin is a fork of Jellyfin, and the fork inherited **three** third-party
provider credentials as compiled-in constants, each used whenever an operator had not
configured one:

| Provider | Inherited shape | Where it ended up |
|---|---|---|
| TheMovieDb | a `const string` API key | inlined into `Tesserafin.Providers.dll` |
| TheAudioDB | a `const string` key folded at compile time into a `public const` base URL | inlined into `Tesserafin.Providers.dll`, as both the bare key and the composed base URL |
| OMDb | a credential-bearing request URL in a method-local `const string` | inlined into `Tesserafin.Providers.dll` |

Each was byte-identical to upstream's. They belong to other projects' accounts, so
Tesserafin could neither rotate them nor take responsibility for them, and all three
were baked into every published image.

Shipping another project's credential is not a defensible default at any scale, and it
made the [C3] gate unachievable as written — a fail-closed secret scan would be red on
`master` from the moment it landed. The disposition taken is the one that removes the
problem rather than annotating it: **no built-in default, operator configuration
only**, for all three.

Two of the three values are additionally short — six characters for TheAudioDB,
eight for OMDb — and sit below any entropy or length threshold a generic secret
scanner applies. That is why this repository carries a **provider-authentication
structural audit** alongside Gitleaks: it recognises a credential by *where it is
used* (a literal reaching an auth-named query parameter, header, or a request URL),
not by how random or how long it looks. See
[`docs/provider-auth-audit.md`](provider-auth-audit.md).

### What this does not undo

- The values remain in this repository's **git history**, at their pre-rename paths.
  History was not rewritten.
- They remain in **already-published image digests**, which are immutable. Those
  digests are superseded, not altered; do not run them.

Neither is recoverable. The guarantee this disposition provides is forward-looking:
no future build carries any of them.
