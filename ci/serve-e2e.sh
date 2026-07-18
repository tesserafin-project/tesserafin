#!/usr/bin/env bash
# Real, TCP-bound Reefin server for browser-driven end-to-end tests.
#
# WHY THIS EXISTS — the gap it closes:
#
#   ci/run.sh (the merge gate) and ci/smoke-e2e.sh (PR #32) both exercise the server through
#   Reefin.Server.Integration.Tests' WebApplicationFactory, i.e. an IN-PROCESS TestServer. That
#   factory's ConfigureWebHostBuilder never actually binds Kestrel: there is NO listening socket, so
#   no real browser can reach it. reefin-web's playwright.config.ts says as much in its own header
#   and deliberately ships no `webServer` block — which is why
#   `npx playwright test tests/e2e/theme-glass.spec.ts` fails with `connect ECONNREFUSED ::1:8096`.
#
#   This script is the missing link: it boots the REAL Reefin.Server executable, bound to a real TCP
#   port, serving a real reefin-web build, with a real admin user and a real movie library — against
#   a throwaway data directory that never touches a developer's actual Reefin installation.
#
# WHAT IT DOES, IN ORDER:
#
#   1. Picks a free TCP port (never assumes 8096 is free — override with --port).
#   2. Creates a throwaway data/config/cache/log tree under a mktemp -d directory.
#   3. Synthesizes real, decodable media fixtures with ffmpeg — the same testsrc/sine lavfi recipe
#      tests/Reefin.Server.Integration.Tests/EndToEnd/EndToEndMediaFixtures.cs (PR #32) and
#      HlsSmokeTests already use, so this depends on no committed binary test assets.
#   4. Writes network.xml with the chosen port. This is the ONLY way to set the listen port:
#      Reefin.Server/Extensions/WebHostBuilderExtensions.cs calls `options.Listen(addr, httpPort)`
#      explicitly from `appHost.HttpPort`, which ApplicationHost reads from
#      NetworkConfiguration.InternalHttpPort. ASPNETCORE_URLS / --urls are IGNORED — don't try.
#   5. Boots Reefin.Server with --datadir/--webdir pointed at the throwaway tree and the web bundle.
#   6. Waits for readiness by POLLING /System/Info/Public — never a fixed `sleep`.
#   7. Seeds through the REAL public API, no database surgery and no invented endpoints:
#        GET  /Startup/User          -> initializes the user manager, creates the default user
#        POST /Startup/User          -> renames it to $REEFIN_E2E_USER, sets $REEFIN_E2E_PASSWORD
#        POST /Startup/Complete      -> marks the setup wizard done
#        POST /Users/AuthenticateByName -> obtains an access token
#        POST /Library/VirtualFolders?collectionType=movies&refreshLibrary=true -> the library
#      then POLLS /UserViews until a CollectionType=="movies" view actually materializes (the
#      library scan is asynchronous — "server up" is NOT "library visible").
#   8. Prints the base URL and the exact environment variables the Playwright specs consume.
#   9. ALWAYS tears down — server process AND temp directory — via a trap, on success, failure,
#      Ctrl-C or SIGTERM alike. Pass --keep to retain the temp tree for debugging.
#
# WHY 127.0.0.1 AND NOT localhost: the original failure was `ECONNREFUSED ::1:8096` — `localhost`
# resolved to IPv6 while the server listens on IPv4. The printed base URL is always explicitly
# 127.0.0.1 to remove that ambiguity.
#
# USAGE:
#
#   # Foreground: boot, seed, print the URL, hold the server up until Ctrl-C.
#   ./ci/serve-e2e.sh --webdir ../reefin-web/dist
#
#   # One-shot: boot, seed, run a command with REEFIN_E2E_* exported, tear everything down.
#   ./ci/serve-e2e.sh --webdir ../reefin-web/dist \
#       --exec 'cd ../reefin-web && npx playwright test tests/e2e/theme-glass.spec.ts'
#
# The web bundle must be a `npm run build:production` output (a directory containing index.html).
# See docs/e2e-real-server.md for the full reefin-web build + run recipe.
#
# OPTIONS:
#   --webdir PATH     reefin-web production bundle to serve (default: ../reefin-web/dist)
#   --port N          TCP port to bind (default: an auto-detected free port)
#   --exec CMD        run CMD once the server is seeded and ready, then tear down; exit with its status
#   --user NAME       admin username to create   (default: $REEFIN_E2E_USER or smokeadmin)
#   --password PW     admin password to set      (default: $REEFIN_E2E_PASSWORD or smokepass123)
#   --datadir PATH    use PATH instead of a mktemp -d tree (implies --keep)
#   --no-build        skip `dotnet build`, reuse the existing Reefin.Server binary
#   --keep            do not delete the temp tree on exit (prints its path)
#   --timeout N       readiness timeout in seconds (default: 180)
#   -h, --help        show this header
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

