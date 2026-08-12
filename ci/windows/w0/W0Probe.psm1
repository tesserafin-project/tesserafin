<#
.SYNOPSIS
    W0 native-Windows probe helpers (issue #234).

.DESCRIPTION
    W0-ONLY. This module exists to MEASURE the current native Windows behaviour of
    the unmodified Tesserafin server and to record that measurement as machine
    readable evidence. It is not part of any shipped Windows distribution, it is
    not a packaging step and nothing it produces is a production input.

    Every function here is deliberately pure or filesystem-local so that
    ci/windows/w0/tests/W0Probe.Tests.ps1 can drive it on any platform that has
    PowerShell, including the Linux gate. The probe SCRIPTS are the part that
    needs a real Windows machine; the classification logic is not.
#>

Set-StrictMode -Version Latest

# The four buckets #234 asks the W0 document to separate, plus the blocked and
# deferred ones. A fact that does not carry one of these is not evidence.
$script:W0Buckets = @(
    'working'              # already true on stock master, no test-host help
    'test-host-dependency' # only true because the RUNNER supplied something
    'missing'              # not implemented; W1..W5 must build it
    'blocked'              # cannot proceed without an external decision or fact
    'deferred'             # deliberately out of 1.1 scope
)

function Get-W0Buckets {
    <#
    .SYNOPSIS
        The closed set of evidence buckets.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    return $script:W0Buckets
}

function New-W0Evidence {
    <#
    .SYNOPSIS
        Start an evidence ledger for one probe.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Probe,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $HeadSha
    )

    return @{
        probe   = $Probe
        headSha = $HeadSha
        facts   = [System.Collections.Generic.List[hashtable]]::new()
    }
}

function Add-W0Fact {
    <#
    .SYNOPSIS
        Record one measured fact.

    .DESCRIPTION
        Detail is free text and is what a human reads. Bucket is what the
        completeness gate reads. A fact with an unknown bucket is rejected
        here rather than at document-writing time, so an unclassified
        measurement can never reach the W0 document.
    #>
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)]
        [hashtable] $Evidence,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Id,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Bucket,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Detail,

        [Parameter()]
        [AllowNull()]
        [object] $Data = $null
    )

    if ($script:W0Buckets -notcontains $Bucket) {
        throw "W0: fact '$Id' carries unknown bucket '$Bucket'. Known: $($script:W0Buckets -join ', ')."
    }

    if ($Evidence.facts.Where({ $_.id -eq $Id }, 'First').Count -gt 0) {
        throw "W0: fact '$Id' recorded twice. Evidence identifiers are unique by construction."
    }

    $Evidence.facts.Add(@{
        id     = $Id
        bucket = $Bucket
        detail = $Detail
        data   = $Data
    })
}

function Save-W0Evidence {
    <#
    .SYNOPSIS
        Write the ledger as JSON.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [hashtable] $Evidence,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Path
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $payload = [ordered]@{
        probe   = $Evidence.probe
        headSha = $Evidence.headSha
        facts   = @($Evidence.facts | ForEach-Object {
            [ordered]@{
                id     = $_.id
                bucket = $_.bucket
                detail = $_.detail
                data   = $_.data
            }
        })
    }

    $json = $payload | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $Path -Value $json -Encoding utf8NoBOM
    return $Path
}

