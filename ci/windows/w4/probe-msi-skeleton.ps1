#Requires -Version 7.2
<#
.SYNOPSIS
    Build the W4-A0 MSI from the accepted win-x64 layout, install it, read the
    service the Service Control Manager actually got, uninstall it, and prove
    the binaries went while the operator's state stayed -- then do the same four
    more times with a deliberately broken package.

.DESCRIPTION
    W4-A0 (#234). This is the whole slice, and it proves ONE thing:

        a WiX project in-tree produces an MSI that installs the accepted
        win-x64 server layout, creates the service `Tesserafin` with
        `--service` and the W0 §4 argument list, and uninstalls the service and
        the binaries while leaving the state directories.

    W4-A2 (#234) adds one property to the same run, measured the same way:

        the installed service carries the W0 §4 recovery policy -- restart
        after 60 s on the first and the second failure, no action on the third
        -- read back out of the Service Control Manager with
        QueryServiceConfig2W AFTER the install, never out of the authoring.

    The SCM is asked rather than the registry parsed: `FailureActions` is a
    REG_BINARY whose layout Microsoft does not document, and
    `QueryServiceConfig2` is the API whose SERVICE_FAILURE_ACTIONS shape is.
    `sc.exe qfailure` is captured verbatim beside it, as evidence a reviewer can
    read, and is deliberately NOT graded -- its output is localised.

    W4-A2-DIAG (#234): a non-zero `msiexec /i` or `/x` prints the decisive lines
    of its own verbose log before this script refuses. `1603` is "fatal error
    during installation" and names nothing by itself; the log already being
    written knows which action failed, and was simply never read. The excerpt is
    capped, goes to the step log rather than into the evidence document, and the
    log itself is never uploaded.

    What it deliberately does NOT do:

      * it does not START the service. W0 §10: a fresh installation leaves it
        installed and enabled but not started. W3 is where the service runs;
      * it makes no claim about upgrade, repair or Add/Remove Programs beyond
        what `MajorUpgrade` emits by default -- none of those paths is driven;
      * it makes no reproducibility claim. W0 §5.6 already measured that MSI
        bytes are not bit-for-bit and accepted a bounded exception; this script
        does not build the same package twice and does not compare digests;
      * it signs nothing, uploads nothing and publishes nothing;
      * it applies none of the W0 §9.3 ACLs;
      * it changes no Tesserafin.Server behaviour and edits none of the frozen
        W1/W2 scripts it runs.

    The package under test is assembled by the FROZEN W2-A2 assembler, from this
    head, and extracted. That is the accepted layout, acquired the accepted way:
    what W2 pins -- the Web payload and the FFmpeg runtime -- travels with the
    commit through the assembler's own committed acceptance manifest. Nothing
    here is downloaded from a tag, an Actions artifact or "the latest".

    The four hostile controls drive THIS authoring through THIS grader with one
    deliberate defect each. Their expected RED sets are declared in
    `W4MsiAssertions.psm1` and are asserted to be EXACTLY what goes red: a
    control that reddens more has broken something else too and is attributable
    to nothing, and one that reddens less has not reproduced its defect.

.PARAMETER RepoRoot
    The checkout under test.

.PARAMETER WorkDir
    Private scratch. Must not already exist or must be empty, so a second run
    cannot inherit a first one's package.

.PARAMETER InstallPrefix
    Where the package is told to install. The ruling asks for a disposable
    prefix rather than the runner's real %ProgramFiles%; this is that prefix,
    and `installedOutsideProgramFiles` is the predicate that proves the
    redirection actually took rather than being assumed.

.PARAMETER EvidencePath
    Where the evidence document is written. It records states, exit codes,
    predicate verdicts and digests -- no host path outside the two this script
    was given, and no run identifier.

.PARAMETER SourceDateEpoch
    Committer time of the commit being built. Required by the frozen assembler.

.PARAMETER OrasPath
    The pinned ORAS client the frozen FFmpeg consumer needs.

.PARAMETER PythonPath
    The interpreter the frozen assembler uses.

.PARAMETER HeadSha
    Recorded in the evidence so a reviewer can see which commit was packaged.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RepoRoot,
    [Parameter(Mandatory = $true)] [string] $WorkDir,
    [Parameter(Mandatory = $true)] [string] $InstallPrefix,
    [Parameter(Mandatory = $true)] [string] $EvidencePath,
    [Parameter(Mandatory = $true)] [int64]  $SourceDateEpoch,
    [Parameter(Mandatory = $true)] [string] $OrasPath,
    [Parameter()] [string] $PythonPath,
    [Parameter(Mandatory = $true)] [string] $HeadSha
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$SERVICE_NAME = 'Tesserafin'
$SERVICE_KEY = "HKLM:\SYSTEM\CurrentControlSet\Services\$SERVICE_NAME"
$MUTATIONS = @('none', 'no-exe', 'no-service-flag', 'no-path-flags', 'no-service-remove',
    'no-failure-actions', 'first-action-not-restart', 'third-action-restart')

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, 'W4MsiAssertions.psm1')) -Force

$evidence = [ordered]@{
    slice = 'W4-A0'
    tracker = 234
    headSha = $HeadSha
    # Stated as data so the closing report cannot claim more than the run did.
    signed = $false
    published = $false
    startedTheService = $false
    appliedAcls = $false
    reproducibilityClaim = 'none -- W0 §5.6 already measured that MSI bytes are not bit-for-bit'
    runs = [ordered]@{}
}

function Save-Evidence {
    $dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($EvidencePath))
    $null = [System.IO.Directory]::CreateDirectory($dir)
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
}