SERVER_PROJECT="Reefin.Server/Reefin.Server.csproj"
# Reefin.Server.csproj sets <AssemblyName>reefin</AssemblyName>, so the built entry
# point is reefin.dll — NOT Reefin.Server.dll.
SERVER_DLL="Reefin.Server/bin/Debug/net10.0/reefin.dll"

# The Authorization header shape Reefin requires on every request. Mirrors the one
# reefin-web's tests/e2e specs send, so seeding and the specs authenticate identically.
AUTH_CLIENT='MediaBrowser Client="Reefin E2E Harness", Device="ci/serve-e2e.sh", DeviceId="reefin-ci-serve-e2e", Version="0.0.0"'

PORT=""
WEBDIR=""
EXEC_CMD=""
DATADIR=""
DO_BUILD=1
KEEP=0
READY_TIMEOUT=180
E2E_USER="${REEFIN_E2E_USER:-smokeadmin}"
E2E_PASSWORD="${REEFIN_E2E_PASSWORD:-smokepass123}"

banner() {
    echo ""
    echo "======================================================================"
    echo "== $*"
    echo "======================================================================"
}

log() { echo "[serve-e2e] $*"; }
fail_usage() { echo "ERROR: $*" >&2; exit 2; }

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --port)     [ $# -ge 2 ] || fail_usage "--port requires a value";     PORT="$2"; shift 2 ;;
        --webdir)   [ $# -ge 2 ] || fail_usage "--webdir requires a PATH";    WEBDIR="$2"; shift 2 ;;
        --exec)     [ $# -ge 2 ] || fail_usage "--exec requires a command";   EXEC_CMD="$2"; shift 2 ;;
        --user)     [ $# -ge 2 ] || fail_usage "--user requires a value";     E2E_USER="$2"; shift 2 ;;
        --password) [ $# -ge 2 ] || fail_usage "--password requires a value"; E2E_PASSWORD="$2"; shift 2 ;;
        --datadir)  [ $# -ge 2 ] || fail_usage "--datadir requires a PATH";   DATADIR="$2"; KEEP=1; shift 2 ;;
        --timeout)  [ $# -ge 2 ] || fail_usage "--timeout requires seconds";  READY_TIMEOUT="$2"; shift 2 ;;
        --no-build) DO_BUILD=0; shift ;;
        --keep)     KEEP=1; shift ;;
        -h|--help)  sed -n '2,84p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)          fail_usage "unexpected argument: $1" ;;
    esac
done

command -v curl >/dev/null 2>&1 || fail_usage "curl is required but not on PATH"
command -v ffmpeg >/dev/null 2>&1 || fail_usage "ffmpeg is required to synthesize media fixtures but is not on PATH"
command -v dotnet >/dev/null 2>&1 || fail_usage "dotnet is required but not on PATH"

# ---------------------------------------------------------------------------
# Resolve the web bundle. Serving a real bundle is mandatory: the specs call
# page.goto('/'), so the SERVER has to hand back the reefin-web SPA.
# ---------------------------------------------------------------------------
[ -n "$WEBDIR" ] || WEBDIR="$REPO_ROOT/../reefin-web/dist"
WEBDIR_ABS="$(cd "$WEBDIR" 2>/dev/null && pwd || true)"
[ -n "$WEBDIR_ABS" ] || fail_usage "--webdir path does not exist: $WEBDIR (build it with: cd reefin-web && npm run build:production)"
[ -f "$WEBDIR_ABS/index.html" ] || fail_usage \
    "'$WEBDIR_ABS' has no index.html — not a reefin-web production bundle (build it with: npm run build:production)"

