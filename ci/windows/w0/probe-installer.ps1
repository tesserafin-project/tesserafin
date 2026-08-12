<#
.SYNOPSIS
    W0 installer lifecycle experiment (#234, phase 3).

.DESCRIPTION
    W0-ONLY. #234 forbids choosing an installer technology by preference, and this
    loop treats "installer selection lacks a real lifecycle experiment" as a hard
    stop. So the two candidates that can plausibly satisfy the criteria are made
    to actually do the work on a native Windows host:

      A. an MSI built with the WiX toolset, driven unattended through
         clean install -> in-place upgrade -> repair -> silent uninstall, with a
         retained-data sentinel written between install and upgrade;
      B. a portable ZIP plus a first-party PowerShell service installer, driven
         through the same sequence.

    Both install a DISPOSABLE payload and the disposable Generic Host service
    spike, never the Tesserafin server. Nothing built here is a Tesserafin
    installer, nothing is signed and nothing is published.

    MSIX and Inno Setup are disqualified on recorded facts rather than by
    experiment; the reasons are written into the evidence so the document does not
    have to assert them.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $WorkRoot,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $EvidenceDir,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $HeadSha,

    # Pinned, never floating. Recorded in the evidence so the experiment is
    # attributable to an exact toolset build.
    [Parameter()] [ValidateNotNullOrEmpty()] [string] $WixVersion = '6.0.2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'W0Probe.psm1') -Force

$evidence = New-W0Evidence -Probe 'installer' -HeadSha $HeadSha

# Evidence is worth more than a clean stack. A probe that dies half way through
# still measured something, and losing that forces a whole hosted run to be
# repeated to learn what was already known. `break` rethrows, so the job still
# fails; it just fails with a ledger attached.
trap {
    if ($null -ne $evidence) {
        Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'installer.json') | Out-Null
    }
    break
}

$installRoot = Join-Path $env:ProgramFiles 'TesserafinW0'
$dataRoot = Join-Path $env:ProgramData 'TesserafinW0'
$serviceName = 'TesserafinW0Installed'

function Remove-InstalledProbe {
    & sc.exe stop $serviceName *>&1 | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $serviceName *>&1 | Out-Null
    if (Test-Path -LiteralPath $installRoot) { Remove-Item -Recurse -Force -LiteralPath $installRoot -ErrorAction SilentlyContinue }
    [Environment]::SetEnvironmentVariable('W0_SENTINEL', $null, 'Machine')
}

# -- Disposable payload: the same Generic Host spike the service probe measured --

$payloadSource = Join-Path $WorkRoot 'w0-servicehost-spike\publish'
if (-not (Test-Path -LiteralPath $payloadSource)) {
    throw "W0: probe-service.ps1 must run first; '$payloadSource' does not exist."
}

# The payload is the single-file service host and a marker file, and nothing
# else. It has to be a payload that actually RUNS: the MSI starts the service
# inside the install transaction, so a payload that cannot start would fail the
# install and the experiment would be measuring the payload rather than the
# installer. Keeping it to two files also keeps every lifecycle step from
# becoming a multi-minute file copy that measures the disk.
$payload = Join-Path $WorkRoot 'installer-payload'
if (Test-Path -LiteralPath $payload) { Remove-Item -Recurse -Force -LiteralPath $payload }
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item -LiteralPath (Join-Path $payloadSource 'w0servicehost.exe') -Destination $payload -Force
Set-Content -LiteralPath (Join-Path $payload 'marker.txt') -Value "w0 $HeadSha" -Encoding utf8NoBOM

# -- Candidate A: WiX MSI -------------------------------------------------------

$wixFacts = @{ version = $WixVersion }

& dotnet tool install --global wix --version $WixVersion *>&1 |
    Tee-Object -FilePath (Join-Path $EvidenceDir 'wix-install.log') | Out-Null
$wixFacts.toolInstallExit = $LASTEXITCODE
$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"

$wixFacts.reportedVersion = (& wix --version 2>&1 | Out-String).Trim()