function Deny {
    param([Parameter(Mandatory = $true)] [string] $Reason,
          [Parameter(Mandatory = $true)] [string] $Detail)
    $evidence.refusal = [ordered]@{ reason = $Reason; detail = $Detail }
    Save-Evidence
    Write-Host "W4-A0 REFUSED [$Reason]: $Detail"
    exit 1
}

function Write-Note { param([string] $Text) Write-Host "W4-A0 :: $Text" }

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------
if (-not $IsWindows) { Deny 'platform' 'this proof only means anything on a native Windows host' }
if ($PSVersionTable.PSVersion.Major -lt 7) { Deny 'platform' "this job needs PowerShell 7 or newer; it has $($PSVersionTable.PSVersion)" }

$identity = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Deny 'privilege' 'a per-machine MSI cannot be installed without elevation'
}

function Remove-ServiceIfPresent {
    <#
        Leave no SCM entry behind. `sc.exe delete` answers 1060 for a service
        that is already gone, which is this function's SUCCESSFUL outcome, and a
        service marked for deletion disappears asynchronously -- so the absence
        is polled rather than assumed from an exit code.
    #>
    & sc.exe stop $SERVICE_NAME *> $null
    & sc.exe delete $SERVICE_NAME *> $null
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-Path -LiteralPath $SERVICE_KEY)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (-not (Test-Path -LiteralPath $SERVICE_KEY))
}

if (Test-Path -LiteralPath $SERVICE_KEY) {
    if (-not (Remove-ServiceIfPresent)) {
        Deny 'precondition' ("a service named '$SERVICE_NAME' is already registered on this host and " +
            'could not be removed, so nothing this run observed about it would be attributable')
    }
}

$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$work = [System.IO.Path]::GetFullPath($WorkDir)
if ([System.IO.Directory]::Exists($work) -and
    @([System.IO.Directory]::EnumerateFileSystemEntries($work)).Count -gt 0) {
    Deny 'work-dir' "'$work' is not empty"
}
$null = [System.IO.Directory]::CreateDirectory($work)

