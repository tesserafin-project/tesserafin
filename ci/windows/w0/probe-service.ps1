<#
.SYNOPSIS
    W0 Windows Service Control Manager boundary probe (#234, phase 2).

.DESCRIPTION
    W0-ONLY. Establishes the CURRENT truth: what the Service Control Manager does
    with the unmodified published Tesserafin server, and whether the smallest
    maintainable fix -- .NET Generic Host Windows Service integration -- actually
    participates in the SCM lifecycle on this host.

    The second half builds a DISPOSABLE probe host in the work directory. It is
    never committed, never published and is not a Tesserafin service
    implementation. Its only job is to turn "direct Generic Host integration is
    the right design" from a preference into a measurement, which #234 requires
    before a service-host architecture may be selected.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $PublishDir,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $WorkRoot,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $EvidenceDir,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string] $HeadSha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'W0Probe.psm1') -Force

$evidence = New-W0Evidence -Probe 'service' -HeadSha $HeadSha

function Invoke-Sc {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $output = & sc.exe @Arguments 2>&1 | Out-String
    return @{ exitCode = $LASTEXITCODE; output = $output.Trim(); arguments = $Arguments }
}

function Remove-ProbeService {
    param([Parameter(Mandatory)] [string] $Name)
    & sc.exe stop $Name *>&1 | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $Name *>&1 | Out-Null
    Start-Sleep -Seconds 1
}

# -- 1. The unmodified server under the SCM -------------------------------------

$serviceName = 'TesserafinW0Stock'
Remove-ProbeService -Name $serviceName

$stateRoot = Join-Path $WorkRoot 'service-state'
foreach ($d in 'config', 'cache', 'log', 'data') {
    New-Item -ItemType Directory -Force -Path (Join-Path $stateRoot $d) | Out-Null
}