# ---------------------------------------------------------------------------
# Pick a free port. Binding :0 and reading back what the kernel assigned is the
# only race-free way to learn a port that is actually free right now.
# ---------------------------------------------------------------------------
if [ -z "$PORT" ]; then
    PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()' 2>/dev/null || true)"
    [ -n "$PORT" ] || fail_usage "could not auto-detect a free port (python3 unavailable?) — pass --port N explicitly"
fi
BASE_URL="http://127.0.0.1:${PORT}"

# ---------------------------------------------------------------------------
# Throwaway data tree. Never the user's real Reefin installation.
# ---------------------------------------------------------------------------
CREATED_TMP=0
if [ -z "$DATADIR" ]; then
    DATADIR="$(mktemp -d -t reefin-e2e-XXXXXXXX)"
    CREATED_TMP=1
fi
mkdir -p "$DATADIR"
DATADIR="$(cd "$DATADIR" && pwd)"
CONFIG_DIR="$DATADIR/config"
MEDIA_DIR="$DATADIR/media/movies"
LOG_FILE="$DATADIR/server.log"
mkdir -p "$CONFIG_DIR" "$MEDIA_DIR"

SERVER_PID=""
# Populated after AuthenticateByName; api() folds it into the Authorization header once set.
TOKEN=""

cleanup() {
    local status=$?
    trap - EXIT INT TERM

    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        log "stopping server (pid $SERVER_PID)"
        # SIGINT first: the host uses UseConsoleLifetime, which treats it as Ctrl-C and
        # shuts down promptly. Plain SIGTERM was observed to take >10s here. SIGKILL is
        # the backstop so this function can never leave an orphan holding the port.
        kill -INT "$SERVER_PID" 2>/dev/null || true
        for _ in $(seq 1 20); do
            kill -0 "$SERVER_PID" 2>/dev/null || break
            sleep 0.5
        done
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            log "server still up after SIGINT — sending SIGKILL"
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi

    if [ "$KEEP" -eq 1 ] || [ "$CREATED_TMP" -eq 0 ]; then
        log "data directory retained at: $DATADIR"
        log "server log: $LOG_FILE"
    else
        rm -rf "$DATADIR"
        log "removed throwaway data directory"
    fi

    exit "$status"
}
# Fires on normal exit, on any `set -e` failure, and on Ctrl-C / SIGTERM alike.
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if [ "$DO_BUILD" -eq 1 ]; then
    banner "Building Reefin.Server (host dotnet SDK)"
    dotnet build "$SERVER_PROJECT" -c Debug --nologo -clp:ErrorsOnly
fi
[ -f "$SERVER_DLL" ] || fail_usage "server binary not found at $SERVER_DLL (drop --no-build, or run: dotnet build $SERVER_PROJECT)"

# ---------------------------------------------------------------------------
# Media fixtures — real, decodable files, synthesized on the fly.
# Named "<Title> (<Year>).mp4" inside a matching folder so Reefin's movie
# resolver actually registers them as movies.
# ---------------------------------------------------------------------------
banner "Synthesizing media fixtures with ffmpeg"
make_movie() {
    local title="$1"
    local dir="$MEDIA_DIR/$title"
    mkdir -p "$dir"
    ffmpeg -hide_banner -loglevel error -y \
        -f lavfi -i "testsrc=size=320x240:rate=15:duration=2" \
        -f lavfi -i "sine=frequency=1000:duration=2" \
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
        -c:a aac -movflags +faststart \
        "$dir/$title.mp4"
    log "fixture: $dir/$title.mp4"
}
make_movie "Reefin E2E Fixture (2020)"
make_movie "Reefin E2E Second Fixture (2021)"

# ---------------------------------------------------------------------------
# network.xml — the ONLY way to set the listen port (see this script's header).
# Unspecified elements fall back to NetworkConfiguration's own defaults.
# ---------------------------------------------------------------------------
cat > "$CONFIG_DIR/network.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <InternalHttpPort>${PORT}</InternalHttpPort>
  <PublicHttpPort>${PORT}</PublicHttpPort>
  <EnableHttps>false</EnableHttps>
  <RequireHttps>false</RequireHttps>
  <EnableIPv4>true</EnableIPv4>
  <EnableIPv6>false</EnableIPv6>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <EnableRemoteAccess>true</EnableRemoteAccess>
