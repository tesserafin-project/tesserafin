<#
.SYNOPSIS
    Compare the native-Windows failing-test SET between the PR head and its base
    (#234, W0-B acceptance debt 3).

.DESCRIPTION
    W0-ONLY. The question this answers is NOT "does the Windows suite pass" -- it
    does not, and W0 neither introduced that nor is allowed to fix it. The
    question is "does this architecture-only pull request change WHICH tests
    fail", and that question has a yes/no answer that a gate can enforce.

    The previous shape of this gate could not: it ran `dotnet test` under
    `continue-on-error`, threw the exit code away and printed a word into the job
    summary. A job that is green whatever happens cannot detect a new failure, a
    disappeared failure, or a renamed one -- and all three are real signals on a
    branch that claims to change no production code.

    So: exact counts from the TRX `Counters` elements (never an approximation),
    the exact failing-test name set from both trees, a required identity between
    them, and a required classification of every failure into one of the three
    families the W0 document names. Anything else fails.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $HeadResultsDir,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $BaseResultsDir,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $EvidenceDir,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $HeadSha,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $BaseSha,

    # Present so the ledger can state whether the run itself errored, separately
    # from what the results say. A `dotnet test` that dies before writing a TRX
    # is a different fact from a `dotnet test` that ran and reported failures.
    [Parameter()] [int] $HeadExitCode = -1,
    [Parameter()] [int] $BaseExitCode = -1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The three families section 2.7 of docs/distribution/W0-windows-server.md names. A
# failure that matches none of them is UNCLASSIFIED and fails this gate: the
# point of the classification is that a new kind of Windows failure cannot hide
# inside a total that happens to stay the same.
$families = [ordered]@{
    'log-forging-crlf' = @(
        'LogForging'
        'WritesExactlyOnePhysicalRecord'
    )
    'graceful-stop-stdin' = @(
        'TranscodingJobStopTests'
        'FfmpegProcessRunnerTests.RunProbeAsync_StandardInput_IsWrittenToChildProcess'
    )
    'openapi-cross-host' = @(
        'OpenApiXmlDocumentationOrderTests'
        'OpenApiContractTests'
    )
}

function Read-TrxResult {
    <#
        Every *.trx under the directory, because `dotnet test <solution>` writes
        one per test project. Reading only the first would silently drop whole
        assemblies -- and the failures in question are spread over four of them.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)] [string] $Root)

    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter *.trx -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) {
        throw "W0: no .trx results found under '$Root'. A missing result file is not an empty result set."
    }

    $failed = [System.Collections.Generic.List[string]]::new()
    $totals = @{ total = 0; passed = 0; failed = 0; skipped = 0 }

    foreach ($file in $files) {
        $xml = [xml](Get-Content -LiteralPath $file.FullName -Raw)

        $counters = $xml.TestRun.ResultSummary.Counters
        if ($counters) {
            # Exact values off the element. #234 forbids reporting an
            # approximate count, and "~3,560" was exactly that.
            $totals.total   += [int]$counters.total
            $totals.passed  += [int]$counters.passed
            $totals.failed  += [int]$counters.failed
            # TRX splits "not executed" across several attributes; a test that
            # did not run is not a test that passed.
            foreach ($attribute in 'notExecuted', 'inconclusive', 'disconnected', 'warning') {
                if ($counters.HasAttribute($attribute)) { $totals.skipped += [int]$counters.$attribute }
            }
        }

        foreach ($result in @($xml.TestRun.Results.UnitTestResult)) {
            if ($null -eq $result) { continue }
            if ($result.outcome -eq 'Failed') { $failed.Add([string]$result.testName) }
        }
    }

    return @{
        failedTests = @($failed | Sort-Object -Unique)
        counters    = $totals
        trxFiles    = @($files | ForEach-Object { $_.Name })
    }
}

function Get-Family {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $TestName)

    foreach ($family in $families.Keys) {
        foreach ($pattern in $families[$family]) {
            if ($TestName -like "*$pattern*") { return $family }
        }
    }
    return 'UNCLASSIFIED'
}

