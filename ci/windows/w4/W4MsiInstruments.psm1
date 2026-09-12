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
# Every OpenDatabase in this module goes through the two helpers below.
#
# W4-A4-R2 (#234): the R1 run reached the table controls, `Set-W4MsiCell`
# committed, and the very next call -- `Get-W4MsiProperty`, i.e. a read-only
# reopen of the same file through `Invoke-W4MsiQuery` -- died with
# `Exception calling "InvokeMember" with "5" argument(s): "OpenDatabase,
# DatabasePath,OpenMode"`. That message is Windows Installer naming the
# parameters of the call it refused, and it carries no reason.
#
# The persist mode was not the fault: `Set-W4MsiCell` already opens with 1,
# msiOpenDatabaseModeTransact, which is a mode that allows the cell edit, and
# the edit was never reported to fail. What was wrong is that the mode was
# never given up. The old `finally` released the Installer object only, so the
# database and view handles taken inside it stayed alive as RCWs awaiting a
# collection that had not happened yet. A transact-mode handle holds the .msi
# open, and the read-only reopen a few statements later hit that handle.
#
# So: release the record, the view and the database as well as the installer,
# in that order, and force the collection rather than hoping for one. And print
# the file's facts before every open, so that a package this module cannot open
# is diagnosable from the log instead of from the parameter names.
function Write-W4MsiFileFacts {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Label
    )
    $info = [System.IO.FileInfo]::new($Path)
    $length = $(if ($info.Exists) { $info.Length } else { '(no file)' })
    $readOnly = $(if ($info.Exists) { $info.IsReadOnly } else { '(no file)' })
    Write-Host ("W4-A4 :: {0} OpenDatabase {1}" -f $Label, $Path)
    Write-Host ("W4-A4 ::   Length {0}  Exists {1}  IsReadOnly {2}" -f `
        $length, $info.Exists, $readOnly)
}

function Open-W4MsiDatabase {
    <#
        `OpenDatabase` on a FULL path, with the file's facts printed first and
        the refusal turned into something a reader can act on. Windows
        Installer's own last error is asked for by hand: the interop exception
        text is the parameter list, never the reason.
    #>
    param(
        [Parameter(Mandatory = $true)] $Installer,
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [int] $Mode,
        [Parameter(Mandatory = $true)] [string] $Label
    )
    $full = [System.IO.Path]::GetFullPath($MsiPath)
    Write-W4MsiFileFacts -Path $full -Label $Label
    try {
        return $Installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $Installer, @($full, $Mode))
    } catch {
        $reason = '(Windows Installer reported no last error)'
        try {
            $errorRecord = $Installer.GetType().InvokeMember(
                'LastErrorRecord', 'InvokeMethod', $null, $Installer, $null)
            if ($null -ne $errorRecord) {
                $reason = [string]$errorRecord.GetType().InvokeMember(
                    'FormatText', 'GetProperty', $null, $errorRecord, $null)
                # `FinalReleaseComObject` returns the remaining RCW reference
                # count, an Int32. Assigned away: this catch runs on the way to a
                # `throw`, and a bare call would put a digit on the output stream.
                $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($errorRecord)
            }
        } catch {
            $reason = "(the last error record could not be read: $($_.Exception.Message))"
        }
        $inner = $_.Exception.InnerException
        $hresult = $(if ($null -ne $inner) { $inner.HResult } else { $_.Exception.HResult })
        $info = [System.IO.FileInfo]::new($full)
        throw ("$Label could not open '$full' in persist mode $Mode " +
            ("(HRESULT 0x{0:x8}); " -f $hresult) +
            "Exists $($info.Exists), " +
            "Length $($(if ($info.Exists) { $info.Length } else { '(no file)' })), " +
            "IsReadOnly $($(if ($info.Exists) { $info.IsReadOnly } else { '(no file)' })). " +
            "Windows Installer says: $reason. " +
            "The interop message was: $($_.Exception.Message)")
    }
}

function Close-W4MsiComObject {
    <#
        Release every handle this module took, most-derived first, and then
        force the collection.

        `FinalReleaseComObject` does NOT return void: it returns the remaining
        RCW reference count as an Int32, and a bare call writes that number to
        the output stream. That is load-bearing here, because this function is
        called from `Invoke-W4MsiQuery`'s `finally` and `Invoke-W4MsiQuery`
        returns its rows through the same stream. One stray number turns the
        caller's `$rows` into a mixed array, and PowerShell hides it: indexing a
        scalar at `[0]` returns the scalar, so `$rows[0][0]` quietly becomes `0`
        instead of a string, while `$row[1]` raises "Unable to index into an
        object of type System.Int32" somewhere else entirely. Every release in
        this module is therefore assigned to `$null`, and every call to this
        function is too.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] [AllowEmptyCollection()] [object[]] $ComObject
    )
    foreach ($item in $ComObject) {
        if ($null -eq $item) { continue }
        if (-not [System.Runtime.InteropServices.Marshal]::IsComObject($item)) { continue }
        try {
            $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)
        } catch {
            Write-Host "W4-A4 :: a COM handle refused release: $($_.Exception.Message)"
        }
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    [System.GC]::Collect()
}

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
    $database = $null
    $view = $null
    try {
        # 0 is msiOpenDatabaseModeReadOnly.
        $database = Open-W4MsiDatabase -Installer $installer -MsiPath $MsiPath -Mode 0 `
            -Label 'Invoke-W4MsiQuery'
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
            # Released here rather than in the teardown: a row handle held for
            # the length of the fetch loop is a handle on the file. Assigned
            # away: this is inside the loop that produces this function's rows,
            # so a bare call would interleave one Int32 per row with them.
            $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        }
        # The leading comma is load-bearing. PowerShell unrolls an array on
        # output, so `return $rows.ToArray()` would emit each row separately and
        # a single-row result would arrive at the caller as a bare `string[]` --
        # making `$rows[0][0]` the first CHARACTER of the first column. The comma
        # wraps the result so exactly one object, the jagged array, comes back.
        return ,$rows.ToArray()
    } finally {
        # The view is closed in the teardown, not on the success path: a query
        # that threw mid-fetch would otherwise leave the file open behind it.
        if ($null -ne $view) {
            try {
                $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
            } catch {
                Write-Host "W4-A4 :: a view refused Close: $($_.Exception.Message)"
            }
        }
        $null = Close-W4MsiComObject -ComObject @($view, $database, $installer)
    }
}

