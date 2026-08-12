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
$packageRoots = @(
    $env:NUGET_PACKAGES
    (Join-Path $env:USERPROFILE '.nuget\packages')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$wixPackage = $packageRoots |
    ForEach-Object { Join-Path $_ "wix\$WixVersion" } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1 |
    ForEach-Object { Get-Item -LiteralPath $_ }

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
        <ServiceControl Id="ProbeServiceControl"
                        Name="$serviceName"
                        Start="install"
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
             "install, retained-data sentinel, in-place major upgrade, file-removal repair, " +
             "refused downgrade and silent uninstall on this native Windows host. " +
             "satisfiesLifecycle=$msiSatisfies. Unsigned output reproducible across two identical " +
             "builds=$($wixFacts.msiReproducible) -- MEASURED. The service lifecycle is inside the " +
             "MSI transaction via ServiceInstall/ServiceControl under a virtual service account, " +
             "and retained data is a Permanent component so an ordinary uninstall cannot remove it.") `
    -Data $wixFacts

Remove-InstalledProbe

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

$required = @('installer.msi', 'installer.zip', 'installer.msix', 'installer.inno')
$completeness = Test-W0EvidenceComplete -Evidence $evidence -RequiredIds $required
Add-W0Fact -Evidence $evidence -Id 'control.completeness' -Bucket 'working' `
    -Detail "Incomplete-evidence control: all $($required.Count) installer candidates classified." `
    -Data $completeness

$path = Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'installer.json')
Write-Host "W0 installer evidence written to $path"

if (-not $msiSatisfies) {
    throw "W0 HARD STOP: the MSI lifecycle experiment did not satisfy the required criteria; see installer.json."
}