$head = Read-TrxResult -Root $HeadResultsDir
$base = Read-TrxResult -Root $BaseResultsDir

# The comparison #234 actually asks for: the SET, not the count. Two trees can
# fail the same NUMBER of tests while failing different ones.
$onlyOnHead = @($head.failedTests | Where-Object { $base.failedTests -notcontains $_ })
$onlyOnBase = @($base.failedTests | Where-Object { $head.failedTests -notcontains $_ })
$setsIdentical = ($onlyOnHead.Count -eq 0) -and ($onlyOnBase.Count -eq 0)

$classified = [ordered]@{}
foreach ($family in $families.Keys) { $classified[$family] = @() }
$classified['UNCLASSIFIED'] = @()
foreach ($test in $head.failedTests) {
    $family = Get-Family -TestName $test
    $classified[$family] += $test
}
$allClassified = $classified['UNCLASSIFIED'].Count -eq 0

$verdict = [ordered]@{
    probe            = 'windows-test-baseline'
    headSha          = $HeadSha
    baseSha          = $BaseSha
    headExitCode     = $HeadExitCode
    baseExitCode     = $BaseExitCode
    head             = [ordered]@{
        total   = $head.counters.total
        passed  = $head.counters.passed
        failed  = $head.counters.failed
        skipped = $head.counters.skipped
        trx     = $head.trxFiles
    }
    base             = [ordered]@{
        total   = $base.counters.total
        passed  = $base.counters.passed
        failed  = $base.counters.failed
        skipped = $base.counters.skipped
        trx     = $base.trxFiles
    }
    failingSetsIdentical = $setsIdentical
    onlyOnHead       = $onlyOnHead
    onlyOnBase       = $onlyOnBase
    families         = $classified
    everyFailureClassified = $allClassified
    suitePasses      = $false
}

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
$verdictPath = Join-Path $EvidenceDir 'windows-test-baseline.json'
$verdict | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verdictPath -Encoding utf8NoBOM

# Said in words, never as a pass. The suite is RED on both trees; what this gate
# asserts is that it is red in exactly the same places.
$summary = @(
    "### Windows test suite: RED on both trees, by design of this gate"
    ""
    "This job never reports the suite as passing. It asserts that an"
    "architecture-only pull request does not change WHICH tests fail."
    ""
    "| | head ``$HeadSha`` | base ``$BaseSha`` |"
    "| --- | --- | --- |"
    "| total | $($head.counters.total) | $($base.counters.total) |"
    "| passed | $($head.counters.passed) | $($base.counters.passed) |"
    "| failed | $($head.counters.failed) | $($base.counters.failed) |"
    "| skipped | $($head.counters.skipped) | $($base.counters.skipped) |"
    "| ``dotnet test`` exit | $HeadExitCode | $BaseExitCode |"
    ""
    "failing sets identical: **$setsIdentical**"
    "only on head: $($onlyOnHead.Count) -- $($onlyOnHead -join ', ')"
    "only on base: $($onlyOnBase.Count) -- $($onlyOnBase -join ', ')"
    ""
    "| family | count |"
    "| --- | --- |"
)
foreach ($family in $classified.Keys) {
    $summary += "| $family | $($classified[$family].Count) |"
}
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
$summary | Write-Host

if (-not $setsIdentical) {
    throw ("W0 HARD STOP: the native Windows failing-test SET differs between the head and its base. " +
           "only-on-head=[$($onlyOnHead -join '; ')] only-on-base=[$($onlyOnBase -join '; ')]. " +
           "An architecture-only change must not add, remove or rename a failure.")
}

if (-not $allClassified) {
    throw ("W0 HARD STOP: $($classified['UNCLASSIFIED'].Count) failing test(s) match none of the three " +
           "families recorded in docs/distribution/W0-windows-server.md section 2.7: " +
           "[$($classified['UNCLASSIFIED'] -join '; ')].")
}

Write-Host "W0 windows-test baseline verdict written to $verdictPath"
exit 0