$prefixRoot = [System.IO.Path]::GetFullPath($InstallPrefix)
$programFilesTesserafin = [System.IO.Path]::Combine($env:ProgramFiles, 'Tesserafin')
if ([System.IO.Directory]::Exists($programFilesTesserafin)) {
    Deny 'precondition' ("'$programFilesTesserafin' already exists, so this run could not tell a real " +
        '%ProgramFiles% install apart from something that was already there')
}

# The state root is NOT redirected. W0 §4's argument list names
# %ProgramData%\Tesserafin\Server, and a run that redirected it would be
# measuring an argument list no operator will ever get. The runner is
# disposable; an operator's machine is not, which is exactly why the retained
# components are Permanent.
$programDataRoot = [System.IO.Path]::Combine($env:ProgramData, 'Tesserafin', 'Server')

# ---------------------------------------------------------------------------
# The accepted layout, read from the script that registers the service for the
# portable ZIP -- never restated here.
# ---------------------------------------------------------------------------
$acceptedScript = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'tesserafin-server-service.ps1')
if (-not [System.IO.File]::Exists($acceptedScript)) { Deny 'layout' "no accepted W2-A5 service script at '$acceptedScript'" }
$acceptedText = [System.IO.File]::ReadAllText($acceptedScript)
function Get-AcceptedConstant {
    param([Parameter(Mandatory = $true)] [string] $Name)
    $match = [regex]::Match($acceptedText, "(?m)^\`$$Name\s*=\s*'([^']+)'\s*$")
    if (-not $match.Success) { Deny 'layout' "the accepted W2-A5 service script does not define `$$Name" }
    return $match.Groups[1].Value
}
$serverRelativeExe = (Get-AcceptedConstant 'SERVER_RELATIVE_EXE') -replace '/', '\'
$webRelativeDir = (Get-AcceptedConstant 'WEB_RELATIVE_DIR') -replace '/', '\'
$ffmpegRelativeExe = (Get-AcceptedConstant 'FFMPEG_RELATIVE_EXE') -replace '/', '\'

# ---------------------------------------------------------------------------
# 1. The package, from the frozen W2-A2 assembler
# ---------------------------------------------------------------------------
$assembler = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'assemble-server-zip.ps1')
if (-not [System.IO.File]::Exists($assembler)) { Deny 'prerequisite' "no assembler at '$assembler'" }

$assemblyOut = [System.IO.Path]::Combine($work, 'assembly', 'out')
$assemblerArguments = @{
    RepoRoot = $repo
    WorkDir = [System.IO.Path]::Combine($work, 'assembly', 'work')
    OutDir = $assemblyOut
    SourceDateEpoch = $SourceDateEpoch
    OrasPath = $OrasPath
}
if ($PythonPath) { $assemblerArguments['PythonPath'] = $PythonPath }
& $assembler @assemblerArguments
if ($LASTEXITCODE -ne 0) { Deny 'assembly' 'the frozen W2-A2 assembler produced no archive' }

$archives = @(Get-ChildItem -LiteralPath $assemblyOut -Filter '*.zip' -File)
if ($archives.Count -ne 1) { Deny 'assembly' "the assembler wrote $($archives.Count) archives" }
$archive = $archives[0]

$extractRoot = [System.IO.Path]::Combine($work, 'pkg')
$null = [System.IO.Directory]::CreateDirectory($extractRoot)
[System.IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName, $extractRoot)
$tops = @(Get-ChildItem -LiteralPath $extractRoot -Force)
if ($tops.Count -ne 1 -or -not $tops[0].PSIsContainer) {
    Deny 'top-level' "the archive extracted $($tops.Count) top-level entries"
}
$stageRoot = $tops[0].FullName

$evidence.package = [ordered]@{
    archiveName = $archive.Name
    archiveSha256 = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    topLevelDirectory = $tops[0].Name
    stagedFileCount = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File).Count
    serverRelativeExe = $serverRelativeExe
    webRelativeDir = $webRelativeDir
    ffmpegRelativeExe = $ffmpegRelativeExe
}
Write-Note "package $($archive.Name), $($evidence.package.stagedFileCount) staged files"
Save-Evidence

