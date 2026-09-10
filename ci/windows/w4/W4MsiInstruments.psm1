#Requires -Version 7.2
<#
    W4-A4 (#234). The instruments the MajorUpgrade proof reads the machine with:
    msiexec, the SCM, `Get-Acl`, and the MSI's own tables.

    Nothing here grades anything. Every function returns an observation in the
    shape `W4MsiAssertions.psm1` documents, and the grading is that module's
    job -- the same separation W4-A0 established, for the same reason: a
    measurement that decided its own verdict could not be driven over synthetic
    observations on a machine that has no Windows Installer at all.

    WHY THIS IS A MODULE AND NOT A CHANGE TO `probe-msi-skeleton.ps1`

    That script carries the same instruments inline, and W4-A4 deliberately does
    not move them. `probe-msi-skeleton.ps1` is the accepted, hosted-measured
    W4-A0/A2/A3 proof; refactoring it would put eleven measured controls and
    three accepted rulings behind an untested edit in order to save a file. The
    duplication is real, it is declared here rather than left to be discovered,
    and converging the two is a change some later slice can make against BOTH
    probes' green runs instead of against one.

    What is NOT duplicated is anything that decides an outcome: every predicate
    in this slice comes from `W4MsiAssertions.psm1`, which W4-A0 already wrote
    and which W4-A4 extended rather than copied.
#>

Set-StrictMode -Version 3.0

# ---------------------------------------------------------------------------
# msiexec
#
# W4-A2-DIAG (#234) established that a bare msiexec exit code names nothing --
# 1603 is "fatal error during installation" and a package that installs
# thousands of files and configures a service can reach it a dozen ways. The
# verbose log knows which one, so a non-zero run prints the decisive lines of
# its OWN log: the tail, where the engine records the failing action and its
# return value, plus every line that names a failure wherever it falls.
#
# The full log is never uploaded. It is hundreds of thousands of lines, it is
# not evidence anyone would read, and an accepted artifact must never become a
# later input. The excerpt goes to the step log and never into the evidence
# document, so that document keeps its property of carrying no host path beyond
# the ones the probe was given.
# ---------------------------------------------------------------------------
$script:MsiFailurePatterns = @(
    'InstallFinalize'
    'MainEngineThread'
    'Return value 3'
    '\b1603\b'
    '\b1920\b'
    '\b1613\b'
    '\b1638\b'
    '\b1939\b'
    'Error 1920'
    'Error 1939'
    'Product:'
)
$script:MsiExcerptTailLines = 80
$script:MsiExcerptMaxLines = 300
$script:MsiExcerptMaxLineLength = 400

function Show-W4MsiFailureExcerpt {
    <#
        Print the decisive lines of one msiexec verbose log.

        The log is read through [System.IO.File]::ReadAllLines, which honours a
        byte-order mark: msiexec writes UTF-16 for some packages and the system
        code page for others, and `Get-Content` under PowerShell 7 would decode
        the first as mojibake and find no match in a log that is full of them.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $LogPath,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    Write-Host "W4-A4 :: msiexec log excerpt for $Label"
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

    $pattern = ($script:MsiFailurePatterns -join '|')

    # Indices, not lines: the tail and the matches overlap, and the excerpt has
    # to print each line once, in the order the installer wrote it.
    $tailStart = [Math]::Max(0, $lines.Count - $script:MsiExcerptTailLines)
    $keep = [System.Collections.Generic.SortedSet[int]]::new()
    for ($i = $tailStart; $i -lt $lines.Count; $i++) { $null = $keep.Add($i) }
    $matchCount = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $pattern) {
            $matchCount++
            $null = $keep.Add($i)
        }
    }

    Write-Host ("  {0} log line(s); {1} naming the failure; tail is line(s) {2}-{3}" -f `
        $lines.Count, $matchCount, ($tailStart + 1), $lines.Count)

    $selected = @($keep)
    if ($selected.Count -gt $script:MsiExcerptMaxLines) {
        # Drop from the FRONT. The end of the log is the part that decides.
        $dropped = $selected.Count - $script:MsiExcerptMaxLines
        Write-Host ("  ... {0} earlier selected line(s) not shown (log line(s) {1}-{2})" -f `
            $dropped, ($selected[0] + 1), ($selected[$dropped - 1] + 1))
        $selected = @($selected | Select-Object -Last $script:MsiExcerptMaxLines)
    }

    $previous = -1
    foreach ($index in $selected) {
        if ($previous -ge 0 -and $index -ne ($previous + 1)) {
            Write-Host ("  ... {0} line(s) skipped" -f ($index - $previous - 1))
        }
        $text = $lines[$index].TrimEnd()
        if ($text.Length -gt $script:MsiExcerptMaxLineLength) {
            $text = $text.Substring(0, $script:MsiExcerptMaxLineLength) + ' ...'
        }
        Write-Host ("  {0,6} | {1}" -f ($index + 1), $text)
        $previous = $index
    }
}