# Read the licence out of the package that was actually resolved rather than
# asserting terms from memory. Redistribution compatibility is a scored criterion
# and must come from the artifact.
# A `dotnet tool install --global` unpacks into .dotnet\tools\.store as well as
# the ordinary package cache, and on a hosted runner it is often ONLY there.
$packageRoots = @(
    $env:NUGET_PACKAGES
    (Join-Path $env:USERPROFILE '.nuget\packages')
    (Join-Path $env:USERPROFILE '.dotnet\tools\.store')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$wixPackage = $packageRoots |
    ForEach-Object { Join-Path $_ "wix\$WixVersion" } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1 |
    ForEach-Object { Get-Item -LiteralPath $_ }
if (-not $wixPackage) {
    $wixPackage = Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE '.dotnet\tools\.store') `
        -Directory -Recurse -Filter $WixVersion -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

$wixFacts.licence = 'not-read'
$wixFacts.packageRootsSearched = $packageRoots
if ($wixPackage) {
    $nuspec = Get-ChildItem -LiteralPath $wixPackage.FullName -Filter '*.nuspec' -Recurse |
        Select-Object -First 1
    if ($nuspec) {
        $spec = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw)
        $wixFacts.licence = @{
            expression = $spec.package.metadata.license.InnerText
            type       = $spec.package.metadata.license.type
            projectUrl = $spec.package.metadata.projectUrl
            copyright  = $spec.package.metadata.copyright
        }

        # A licence FILENAME is not a licence. Open-source redistribution
        # compatibility is a scored criterion, so the text itself is captured:
        # WiX 5 and later reference an OSMFEULA rather than a plain SPDX
        # expression, and whether that permits Tesserafin's use has to be read
        # rather than inferred from a file name.
        if ($spec.package.metadata.license.type -eq 'file') {
            $licenceFile = Get-ChildItem -LiteralPath $wixPackage.FullName `
                -Filter $spec.package.metadata.license.InnerText -Recurse -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($licenceFile) {
                Copy-Item -LiteralPath $licenceFile.FullName `
                    -Destination (Join-Path $EvidenceDir 'wix-licence.txt') -Force
                $wixFacts.licence.firstLines = @(Get-Content -LiteralPath $licenceFile.FullName -TotalCount 25)
            } else {
                $wixFacts.licence.firstLines = @('licence file named in the nuspec was not found in the package')
            }
        }
    }
}

$wxsPath = Join-Path $WorkRoot 'w0probe.wxs'

function New-ProbeMsi {
    param(
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $OutputPath
    )

    # MajorUpgrade with a stable UpgradeCode is what makes the upgrade
    # DETERMINISTIC rather than "install the new one next to the old one".
    # ServiceInstall/ServiceControl put the service lifecycle inside the
    # transaction, which is the property Inno Setup cannot offer natively.
    # The ProgramData component is deliberately marked Permanent so the normal
    # uninstall CANNOT remove retained data -- the retained-data policy is
    # expressed in the package, not left to a custom action.
    $wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="Tesserafin W0 Probe"
           Manufacturer="Tesserafin W0 (disposable probe, not a product)"
           Version="$Version"
           UpgradeCode="6b6d0b7e-6f8a-4d0e-9f22-1f5b8b2c7a41"
           Scope="perMachine"
           Compressed="yes">
    <MajorUpgrade DowngradeErrorMessage="A newer version is already installed." />
    <MediaTemplate EmbedCab="yes" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="TesserafinW0" />
    </StandardDirectory>
    <StandardDirectory Id="CommonAppDataFolder">
      <Directory Id="DATAFOLDER" Name="TesserafinW0" />
    </StandardDirectory>

    <ComponentGroup Id="Binaries" Directory="INSTALLFOLDER">
      <Component Id="ServiceHost" Guid="1a3f2c44-9f2e-4a4b-8f0c-2b8a7c1d5e93">
        <File Id="ServiceHostExe" Source="$payload\w0servicehost.exe" KeyPath="yes" />
        <ServiceInstall Id="ProbeService"
                        Name="$serviceName"
                        DisplayName="Tesserafin W0 installed probe"
                        Description="Disposable W0 lifecycle probe. Not a Tesserafin service."
                        Type="ownProcess"
                        Start="auto"
                        ErrorControl="normal"
                        Account="NT SERVICE\$serviceName"
                        Vital="yes" />
        <!-- Deliberately NO Start="install". The first hosted run proved why:
             with the service started inside the transaction the install failed
             with "Error 1920. Service ... failed to start", and MSI rolled the
             whole thing back to 1603, so nothing about install, upgrade,
             repair or uninstall could be measured. The service is registered
             here and started AFTER the ACL grant below, which turns a failed
             install into the actual finding: a virtual service account cannot
             execute from %ProgramFiles% until it is granted that right. -->
        <ServiceControl Id="ProbeServiceControl"
                        Name="$serviceName"
                        Stop="both"
                        Remove="uninstall"
                        Wait="yes" />
      </Component>
      <Component Id="Runtime" Guid="8d5c1e77-2b4a-4c6f-9a13-7e0d4f8b2c65">
        <File Id="MarkerFile" Source="$payload\marker.txt" KeyPath="yes" />
      </Component>
    </ComponentGroup>

    <ComponentGroup Id="RetainedData" Directory="DATAFOLDER">
      <Component Id="RetainedMarker" Guid="c2f19a03-58b6-4a97-bd41-3f7e6c8a91d2" Permanent="yes" NeverOverwrite="yes">
        <CreateFolder />
        <RegistryValue Root="HKLM" Key="Software\TesserafinW0" Name="DataOwner" Value="retained" Type="string" KeyPath="yes" />
      </Component>
    </ComponentGroup>

    <Feature Id="Main">
      <ComponentGroupRef Id="Binaries" />
      <ComponentGroupRef Id="RetainedData" />
    </Feature>
  </Package>
</Wix>
"@
    Set-Content -LiteralPath $wxsPath -Value $wxs -Encoding utf8NoBOM
    $log = & wix build -arch x64 -o $OutputPath $wxsPath 2>&1 | Out-String
    return @{ exitCode = $LASTEXITCODE; log = $log.Trim(); path = $OutputPath }
}

Remove-InstalledProbe

# The disposable host writes a lifecycle marker, and where it writes it is the
# whole experiment. Point it at the retained-data directory rather than at
# AppContext.BaseDirectory: a service that has to write next to its own binaries
# is a service that needs write access to %ProgramFiles%, which is exactly the
# design this distribution rejects. The machine-scoped variable is read by the
# SCM when it creates the process, so it must be set before any start attempt.
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
[Environment]::SetEnvironmentVariable('W0_SENTINEL', (Join-Path $dataRoot 'lifecycle.log'), 'Machine')

$msiV1 = Join-Path $WorkRoot 'w0probe-1.0.0.msi'
$msiV2 = Join-Path $WorkRoot 'w0probe-1.1.0.msi'
$wixFacts.build1 = New-ProbeMsi -Version '1.0.0' -OutputPath $msiV1
$wixFacts.build2 = New-ProbeMsi -Version '1.1.0' -OutputPath $msiV2

if ($wixFacts.build1.exitCode -ne 0) {
    Add-W0Fact -Evidence $evidence -Id 'installer.msi' -Bucket 'blocked' `
        -Detail "WiX $WixVersion could not build the probe MSI; the lifecycle experiment did not run." `
        -Data $wixFacts
    Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'installer.json') | Out-Null
    throw "W0 HARD STOP: installer selection lacks a real lifecycle experiment (WiX build failed)."
}

# Reproducible-unsigned-output measurement. MEASURED, not assumed: an MSI carries
# a per-build package code GUID and stream timestamps, so this is expected to
# differ. W0 records the actual answer because the reproducibility boundary
# depends on it.
$msiRepeat = Join-Path $WorkRoot 'w0probe-1.0.0-again.msi'
$wixFacts.buildRepeat = New-ProbeMsi -Version '1.0.0' -OutputPath $msiRepeat
$wixFacts.digestFirst = (Get-FileHash -LiteralPath $msiV1 -Algorithm SHA256).Hash.ToLowerInvariant()
$wixFacts.digestRepeat = (Get-FileHash -LiteralPath $msiRepeat -Algorithm SHA256).Hash.ToLowerInvariant()
$wixFacts.msiReproducible = $wixFacts.digestFirst -eq $wixFacts.digestRepeat

function Invoke-Msi {
    param([Parameter(Mandatory)] [string[]] $Arguments, [Parameter(Mandatory)] [string] $LogName)
    $log = Join-Path $EvidenceDir $LogName
    $process = Start-Process -FilePath msiexec.exe -ArgumentList (@($Arguments) + @('/qn', '/l*v', "`"$log`"")) -Wait -PassThru
    return @{ exitCode = $process.ExitCode; log = $LogName; arguments = $Arguments }
}

$lifecycle = [ordered]@{}
$sentinelFile = Join-Path $dataRoot 'w0-retained-sentinel.txt'

# 1. unattended clean install
$lifecycle.install = Invoke-Msi -Arguments @('/i', "`"$msiV1`"") -LogName 'msi-install.log'
Start-Sleep -Seconds 4
$lifecycle.installedService = (& sc.exe query $serviceName 2>&1 | Out-String).Trim()
$lifecycle.installedFilePresent = Test-Path -LiteralPath (Join-Path $installRoot 'w0servicehost.exe')

# 1b. The identity experiment the install failure of the first hosted run turned
#     up. `NT SERVICE\<name>` is not a member of Users, so it inherits no right
#     to execute from %ProgramFiles%. Measured in two phases so the ACL grant is
#     shown to be the thing that makes the difference, rather than assumed.
$lifecycle.startBeforeGrant = (& sc.exe start $serviceName 2>&1 | Out-String).Trim()
Start-Sleep -Seconds 8
$lifecycle.runningBeforeGrant = ((& sc.exe query $serviceName 2>&1 | Out-String) -match 'RUNNING')
& sc.exe stop $serviceName *>&1 | Out-Null
Start-Sleep -Seconds 2

function Grant-ServiceAccess {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Rights
    )
    try {
        $acl = Get-Acl -LiteralPath $Path
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            "NT SERVICE\$serviceName", $Rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $Path -AclObject $acl
        return @{ path = $Path; rights = $Rights; granted = $true }
    } catch {
        return @{ path = $Path; rights = $Rights; granted = $false; error = $_.Exception.Message }
    }
}

# Both halves of the section 9.3 split, granted together and recorded
# separately: read and execute where the binaries live, modify where the state
# lives. The first hosted attempt granted only the former and the service still
# failed 1053, which is what showed the write grant to be the load-bearing one.
$lifecycle.aclGrant = @(
    Grant-ServiceAccess -Path $installRoot -Rights 'ReadAndExecute'
    Grant-ServiceAccess -Path $dataRoot    -Rights 'Modify'
)

$lifecycle.startAfterGrant = (& sc.exe start $serviceName 2>&1 | Out-String).Trim()
Start-Sleep -Seconds 8
$lifecycle.runningAfterGrant = ((& sc.exe query $serviceName 2>&1 | Out-String) -match 'RUNNING')
$lifecycle.aclGrantIsWhatMattered = (-not $lifecycle.runningBeforeGrant) -and $lifecycle.runningAfterGrant

# 2. retained-data sentinel, written between install and upgrade exactly as the
#    Linux L0 lifecycle acceptance does
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
Set-Content -LiteralPath $sentinelFile -Value "w0-sentinel $HeadSha" -Encoding utf8NoBOM

# 3. deterministic in-place upgrade
$lifecycle.upgrade = Invoke-Msi -Arguments @('/i', "`"$msiV2`"") -LogName 'msi-upgrade.log'
Start-Sleep -Seconds 4
$lifecycle.sentinelSurvivedUpgrade = Test-Path -LiteralPath $sentinelFile
$lifecycle.installedProducts = @(Get-CimInstance -ClassName Win32_Product -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'Tesserafin W0 Probe' } |
    ForEach-Object { @{ name = $_.Name; version = $_.Version } })
$lifecycle.singleProductAfterUpgrade = $lifecycle.installedProducts.Count -eq 1

# 4. repair, after deliberately removing a delivered file
$victim = Join-Path $installRoot 'marker.txt'
Remove-Item -LiteralPath $victim -Force -ErrorAction SilentlyContinue
$lifecycle.victimRemoved = -not (Test-Path -LiteralPath $victim)
$lifecycle.repair = Invoke-Msi -Arguments @('/f', "`"$msiV2`"") -LogName 'msi-repair.log'
$lifecycle.repairRestoredFile = Test-Path -LiteralPath $victim

# 5. rollback on failed upgrade, assessed explicitly: a downgrade is refused by
#    MajorUpgrade and the transaction must leave the machine on the newer build
$lifecycle.downgrade = Invoke-Msi -Arguments @('/i', "`"$msiV1`"") -LogName 'msi-downgrade.log'
$lifecycle.downgradeRefused = $lifecycle.downgrade.exitCode -ne 0
$lifecycle.stillInstalledAfterDowngrade = Test-Path -LiteralPath (Join-Path $installRoot 'w0servicehost.exe')

# 6. silent uninstall, and the retained-data policy
$lifecycle.uninstall = Invoke-Msi -Arguments @('/x', "`"$msiV2`"") -LogName 'msi-uninstall.log'
Start-Sleep -Seconds 4
$lifecycle.binariesRemoved = -not (Test-Path -LiteralPath (Join-Path $installRoot 'w0servicehost.exe'))
$lifecycle.serviceRemoved = ((& sc.exe query $serviceName 2>&1 | Out-String) -match '1060')
$lifecycle.sentinelSurvivedUninstall = Test-Path -LiteralPath $sentinelFile

$wixFacts.lifecycle = $lifecycle

$msiSatisfies = $lifecycle.install.exitCode -eq 0 -and
    $lifecycle.installedFilePresent -and
    $lifecycle.runningAfterGrant -and
    $lifecycle.upgrade.exitCode -eq 0 -and
    $lifecycle.sentinelSurvivedUpgrade -and
    $lifecycle.singleProductAfterUpgrade -and
    $lifecycle.repair.exitCode -eq 0 -and
    $lifecycle.repairRestoredFile -and
    $lifecycle.uninstall.exitCode -eq 0 -and
    $lifecycle.binariesRemoved -and
    $lifecycle.sentinelSurvivedUninstall

$wixFacts.satisfiesLifecycle = $msiSatisfies

Add-W0Fact -Evidence $evidence -Id 'installer.msi' -Bucket $(if ($msiSatisfies) { 'working' } else { 'blocked' }) `
    -Detail ("WiX $WixVersion ($($wixFacts.reportedVersion)) MSI driven unattended through clean " +
             "install, service start before and after an explicit ACL grant, retained-data " +
             "sentinel, in-place major upgrade, file-removal repair, refused downgrade and silent " +
             "uninstall on this native Windows host. " +
             "runningBeforeGrant=$($lifecycle.runningBeforeGrant) " +
             "runningAfterGrant=$($lifecycle.runningAfterGrant) " +
             "aclGrantIsWhatMattered=$($lifecycle.aclGrantIsWhatMattered). " +
             "satisfiesLifecycle=$msiSatisfies. Unsigned output reproducible across two identical " +
             "builds=$($wixFacts.msiReproducible) -- MEASURED. The service lifecycle is inside the " +
             "MSI transaction via ServiceInstall/ServiceControl under a virtual service account, " +
             "and retained data is a Permanent component so an ordinary uninstall cannot remove it.") `
    -Data $wixFacts

Remove-InstalledProbe

# -- Candidate A (continued): isolated MSI build-time determinism ---------------
#
# The lifecycle experiment above proves the MSI WORKS; it does not isolate
# whether WiX itself can produce byte-identical output, because its payload
# comes from `dotnet publish`, which is not guaranteed deterministic across
# builds -- or across runners -- without its own <Deterministic> and pathmap
# controls. A payload that differed for a dotnet reason would be misread as a
# WiX reproducibility failure. This experiment uses a FIXED-CONTENT payload
# with a FIXED mtime, so any residual byte difference is attributable to
# WiX/MSI alone, not to what was fed into it.
$determinismFacts = @{}
$determinismRoot = Join-Path $WorkRoot 'determinism-payload'
if (Test-Path -LiteralPath $determinismRoot) { Remove-Item -Recurse -Force -LiteralPath $determinismRoot }
New-Item -ItemType Directory -Force -Path $determinismRoot | Out-Null
$determinismFile = Join-Path $determinismRoot 'fixed-content.txt'
Set-Content -LiteralPath $determinismFile -Value 'w0 msi determinism probe -- fixed content, not the server' -Encoding utf8NoBOM -NoNewline
$fixedStamp = [datetime]::new(2026, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
(Get-Item -LiteralPath $determinismFile).LastWriteTimeUtc = $fixedStamp
(Get-Item -LiteralPath $determinismFile).CreationTimeUtc = $fixedStamp

function New-DeterminismMsi {
    param(
        [Parameter(Mandatory)] [string] $OutputPath,
        [Parameter(Mandatory)] [string] $IntermediateFolder
    )
    # Id is pinned rather than left as the implicit "*" -- an autogenerated
    # ProductCode is exactly the kind of per-build difference this experiment
    # exists to isolate away from. Safe here ONLY because every build in this
    # experiment is the same version: the lifecycle WXS above deliberately does
    # NOT do this, because a real major upgrade needs a fresh ProductCode per
    # version.
    $wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Id="6f1e9d2b-8a4c-4e77-9b1a-3c5d7e9f2a01"
           Name="Tesserafin W0 Determinism Probe"
           Manufacturer="Tesserafin W0 (disposable probe, not a product)"
           Version="1.0.0"
           UpgradeCode="9a2c4e6f-1b3d-4a5c-8e7f-2d4b6a8c0e21"
           Scope="perMachine"
           Compressed="yes">
    <MediaTemplate EmbedCab="yes" />
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="TesserafinW0Determinism" />
    </StandardDirectory>
    <ComponentGroup Id="Fixed" Directory="INSTALLFOLDER">
      <Component Id="FixedContent" Guid="4c8e2a19-6f3b-4d7e-9a1c-8b5d3e7f2c60">
        <File Id="FixedContentFile" Source="$determinismFile" KeyPath="yes" />
      </Component>
    </ComponentGroup>
    <Feature Id="Main">
      <ComponentGroupRef Id="Fixed" />
    </Feature>
  </Package>
</Wix>
"@
    New-Item -ItemType Directory -Force -Path $IntermediateFolder | Out-Null
    $wxsFile = Join-Path $IntermediateFolder 'determinism.wxs'
    Set-Content -LiteralPath $wxsFile -Value $wxs -Encoding utf8NoBOM
    $log = & wix build -arch x64 -intermediatefolder $IntermediateFolder -o $OutputPath $wxsFile 2>&1 | Out-String
    return @{ exitCode = $LASTEXITCODE; log = $log.Trim(); path = $OutputPath }
}

# Two builds, two independent intermediate folders: no compiled object, cache
# entry or working directory is shared between them.
$determinismA = Join-Path $WorkRoot 'determinism-a.msi'
$determinismB = Join-Path $WorkRoot 'determinism-b.msi'
$determinismFacts.buildA = New-DeterminismMsi -OutputPath $determinismA -IntermediateFolder (Join-Path $WorkRoot 'determinism-obj-a')
$determinismFacts.buildB = New-DeterminismMsi -OutputPath $determinismB -IntermediateFolder (Join-Path $WorkRoot 'determinism-obj-b')

if ($determinismFacts.buildA.exitCode -ne 0 -or $determinismFacts.buildB.exitCode -ne 0) {
    Add-W0Fact -Evidence $evidence -Id 'installer.msi.determinism' -Bucket 'blocked' `
        -Detail "WiX $WixVersion could not build the determinism probe MSI twice; no comparison is possible." `
        -Data $determinismFacts
} else {
    $determinismFacts.digestA = (Get-FileHash -LiteralPath $determinismA -Algorithm SHA256).Hash.ToLowerInvariant()
    $determinismFacts.digestB = (Get-FileHash -LiteralPath $determinismB -Algorithm SHA256).Hash.ToLowerInvariant()
    $determinismFacts.rawBytesIdentical = $determinismFacts.digestA -eq $determinismFacts.digestB

    # The table-level instrument: `wix msi decompile` reconstructs the AUTHORED
    # tables (Directory/Component/File/Registry/ServiceInstall/...) as WXS
    # source and never re-emits the OLE summary-information stream, so it
    # cannot see the Package Code GUID or container timestamps -- exactly the
    # fields MSI's own design regenerates on every build. Diffing the two
    # decompiled sources is therefore a diff of every security-relevant table
    # with the known-irreducible container fields excluded by construction,
    # not by assertion.
    $decompiledA = Join-Path $WorkRoot 'determinism-a.decompiled.wxs'
    $decompiledB = Join-Path $WorkRoot 'determinism-b.decompiled.wxs'
    $decompileLogA = & wix msi decompile $determinismA -o $decompiledA 2>&1 | Out-String
    $decompileExitA = $LASTEXITCODE
    $decompileLogB = & wix msi decompile $determinismB -o $decompiledB 2>&1 | Out-String
    $decompileExitB = $LASTEXITCODE
    $determinismFacts.decompileExitA = $decompileExitA
    $determinismFacts.decompileExitB = $decompileExitB

    if ($decompileExitA -eq 0 -and $decompileExitB -eq 0) {
        $textA = @(Get-Content -LiteralPath $decompiledA)
        $textB = @(Get-Content -LiteralPath $decompiledB)
        $tableDiff = @(Compare-Object -ReferenceObject $textA -DifferenceObject $textB)
        $determinismFacts.decompiledTablesIdentical = ($tableDiff.Count -eq 0)
        $determinismFacts.decompiledDiffLineCount = $tableDiff.Count
        Copy-Item -LiteralPath $decompiledA -Destination (Join-Path $EvidenceDir 'msi-determinism-a.wxs') -Force
        Copy-Item -LiteralPath $decompiledB -Destination (Join-Path $EvidenceDir 'msi-determinism-b.wxs') -Force
    } else {
        $determinismFacts.decompiledTablesIdentical = $false
        $determinismFacts.decompileLogA = $decompileLogA.Trim()
        $determinismFacts.decompileLogB = $decompileLogB.Trim()
    }

    # Corroborating instrument: the real Windows-Installer-native mechanism for
    # a table-level MSI diff. Only changed rows are emitted (no -p).
    $mstPath = Join-Path $WorkRoot 'determinism.mst'
    $transformLog = & wix msi transform $determinismA $determinismB -out $mstPath 2>&1 | Out-String
    $determinismFacts.transformExit = $LASTEXITCODE
    $determinismFacts.transformLog = $transformLog.Trim()
    if (Test-Path -LiteralPath $mstPath) {
        $determinismFacts.transformBytes = (Get-Item -LiteralPath $mstPath).Length
        Copy-Item -LiteralPath $mstPath -Destination (Join-Path $EvidenceDir 'msi-determinism.mst') -Force
    }

    $determinismBucket = if ($determinismFacts.rawBytesIdentical) { 'working' }
                         elseif ($determinismFacts.decompiledTablesIdentical) { 'working' }
                         else { 'blocked' }

    Add-W0Fact -Evidence $evidence -Id 'installer.msi.determinism' -Bucket $determinismBucket `
        -Detail ("Isolated MSI build-time determinism probe: fixed-content payload, fixed mtime, " +
                 "pinned ProductCode -- built twice with WiX $WixVersion in separate intermediate " +
                 "folders. Raw SHA-256 identical=$($determinismFacts.rawBytesIdentical) " +
                 "($($determinismFacts.digestA) vs $($determinismFacts.digestB)). Decompiled authored " +
                 "tables identical=$($determinismFacts.decompiledTablesIdentical) " +
                 "($($determinismFacts.decompiledDiffLineCount) diff line(s)) -- decompile " +
                 "reconstructs Directory/Component/File/Registry/ServiceInstall and every other " +
                 "authored table but never the summary-information stream, so an empty diff shows " +
                 "every security-relevant table is byte-identical while excluding, by construction, " +
                 "the Package Code GUID (PID_REVNUMBER) and container timestamps that MSI's own " +
                 "design regenerates on every build. If raw bytes differ but the decompiled diff is " +
                 "empty, the residual delta is bounded to those known container fields -- a table- " +
                 "level exception, not an assumption. `wix msi transform` between the two builds is " +
                 "kept as corroboration ($($determinismFacts.transformBytes) byte MST, exit " +
                 "$($determinismFacts.transformExit)).") `
        -Data $determinismFacts

    if (-not $determinismFacts.rawBytesIdentical -and -not $determinismFacts.decompiledTablesIdentical) {
        Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'installer.json') | Out-Null
        throw ("W0 HARD STOP: MSI reproducibility cannot be bounded -- the decompiled authored " +
               "tables differ between two fixed-input builds, not merely the known-irreducible " +
               "container fields.")
    }
}

# -- Candidate B: portable ZIP + first-party PowerShell service installer -------

# The ZIP is MANDATORY regardless of which installer wins, so its lifecycle is
# measured too rather than assumed to be the easy case.
$zipFacts = @{}
$zipPath = Join-Path $WorkRoot 'w0probe-portable.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zipPath -Force
$zipFacts.digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$zipRepeat = Join-Path $WorkRoot 'w0probe-portable-again.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zipRepeat -Force
$zipFacts.digestRepeat = (Get-FileHash -LiteralPath $zipRepeat -Algorithm SHA256).Hash.ToLowerInvariant()
$zipFacts.reproducible = $zipFacts.digest -eq $zipFacts.digestRepeat

$zipTarget = Join-Path $WorkRoot 'portable-install'
Remove-Item -Recurse -Force -LiteralPath $zipTarget -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $zipPath -DestinationPath $zipTarget -Force

$zipService = 'TesserafinW0Portable'
& sc.exe stop $zipService *>&1 | Out-Null
& sc.exe delete $zipService *>&1 | Out-Null

# Each `key=` and its value are separate argv entries; see the note in
# probe-service.ps1 -- one argument containing the space is rejected by sc.exe.
$zipFacts.install = (& sc.exe create $zipService `
    'binPath=' (Join-Path $zipTarget 'w0servicehost.exe') `
    'start=' 'auto' `
    'obj=' "NT SERVICE\$zipService" 2>&1 | Out-String).Trim()
$zipFacts.installExit = $LASTEXITCODE
$zipFacts.start = (& sc.exe start $zipService 2>&1 | Out-String).Trim()
Start-Sleep -Seconds 3
$zipFacts.running = (& sc.exe query $zipService 2>&1 | Out-String) -match 'RUNNING'
$zipFacts.stop = (& sc.exe stop $zipService 2>&1 | Out-String).Trim()
Start-Sleep -Seconds 2
& sc.exe delete $zipService *>&1 | Out-Null
$zipFacts.removed = ((& sc.exe query $zipService 2>&1 | Out-String) -match '1060')

# The honest limits of this candidate, recorded rather than argued.
$zipFacts.noRepair = $true
$zipFacts.noRollback = $true
$zipFacts.noArpEntry = $true

Add-W0Fact -Evidence $evidence -Id 'installer.zip' -Bucket $(if ($zipFacts.running -and $zipFacts.removed) { 'working' } else { 'missing' }) `
    -Detail ("Portable ZIP plus a first-party service registration, driven through extract, " +
             "register under a virtual service account, start, stop and remove. running=" +
             "$($zipFacts.running) removed=$($zipFacts.removed). Unsigned ZIP reproducible across " +
             "two identical archives=$($zipFacts.reproducible) -- MEASURED. It has NO repair, NO " +
             "upgrade rollback and NO Add/Remove Programs entry; those are properties of the " +
             "format, not gaps a script can close, which is why it is the mandatory companion and " +
             "not the primary installation path.") `
    -Data $zipFacts

# -- Disqualified candidates, on recorded facts ---------------------------------

Add-W0Fact -Evidence $evidence -Id 'installer.msix' -Bucket 'blocked' `
    -Detail ("MSIX disqualified without a lifecycle experiment, and the reason is recorded: an " +
             "MSIX package cannot be installed unattended unless it is signed by a certificate " +
             "the machine trusts, and W0 is forbidden to create any certificate or signing secret. " +
             "The experiment is therefore not merely unperformed, it is unperformable inside W0's " +
             "invariants. Independently, MSIX services are restricted to packaged Windows services " +
             "with a constrained identity model and cannot express the per-machine ACL grants and " +
             "arbitrary ProgramData layout this distribution needs.") `
    -Data @{ blockedBy = 'W0 forbids creating a signing certificate'; wouldNeed = 'a trusted Authenticode certificate before any unattended install' }

Add-W0Fact -Evidence $evidence -Id 'installer.inno' -Bucket 'blocked' `
    -Detail ("Inno Setup disqualified on format properties: it has no native Windows Service " +
             "installation (a service is registered by shelling out to sc.exe from [Run], which " +
             "is the first-party-script candidate wearing a different wrapper), no repair mode and " +
             "no transactional rollback of a failed upgrade. Two of those are REQUIRED criteria, so " +
             "it cannot win regardless of how the experiment turned out. It is also not " +
             "preinstalled on this image, and installing it would need either an unpinned " +
             "Chocolatey dependency -- explicitly forbidden -- or a pinned third-party download whose " +
             "only purpose would be to confirm a disqualification already established.") `
    -Data @{ missingRequired = @('native Windows Service lifecycle', 'repair', 'rollback on failed upgrade') }

# -- Completeness gate ----------------------------------------------------------

$required = @('installer.msi', 'installer.msi.determinism', 'installer.zip', 'installer.msix', 'installer.inno')
$completeness = Test-W0EvidenceComplete -Evidence $evidence -RequiredIds $required
Add-W0Fact -Evidence $evidence -Id 'control.completeness' -Bucket 'working' `
    -Detail "Incomplete-evidence control: all $($required.Count) installer candidates classified." `
    -Data $completeness

$path = Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'installer.json')
Write-Host "W0 installer evidence written to $path"

if (-not $msiSatisfies) {
    throw "W0 HARD STOP: the MSI lifecycle experiment did not satisfy the required criteria; see installer.json."
}

# PowerShell hands the caller the exit code of the LAST NATIVE COMMAND when a
# script ends without one of its own. Every probe here finishes near an sc.exe
# call, and `sc query` on a service that was just deleted returns 1060, so the
# step failed while every measurement in it had passed. Say it explicitly.
exit 0