function Get-W4MsiProperty {
    <#
        One row of the MSI `Property` table, or `$null` when the package carries
        no such row. `$null` and the empty string are deliberately different: a
        package with no UpgradeCode at all is not a package whose UpgradeCode is
        blank, and the grader treats them differently.

        The documented output is exactly one object, and that object is a
        `[string]` or `$null` -- nothing else, and never more than one. The type
        is asserted rather than assumed because the failure it guards against is
        silent: with a polluted output stream `$rows[0]` can be an Int32, and
        PowerShell answers `(0)[0]` with `0` rather than raising, so this
        function would return the number 0 and every caller would compare it
        against a GUID and find an honest-looking mismatch.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $Name
    )
    $rows = Invoke-W4MsiQuery -MsiPath $MsiPath -ColumnCount 1 `
        -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'"
    if (@($rows).Count -eq 0) { return $null }
    $value = $rows[0][0]
    if ($null -ne $value -and $value -isnot [string]) {
        throw ("Get-W4MsiProperty read '$Name' out of '$MsiPath' and got a " +
            "$($value.GetType().FullName) rather than a string. Invoke-W4MsiQuery must put exactly " +
            'one jagged string array on its output stream; a COM release return value that was not ' +
            'assigned away is the usual cause.')
    }
    if ($null -eq $value) { return $null }
    return [string]$value
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

    # The copy inherits the source's attributes. A read-only .msi cannot be
    # opened in a persist mode, and the failure would name the parameters
    # rather than the attribute, so it is cleared here and said out loud.
    $cellPath = [System.IO.Path]::GetFullPath($MsiPath)
    $cellFile = [System.IO.FileInfo]::new($cellPath)
    if ($cellFile.Exists -and $cellFile.IsReadOnly) {
        Write-Host "W4-A4 :: Set-W4MsiCell cleared the read-only attribute on $cellPath"
        $cellFile.IsReadOnly = $false
    }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        # 1 is msiOpenDatabaseModeTransact: a persist mode, so the cell edit is
        # allowed, and the Commit below is what makes it durable.
        $database = Open-W4MsiDatabase -Installer $installer -MsiPath $cellPath -Mode 1 `
            -Label 'Set-W4MsiCell'
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
        $view = $null
        $null = $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null)
    } finally {
        # The transact-mode handle holds the .msi open. The caller's very next
        # act is a read-only reopen of this same file, so every handle taken
        # here is given up before this function returns.
        if ($null -ne $view) {
            try {
                $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
            } catch {
                Write-Host "W4-A4 :: a view refused Close: $($_.Exception.Message)"
            }
        }
        $null = Close-W4MsiComObject -ComObject @($record, $view, $database, $installer)
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

function Get-W4MsiStreamItem {
    <#
        Every object a scriptblock put on its output stream, counted honestly.

        `@( & $Call )` cannot do this job: `Invoke-W4MsiQuery` emits ONE object
        that happens to be an array, and `@()` around an array returns that same
        array -- so a correct one-row result and a polluted five-item stream both
        count as 1. Piping into `ForEach-Object` counts the items the function
        actually wrote, which is the thing under test.
    #>
    param([Parameter(Mandatory = $true)] [scriptblock] $Call)
    $items = [System.Collections.Generic.List[object]]::new()
    & $Call | ForEach-Object { $items.Add($_) }
    return ,$items.ToArray()
}

function Test-W4MsiInstrumentType {
    <#
        REDs if any of these helpers puts something other than its documented
        type on its output stream, read off a REAL package.

        This exists because the defect it catches is invisible from the outside.
        `Marshal::FinalReleaseComObject` returns an Int32, and a bare call inside
        `Invoke-W4MsiQuery` or its teardown appends that number to the rows the
        function returns. Nothing throws at the point of the mistake. What throws
        is `$row[1]` in a different function several hundred lines away, while
        `Get-W4MsiProperty` -- whose caller indexes at `[0]`, which PowerShell
        answers for a scalar -- goes on returning the number 0 as though it were
        a property value, and a readback check that compares it against a GUID
        records an honest-looking mismatch.

        So the types are asserted here, on the real package, before any grading
        reads them. Returns the problems as loose strings; NO leading comma, so
        a clean run emits nothing at all and the caller's `@(...)` counts 0. The
        comma idiom used by `Invoke-W4MsiQuery` would be exactly wrong here: it
        wraps the EMPTY array as one object, `@(...)` then counts 1, and the
        probe would refuse every clean run for having found no problems.
    #>
    param([Parameter(Mandatory = $true)] [string] $MsiPath)

    $problems = [System.Collections.Generic.List[string]]::new()

    # These scriptblocks are deliberately NOT closures. `.GetNewClosure()` was
    # tried and is wrong here: it rebinds the scriptblock to a new dynamic
    # module, which discards THIS module's session state, so an unexported
    # function stops resolving -- `Close-W4MsiComObject` below died with "The
    # term ... is not recognized" while the exported helpers went on working.
    # It bought nothing either: `Get-W4MsiStreamItem` is called from this
    # function, so a plain scriptblock already reads `$MsiPath` off the scope
    # chain.

    # Invoke-W4MsiQuery: exactly one object, and that object is the jagged array.
    # Each case is wrapped: `Get-W4MsiProperty`'s own type guard THROWS on a
    # polluted stream, and an escaping exception would take the probe down
    # without reaching `Deny` -- so the refusal would never be written into the
    # evidence document, which is the only thing a later reader has.
    $queried = @()
    try {
        $queried = Get-W4MsiStreamItem -Call {
            Invoke-W4MsiQuery -MsiPath $MsiPath -ColumnCount 1 `
                -Query 'SELECT `Value` FROM `Property`'
        }
    } catch { $problems.Add("Invoke-W4MsiQuery threw: $($_.Exception.Message)") }
    if ($queried.Count -ne 1) {
        $problems.Add(("Invoke-W4MsiQuery put $($queried.Count) objects on its output stream, not 1: " +
            (($queried | ForEach-Object { $(if ($null -eq $_) { '$null' } else { $_.GetType().FullName }) }) -join ', ')))
    } elseif ($queried[0] -isnot [string[][]]) {
        $problems.Add("Invoke-W4MsiQuery returned a $($queried[0].GetType().FullName), not a string[][]")
    }

    # Get-W4MsiProperty: one object, and a string for a property that exists.
    $present = @()
    $presentThrew = $false
    try {
        $present = Get-W4MsiStreamItem -Call { Get-W4MsiProperty -MsiPath $MsiPath -Name 'ProductCode' }
    } catch {
        $presentThrew = $true
        $problems.Add("Get-W4MsiProperty threw for ProductCode: $($_.Exception.Message)")
    }
    if ($presentThrew) {
        # the throw is the finding; a follow-on count of 0 would only repeat it
    } elseif ($present.Count -ne 1) {
        $problems.Add("Get-W4MsiProperty put $($present.Count) objects on its output stream for ProductCode, not 1")
    } elseif ($present[0] -isnot [string]) {
        $problems.Add(("Get-W4MsiProperty returned a " +
            "$($(if ($null -eq $present[0]) { '$null' } else { $present[0].GetType().FullName })) " +
            'for ProductCode, which every built package carries, rather than a string'))
    }

    # ...and $null, still as exactly one object, for one that does not exist.
    # `$null` is the documented answer for an absent row and is not an error.
    $absent = @()
    $absentThrew = $false
    try {
        $absent = Get-W4MsiStreamItem -Call {
            Get-W4MsiProperty -MsiPath $MsiPath -Name 'W4A4NoSuchPropertyExists'
        }
    } catch {
        $absentThrew = $true
        $problems.Add("Get-W4MsiProperty threw for an absent property: $($_.Exception.Message)")
    }
    if (-not $absentThrew -and ($absent.Count -ne 1 -or $null -ne $absent[0])) {
        $problems.Add(("Get-W4MsiProperty answered an absent property with $($absent.Count) object(s) " +
            "of type $(($absent | ForEach-Object { $(if ($null -eq $_) { '$null' } else { $_.GetType().FullName }) }) -join ', '), not one `$null"))
    }

    # Close-W4MsiComObject: nothing at all. This is the helper whose return value
    # started the class of defect, so it is measured on a real COM handle.
    $released = @()
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $released = Get-W4MsiStreamItem -Call { Close-W4MsiComObject -ComObject @($installer) }
    } catch { $problems.Add("Close-W4MsiComObject threw: $($_.Exception.Message)") }
    if ($released.Count -ne 0) {
        $problems.Add(("Close-W4MsiComObject put $($released.Count) object(s) on its output stream: " +
            (($released | ForEach-Object { "$($_.GetType().FullName) '$_'" }) -join ', ') +
            '. It must emit nothing at all.'))
    }

    return $problems.ToArray()
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

# ---------------------------------------------------------------------------
# W4-A6 (#234): the Windows Event Log source, and the events written under it.
# ---------------------------------------------------------------------------
function Get-W4EventLogSource {
    <#
        Read an Event Log source out of the registry, which is what an Event Log
        source IS. Returns $null when the SUBKEY is absent -- which is exactly
        the question `[System.Diagnostics.EventLog]::SourceExists` answers, and
        is why an emptied key is NOT an absent source.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $LogName,
        [Parameter(Mandatory = $true)] [string] $SourceName
    )
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\$LogName\$SourceName"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $raw = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    $read = {
        param($name)
        if ($null -ne $raw -and $raw.PSObject.Properties.Name -contains $name) { $raw.$name } else { $null }
    }
    $types = & $read 'TypesSupported'
    return @{
        key = $key
        log = $LogName
        eventMessageFile = [string](& $read 'EventMessageFile')
        typesSupported = $(if ($null -eq $types) { -1 } else { [int]$types })
    }
}