# ---------------------------------------------------------------------------
# Instruments
# ---------------------------------------------------------------------------
function Get-MsiFileNames {
    <#
        The long file names in the MSI's own File table, read through Windows
        Installer rather than by unpacking the cab: what the installer will
        deliver is what the installer says it will deliver. The FileName column
        is `short|long` when a short name is needed, so the long half is taken.
    #>
    param([Parameter(Mandatory = $true)] [string] $MsiPath)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))
        $view = $database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $database, @('SELECT `FileName` FROM `File`'))
        $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

        $names = [System.Collections.Generic.List[string]]::new()
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $record) { break }
            $value = [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
            $null = $names.Add($(if ($value.Contains('|')) { $value.Split('|')[-1] } else { $value }))
        }
        $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        return $names.ToArray()
    } finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }
}

# W4-A2-DIAG (#234). msiexec answers a single number. 1603 is "fatal error
# during installation", which is every failure the installer decided to roll
# back, and on its own it names nothing -- a package that installs 2871 files
# and configures a service can reach it a dozen ways. The verbose log knows
# which one, and it is already being written; it was simply never read.
#
# So a refused msiexec now prints the lines of its OWN log that name the
# failure, and nothing else. The full log is NOT uploaded: it is hundreds of
# thousands of lines, it is not evidence anyone would read, and an accepted
# artifact must never become a later input. The excerpt goes to the step log,
# never into the evidence document, so that document keeps its property of
# carrying no host path beyond the two this script was given.
$MSI_FAILURE_PATTERNS = @(
    'Return value 3'
    'MsiConfigureServices'
    'ServiceConfig'
    '\b1603\b'
    '\b1920\b'
    '\b1613\b'
    'Error status'
    'Product: .*-- Error'
    'Note: 1:'
)
$MSI_EXCERPT_MAX_LINES = 120
$MSI_EXCERPT_MAX_LINE_LENGTH = 400

function Show-MsiFailureExcerpt {
    <#
        Print the decisive lines of one msiexec verbose log.

        The log is read through [System.IO.File]::ReadAllLines, which honours a
        byte-order mark: msiexec writes UTF-16 for some packages and the system
        code page for others, and `Get-Content` under PowerShell 7 would decode
        the first as mojibake and find no match in a log that is full of them.

        Capped in both directions -- a bounded number of lines, each truncated
        -- because an uncapped excerpt of an MSI log is the whole MSI log.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $LogPath,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    Write-Host "W4-A2-DIAG :: msiexec log excerpt for $Label"
    if (-not [System.IO.File]::Exists($LogPath)) {
        Write-Host '  (msiexec wrote no log at all)'
        return
    }

    $lines = $(try {
        [System.IO.File]::ReadAllLines($LogPath)
    } catch {
        Write-Host "  (the log could not be read: $($_.Exception.Message))"
        return
    })

    $pattern = ($MSI_FAILURE_PATTERNS -join '|')
    $matched = @($lines | Where-Object { $_ -match $pattern })
    Write-Host "  $($lines.Count) log line(s), $($matched.Count) naming the failure"
    if ($matched.Count -eq 0) {
        # A refusal whose log names none of the patterns is itself the finding:
        # the tail is printed so the run is not silent about it.
        Write-Host '  no line matched; last 20 lines instead:'
        $matched = @($lines | Select-Object -Last 20)
    }

    $shown = 0
    foreach ($line in $matched) {
        if ($shown -ge $MSI_EXCERPT_MAX_LINES) {
            Write-Host "  ... $($matched.Count - $shown) further matching line(s) not shown"
            break
        }
        $text = $line.TrimEnd()
        if ($text.Length -gt $MSI_EXCERPT_MAX_LINE_LENGTH) {
            $text = $text.Substring(0, $MSI_EXCERPT_MAX_LINE_LENGTH) + ' ...'
        }
        Write-Host "  | $text"
        $shown++
    }
}

