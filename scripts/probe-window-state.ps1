[CmdletBinding()]
param(
    [int]$DurationSeconds = 900,
    [int]$PollMilliseconds = 20,
    [string]$Output = (Join-Path $env:LOCALAPPDATA "Rot\Validation\window-state.log")
)

$ErrorActionPreference = "Stop"
$outputPath = [IO.Path]::GetFullPath($Output)
$outputDirectory = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

if (-not ("RotWindowProbe" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class RotWindowProbe
{
    public sealed class WindowState
    {
        public long Handle { get; set; }
        public string Title { get; set; }
        public bool Visible { get; set; }
        public long ExtendedStyle { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public WindowState()
        {
            Title = "";
        }
    }

    public sealed class SnapshotState
    {
        public int ProcessId { get; set; }
        public int ForegroundProcessId { get; set; }
        public List<WindowState> Windows { get; set; }

        public SnapshotState()
        {
            Windows = new List<WindowState>();
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private const int GwlExStyle = -20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static SnapshotState Capture(int processId)
    {
        var snapshot = new SnapshotState { ProcessId = processId };
        var foreground = GetForegroundWindow();
        uint foregroundPid;
        GetWindowThreadProcessId(foreground, out foregroundPid);
        snapshot.ForegroundProcessId = unchecked((int)foregroundPid);

        EnumWindows((hwnd, _) =>
        {
            uint candidatePid;
            GetWindowThreadProcessId(hwnd, out candidatePid);
            if (candidatePid != (uint)processId) return true;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            Rect rect;
            GetWindowRect(hwnd, out rect);
            snapshot.Windows.Add(new WindowState
            {
                Handle = hwnd.ToInt64(),
                Title = title.ToString(),
                Visible = IsWindowVisible(hwnd),
                ExtendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64(),
                Left = rect.Left,
                Top = rect.Top,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top
            });
            return true;
        }, IntPtr.Zero);
        snapshot.Windows.Sort((left, right) => left.Handle.CompareTo(right.Handle));
        return snapshot;
    }
}
'@
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$lastJson = $null
while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $process = Get-Process -Name Rot -ErrorAction SilentlyContinue | Select-Object -First 1
    $snapshot = if ($null -eq $process) {
        [ordered]@{ processId = 0; foregroundProcessId = 0; windows = @() }
    } else {
        [RotWindowProbe]::Capture($process.Id)
    }
    $json = $snapshot | ConvertTo-Json -Compress -Depth 5
    if ($json -ne $lastJson) {
        $line = "{0} {1}" -f [DateTimeOffset]::UtcNow.ToString("O"), $json
        Add-Content -LiteralPath $outputPath -Value $line -Encoding utf8
        Write-Host $line
        $lastJson = $json
    }
    Start-Sleep -Milliseconds ([Math]::Max(10, $PollMilliseconds))
}