</NetworkConfiguration>
XML

# ---------------------------------------------------------------------------
# Boot
# ---------------------------------------------------------------------------
banner "Starting Reefin.Server on ${BASE_URL}"
log "data directory: $DATADIR"
log "web bundle:     $WEBDIR_ABS"
log "server log:     $LOG_FILE"

dotnet "$SERVER_DLL" \
    --datadir "$DATADIR" \
    --configdir "$CONFIG_DIR" \
    --cachedir "$DATADIR/cache" \
    --logdir "$DATADIR/logs" \
    --webdir "$WEBDIR_ABS" \
    --nonetchange \
    >"$LOG_FILE" 2>&1 &
SERVER_PID=$!

# ---------------------------------------------------------------------------
# Readiness — polling, never a fixed sleep. Also fails fast if the process dies.
# ---------------------------------------------------------------------------
# CAREFUL — /System/Info/Public is NOT a valid readiness probe. Reefin.Server binds the
# configured port TWICE in sequence: first with ServerSetupApp/SetupServer.cs (a placeholder
# host shown while the real app boots), then with the real application. SetupServer explicitly
# maps /System/Info/Public and answers it 200 with StartupWizardCompleted=false, while 503-ing
# every OTHER route. So a probe on /System/Info/Public goes green during startup and every
# subsequent seeding call then fails with 503.
#
# /Startup/User is the discriminator: SetupServer 503s it (its catch-all), the real app answers
# it 200. Polling it until 200 is therefore a true "the real app is serving" signal — and it is
# the first seeding step anyway.
banner "Waiting for the real application to serve (not the startup placeholder)"
ready=0
for _ in $(seq 1 "$READY_TIMEOUT"); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "ERROR: server process exited before becoming ready. Last 40 log lines:" >&2
        tail -40 "$LOG_FILE" >&2 || true
        exit 1
    fi
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 \
        -H "Authorization: ${AUTH_CLIENT}" "${BASE_URL}/Startup/User" 2>/dev/null || echo 000)"
    if [ "$code" = "200" ]; then
        ready=1
        break
    fi
    sleep 1
done
if [ "$ready" -ne 1 ]; then
    echo "ERROR: the real application did not start serving within ${READY_TIMEOUT}s (last /Startup/User status: ${code:-none})." >&2
    echo "       A persistent 503 means the startup placeholder never handed over." >&2
    tail -40 "$LOG_FILE" >&2 || true
    exit 1
fi
log "real application is serving on ${BASE_URL}"

# ---------------------------------------------------------------------------
# Seeding, entirely through the real public API.
# ---------------------------------------------------------------------------
banner "Seeding admin user '${E2E_USER}' and a movie library"

# Reefin authenticates via the Authorization header, with the access token folded into
# it as a Token="..." field — the exact shape reefin-web's specs send. A bare
# `X-Emby-Token` alongside a tokenless Authorization header is rejected with 401, so
# once TOKEN is known every call must carry it *inside* Authorization.
api() {
    # api METHOD PATH [curl args...]
    local method="$1" path="$2"; shift 2
    local auth="$AUTH_CLIENT"
    if [ -n "$TOKEN" ]; then
        auth="${AUTH_CLIENT}, Token=\"${TOKEN}\""
    fi
    curl -fsS --max-time 30 -X "$method" "${BASE_URL}${path}" \
        -H "Authorization: ${auth}" "$@"
}

# GET /Startup/User initializes the user manager and creates the default user.
api GET /Startup/User >/dev/null
log "default user initialized"

# POST /Startup/User renames it and sets the password.
api POST /Startup/User \
    -H 'Content-Type: application/json' \
    -d "{\"Name\":$(printf '%s' "$E2E_USER" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),\"Password\":$(printf '%s' "$E2E_PASSWORD" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}" \
    >/dev/null
log "admin user configured: ${E2E_USER}"

# Mark the wizard complete so the normal auth path is the one under test.
api POST /Startup/Complete >/dev/null
log "startup wizard completed"