function Invoke-W4Msi {
    <#
        One msiexec run. The exit code is RETURNED, never thrown on: W4-A4 has
        controls whose whole point is a non-zero install, and a helper that
        refused would decide their verdict for them.
    #>
    param(
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [string] $LogPath,
        [Parameter(Mandatory = $true)] [string] $Label
    )
    $process = Start-Process -FilePath msiexec.exe `
        -ArgumentList (@($Arguments) + @('/qn', '/norestart', '/l*v', "`"$LogPath`"")) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Show-W4MsiFailureExcerpt -LogPath $LogPath -Label "$Label (msiexec exited $($process.ExitCode))"
    }
    return $process.ExitCode
}

# ---------------------------------------------------------------------------
# The MSI's own tables, through Windows Installer.
#
# W4-A4 grades three things off them: the identity rows in `Property`, which is
# where a machine actually looks to decide what supersedes what; and the
# `ServiceControl` event bits, which is where a package says whether it wants
# the service started inside the transaction. Both are read out of the BUILT
# package rather than out of the authoring, because the authoring is not what
# msiexec is handed.
# ---------------------------------------------------------------------------
function Invoke-W4MsiQuery {
    <#
        Run one SQL query against an MSI and return its rows as string arrays.
        `ColumnCount` is required rather than discovered: the MSI SQL surface
        answers column metadata through a second record type, and every query in
        this slice knows its own shape.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $Query,
        [Parameter(Mandatory = $true)] [int] $ColumnCount
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        # 0 is msiOpenDatabaseModeReadOnly.
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))
        $view = $database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $database, @($Query))
        $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

        $rows = [System.Collections.Generic.List[string[]]]::new()
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $record) { break }
            $values = [string[]]::new($ColumnCount)
            for ($column = 1; $column -le $ColumnCount; $column++) {
                $values[$column - 1] = [string]$record.GetType().InvokeMember(
                    'StringData', 'GetProperty', $null, $record, @($column))
            }
            $null = $rows.Add($values)
        }
        $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        # The leading comma is load-bearing. PowerShell unrolls an array on
        # output, so `return $rows.ToArray()` would emit each row separately and
        # a single-row result would arrive at the caller as a bare `string[]` --
        # making `$rows[0][0]` the first CHARACTER of the first column. The comma
        # wraps the result so exactly one object, the jagged array, comes back.
        return ,$rows.ToArray()
    } finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }
}

function Get-W4MsiProperty {
    <#
        One row of the MSI `Property` table, or `$null` when the package carries
        no such row. `$null` and the empty string are deliberately different: a
        package with no UpgradeCode at all is not a package whose UpgradeCode is
        blank, and the grader treats them differently.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $Name
    )
    $rows = Invoke-W4MsiQuery -MsiPath $MsiPath -ColumnCount 1 `
        -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'"
    if (@($rows).Count -eq 0) { return $null }
    return $rows[0][0]
}

function Get-W4MsiFileNames {
    <#
        The long file names in the MSI's own File table. The FileName column is
        `short|long` when a short name is needed, so the long half is taken.
    #>
    param([Parameter(Mandatory = $true)] [string] $MsiPath)
    $rows = Invoke-W4MsiQuery -MsiPath $MsiPath -ColumnCount 1 `
        -Query 'SELECT `FileName` FROM `File`'
    return @(foreach ($row in $rows) {
        $value = $row[0]
        $(if ($value.Contains('|')) { $value.Split('|')[-1] } else { $value })
    })
}

# msidbServiceControlEventStart. The `Event` column is a bitfield and this is
# the bit that means "start this service during the install transaction" -- the
# thing W0 §5.2 measured failing with 1920 and rolling the install back to 1603,
# and the thing this authoring has never asked for.
$script:ServiceControlEventStart = 1

