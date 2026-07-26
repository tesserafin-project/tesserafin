# [A5] Health endpoint and structured logs — operator guide

Issue: tesserafin-project/tesserafin #91. Depends on #87 [A1] (the prebuilt image).
Consumed by [A4]'s startup decision logging and by the release go/no-go checks.

This is the whole of Tesserafin's production observability surface today: **one
health endpoint and JSON application logs**. Nothing to install, nothing to scrape,
no agent, no sidecar.

Related docs:
- Image (build + contents): [`A1-implementation-note.md`](./A1-implementation-note.md)
- Volumes, permissions, backup/restore: [`A2-persistent-state.md`](./A2-persistent-state.md)
- Guided install: [`A3-guided-install.md`](./A3-guided-install.md)
- Hardware acceleration: [`A4-hardware-acceleration.md`](./A4-hardware-acceleration.md)

---

## 1. `GET /health`

Unauthenticated, cheap, side-effect-free, and answered on the same port as
everything else (8096 inside the container).

```console
$ curl -i http://127.0.0.1:8096/health
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store

{"status":"healthy","version":"12.0.0","database":"healthy"}
```

### The contract

| Field      | Type   | Values                                     |
|------------|--------|--------------------------------------------|
| `status`   | string | `healthy`, `starting`, `unhealthy`         |
| `version`  | string | the running server's version, e.g. `12.0.0` |
| `database` | string | `healthy`, `unhealthy`, `unknown`          |

| Situation                                            | HTTP | `status`    | `database`             |
|------------------------------------------------------|------|-------------|------------------------|
| Server up, database answered                          | 200  | `healthy`   | `healthy`              |
| Server still starting (startup screen still owns it)  | 503  | `starting`  | `unknown`              |
| Server serving, core startup not finished             | 503  | `starting`  | `healthy` or `unhealthy` |
| Database did not answer                               | 503  | `unhealthy` | `unhealthy`            |
| Startup failed                                        | 503  | `unhealthy` | `unknown`              |

The three fields are always present, in every case — **from the very first response
the server gives, not only once it is ready** — so a probe only ever has to parse
one shape. **200 means ready**, not merely "the process is alive": it is emitted
only when the host reports core startup complete *and* the database answered, so
this endpoint is safe to use as a readiness gate in front of a reverse proxy.

### What the database check actually does

It opens the application database and executes `SELECT 1`, bounded by a five-second
timeout and cancelled with the request. It does **not** check that a file exists: a
database that is present but locked, corrupt or unreadable fails this check, which is
the entire point.

Tesserafin's database is embedded SQLite running in the server process. There is no
separate database service, so "the database is down" here means the embedded engine
stopped answering — not that a remote server became unreachable.

Nothing about the failure reaches the response. No path, no connection string, no
username, no exception message and no stack trace is ever serialised: the reason goes
to the log, where it is protected by the same access controls as the rest of the
container's output. The body has exactly the three fields above and nothing else.

### Container health

The image declares its own `HEALTHCHECK`, so this works with no configuration:

```console
$ docker compose ps
NAME         IMAGE                     STATUS
tesserafin   ghcr.io/.../tesserafin    Up 3 minutes (healthy)
```

The check polls every 30 s after a 120 s start period, and needs three consecutive
failures to flip to `unhealthy`. A slow first boot (large library, emulated
architecture) therefore does not mark the container unhealthy.

---

## 2. Structured logs

In the container, every application log line on stdout is **one JSON object**:

```json
{"timestamp":"2026-07-25T09:14:02.7180000Z","level":"Information","message":"Startup complete 0:00:12","sourceContext":"Main","threadId":1}
```

Which means `docker logs` output can be piped straight into anything that reads
JSON lines:

```console
# every warning and error, as objects
$ docker compose logs --no-log-prefix tesserafin | jq 'select(.level=="Warning" or .level=="Error")'

# what did hardware detection decide this boot? (see A4)
$ docker compose logs --no-log-prefix tesserafin | jq 'select(.Mode) | {Mode, Backend, Reason}'
{"Mode":"Software","Backend":"none","Reason":"AllProbesFailed"}
```

That second query is the point of structured logging here: the [A4] hardware
decision keeps its fields (`Mode`, `Backend`, `Reason`, `ConfiguredBackend`,
`CandidatesConsidered`, `CandidatesProbed`, `ProbeFailureCategories`) as real JSON
values instead of being flattened into a sentence you would have to parse with a
regular expression. The same is true of every other event the server logs.

Exceptions keep a field of their own (`exception`); they are never concatenated
into the message.

### The two settings