function Invoke-Msi {
    param([Parameter(Mandatory = $true)] [string[]] $Arguments,
          [Parameter(Mandatory = $true)] [string] $LogPath,
          [Parameter(Mandatory = $true)] [string] $Label)
    $process = Start-Process -FilePath msiexec.exe `
        -ArgumentList (@($Arguments) + @('/qn', '/norestart', '/l*v', "`"$LogPath`"")) -Wait -PassThru
    # W4-A2-DIAG: a non-zero msiexec explains itself before this run refuses.
    # Printed here rather than at the refusal site so that /i and /x are covered
    # by construction and neither can be forgotten.
    if ($process.ExitCode -ne 0) {
        Show-MsiFailureExcerpt -LogPath $LogPath -Label "$Label (msiexec exited $($process.ExitCode))"
    }
    return $process.ExitCode
}

function Get-ServiceRegistry {
    if (-not (Test-Path -LiteralPath $SERVICE_KEY)) { return $null }
    $raw = Get-ItemProperty -LiteralPath $SERVICE_KEY
    $read = { param($name) if ($raw.PSObject.Properties.Name -contains $name) { $raw.$name } else { $null } }
    return @{
        ImagePath = [string](& $read 'ImagePath')
        Start = (& $read 'Start')
        DelayedAutostart = $(if ($null -eq (& $read 'DelayedAutostart')) { 0 } else { (& $read 'DelayedAutostart') })
        ObjectName = [string](& $read 'ObjectName')
        DisplayName = [string](& $read 'DisplayName')
        Description = [string](& $read 'Description')
    }
}

# ---------------------------------------------------------------------------
# W4-A2: the failure policy, asked of the Service Control Manager itself.
#
# `QueryServiceConfig2(SERVICE_CONFIG_FAILURE_ACTIONS)` is the documented way to
# read what an installed service will actually do when it dies. The alternatives
# were both worse: the `FailureActions` REG_BINARY under the service key has no
# documented layout, and `sc.exe qfailure` prints localised text. Both are still
# recorded in the evidence -- they are just not what the predicates are graded
# on.
#
# The two-call shape is the Win32 idiom: ask with a null buffer, be told
# ERROR_INSUFFICIENT_BUFFER and how many bytes are needed, then ask again.
# ---------------------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class W4Scm
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(IntPtr service, uint level, IntPtr buffer,
        uint bufferSize, out uint bytesNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    // "<resetPeriodSeconds>;<type>/<delayMs>,<type>/<delayMs>,..." or null when
    // the service carries no failure policy at all. A string rather than a
    // structure so the caller parses one documented shape instead of marshalling
    // a second time.
    public static string ReadFailureActions(string serviceName)
    {
        IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
        {
            throw new Exception("OpenSCManager failed with " + Marshal.GetLastWin32Error());
        }
        try
        {
            IntPtr service = OpenServiceW(manager, serviceName, SERVICE_QUERY_CONFIG);
            if (service == IntPtr.Zero)
            {
                throw new Exception("OpenService '" + serviceName + "' failed with " +
                    Marshal.GetLastWin32Error());
            }
            try
            {
                uint needed = 0;
                if (QueryServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, IntPtr.Zero, 0, out needed))
                {
                    return null;
                }
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new Exception("QueryServiceConfig2 sizing failed with " + error);
                }
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!QueryServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, buffer, needed, out needed))
                    {
                        throw new Exception("QueryServiceConfig2 failed with " + Marshal.GetLastWin32Error());
                    }
                    SERVICE_FAILURE_ACTIONS actions =
                        (SERVICE_FAILURE_ACTIONS)Marshal.PtrToStructure(buffer, typeof(SERVICE_FAILURE_ACTIONS));
                    if (actions.cActions == 0 || actions.lpsaActions == IntPtr.Zero)
                    {
                        return null;
                    }
                    string rendered = actions.dwResetPeriod.ToString() + ";";
                    for (int i = 0; i < actions.cActions; i++)
                    {
                        IntPtr entry = new IntPtr(actions.lpsaActions.ToInt64() + (i * 8));
                        int type = Marshal.ReadInt32(entry);
                        uint delay = (uint)Marshal.ReadInt32(entry, 4);
                        string name;
                        switch (type)
                        {
                            case 0: name = "none"; break;
                            case 1: name = "restartService"; break;
                            case 2: name = "restartComputer"; break;
                            case 3: name = "runCommand"; break;
                            default: name = "unknown(" + type + ")"; break;
                        }
                        if (i > 0) { rendered += ","; }
                        rendered += name + "/" + delay.ToString();
                    }
                    return rendered;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