function Get-W4MsiStartsServiceOnInstall {
    <#
        Whether the package asks the installer to START the named service inside
        the transaction, read off the `ServiceControl` table's Event bitfield.

        A package with no ServiceControl row for the service answers `$false`,
        which is correct and is not the same statement as "the package has no
        service": `Get-W4MsiProperty` and the live SCM read answer that one.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $ServiceName
    )
    $rows = Invoke-W4MsiQuery -MsiPath $MsiPath -ColumnCount 2 `
        -Query 'SELECT `Name`, `Event` FROM `ServiceControl`'
    foreach ($row in $rows) {
        if (-not ([string]$row[0]).Equals($ServiceName, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        # Not `$event`: that shadows PowerShell's own automatic variable.
        $eventBits = 0
        if ([int]::TryParse([string]$row[1], [ref]$eventBits) -and
            (($eventBits -band $script:ServiceControlEventStart) -ne 0)) {
            return $true
        }
    }
    return $false
}

function Set-W4MsiCell {
    <#
        CONTROL-ONLY. Change one cell of one row of one table in a COPY of a
        built package, so that a hostile control can exist for a defect the
        authoring cannot be made to express.

        Two W4-A4 controls need this and only those two.
        `ci/windows/w4/msi-controls.py` reddens a second `UpgradeCode` string
        anywhere in `Tesserafin.wxs` and reddens `Start="install"` anywhere in
        it. That is correct and is not being worked around: both gates exist
        because either string in the REAL authoring would be the defect itself.
        The control has to live somewhere that is not the authoring, and the
        built package's own tables are the closest place to the thing graded.

        Neither edited package is ever installed. A second UpgradeCode installs
        BESIDE the first product -- the second product the W4-A4 ruling forbids
        inventing -- and a start-on-install is the 1920-to-1603 rollback W0 §5.2
        measured, which would leave the FIRST package on the machine and hide
        the defect behind an install failure. Both are graded from the tables,
        and the probe records in the evidence that they were.

        The update goes through View/Modify rather than through an MSI SQL
        `UPDATE ... SET x = <literal>`: the MSI SQL surface is a small subset,
        its UPDATE takes assignments the documentation describes through
        parameters, and a query it silently declines would leave an UNEDITED
        copy behind -- a control that is inert while looking exactly like one
        that fired. The caller re-reads the value afterwards for the same
        reason.

        `1` is msiOpenDatabaseModeTransact; `2` is msiViewModifyUpdate. Without
        the Commit the edit is discarded when the handle is released.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $Query,
        [Parameter(Mandatory = $true)] [int] $Column,
        [Parameter()] [AllowNull()] [string] $StringValue,
        [Parameter()] [AllowNull()] [System.Nullable[int]] $IntegerValue
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 1))
        $view = $database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $database, @($Query))
        $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) {
            throw "the query matched no row, so the control would have been inert: $Query"
        }
        if ($null -ne $IntegerValue) {
            $null = $record.GetType().InvokeMember(
                'IntegerData', 'SetProperty', $null, $record, @($Column, [int]$IntegerValue))
        } else {
            $null = $record.GetType().InvokeMember(
                'StringData', 'SetProperty', $null, $record, @($Column, [string]$StringValue))
        }
        $null = $view.GetType().InvokeMember('Modify', 'InvokeMethod', $null, $view, @(2, $record))
        $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        $null = $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }
}

function Get-W4MsiServiceControlEvent {
    <#
        The `Event` bitfield of the ServiceControl row for one service, or
        `$null` when the package carries no such row. The mutant ORs a bit into
        what the package really has rather than writing a number this file made
        up, so the control stays a package that differs from the real one in
        exactly one bit.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $ServiceName
    )
    $rows = Invoke-W4MsiQuery -MsiPath $MsiPath -ColumnCount 3 `
        -Query 'SELECT `ServiceControl`, `Name`, `Event` FROM `ServiceControl`'
    foreach ($row in $rows) {
        if (-not ([string]$row[1]).Equals($ServiceName, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $bits = 0
        if ([int]::TryParse([string]$row[2], [ref]$bits)) {
            return [ordered]@{ key = [string]$row[0]; event = $bits }
        }
    }
    return $null
}

function Get-W4ProductInstallState {
    <#
        Whether a ProductCode is installed on this machine, asked of Windows
        Installer: `ProductState` is the documented question, and 5 is
        INSTALLSTATE_DEFAULT -- "installed for the current user or machine". An
        unknown ProductCode answers -1.

        This is how "MajorUpgrade removed the old product rather than installing
        the new one beside it" is measured.

        It returns the STATE and any error rather than a bare boolean, and the
        probe records all of it. A helper that swallowed the exception into
        `$false` would redden `upgradedProductInstalled` on the real pair with
        nothing to diagnose it by -- and `previousProductRemoved` would stay
        GREEN under the same fault, because "A is absent" and "the question
        could not be asked" would have collapsed into one answer. This slice
        gets one hosted run and cannot be amended after the push, so the
        distinction is recorded rather than inferred.

        The Uninstall key is read as a SECOND, independent signal and is never
        combined with the first. Only `state -eq 5` is graded. If the two
        disagree, that disagreement is in the evidence for the owner to rule on
        instead of being hidden by an OR.
    #>
    param([Parameter(Mandatory = $true)] [string] $ProductCode)

    $state = $null
    $failure = $null
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        try {
            $state = [int]$installer.GetType().InvokeMember(
                'ProductState', 'GetProperty', $null, $installer, @($ProductCode))
        } catch {
            $failure = $_.Exception.Message
        }
    } finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }

    $uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$ProductCode"
    return [ordered]@{
        productCode = $ProductCode
        installed = ($state -eq 5)
        state = $state
        error = $failure
        uninstallKeyPresent = (Test-Path -LiteralPath $uninstallKey)
    }
}

