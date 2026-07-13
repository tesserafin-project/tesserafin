# Playback compatibility fixtures — mandatory categories

One fixture file per case, validated against `../schema/fixture.schema.json`.
Status: `seed` = provided in PR93 (exemplar), `à compléter` = to be added with the runner (PR96+).

| Category | Fixture | Status |
| --- | --- | --- |
| direct-play | `video-h264-aac-mp4-directplay.json` | seed |
| remux | `video-mkv-remux-mp4.json` | seed |
| audio-transcode | `video-mkv-dts-to-aac.json` | seed |
| downmix | `audio-downmix-51-to-stereo.json` | seed |
| no-viable-plan | `video-no-viable-plan.json` | seed |
| video-codec-incompatible | — | à compléter |
| bitrate-resolution-limit | — | à compléter |
| hdr-tonemap | — | à compléter |
| subtitle-burn-in | — | à compléter |
| subtitle-external | — | à compléter |
| live-tv | — | à compléter |
| alternate-versions | — | à compléter |

The seed set covers the distinct mechanics (direct play, container remux, audio
transcode, channel downmix, unviable) so the format and category-comparison
method (see `../../../docs/pr93-compatibility-lab.md` §4) are exercised before the
engine exists. The remaining categories are added alongside the legacy-vs-v2
runner in PR98.
