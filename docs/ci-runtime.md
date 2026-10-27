# Hosted test runtime

The 30-minute ceiling is `timeout-minutes: 30` in `ci-tests.yml`, not a
GitHub-hosted runner platform limit. Keep that ceiling and shorten its critical
path rather than allowing a stuck suite to occupy a runner indefinitely.

## Measurement, 2026-10-27

The first attempt of [master run 40567555335](https://github.com/tesserafin-project/tesserafin/actions/runs/40567555335/attempts/1)
passed all tests and merged coverage, then was cancelled during cache finalization
at the job's 30-minute ceiling:

| Stage | Time |
| --- | ---: |
| Native dependencies | 251 s |
| Combined restore, build and tests, with coverage | 1,518 s |
| Integration assembly alone, inside that stage | about 19 min |
| Coverage merge | 11 s |

In [PR run 40564032436](https://github.com/tesserafin-project/tesserafin/actions/runs/40564032436),
without coverage, the combined stage still took 1,246 s and integration took
17 min 17 s. NuGet was restored in seconds. Removing coverage alone would not
address the main bottleneck. Another successful master run,
[40515889073](https://github.com/tesserafin-project/tesserafin/actions/runs/40515889073),
took 1,235 s in the combined stage: runner and native-package mirror variation
also matter. These are job/step durations, not queue time or workflow wall time.

## Execution

Three independent jobs run for each selected OS:

| Suite | Target | Selection (in addition to `Category!=Smoke`) |
| --- | --- | --- |
| unit | Full solution | Names outside `Tesserafin.Server.Integration.Tests.` |
| integration | Integration project | Names outside `Tesserafin.Server.Integration.Tests.EndToEnd.` |
| end-to-end | Integration project | Names inside `Tesserafin.Server.Integration.Tests.EndToEnd.` |

The integration project uses the `Tesserafin.Server.Integration.Tests` namespace
and its children. Keep new tests under that namespace so the three selections
remain complementary. All existing non-Smoke tests remain included, and the
unit job still builds the entire solution. The integration assembly's deliberate
serial xUnit settings are unchanged: separate runner processes isolate static
server state rather than enabling unsafe in-process parallelism.

Restore, build and test have separate steps. Tests use `--no-build` against
outputs built in the same job, not cached binaries. NuGet remains cached; native
dependencies remain installed in every Linux suite, with bounded HTTP waits and
a five-minute installation timeout. No test timeout or assertion is relaxed.

PRs remain uninstrumented. Master pushes collect coverage in all three suites,
merge it in a separate job, and retain the merged Cobertura report. The existing
`run-tests (ubuntu-latest)` required-check name remains an explicit verdict over
all suites and the expected coverage result. A failed, cancelled or unexpectedly
skipped dependency cannot yield a successful verdict. Draft gating and the manual
Linux/macOS/Windows matrix are preserved.

Each suite retains TRX reports for three days and summarizes its 20 slowest test
cases. A suite reporting no executed tests fails. TRX case durations exclude
fixture setup/teardown; compare them with the complete test-step duration before
attributing time to individual tests. OpenAPI drift evidence is still retained
on failure, with distinct artifact names per suite.

Parallel jobs reduce the critical path but repeat some builds, NuGet extraction
and native installation, so aggregate runner minutes can increase. Measure the
first hosted runs before claiming a speedup or a monthly-minute saving. If one
integration partition still dominates, use its TRX timings and fixture lifecycle
to choose the next change; do not blindly parallelize tests sharing global state.