# ---------------------------------------------------------------------------
# The Service Control Manager
# ---------------------------------------------------------------------------
function Get-W4ServiceRegistry {
    param([Parameter(Mandatory = $true)] [string] $ServiceKey)
    if (-not (Test-Path -LiteralPath $ServiceKey)) { return $null }
    $raw = Get-ItemProperty -LiteralPath $ServiceKey
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

function Get-W4ServiceState {
    param([Parameter(Mandatory = $true)] [string] $ServiceName)
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { return 'Absent' }
    return [string]$service.Status
}

function Remove-W4ServiceIfPresent {
    <#
        Leave no SCM entry behind. `sc.exe delete` answers 1060 for a service
        that is already gone, which is this function's SUCCESSFUL outcome, and a
        service marked for deletion disappears asynchronously -- so the absence
        is polled rather than assumed from an exit code.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $ServiceName,
        [Parameter(Mandatory = $true)] [string] $ServiceKey
    )
    & sc.exe stop $ServiceName *> $null
    & sc.exe delete $ServiceName *> $null
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-Path -LiteralPath $ServiceKey)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (-not (Test-Path -LiteralPath $ServiceKey))
}

# W4-A2's instrument, unchanged in behaviour and re-declared here so that the
# accepted W4-A3 probe does not have to be edited to share it.
# `QueryServiceConfig2(SERVICE_CONFIG_FAILURE_ACTIONS)` is the documented way to
# read what an installed service will actually do when it dies. The two
# alternatives are both worse: the `FailureActions` REG_BINARY under the service
# key has no documented layout, and `sc.exe qfailure` prints localised text.
# Both are still recorded by the probe -- they are just not what is graded.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class W4UpgradeScm
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

function Get-W4ServiceFailureActions {
    <#
        The SCM's answer, in the pure grader's observation shape, or `$null`
        when the service has no failure policy. `$null` is the honest answer for
        a service the SCM would do nothing for, and it is what
        `serviceFailureActionsConfigured` exists to redden.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $ServiceName,
        [Parameter(Mandatory = $true)] [string] $ServiceKey
    )
    if (-not (Test-Path -LiteralPath $ServiceKey)) { return $null }
    $rendered = [W4UpgradeScm]::ReadFailureActions($ServiceName)
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

