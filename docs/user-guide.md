# Tesserafin user guide

What to do once the server is running: complete first-run onboarding, sign in, and find and
play your media.

**Installing** is a different document — [`docs/container/A3-guided-install.md`](./container/A3-guided-install.md)
takes you from nothing to a running server in five steps, and the
[README](../README.md) names it as the primary path. **Operating** the server — health, logs,
backup, upgrade, transcoding — is [`docs/admin-guide.md`](./admin-guide.md).

> **Provenance of the steps below.** Every step in §1 is exercised by
> `docker/browser-gate/tests/onboarding.spec.ts`, a real-browser gate that drives the bundled
> web client against the release image and is part of the A3/A7 acceptance. The §2–§5 flows are
> exercised by the `tesserafin-web` end-to-end suite (`home`, `library`, `search`,
> `b2-theme-persistence`). Where this guide is silent, it is because nothing proves the
> behaviour yet — not because the feature is missing. This document does **not** yet carry the
> reviewer walkthrough that E2 (#102) requires.

---

## 1. First-run onboarding

Browse to `http://<host-ip>:8096/` (or `http://localhost:8096/` on the same machine). The
server redirects `/` to `/web/` and serves the web client from the same origin and port as the
API. A server that has not been onboarded sends you straight to the setup wizard.

> If you land on API documentation instead of the web client, you are running a superseded
> API-only image. Check the image reference against
> [A3](./container/A3-guided-install.md#the-image).

The wizard has six steps:

1. **Server name and display language.** The name is what the server reports to clients.
2. **Your administrator account.** Username, password, password confirmation. This is the first
   account and it is an administrator.
3. **Your first media library.** Choose *Add media library*, pick a content type (for example
   *Movies*), give it a display name, then add a folder — with the container defaults from A3,
   your media is mounted read-only at `/media`. **Confirm the folder appears in the folder list
   before saving**; a library saved with no path is created empty and looks like a scanning
   problem later.
4. **Metadata language and country.** Defaults are fine.
5. **Remote access.** Defaults are fine; this can be changed later.
6. **Finish.** The wizard completes and hands you to the sign-in page.

**Online metadata is opt-in.** Tesserafin ships **no** third-party provider credential, so
TheMovieDb, TheAudioDB and OMDb each fetch nothing until you supply your own key for that
provider — see [`metadata-provider-keys.md`](./metadata-provider-keys.md). All three are
independent and optional, and everything in this guide works with none of them.

## 2. Signing in

After onboarding, `/` serves the sign-in page. Use the account you created in step 2. There is
no separate "connect to server" step: the client is served by the server it talks to.

## 3. Home

Home is organised as a tab strip — **Home** and **Favourites** — with the selected tab
reflected in the URL, so a home view can be linked and reloaded. The Home panel lists your
libraries under a *My media* section. The tab strip is keyboard-operable with the arrow keys.

## 4. Browsing a library

Selecting a library opens its grid. The grid offers:

* **Sort order**, ascending or descending by name;
* a **year filter**, including an *All* option that restores the unfiltered grid;
* a **density toggle** switching to a compact grid, and that choice **persists across a
  reload**.

Sort and filter choices are reflected in the URL, so a filtered view survives a reload and can
be shared as a link.

## 5. Search

The app bar carries a search control. Searching a title shows matching items, and opening a
result card takes you to that exact item. A query that matches nothing reports *no results*
rather than an empty screen.

**Search respects library permissions**: a user who has not been granted a library does not see
its items in search results.

## 6. Themes

Two themes ship — **Classic** and **Glass**. The choice is stored per user, and it survives
both client-side navigation and a full reload. Classic is a flat presentation; Glass adds
translucency. Both are usable across desktop, mobile and TV breakpoints.

## 7. Adding more libraries and users later

Both are administrator actions in the server's admin area rather than in this browsing flow.
The library form is the same one the wizard's step 3 uses, including the folder-list
confirmation. This guide does not walk the admin screens step by step, because no automated
gate covers them yet and guessing at the navigation would be worse than saying so.

## 8. What this guide does not cover

* **Native mobile and TV clients.** None exist. Server plus browser client is the whole product
  today.
* **Jellyfin clients.** Tesserafin does not claim client, plugin or protocol compatibility with
  Jellyfin, and a Jellyfin client is not expected to work against it.
* **Playback troubleshooting.** If something will not play, the decisive evidence is
  server-side — start from the hardware-acceleration decision line and the health endpoint in
  the [admin guide](./admin-guide.md).
* **The admin screens**, per §7.