# Authenticate — this both proves the credentials the specs use actually work and
# yields the token needed to create the library.
AUTH_JSON="$(api POST /Users/AuthenticateByName \
    -H 'Content-Type: application/json' \
    -d "{\"Username\":$(printf '%s' "$E2E_USER" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),\"Pw\":$(printf '%s' "$E2E_PASSWORD" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}")"
TOKEN="$(printf '%s' "$AUTH_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["AccessToken"])')"
# From here on api() automatically authenticates with this token.
USER_ID="$(printf '%s' "$AUTH_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["User"]["Id"])')"
[ -n "$TOKEN" ] || { echo "ERROR: authentication returned no access token" >&2; exit 1; }
log "authenticated — userId=${USER_ID}"

# The specs specifically look for a view with CollectionType == "movies", so the
# library MUST be created as a movies collection.
api POST "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=$(printf '%s' "$MEDIA_DIR" | python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.stdin.read(), safe=""))')&refreshLibrary=true" \
    -H 'Content-Type: application/json' \
    -d '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableChapterImageExtraction":false,"ExtractChapterImagesDuringLibraryScan":false}}' \
    >/dev/null
log "movies library created over ${MEDIA_DIR}"

# ---------------------------------------------------------------------------
# Wait for the library scan — asynchronous, so "server up" is NOT "view present".
# ---------------------------------------------------------------------------
banner "Waiting for the movies view to materialize in /UserViews"
views_ready=0
for _ in $(seq 1 "$READY_TIMEOUT"); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "ERROR: server exited while waiting for the library scan. Last 40 log lines:" >&2
        tail -40 "$LOG_FILE" >&2 || true
        exit 1
    fi
    VIEWS="$(curl -fsS --max-time 10 "${BASE_URL}/UserViews?userId=${USER_ID}" \
        -H "Authorization: ${AUTH_CLIENT}, Token=\"${TOKEN}\"" 2>/dev/null || true)"
    if [ -n "$VIEWS" ] && printf '%s' "$VIEWS" | python3 -c '
import json,sys
try:
    items = json.load(sys.stdin).get("Items", [])
except Exception:
    sys.exit(1)
sys.exit(0 if any(i.get("CollectionType") == "movies" for i in items) else 1)
' 2>/dev/null; then
        views_ready=1
        break
    fi
    sleep 1
done
if [ "$views_ready" -ne 1 ]; then
    echo "ERROR: no CollectionType=movies view appeared in /UserViews within ${READY_TIMEOUT}s." >&2
    echo "Last /UserViews response: ${VIEWS:-<none>}" >&2
    tail -40 "$LOG_FILE" >&2 || true
    exit 1
fi
log "movies view is visible in /UserViews"

# ---------------------------------------------------------------------------
# Ready
# ---------------------------------------------------------------------------
banner "READY"
cat <<EOF
Base URL:  ${BASE_URL}
User:      ${E2E_USER}
Password:  ${E2E_PASSWORD}
Data dir:  ${DATADIR}
Web bundle:${WEBDIR_ABS}
Server log:${LOG_FILE}

Point the reefin-web Playwright specs at it with:

  export REEFIN_E2E_BASE_URL='${BASE_URL}'
  export REEFIN_E2E_USER='${E2E_USER}'
  export REEFIN_E2E_PASSWORD='${E2E_PASSWORD}'
  cd <reefin-web> && npx playwright test tests/e2e/theme-glass.spec.ts
EOF

export REEFIN_E2E_BASE_URL="$BASE_URL"
export REEFIN_E2E_USER="$E2E_USER"
export REEFIN_E2E_PASSWORD="$E2E_PASSWORD"

if [ -n "$EXEC_CMD" ]; then
    banner "Running: ${EXEC_CMD}"
    set +e
    bash -c "$EXEC_CMD"
    EXEC_STATUS=$?
    set -e
    banner "Command exited with status ${EXEC_STATUS}"
    exit "$EXEC_STATUS"
fi

banner "Server is up — press Ctrl-C to stop and clean up"
# `wait` would swallow the INT trap until the child exits; poll instead so Ctrl-C
# is handled promptly and cleanup always runs.
while kill -0 "$SERVER_PID" 2>/dev/null; do
    sleep 1
done
echo "ERROR: server process exited unexpectedly. Last 40 log lines:" >&2
tail -40 "$LOG_FILE" >&2 || true
exit 1