'@

function Get-ServiceFailureActions {
    <#
        The SCM's answer, as the pure grader's observation shape, or $null when
        the service has no failure policy. `$null` is the honest answer for a
        service the SCM would do nothing for, and it is what
        `serviceFailureActionsConfigured` exists to redden.
    #>
    if (-not (Test-Path -LiteralPath $SERVICE_KEY)) { return $null }
    $rendered = [W4Scm]::ReadFailureActions($SERVICE_NAME)
    if ([string]::IsNullOrWhiteSpace($rendered)) { return $null }
    $halves = $rendered.Split(';')
    if ($halves.Count -ne 2) { return $null }
    $actions = @(
        foreach ($entry in $halves[1].Split(',')) {
            $pair = $entry.Split('/')
            if ($pair.Count -ne 2) { continue }
            @{ type = $pair[0]; delayMs = [int]$pair[1] }
        }
    )
    return @{
        resetPeriodSeconds = [int]$halves[0]
        actions = $actions
        rendered = $rendered
    }
}

function Get-ServiceFailureEvidence {
    <#
        The same policy in the two forms a reviewer can read directly, recorded
        and never graded: `sc.exe qfailure`, whose text is localised, and the
        undocumented `FailureActions` REG_BINARY as hex. `FailureActionsOnNonCrashFailures`
        comes along because it decides whether a NON-crash exit -- which is what
        W3's non-zero exit on a fatal startup failure is -- counts as a failure
        at all. W0 §4 is silent on it and this slice does not set it, so it is
        reported rather than asserted.
    #>
    $scText = $(try { (& sc.exe qfailure $SERVICE_NAME 2>&1 | Out-String).Trim() } catch { "sc.exe qfailure threw: $_" })
    $binary = $null
    $onNonCrash = $null
    if (Test-Path -LiteralPath $SERVICE_KEY) {
        $raw = Get-ItemProperty -LiteralPath $SERVICE_KEY
        if ($raw.PSObject.Properties.Name -contains 'FailureActions') {
            $binary = -join (@($raw.FailureActions) | ForEach-Object { '{0:x2}' -f $_ })
        }
        if ($raw.PSObject.Properties.Name -contains 'FailureActionsOnNonCrashFailures') {
            $onNonCrash = $raw.FailureActionsOnNonCrashFailures
        }
    }
    return [ordered]@{
        scQueryFailure = $scText
        registryFailureActionsHex = $binary
        failureActionsOnNonCrashFailures = $onNonCrash
    }
}

function Get-ServiceState {
    $service = Get-Service -Name $SERVICE_NAME -ErrorAction SilentlyContinue
    if ($null -eq $service) { return 'Absent' }
    return [string]$service.Status
}

function Get-StateObservation {
    param([Parameter(Mandatory = $true)] [string] $SentinelName)
    $observation = @{}
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $directory = [System.IO.Path]::Combine($programDataRoot, $name)
        $observation[$name] = @{
            directory = [System.IO.Directory]::Exists($directory)
            sentinel = [System.IO.File]::Exists([System.IO.Path]::Combine($directory, $SentinelName))
        }
    }
    return $observation
}

