# Playback compatibility fixtures — mandatory categories

One fixture file per case, validated against `../schema/fixture.schema.json`.
Status: `seed` = provided. `à compléter` = to be added with the legacy-vs-v2 runner (PR98).

| Category | Fixture | Status |
| --- | --- | --- |
| direct-play | `video-h264-aac-mp4-directplay.json` | seed |
| remux | `video-mkv-remux-mp4.json` | seed |
| audio-transcode | `video-mkv-dts-to-aac.json` | seed |
| downmix | `audio-downmix-51-to-stereo.json` | seed |
| no-viable-plan | `video-no-viable-plan.json` | seed |
| video-codec-incompatible | `video-codec-incompatible.json` | seed |
| bitrate-resolution-limit | `video-resolution-limit.json` | seed |
| hdr-tonemap | `video-hdr-tonemap.json` | seed |
| subtitle-burn-in | `subtitle-pgs-burn-in.json` | seed |
| subtitle-external | `subtitle-srt-external.json` | seed |
| live-tv | — | à compléter |
| alternate-versions | — | à compléter |

Each fixture deliberately isolates a single "exceed" dimension (other capability
limits set generous) so its `expected.reasonCodes` set equals exactly what the
engine emits — see `docs/pr93-compatibility-lab.md` §4 and the PR97 engine.
`live-tv` and `alternate-versions` need runner/orchestration context and are added
with the legacy-vs-v2 runner in PR98.
