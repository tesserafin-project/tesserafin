<#
    Negative controls for the Windows failing-test baseline gate (#234).

    The gate exists because the previous one could not fail. Its own logic must
    therefore be shown to fail on each thing it claims to catch, or it is the
    same problem one level up. Every case below drives the real script against
    synthetic TRX fixtures, on any platform with PowerShell -- the comparison is
    pure XML and set arithmetic, so it does not need the machine under test.
#>

BeforeAll {
    $script:ScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Compare-W0TestBaseline.ps1'

    function New-Trx {
        <#
            One TRX file with the given outcomes. `dotnet test <solution>` emits
            one per test project, so the fixtures can place several in a
            directory to prove the reader aggregates rather than reading the
            first and stopping.
        #>
        param(
            [Parameter(Mandatory)] [string] $Path,
            [Parameter(Mandatory)] [hashtable] $Outcomes,
            [Parameter()] [int] $Total = 0,
            [Parameter()] [int] $Passed = 0,
            [Parameter()] [int] $Skipped = 0
        )

        $failed = @($Outcomes.Values | Where-Object { $_ -eq 'Failed' }).Count
        if ($Total -eq 0) { $Total = $Outcomes.Count }
        if ($Passed -eq 0) { $Passed = $Total - $failed - $Skipped }

        $results = ($Outcomes.Keys | ForEach-Object {
            "      <UnitTestResult testName=""$_"" outcome=""$($Outcomes[$_])"" />"
        }) -join "`n"

        $xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="">
  <Results>
$results
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="$Total" passed="$Passed" failed="$failed" notExecuted="$Skipped" />
  </ResultSummary>
</TestRun>
"@
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        Set-Content -LiteralPath $Path -Value $xml -Encoding utf8NoBOM
    }

    function New-Case {
        param([Parameter(Mandatory)] [hashtable] $Head, [Parameter(Mandatory)] [hashtable] $Base)

        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-cmp-{0}" -f [guid]::NewGuid())
        New-Trx -Path (Join-Path $root 'head/one.trx') -Outcomes $Head
        New-Trx -Path (Join-Path $root 'base/one.trx') -Outcomes $Base
        return $root
    }

    function Invoke-Gate {
        param([Parameter(Mandatory)] [string] $Root)

        & $script:ScriptPath `
            -HeadResultsDir (Join-Path $Root 'head') `
            -BaseResultsDir (Join-Path $Root 'base') `
            -EvidenceDir (Join-Path $Root 'evidence') `
            -HeadSha 'headsha' -BaseSha 'basesha'
    }

    # Real names from the three families the W0 document records, so the
    # classifier is exercised against what it will actually see.
    $script:Crlf = 'Tesserafin.Server.Tests.LogForging.AuthLogTests.Hostile_WritesExactlyOnePhysicalRecord'
    $script:Stdin = 'Tesserafin.MediaEncoding.Tests.TranscodingJobStopTests.Stop_ProcessReadsQFromStdin_ExitsGracefullyWithoutBeingKilled'
    $script:OpenApi = 'Tesserafin.Server.Tests.OpenApiXmlDocumentationOrderTests.XmlDocumentationFiles_ReadsTopLevelXmlInCanonicalOrder'
}

Describe 'the failing-test set must be identical between head and base' {
    It 'accepts an identical, fully classified failure set' {
        $root = New-Case -Head @{ $script:Crlf = 'Failed'; 'A.Passes' = 'Passed' } `
                         -Base @{ $script:Crlf = 'Failed'; 'A.Passes' = 'Passed' }
        try {
            { Invoke-Gate -Root $root } | Should -Not -Throw
            Test-Path -LiteralPath (Join-Path $root 'evidence/windows-test-baseline.json') | Should -BeTrue
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }

    It 'rejects a failure that exists ONLY on the head -- the hard stop #234 names' {
        $root = New-Case -Head @{ $script:Crlf = 'Failed'; $script:Stdin = 'Failed' } `
                         -Base @{ $script:Crlf = 'Failed'; $script:Stdin = 'Passed' }
        try {
            { Invoke-Gate -Root $root } | Should -Throw '*failing-test SET differs*'
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }

    It 'rejects a failure that DISAPPEARED on the head, because that is a changed set too' {
        $root = New-Case -Head @{ $script:Crlf = 'Failed'; $script:Stdin = 'Passed' } `
                         -Base @{ $script:Crlf = 'Failed'; $script:Stdin = 'Failed' }
        try {
            { Invoke-Gate -Root $root } | Should -Throw '*failing-test SET differs*'
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }

    It 'rejects a RENAMED failure even though the count is unchanged' {
        $renamed = $script:Crlf -replace 'AuthLogTests', 'AuthLogTestsRenamed'
        $root = New-Case -Head @{ $renamed = 'Failed' } -Base @{ $script:Crlf = 'Failed' }
        try {
            { Invoke-Gate -Root $root } | Should -Throw '*failing-test SET differs*'
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }
}

Describe 'every failure must classify into one of the three recorded families' {
    It 'accepts all three families' {
        $set = @{ $script:Crlf = 'Failed'; $script:Stdin = 'Failed'; $script:OpenApi = 'Failed' }
        $root = New-Case -Head $set -Base $set
        try {
            { Invoke-Gate -Root $root } | Should -Not -Throw
            $verdict = Get-Content -LiteralPath (Join-Path $root 'evidence/windows-test-baseline.json') -Raw | ConvertFrom-Json
            $verdict.families.'log-forging-crlf'.Count | Should -Be 1
            $verdict.families.'graceful-stop-stdin'.Count | Should -Be 1
            $verdict.families.'openapi-cross-host'.Count | Should -Be 1
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }

    It 'rejects a failure matching no family, even when both trees agree on it' {
        $set = @{ 'Tesserafin.Server.Tests.SomethingEntirelyNew.Explodes' = 'Failed' }
        $root = New-Case -Head $set -Base $set
        try {
            { Invoke-Gate -Root $root } | Should -Throw '*match none of the three*'
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }
}

Describe 'the counts are exact and aggregated, never approximate' {
    It 'sums the Counters of every TRX in the directory' {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-cmp-{0}" -f [guid]::NewGuid())
        try {
            New-Trx -Path (Join-Path $root 'head/one.trx') -Outcomes @{ $script:Crlf = 'Failed' } -Total 2000 -Passed 1999
            New-Trx -Path (Join-Path $root 'head/two.trx') -Outcomes @{ 'B.Passes' = 'Passed' } -Total 1560 -Passed 1560
            New-Trx -Path (Join-Path $root 'base/one.trx') -Outcomes @{ $script:Crlf = 'Failed' } -Total 2000 -Passed 1999
            New-Trx -Path (Join-Path $root 'base/two.trx') -Outcomes @{ 'B.Passes' = 'Passed' } -Total 1560 -Passed 1560

            Invoke-Gate -Root $root
            $verdict = Get-Content -LiteralPath (Join-Path $root 'evidence/windows-test-baseline.json') -Raw | ConvertFrom-Json
            # 3,560 exactly -- the number the document could only call "roughly".
            $verdict.head.total | Should -Be 3560
            $verdict.head.failed | Should -Be 1
            $verdict.suitePasses | Should -BeFalse
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }

    It 'refuses to treat a missing TRX as an empty result set' {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-cmp-{0}" -f [guid]::NewGuid())
        try {
            New-Trx -Path (Join-Path $root 'head/one.trx') -Outcomes @{ $script:Crlf = 'Failed' }
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'base') | Out-Null
            { Invoke-Gate -Root $root } | Should -Throw '*no .trx results found*'
        } finally { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
    }
}