# ---------------------------------------------------------------------------
# 2. One package, one lifecycle, one verdict -- five times
# ---------------------------------------------------------------------------
$builder = [System.IO.Path]::Combine($PSScriptRoot, 'build-msi.ps1')
$logDir = [System.IO.Path]::Combine($work, 'logs')
$null = [System.IO.Directory]::CreateDirectory($logDir)

# Materialised by the first build and reused by the other four: the accepted
# stage minus the server executable and minus the portable ZIP's service script.
# One copy, not five, and the builder re-validates it on every call.
$harvestRoot = [System.IO.Path]::Combine($work, 'harvest')

$allPassed = $true

foreach ($mutation in $MUTATIONS) {
    Write-Note "=== $mutation"
    $run = [ordered]@{ mutation = $mutation }
    $prefix = [System.IO.Path]::Combine($prefixRoot, $mutation)
    $msiPath = [System.IO.Path]::Combine($work, "tesserafin-$mutation.msi")
    $sentinel = "w4a0-$mutation.sentinel"

    & $builder -RepoRoot $repo -StageRoot $stageRoot -HarvestRoot $harvestRoot `
        -OutPath $msiPath -Mutation $mutation
    if ($LASTEXITCODE -ne 0 -or -not [System.IO.File]::Exists($msiPath)) {
        Deny 'build' "the MSI for mutation '$mutation' was not built"
    }
    $run.msiSha256 = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $run.msiBytes = (Get-Item -LiteralPath $msiPath).Length

    $msiFileNames = Get-MsiFileNames -MsiPath $msiPath
    $run.msiFileCount = $msiFileNames.Count

    $run.installExit = Invoke-Msi -Arguments @('/i', "`"$msiPath`"", "INSTALLFOLDER=`"$prefix`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "install-$mutation.log")) `
        -Label "install of mutation '$mutation'"
    if ($run.installExit -ne 0) {
        Deny 'install' ("msiexec /i exited $($run.installExit) for mutation '$mutation'. Every control " +
            'in this slice is a control over an INSTALLED package; a refused install measures nothing')
    }

    $service = Get-ServiceRegistry
    $run.serviceImagePath = $(if ($null -eq $service) { $null } else { $service.ImagePath })
    $run.serviceState = Get-ServiceState
    # W4-A2: read BEFORE the uninstall, while the service the installer created
    # still exists. Asking afterwards would answer about nothing.
    $failureActions = Get-ServiceFailureActions
    $run.failureActions = $failureActions
    $run.failureActionsEvidence = Get-ServiceFailureEvidence
    $run.installPrefixUsed = $prefix
    $run.programFilesTesserafinExists = [System.IO.Directory]::Exists($programFilesTesserafin)

    # The retained directories have to hold something before "the uninstall left
    # them alone" can be more than a statement about an empty directory.
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $directory = [System.IO.Path]::Combine($programDataRoot, $name)
        if ([System.IO.Directory]::Exists($directory)) {
            Set-Content -LiteralPath ([System.IO.Path]::Combine($directory, $sentinel)) `
                -Value "w4a0 $mutation $HeadSha" -Encoding utf8NoBOM
        }
    }

    $observation = @{
        msiFileNames = $msiFileNames
        installPrefix = $prefix
        programDataRoot = $programDataRoot
        serverRelativeExe = $serverRelativeExe
        webRelativeDir = $webRelativeDir
        ffmpegRelativeExe = $ffmpegRelativeExe
        programFilesTesserafinExists = $run.programFilesTesserafinExists
        installedServerExe = [System.IO.File]::Exists([System.IO.Path]::Combine($prefix, $serverRelativeExe))
        installedWebDir = [System.IO.Directory]::Exists([System.IO.Path]::Combine($prefix, $webRelativeDir))
        installedFfmpegExe = [System.IO.File]::Exists([System.IO.Path]::Combine($prefix, $ffmpegRelativeExe))
        service = $service
        serviceState = $run.serviceState
        failureActions = $failureActions
    }
    $run.installedFileCount = $(if ([System.IO.Directory]::Exists($prefix)) {
        @(Get-ChildItem -LiteralPath $prefix -Recurse -File -Force).Count } else { 0 })

    $run.uninstallExit = Invoke-Msi -Arguments @('/x', "`"$msiPath`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "uninstall-$mutation.log")) `
        -Label "uninstall of mutation '$mutation'"
    if ($run.uninstallExit -ne 0) {
        Deny 'uninstall' "msiexec /x exited $($run.uninstallExit) for mutation '$mutation'"
    }

    $observation['serviceKeyAfterUninstall'] = Test-Path -LiteralPath $SERVICE_KEY
    $observation['filesUnderPrefixAfterUninstall'] = $(if ([System.IO.Directory]::Exists($prefix)) {
        @(Get-ChildItem -LiteralPath $prefix -Recurse -File -Force).Count } else { 0 })
    $observation['stateAfterUninstall'] = Get-StateObservation -SentinelName $sentinel

    $predicates = Get-W4Predicates -Observation $observation
    $verdict = Get-W4Verdict -Predicates $predicates -Mutation $mutation

    $run.predicates = $predicates
    $run.verdict = $verdict
    $run.serviceKeyAfterUninstall = $observation['serviceKeyAfterUninstall']
    $run.filesUnderPrefixAfterUninstall = $observation['filesUnderPrefixAfterUninstall']
    $run.stateAfterUninstall = $observation['stateAfterUninstall']
    $evidence.runs[$mutation] = $run
    Save-Evidence

    Write-Note "$mutation -> $(if ($verdict.passed) { 'PASS' } else { 'FAIL' }): $($verdict.detail)"
    if (-not $verdict.passed) { $allPassed = $false }

    # Between controls, and never between a control and its own measurement.
    if (-not (Remove-ServiceIfPresent)) {
        Deny 'cleanup' ("the '$SERVICE_NAME' service survived mutation '$mutation' and could not be " +
            'removed, so the next control would have measured this one''s leftovers')
    }
    Remove-Item -LiteralPath $prefix -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $msiPath -Force -ErrorAction SilentlyContinue
}