| Variable                | Effective default in the container | Values                                                     |
|-------------------------|------------------------------------|------------------------------------------------------------|
| `TESSERAFIN_LOG_LEVEL`  | `Information`                      | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal` |
| `TESSERAFIN_LOG_FORMAT` | `json`                             | `json`, `text`                                              |

`TESSERAFIN_LOG_FORMAT=json` is baked into the image; `TESSERAFIN_LOG_LEVEL` is
left unset there on purpose, so `/config/logging.json` stays authoritative
(`Information`) unless you set the variable. `docker-compose.yml` passes both
through explicitly so the knobs are visible where you configure the stack.

Both are ordinary environment variables — the same mechanism as
`TESSERAFIN_CONFIG_DIR` and friends — so they are set in `docker-compose.yml`, in
`.env`, or with `docker run -e`. Changing either needs a container restart, never a
rebuild:

```console
$ TESSERAFIN_LOG_LEVEL=Debug docker compose up -d
```

An unrecognised value is **ignored, not fatal**. The server starts normally, keeps
the previous setting, and says so — as a structured event you can find:

```json
{"timestamp":"...","level":"Warning","message":"Ignoring invalid LOG_LEVEL value loud; keeping the configured minimum level. Valid values: [\"Verbose\", \"Debug\", ...]","RejectedLogLevel":"loud"}
```

`TESSERAFIN_LOG_FORMAT=json` is set by the image, not by the server, so running
the server directly on your machine still gives you the readable console format.

### The rolling log file

`/config/log/log_*.log` keeps the same rendering as the console, so the file and
`docker logs` agree. Retention is unchanged (3 files, daily rotation, 100 MB cap).

### Two honest limitations

- One writer bypasses all of this: if the server cannot create its own data
  directories, it reports that on **stderr** before any logging framework exists.
  That is a fatal bootstrap failure and its output is not JSON. Everything after it
  is.
- With `TESSERAFIN_LOG_FORMAT=json` the sinks are chosen by the server, so a
  `WriteTo` section edited into `/config/logging.json` is ignored. Minimum levels,
  per-source overrides and enrichers from that file are still honoured. Use
  `TESSERAFIN_LOG_FORMAT=text` if you need full control of the sinks from the file.

---

## 3. Metrics are deferred to Phase F

A5 ships **health and structured logs only**. There is deliberately no metrics
endpoint, no counters, no histograms and no exporter, and this is a decision rather
than an omission: a metrics surface is a compatibility contract (names, labels,
cardinality, units), and publishing one before that contract exists would commit a
pre-release image to something nobody consumes yet. It is scheduled as a whole for
**Phase F, post-release**. The reasoning is recorded on issue #91.

What you can answer today without metrics: *is the container up and is its database
usable* (`/health`), and *what did the server decide and why* (the JSON logs).

One footnote for completeness: the server inherits a Prometheus surface from its
upstream lineage, gated behind the `EnableMetrics` server setting, which is **off by
default**. A5 neither enables, extends, documents nor validates it; treat it as not
part of the supported surface until Phase F says otherwise.

---

## 4. The validated image

Everything above was verified against one immutable artifact, pulled by digest into
a clean environment after the local copies were removed. This is the image the
canonical `docker-compose.yml`, `.env.example`, Unraid template and
[`A3-guided-install.md`](./A3-guided-install.md) point at.

```
ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.700c499f3e19
```

| | Digest |
|---|---|
| Multi-arch manifest | `sha256:6e3dbaab6eeaef163e81f9cc5ffb03f5a05bb9d8165e3f6487b2bb3003bc7608` |
| `linux/amd64` | `sha256:75826213611bb6ac58205ead6365436418831fd7ea115cbf5cb5e3710d7215cb` |
| `linux/arm64` | `sha256:7e2f5eb7fa8873fcc7bd882b6742fbe4524e6b014eaf4cb0c3c30a01045c8ff5` |

Built from `700c499f3e1936460728fa6c21965ec814f4c818`, which is #121 plus the
startup-contract fix in #122. The equally immutable `sha-700c499f3e1936…` tag names
the same manifest.

> **Do not use `12.0.0-dev.98442bd884eb`.** It is #121 without #122, so its `/health`
> answers a plain-text HTML 503 while the server is starting instead of the JSON
> contract documented above. It stays published and is not deleted, but it is
> superseded for A5.

Pull it by digest rather than by tag if you want the guarantee in writing:

```console
$ docker pull ghcr.io/tesserafin-project/tesserafin@sha256:6e3dbaab6eeaef163e81f9cc5ffb03f5a05bb9d8165e3f6487b2bb3003bc7608
```

Gates run against that digest, on `linux/amd64`: `docker/smoke.sh`,
`docker/observability.sh` (all 28 checks, including "every `/health` body from boot
onwards is the same JSON contract"), `docker/state-roundtrip.sh` and
`docker/browser-onboarding.sh`. `/health` reaches `200 {"status":"healthy",…}` about
11 seconds after container start; before that it answers `503` with
`status=starting` on the same schema, which is the readiness contract, not a fault.

No hardware-acceleration backend received new validation in this loop: QSV, NVENC,
AMF, VideoToolbox, RKMPP and V4L2M2M are exactly where [A4] left them.