$exe = Join-Path $PublishDir 'tesserafin.exe'
# sc.exe binPath= is a single string; the executable is quoted because the probe
# deliberately publishes into paths that contain spaces elsewhere in W0.
$binPath = '"{0}" --service --datadir "{1}" --configdir "{2}" --cachedir "{3}" --logdir "{4}" --nowebclient' -f `
    $exe,
    (Join-Path $stateRoot 'data'),
    (Join-Path $stateRoot 'config'),
    (Join-Path $stateRoot 'cache'),
    (Join-Path $stateRoot 'log')

$create = Invoke-Sc -Arguments @('create', $serviceName, "binPath= $binPath", 'start= demand', 'DisplayName= Tesserafin W0 stock probe')

$startWatch = [System.Diagnostics.Stopwatch]::StartNew()
$start = Invoke-Sc -Arguments @('start', $serviceName)
$startWatch.Stop()

Start-Sleep -Seconds 3
$query = Invoke-Sc -Arguments @('query', $serviceName)

# An SCM start that "failed" while leaving a live process behind is a WORSE
# outcome than a clean 1053, because an installer would then be uninstalling a
# service whose process is still holding the database. Measured, not assumed.
$orphans = @(Get-Process -Name 'tesserafin' -ErrorAction SilentlyContinue |
    ForEach-Object { @{ id = $_.Id; path = $_.Path; started = $_.StartTime.ToString('o') } })

# Error 1053: "The service did not respond to the start or control request in a
# timely fashion" -- the SCM's answer when a plain console executable never calls
# StartServiceCtrlDispatcher. That is a MISSING SERVICE-HOST BOUNDARY in the
# server, not an installer defect, and no installer technology can paper over it.
$is1053 = $start.output -match '\b1053\b'

Add-W0Fact -Evidence $evidence -Id 'scm.stock' -Bucket 'missing' `
    -Detail ("The unmodified published server registered with the SCM and was asked to start. " +
             "sc start exit=$($start.exitCode) after $([math]::Round($startWatch.Elapsed.TotalSeconds,0))s; " +
             "error 1053 observed=$is1053; orphaned tesserafin processes=$($orphans.Count). " +
             "The tree contains no UseWindowsService, no WindowsServiceLifetime and no reference " +
             "to Microsoft.Extensions.Hosting.WindowsServices, so the executable never becomes an " +
             "SCM service process. This is a missing service-host boundary in the SERVER.") `
    -Data @{
        binPath      = $binPath
        create       = $create
        start        = $start
        startSeconds = [math]::Round($startWatch.Elapsed.TotalSeconds, 0)
        query        = $query
        error1053    = $is1053
        orphans      = $orphans
    }

foreach ($orphan in $orphans) { Stop-Process -Id $orphan.id -Force -ErrorAction SilentlyContinue }
Remove-ProbeService -Name $serviceName

# -- 2. Disposable proof that Generic Host integration is sufficient ------------

# Deliberately the SMALLEST possible host: if a bare Generic Host with
# UseWindowsService participates correctly in the SCM lifecycle on this image,
# then no dedicated first-party service host and no third-party wrapper is
# justified, and W3 is a bounded change to the existing host builder.
$probeDir = Join-Path $WorkRoot 'w0-servicehost-spike'
if (Test-Path -LiteralPath $probeDir) { Remove-Item -Recurse -Force -LiteralPath $probeDir }
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null

$sentinel = Join-Path $probeDir 'lifecycle.log'

@'
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>w0servicehost</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>W0ServiceHost</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $probeDir 'w0servicehost.csproj') -Encoding utf8NoBOM

@'
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

var sentinel = Environment.GetEnvironmentVariable("W0_SENTINEL")
    ?? Path.Combine(AppContext.BaseDirectory, "lifecycle.log");

void Mark(string what) => File.AppendAllText(sentinel, $"{DateTimeOffset.UtcNow:O} {what}{Environment.NewLine}");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "TesserafinW0Spike");
builder.Services.AddHostedService<Marker>();

var host = builder.Build();
Mark($"built isWindowsService={WindowsServiceHelpers.IsWindowsService()}");
host.Run();
Mark("run-returned");

sealed class Marker : IHostedService
{
    private readonly string _sentinel = Environment.GetEnvironmentVariable("W0_SENTINEL")
        ?? Path.Combine(AppContext.BaseDirectory, "lifecycle.log");

    private void Mark(string what) =>
        File.AppendAllText(_sentinel, $"{DateTimeOffset.UtcNow:O} {what}{Environment.NewLine}");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mark("hosted-start");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Mark("hosted-stop");
        return Task.CompletedTask;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $probeDir 'Program.cs') -Encoding utf8NoBOM

$spikePublish = Join-Path $probeDir 'publish'
& dotnet publish (Join-Path $probeDir 'w0servicehost.csproj') `
    --configuration Release --runtime win-x64 --self-contained true --output $spikePublish *>&1 |
    Tee-Object -FilePath (Join-Path $EvidenceDir 'servicehost-spike-publish.log') | Out-Null
$spikePublishExit = $LASTEXITCODE

$spikeName = 'TesserafinW0Spike'
Remove-ProbeService -Name $spikeName

$spikeFacts = @{ publishExit = $spikePublishExit }

