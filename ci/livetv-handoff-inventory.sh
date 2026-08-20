#!/usr/bin/env bash
# Live TV credentialless ffmpeg handoff - structural inventory gate (#153-LTV-S0).
#
# The Live TV HLS path only works because seven distinct places cooperate. If any one of them
# disappears - renamed, refactored away, or quietly deleted - the handoff silently reverts to
# "ffmpeg fetches an [Authorize]-protected URL with no credential", which is exactly the defect
# this branch fixes and which reads as a 500 on live.m3u8 rather than as a compile error.
#
# This gate is deliberately structural, not behavioural: it asserts the chain still EXISTS. It
# does not assert the chain is correct - that is what the unit and acceptance suites do.
#
# Exits non-zero if any category has zero matches.
set -uo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

FAILED=0
TOTAL=0

# count_category <name> <file> <pattern>
# Uses grep -c into a variable rather than `grep -q` in a pipeline: under `set -o pipefail`
# a `grep -q` that matches early can exit 141 (SIGPIPE), which inverts the assertion.
count_category() {
    local name="$1" file="$2" pattern="$3"
    local count=0
    TOTAL=$((TOTAL + 1))

    if [ ! -f "$file" ]; then
        printf 'EMPTY  %-34s (missing file: %s)\n' "$name" "$file"
        FAILED=$((FAILED + 1))
        return
    fi

    count="$(grep -c -E -- "$pattern" "$file" || true)"
    count="${count:-0}"

    if [ "$count" -eq 0 ]; then
        printf 'EMPTY  %-34s (no match for /%s/ in %s)\n' "$name" "$pattern" "$file"
        FAILED=$((FAILED + 1))
    else
        printf 'OK     %-34s %s match(es) in %s\n' "$name" "$count" "$file"
    fi
}

echo "Live TV credentialless handoff inventory"
echo "========================================"

# 1. The tuner stream opens and starts filling its temp file.
count_category "SharedHttpStream.Open" \
    "src/Tesserafin.LiveTv/TunerHosts/SharedHttpStream.cs" \
    'public override async Task Open\(CancellationToken'

# 2. That same stream publishes the [Authorize]d LiveStreamFiles endpoint as MediaSource.Path.
#    This is the URL ffmpeg must NOT be pointed at; it stays because external/legacy consumers
#    that DO carry a credential still resolve it.
count_category "MediaSource.Path" \
    "src/Tesserafin.LiveTv/TunerHosts/SharedHttpStream.cs" \
    'MediaSource\.Path = .*LiveTv/LiveStreamFiles/'

# 3. The streaming request resolves the live stream and captures its provider.
count_category "StreamingHelpers.GetStreamingState" \
    "Tesserafin.Api/Helpers/StreamingHelpers.cs" \
    'GetLiveStreamWithDirectStreamProvider'

# 4. The provider is carried on the state the encoder and transcode manager both see.
count_category "DirectStreamProvider" \
    "Tesserafin.Controller/Streaming/StreamState.cs" \
    'IDirectStreamProvider\? DirectStreamProvider'

# 5. The encoder selects stdin instead of the URL when a provider is present.
count_category "EncodingHelper" \
    "Tesserafin.Controller/MediaEncoding/EncodingHelper.cs" \
    'DirectStreamProvider: not null'

# 6. The transcode manager actually feeds that stdin.
count_category "TranscodeManager.StartFfMpeg" \
    "Tesserafin.MediaEncoding/Transcoding/TranscodeManager.cs" \
    'DirectStreamPump\.Start'

# 7. The endpoint ffmpeg no longer calls keeps its authorization.
count_category "LiveTvController.GetLiveStreamFile" \
    "Tesserafin.Api/Controllers/LiveTvController.cs" \
    'GetLiveStreamFile'

# #153-LTV-R1. The PROBE, not only the transcode. LTV-R0 measured that S0 routed the transcode
# through pipe:0 and left ffprobe opening the [Authorize]d LiveStreamFiles url: one uncredentialed
# GET, one 401, and a media source published with Codec null / Index -1.

# 8. The live-stream open hands the probe the provider the tuner host returned.
count_category "MediaSourceManager probe reader" \
    "Tesserafin.Server.Core/Library/MediaSourceManager.cs" \
    'AddMediaInfoWithProbe\(mediaSource, false, cacheKey, true, liveStream as IDirectStreamProvider'

# 9. The helper forwards it onto the probe request rather than dropping it.
count_category "LiveStreamHelper forwards the reader" \
    "Tesserafin.Server.Core/Library/LiveStreamHelper.cs" \
    'DirectStreamReader = directStreamReader'

# 10. The encoder probes that reader over stdin instead of opening the path.
count_category "MediaEncoder probes pipe:0" \
    "Tesserafin.MediaEncoding/Encoder/MediaEncoder.cs" \
    '"pipe:0",'

# 11. And it feeds that stdin, with the same pump the transcode uses.
count_category "MediaEncoder feeds the probe pipe" \
    "Tesserafin.MediaEncoding/Encoder/MediaEncoder.cs" \
    'pump = DirectStreamPump\.Start'

# 12. A piped probe carries no protocol option, or ffprobe refuses the whole invocation.
count_category "piped probe drops HTTP options" \
    "Tesserafin.MediaEncoding/Encoder/MediaEncoder.cs" \
    'if \(request\.DirectStreamReader is null\)'

echo "========================================"
if [ "$FAILED" -ne 0 ]; then
    echo "FAIL: $FAILED of $TOTAL inventory categories are empty."
    exit 1
fi

echo "PASS: all $TOTAL inventory categories are populated."
