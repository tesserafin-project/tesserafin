<#
    Tests for the W0 probe harness (#234).

    These cover the CLASSIFICATION and INSPECTION logic, which is what decides
    whether a probe run reads as green. They run on any platform with PowerShell,
    including the Linux gate, because a harness whose own correctness could only
    be checked on the machine under test would be checking nothing.

    The probe SCRIPTS themselves need a real Windows host and are exercised by
    .github/workflows/w0-windows-probe.yml.
#>

BeforeAll {
    $modulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'W0Probe.psm1'
    Import-Module $modulePath -Force
}

Describe 'Add-W0Fact' {
    It 'rejects a bucket outside the closed set' {
        $evidence = New-W0Evidence -Probe 'unit' -HeadSha 'deadbeef'
        { Add-W0Fact -Evidence $evidence -Id 'x' -Bucket 'probably-fine' -Detail 'd' } |
            Should -Throw '*unknown bucket*'
    }

    It 'accepts every declared bucket' {
        foreach ($bucket in Get-W0Buckets) {
            $evidence = New-W0Evidence -Probe 'unit' -HeadSha 'deadbeef'
            { Add-W0Fact -Evidence $evidence -Id 'x' -Bucket $bucket -Detail 'd' } | Should -Not -Throw
        }
    }

    It 'refuses a duplicate identifier so a later fact cannot quietly replace an earlier one' {
        $evidence = New-W0Evidence -Probe 'unit' -HeadSha 'deadbeef'
        Add-W0Fact -Evidence $evidence -Id 'x' -Bucket 'working' -Detail 'first'
        { Add-W0Fact -Evidence $evidence -Id 'x' -Bucket 'missing' -Detail 'second' } |
            Should -Throw '*recorded twice*'
    }
}

Describe 'Test-W0EvidenceComplete -- the incomplete-evidence negative control' {
    It 'reports a skipped measurement rather than passing' {
        $evidence = New-W0Evidence -Probe 'unit' -HeadSha 'deadbeef'
        Add-W0Fact -Evidence $evidence -Id 'present' -Bucket 'working' -Detail 'd'

        $result = Test-W0EvidenceComplete -Evidence $evidence -RequiredIds @('present', 'skipped')
        $result.complete | Should -BeFalse
        $result.absent | Should -Contain 'skipped'
    }

    It 'passes only when every required measurement is present' {
        $evidence = New-W0Evidence -Probe 'unit' -HeadSha 'deadbeef'
        Add-W0Fact -Evidence $evidence -Id 'a' -Bucket 'working' -Detail 'd'
        Add-W0Fact -Evidence $evidence -Id 'b' -Bucket 'missing' -Detail 'd'

        (Test-W0EvidenceComplete -Evidence $evidence -RequiredIds @('a', 'b')).complete | Should -BeTrue
    }
}

Describe 'Get-W0PeMachine -- the wrong-architecture negative control' {
    BeforeAll {
        function New-SyntheticPe {
            param([uint16] $Machine)

            $bytes = [byte[]]::new(0x100)
            $bytes[0] = 0x4D  # M
            $bytes[1] = 0x5A  # Z
            [System.BitConverter]::GetBytes([int]0x80).CopyTo($bytes, 0x3C)
            $bytes[0x80] = 0x50  # P
            $bytes[0x81] = 0x45  # E
            $bytes[0x82] = 0x00
            $bytes[0x83] = 0x00
            [System.BitConverter]::GetBytes($Machine).CopyTo($bytes, 0x84)
            return $bytes
        }
    }

    It 'identifies <expected> from the COFF machine word' -ForEach @(
        @{ Machine = [uint16]0x8664; Expected = 'x64' }
        @{ Machine = [uint16]0x014C; Expected = 'x86' }
        @{ Machine = [uint16]0xAA64; Expected = 'arm64' }
    ) {
        $file = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-pe-{0}.bin" -f [guid]::NewGuid())
        try {
            [System.IO.File]::WriteAllBytes($file, (New-SyntheticPe -Machine $Machine))
            Get-W0PeMachine -Path $file | Should -Be $Expected
        } finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses a file that is not a PE image instead of guessing' {
        $file = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-notpe-{0}.bin" -f [guid]::NewGuid())
        try {
            [System.IO.File]::WriteAllBytes($file, [byte[]]::new(0x100))
            { Get-W0PeMachine -Path $file } | Should -Throw '*no MZ signature*'
        } finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'Test-W0SelfContained -- the missing-runtime negative control' {
    BeforeAll {
        function New-PublishFixture {
            param([switch] $Complete, [switch] $FrameworkDependent)

            $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-pub-{0}" -f [guid]::NewGuid())
            New-Item -ItemType Directory -Force -Path $dir | Out-Null

            $files = if ($Complete) {
                @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')
            } else {
                @('hostfxr.dll')
            }
            foreach ($f in $files) { Set-Content -LiteralPath (Join-Path $dir $f) -Value 'x' }

            $config = if ($FrameworkDependent) {
                '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}}}'
            } else {
                '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}'
            }
            Set-Content -LiteralPath (Join-Path $dir 'tesserafin.runtimeconfig.json') -Value $config

            return $dir
        }
    }

    It 'accepts a publish that carries its own runtime' {
        $dir = New-PublishFixture -Complete
        try {
            (Test-W0SelfContained -PublishDir $dir).selfContained | Should -BeTrue
        } finally { Remove-Item -Recurse -Force $dir }
    }

    It 'rejects a publish that is missing runtime files' {
        $dir = New-PublishFixture
        try {
            $result = Test-W0SelfContained -PublishDir $dir
            $result.selfContained | Should -BeFalse
            $result.missing | Should -Contain 'coreclr.dll'
        } finally { Remove-Item -Recurse -Force $dir }
    }

    It 'rejects a publish that still declares a shared framework' {
        $dir = New-PublishFixture -Complete -FrameworkDependent
        try {
            $result = Test-W0SelfContained -PublishDir $dir
            $result.declaresFramework | Should -BeTrue
            $result.selfContained | Should -BeFalse
        } finally { Remove-Item -Recurse -Force $dir }
    }
}

Describe 'Get-W0TreeDigest -- delivered-path comparison before digest comparison' {
    BeforeAll {
        function New-Tree {
            param([hashtable] $Files)
            $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("w0-tree-{0}" -f [guid]::NewGuid())
            foreach ($relative in $Files.Keys) {
                $full = Join-Path $dir $relative
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $full) | Out-Null
                Set-Content -LiteralPath $full -Value $Files[$relative] -NoNewline
            }
            return $dir
        }
    }

    It 'gives identical trees the same digest and the same path list' {
        $a = New-Tree -Files @{ 'a.txt' = 'one'; 'sub/b.txt' = 'two' }
        $b = New-Tree -Files @{ 'a.txt' = 'one'; 'sub/b.txt' = 'two' }
        try {
            $left = Get-W0TreeDigest -Root $a
            $right = Get-W0TreeDigest -Root $b
            $left.digest | Should -Be $right.digest
            $left.paths | Should -Be $right.paths
        } finally { Remove-Item -Recurse -Force $a, $b }
    }

    It 'distinguishes a content difference' {
        $a = New-Tree -Files @{ 'a.txt' = 'one' }
        $b = New-Tree -Files @{ 'a.txt' = 'ONE' }
        try {
            (Get-W0TreeDigest -Root $a).digest | Should -Not -Be (Get-W0TreeDigest -Root $b).digest
        } finally { Remove-Item -Recurse -Force $a, $b }
    }

    It 'distinguishes a missing delivered path before any digest is compared' {
        $a = New-Tree -Files @{ 'a.txt' = 'one'; 'b.txt' = 'two' }
        $b = New-Tree -Files @{ 'a.txt' = 'one' }
        try {
            $left = Get-W0TreeDigest -Root $a
            $right = Get-W0TreeDigest -Root $b
            $left.count | Should -Not -Be $right.count
            (Compare-Object $left.paths $right.paths) | Should -Not -BeNullOrEmpty
        } finally { Remove-Item -Recurse -Force $a, $b }
    }
}