function Get-W4ServiceFailureEvidence {
    <#
        The same policy in the two forms a reviewer can read directly, recorded
        and never graded: `sc.exe qfailure`, whose text is localised, and the
        undocumented `FailureActions` REG_BINARY as hex.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $ServiceName,
        [Parameter(Mandatory = $true)] [string] $ServiceKey
    )
    $scText = $(try { (& sc.exe qfailure $ServiceName 2>&1 | Out-String).Trim() } catch { "sc.exe qfailure threw: $_" })
    $binary = $null
    $onNonCrash = $null
    if (Test-Path -LiteralPath $ServiceKey) {
        $raw = Get-ItemProperty -LiteralPath $ServiceKey
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

# ---------------------------------------------------------------------------
# The ACLs, read off the installed layout with Get-Acl.
#
# Every rule is taken as a SecurityIdentifier rather than as an NTAccount:
# `NT SERVICE\Tesserafin` is a virtual account, its SID is what the descriptor
# actually carries, and a name translation is one more thing that can quietly
# fail and leave a row unattributable.
#
# `AreAccessRulesProtected` is the managed name for SE_DACL_PROTECTED, which is
# what the `D:P` in the authored SDDL asks Windows Installer to set.
# ---------------------------------------------------------------------------
function Get-W4AclObservation {
    param([Parameter(Mandatory = $true)] [string] $Path)

    if (-not [System.IO.Directory]::Exists($Path)) { return $null }
    $acl = $(try { Get-Acl -LiteralPath $Path } catch { $null })
    if ($null -eq $acl) { return $null }

    $rules = @(
        foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
            [ordered]@{
                sid = [string]$rule.IdentityReference.Value
                rights = [int]$rule.FileSystemRights
                type = [string]$rule.AccessControlType
                inherited = [bool]$rule.IsInherited
                inheritanceFlags = [string]$rule.InheritanceFlags
                propagationFlags = [string]$rule.PropagationFlags
            }
        }
    )

    $sddl = $(try {
        $acl.GetSecurityDescriptorSddlForm([System.Security.AccessControl.AccessControlSections]::Access)
    } catch { $null })
    $owner = $(try { [string]$acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value } catch { $null })

    return [ordered]@{
        path = $Path
        protected = [bool]$acl.AreAccessRulesProtected
        owner = $owner
        sddl = $sddl
        rules = $rules
    }
}

function Get-W4AclObservations {
    <#
        Every directory the W0 §9.3 contract reaches, under the labels
        `W4MsiAssertions.psm1` grades them by. A directory that does not exist
        comes back `$null` rather than as an empty ACL: "the install never
        created it" and "it exists and grants nobody anything" are different
        findings and must not collapse into one.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $InstallPrefix,
        [Parameter(Mandatory = $true)] [string] $DataRoot,
        [Parameter(Mandatory = $true)] [string] $StateRoot
    )
    $observations = [ordered]@{
        installFolder = Get-W4AclObservation -Path $InstallPrefix
        dataRoot = Get-W4AclObservation -Path $DataRoot
        server = Get-W4AclObservation -Path $StateRoot
    }
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $observations[$name] = Get-W4AclObservation -Path ([System.IO.Path]::Combine($StateRoot, $name))
    }
    return $observations
}

function Show-W4AclObservations {
    <#
        The ruling's stop condition, printed rather than left inside an evidence
        document: the SID, the rights, and whether inheritance is broken. It goes
        to the step log before anything is graded, so a run that later refuses
        still leaves the dump behind.
    #>
    param(
        [Parameter(Mandatory = $true)] $Observations,
        [Parameter(Mandatory = $true)] [string] $Label
    )
    Write-Host "W4-A4 :: live ACLs $Label"
    foreach ($name in $Observations.Keys) {
        $acl = $Observations[$name]
        if ($null -eq $acl) {
            Write-Host ("  {0,-14} (the directory does not exist)" -f $name)
            continue
        }
        Write-Host ("  {0,-14} inheritance broken: {1,-5}  {2}" -f $name, $acl.protected, $acl.path)
        foreach ($rule in @($acl.rules)) {
            Write-Host ("  {0,-14}   {1} {2,-46} 0x{3:x6} {4}" -f '', `
                $rule.type.PadRight(5), $rule.sid, $rule.rights, `
                $(if ($rule.inherited) { 'inherited' } else { 'explicit' }))
        }
        if ($acl.sddl) { Write-Host ("  {0,-14}   sddl {1}" -f '', $acl.sddl) }
    }
}

Export-ModuleMember -Function Show-W4MsiFailureExcerpt, Invoke-W4Msi, Invoke-W4MsiQuery,
    Get-W4MsiProperty, Get-W4MsiFileNames, Get-W4MsiStartsServiceOnInstall, Set-W4MsiCell, Get-W4MsiServiceControlEvent,
    Get-W4ProductInstallState, Get-W4ServiceRegistry, Get-W4ServiceState, Remove-W4ServiceIfPresent,
    Get-W4ServiceFailureActions, Get-W4ServiceFailureEvidence, Get-W4AclObservation,
    Get-W4AclObservations, Show-W4AclObservations