function Test-W4EventLogSourceExists {
    <#
        What every READER of the log asks. Kept beside the registry read rather
        than folded into it because the two can disagree in exactly the way this
        slice has to rule out: a key whose values were removed and whose subkey
        was not still answers true here.
    #>
    param([Parameter(Mandatory = $true)] [string] $SourceName)
    try { return [System.Diagnostics.EventLog]::SourceExists($SourceName) }
    catch { return $false }
}

function Get-W4LifecycleEvents {
    <#
        The Application-log events written under a source since a given instant.

        Bounded at BOTH ends on purpose. `Since` is taken immediately before the
        `sc start` that is supposed to produce them, so an event left behind by
        an earlier run of this job cannot be mistaken for this run's, and the
        provider is matched rather than the message text -- `ServiceBase` writes
        a localised string and a gate that read it would be measuring the
        runner's display language.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $LogName,
        [Parameter(Mandatory = $true)] [string] $SourceName,
        [Parameter(Mandatory = $true)] [DateTime] $Since
    )
    $filter = @{ LogName = $LogName; ProviderName = $SourceName; StartTime = $Since }
    try {
        $found = @(Get-WinEvent -FilterHashtable $filter -ErrorAction SilentlyContinue |
            Sort-Object -Property TimeCreated)
    }
    catch {
        # An unregistered provider does not make `Get-WinEvent` answer "no events" -- it
        # makes it THROW ERROR_INVALID_PARAMETER, and as a terminating error, which is
        # why the `-ErrorAction` above never reached it and why `eventlog-no-source` died
        # on the reader rather than grading. A source that is not registered has written
        # nothing, so that is the answer. Keyed on whether the source exists rather than
        # on the exception's text: the message is localised and the runner's display
        # language is not part of this measurement. Any other reader failure stays loud.
        if (Test-W4EventLogSourceExists -SourceName $SourceName) { throw }
        return @()
    }
    return @($found | ForEach-Object {
        [ordered]@{
            providerName = [string]$_.ProviderName
            id = [int]$_.Id
            level = [string]$_.LevelDisplayName
            timeCreated = $_.TimeCreated.ToUniversalTime().ToString('o')
            message = [string]$_.Message
        }
    })
}

Export-ModuleMember -Function Show-W4MsiFailureExcerpt, Invoke-W4Msi, Invoke-W4MsiQuery,
    Test-W4MsiInstrumentType,
    Get-W4MsiProperty, Get-W4MsiFileNames, Get-W4MsiStartsServiceOnInstall, Set-W4MsiCell, Get-W4MsiServiceControlEvent,
    Get-W4ProductInstallState, Get-W4ServiceRegistry, Get-W4ServiceState, Remove-W4ServiceIfPresent,
    Get-W4ServiceFailureActions, Get-W4ServiceFailureEvidence, Get-W4AclObservation,
    Get-W4AclObservations, Show-W4AclObservations,
    Get-W4EventLogSource, Test-W4EventLogSourceExists, Get-W4LifecycleEvents