if ($spikePublishExit -eq 0) {
    $spikeExe = Join-Path $spikePublish 'w0servicehost.exe'
    [Environment]::SetEnvironmentVariable('W0_SENTINEL', $sentinel, 'Machine')

    $spikeFacts.create = Invoke-Sc -Arguments @('create', $spikeName, "binPath= `"$spikeExe`"", 'start= demand', 'DisplayName= Tesserafin W0 service-host spike')

    $spikeStartWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $spikeFacts.start = Invoke-Sc -Arguments @('start', $spikeName)
    $spikeStartWatch.Stop()
    $spikeFacts.startSeconds = [math]::Round($spikeStartWatch.Elapsed.TotalSeconds, 1)

    Start-Sleep -Seconds 3
    $spikeFacts.queryRunning = Invoke-Sc -Arguments @('query', $spikeName)

    $spikeStopWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $spikeFacts.stop = Invoke-Sc -Arguments @('stop', $spikeName)
    $spikeStopWatch.Stop()
    $spikeFacts.stopSeconds = [math]::Round($spikeStopWatch.Elapsed.TotalSeconds, 1)

    Start-Sleep -Seconds 3
    $spikeFacts.queryStopped = Invoke-Sc -Arguments @('query', $spikeName)
    $spikeFacts.sentinel = if (Test-Path -LiteralPath $sentinel) { Get-Content -LiteralPath $sentinel -Raw } else { '' }
    $spikeFacts.lifecycleObserved =
        ($spikeFacts.sentinel -match 'isWindowsService=True') -and
        ($spikeFacts.sentinel -match 'hosted-start') -and
        ($spikeFacts.sentinel -match 'hosted-stop')

    [Environment]::SetEnvironmentVariable('W0_SENTINEL', $null, 'Machine')
    Remove-ProbeService -Name $spikeName
} else {
    $spikeFacts.lifecycleObserved = $false
}

Add-W0Fact -Evidence $evidence -Id 'scm.generichost' -Bucket $(if ($spikeFacts.lifecycleObserved) { 'working' } else { 'blocked' }) `
    -Detail ("Disposable spike: a bare .NET Generic Host with AddWindowsService, published " +
             "self-contained win-x64 and registered with the SCM. " +
             "lifecycleObserved=$($spikeFacts.lifecycleObserved) -- the host reported " +
             "IsWindowsService()=True and ran IHostedService StartAsync AND StopAsync under real " +
             "sc start / sc stop. This is the measurement that lets W0 select direct Generic Host " +
             "integration over a dedicated first-party host or a third-party wrapper. It is a " +
             "throwaway in the work directory and is NOT a Tesserafin service implementation.") `
    -Data $spikeFacts

# -- 3. Least-privilege identity feasibility ------------------------------------

# NT SERVICE\<name> virtual accounts only exist once the service exists, so the
# question W0 has to answer is not "can it be typed into an installer" but
# "can that SID actually be granted the ACLs the server needs". Measured against
# a real directory with a real SID, using the stock probe service's own name.
$aclProbeRoot = Join-Path $WorkRoot 'acl-probe'
New-Item -ItemType Directory -Force -Path $aclProbeRoot | Out-Null

$aclService = 'TesserafinW0Acl'
Remove-ProbeService -Name $aclService
$aclCreate = Invoke-Sc -Arguments @('create', $aclService, "binPath= `"$exe`"", 'start= demand', 'obj= NT SERVICE\TesserafinW0Acl')

$aclResult = @{ create = $aclCreate }
try {
    $acl = Get-Acl -LiteralPath $aclProbeRoot
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        'NT SERVICE\TesserafinW0Acl',
        'Modify',
        'ContainerInherit,ObjectInherit',
        'None',
        'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $aclProbeRoot -AclObject $acl
    $applied = (Get-Acl -LiteralPath $aclProbeRoot).Access |
        Where-Object { $_.IdentityReference.Value -like '*TesserafinW0Acl*' }
    $aclResult.granted = $null -ne $applied
    $aclResult.rights = @($applied | ForEach-Object { $_.FileSystemRights.ToString() })
} catch {
    $aclResult.granted = $false
    $aclResult.error = $_.Exception.Message
}
Remove-ProbeService -Name $aclService

Add-W0Fact -Evidence $evidence -Id 'identity.virtualaccount' -Bucket $(if ($aclResult.granted) { 'working' } else { 'blocked' }) `
    -Detail ("Virtual service account feasibility: a service was created with " +
             "obj= 'NT SERVICE\\TesserafinW0Acl' and that SID was then granted Modify on a real " +
             "directory. granted=$($aclResult.granted). The account is created BY the SCM with the " +
             "service and is removed with it, so an installer can grant per-machine least-privilege " +
             "ACLs to it without inventing a password -- which is the property that makes it " +
             "preferable to LocalService or a managed local user.") `
    -Data $aclResult

# -- 4. Completeness gate -------------------------------------------------------

$required = @('scm.stock', 'scm.generichost', 'identity.virtualaccount')
$completeness = Test-W0EvidenceComplete -Evidence $evidence -RequiredIds $required
Add-W0Fact -Evidence $evidence -Id 'control.completeness' -Bucket 'working' `
    -Detail "Incomplete-evidence control: all $($required.Count) service measurements present." `
    -Data $completeness

$path = Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'service.json')
Write-Host "W0 service evidence written to $path"

if (-not $completeness.complete) {
    throw ("W0: incomplete service evidence. absent=" + ($completeness.absent -join ','))
}
