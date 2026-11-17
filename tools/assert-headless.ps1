[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $FilePath,

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]] $ArgumentList = @()
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class HeadlessWindowInfo
{
    public long Handle { get; set; }
    public int ProcessId { get; set; }
    public string Title { get; set; }
}

public static class HeadlessNative
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static HeadlessWindowInfo[] VisibleWindows()
    {
        var windows = new List<HeadlessWindowInfo>();
        EnumWindows(delegate(IntPtr handle, IntPtr unused)
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            int length = GetWindowTextLength(handle);
            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            windows.Add(new HeadlessWindowInfo
            {
                Handle = handle.ToInt64(),
                ProcessId = unchecked((int)processId),
                Title = title.ToString()
            });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static Dictionary<int, int> ProcessParents()
    {
        const uint snapshotProcesses = 0x00000002;
        IntPtr invalidHandle = new IntPtr(-1);
        IntPtr snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0);
        if (snapshot == invalidHandle)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var entry = new PROCESSENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parents[unchecked((int)entry.th32ProcessID)] = unchecked((int)entry.th32ParentProcessID);
                }
                while (Process32Next(snapshot, ref entry));
            }
            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
'@

function ConvertTo-NativeArgument([string] $Value) {
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object System.Text.StringBuilder
    [void] $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void] $builder.Append(('\' * (($backslashes * 2) + 1)))
            [void] $builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void] $builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void] $builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void] $builder.Append(('\' * ($backslashes * 2)))
    }
    [void] $builder.Append('"')
    return $builder.ToString()
}

function Test-IsDescendant(
    [int] $CandidateProcessId,
    [System.Collections.Generic.Dictionary[int, int]] $Parents,
    [System.Collections.Generic.HashSet[int]] $KnownTree) {
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $current = $CandidateProcessId
    while ($Parents.ContainsKey($current) -and $seen.Add($current)) {
        $current = $Parents[$current]
        if ($KnownTree.Contains($current)) {
            return $true
        }
    }
    return $false
}

$baselineHandles = New-Object 'System.Collections.Generic.HashSet[long]'
foreach ($window in [HeadlessNative]::VisibleWindows()) {
    [void] $baselineHandles.Add($window.Handle)
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $FilePath
$startInfo.Arguments = (($ArgumentList | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
# CreateNoWindow gives the child a hidden console, so its output would be lost
# unless redirected; capture both streams (read asynchronously so a full pipe
# cannot deadlock the child) and relay them after exit as gate evidence.
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
if (-not $process.Start()) {
    throw "Could not start '$FilePath'."
}
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()

$knownTree = New-Object 'System.Collections.Generic.HashSet[int]'
[void] $knownTree.Add($process.Id)
$violations = @{}
$watchedNames = @(
    'WindowsTerminal', 'OpenConsole', 'conhost',
    'explorer', 'iexplore', 'msedge', 'chrome', 'firefox', 'brave', 'opera'
)

do {
    $parents = [HeadlessNative]::ProcessParents()
    foreach ($candidate in @($parents.Keys)) {
        if (-not $knownTree.Contains($candidate) -and (Test-IsDescendant $candidate $parents $knownTree)) {
            [void] $knownTree.Add($candidate)
        }
    }

    foreach ($window in [HeadlessNative]::VisibleWindows()) {
        if ($baselineHandles.Contains($window.Handle)) {
            continue
        }

        $name = '<exited>'
        try {
            $name = [System.Diagnostics.Process]::GetProcessById($window.ProcessId).ProcessName
        }
        catch [System.ArgumentException] {
            # A short-lived process may disappear between enumeration and lookup.
        }

        if ($knownTree.Contains($window.ProcessId) -or $watchedNames -contains $name) {
            $key = '{0}:{1}' -f $window.ProcessId, $window.Handle
            $violations[$key] = [PSCustomObject]@{
                Pid = $window.ProcessId
                Process = $name
                Title = $window.Title
            }
        }
    }

    if (-not $process.HasExited) {
        Start-Sleep -Milliseconds 100
    }
}
while (-not $process.HasExited)

$process.WaitForExit()
$commandExitCode = $process.ExitCode
[Console]::Out.Write($stdoutTask.Result)
[Console]::Error.Write($stderrTask.Result)
$process.Dispose()

if ($violations.Count -gt 0) {
    Write-Error 'Headless assertion failed: new visible window(s) appeared during the command.' -ErrorAction Continue
    foreach ($violation in $violations.Values | Sort-Object Pid, Process, Title) {
        Write-Error ("pid={0} process={1} title={2}" -f $violation.Pid, $violation.Process, $violation.Title) -ErrorAction Continue
    }
    exit 1
}

exit $commandExitCode