function Get-W0PeMachine {
    <#
    .SYNOPSIS
        Read the COFF machine type out of a PE image.

    .DESCRIPTION
        A wrong-architecture negative control needs an answer that does not come
        from the thing under test. Reading the PE header directly means an x86 or
        arm64 binary is rejected on its bytes rather than on whether it happened
        to start on this particular host.

        e_lfanew lives at 0x3C, the PE signature at that offset, the COFF machine
        word four bytes after it.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Path
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40) {
        throw "W0: '$Path' is too small to be a PE image ($($bytes.Length) bytes)."
    }

    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "W0: '$Path' has no MZ signature; it is not a PE image."
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or ($peOffset + 6) -gt $bytes.Length) {
        throw "W0: '$Path' carries an out-of-range e_lfanew ($peOffset)."
    }

    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 `
        -or $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "W0: '$Path' has no PE\0\0 signature at e_lfanew."
    }

    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)

    switch ($machine) {
        0x8664  { return 'x64' }
        0x014C  { return 'x86' }
        0xAA64  { return 'arm64' }
        0x01C4  { return 'arm' }
        default { return ('unknown-0x{0:X4}' -f $machine) }
    }
}

function Test-W0SelfContained {
    <#
    .SYNOPSIS
        Decide from a publish directory alone whether it is self-contained.

    .DESCRIPTION
        "Self-contained" is not a claim to take from the build log. A publish
        that needs a system .NET runtime does not carry hostfxr and the runtime
        assemblies next to the apphost. Answering from the delivered tree is what
        makes the "missing self-contained runtime" negative control meaningful.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $PublishDir
    )

    $required = @(
        'hostfxr.dll'
        'hostpolicy.dll'
        'coreclr.dll'
        'System.Private.CoreLib.dll'
    )

    $missing = @($required | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $PublishDir $_))
    })

    $runtimeConfig = Join-Path $PublishDir 'tesserafin.runtimeconfig.json'
    $declaresFramework = $false
    if (Test-Path -LiteralPath $runtimeConfig) {
        $config = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
        $declaresFramework = $null -ne ($config.runtimeOptions.PSObject.Properties |
            Where-Object { $_.Name -in @('framework', 'frameworks') })
    }

    return @{
        selfContained     = ($missing.Count -eq 0) -and -not $declaresFramework
        missing           = $missing
        declaresFramework = $declaresFramework
    }
}

function Test-W0EvidenceComplete {
    <#
    .SYNOPSIS
        The incomplete-evidence negative control.

    .DESCRIPTION
        A probe that silently skips a measurement must not read as a green probe.
        This asserts that every identifier the caller declared mandatory is
        actually present and classified. It returns the verdict rather than
        throwing so the caller can report every gap at once.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [hashtable] $Evidence,

        [Parameter(Mandatory)]
        [string[]] $RequiredIds
    )

    $present = @($Evidence.facts | ForEach-Object { $_.id })
    $absent = @($RequiredIds | Where-Object { $present -notcontains $_ })

    $unclassified = @($Evidence.facts |
        Where-Object { $script:W0Buckets -notcontains $_.bucket } |
        ForEach-Object { $_.id })

    return @{
        complete     = ($absent.Count -eq 0) -and ($unclassified.Count -eq 0)
        absent       = $absent
        unclassified = $unclassified
    }
}

function Get-W0TreeDigest {
    <#
    .SYNOPSIS
        Path-and-content digest of a delivered tree.

    .DESCRIPTION
        #234 requires a complete delivered-path comparison BEFORE a digest
        comparison, so this returns both: the ordered relative path list and a
        single digest over "<relative path>\n<sha256>\n" lines. Two trees that
        differ only in which files exist are then distinguishable from two trees
        that differ in content, which a single opaque digest cannot do.

        Paths are normalised to forward slashes and ordered ordinally so the
        answer does not depend on the enumerating filesystem. That is the same
        defect OpenApiXmlDocumentationOrderTests guards on the server side.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Root
    )

    $rootFull = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('/', '\')
    $entries = [System.Collections.Generic.List[string]]::new()

    Get-ChildItem -LiteralPath $rootFull -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $entries.Add("${relative}`n${hash}")
    }

    $ordered = @($entries | Sort-Object -CaseSensitive)
    $manifest = ($ordered -join "`n") + "`n"

    $stream = [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($manifest))
    try {
        $digest = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()
    } finally {
        $stream.Dispose()
    }

    return @{
        paths  = @($ordered | ForEach-Object { $_.Split("`n")[0] })
        digest = $digest
        count  = $ordered.Count
    }
}

Export-ModuleMember -Function `
    Get-W0Buckets, `
    New-W0Evidence, `
    Add-W0Fact, `
    Save-W0Evidence, `
    Get-W0PeMachine, `
    Test-W0SelfContained, `
    Test-W0EvidenceComplete, `
    Get-W0TreeDigest