# Two controls with identical failure lists is the tell that the harness graded
# nothing at all, and it is invisible from a table of passes.
$redSets = @{}
foreach ($mutation in $MUTATIONS) { $redSets[($evidence.runs[$mutation].verdict.red -join '|')] = $true }
$evidence.distinctRedSets = $redSets.Count
if ($redSets.Count -ne $MUTATIONS.Count) {
    $allPassed = $false
    $evidence.inertHarness = ("only $($redSets.Count) distinct red sets across $($MUTATIONS.Count) runs")
}

# The ruling asks which prefix the install used. Answered from the measurement.
$evidence.installPrefixKind = $(if ($evidence.runs['none'].programFilesTesserafinExists) {
    'real %ProgramFiles% -- the INSTALLFOLDER override did not take'
} else {
    'disposable prefix, %ProgramFiles% untouched'
})
$evidence.installPrefixUsed = $evidence.runs['none'].installPrefixUsed
# W4-A2's stop condition, answered from the measurement rather than from the
# authoring: what the SCM said the real package's service would do when it dies.
$evidence.failureActionsObserved = $evidence.runs['none'].failureActions
$evidence.allPassed = $allPassed
Save-Evidence

if (-not $allPassed) {
    Write-Host 'W4-A0 REFUSED: at least one run did not grade as declared'
    exit 1
}
Write-Note "all $($MUTATIONS.Count) runs graded as declared; prefix: $($evidence.installPrefixKind)"
exit 0
