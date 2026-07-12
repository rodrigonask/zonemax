<#
  true-maximize-spike.ps1

  Question: can we put a window into a REAL maximized state (WS_MAXIMIZE) inside an
  arbitrary sub-region of ONE physical monitor? That is the thing DisplayFusion cannot do,
  and it is the whole basis of the app.

  Method A "workarea": temporarily shrink the monitor's work area to the target region,
                       issue a genuine SW_MAXIMIZE (Windows maximizes to the work area, so
                       the window fills only the region and is TRULY maximized), then restore
                       the work area WITHOUT broadcasting the change.
  Method B "stylebit": set the WS_MAXIMIZE style bit ourselves and position the frame with the
                       same off-screen overhang a real maximized window has.

  Success = IsZoomed() is True AND the window rect matches the target region AND it SURVIVES
  the work-area restore + a settle delay.

  Tests against an isolated throwaway Chrome (its own --user-data-dir), so the user's real
  Chrome session is never touched. Kills the test window at the end.
#>
[CmdletBinding()]
param(
    # Deliberately a weird rect that DisplayFusion would never produce on its own --
    # if the window lands exactly here, it was US, not DF.
    [int]$X = 200,
    [int]$Y = 200,
    [int]$W = 800,
    [int]$H = 600,
    [switch]$KeepWindow
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Nx {
    public const int  GWL_STYLE        = -16;
    public const int  WS_MAXIMIZE      = 0x01000000;
    public const int  SW_RESTORE       = 9;
    public const int  SW_MAXIMIZE      = 3;
    public const int  SPI_SETWORKAREA  = 0x002F;
    public const uint SWP_NOZORDER     = 0x0004;
    public const uint SWP_NOACTIVATE   = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const int  SM_CXSIZEFRAME   = 32;
    public const int  SM_CXPADDEDBORDER= 92;
    public const int  SPIF_SENDCHANGE  = 0x0002;
    public const int  MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
    }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int  GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int  SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern int  GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT r, int flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(int a, int u, ref RECT r, int w);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(int a, int u, IntPtr r, int w);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    // All visible top-level windows of a given class owned by a given process.
    public static IntPtr[] WindowsOf(uint pid, string cls) {
        var found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid) return true;
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            if (sb.ToString() == cls) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }

    public static int Border() { return GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER); }

    // Reset a monitor's work area back to what Windows calculates from the taskbar/appbars.
    public static void ResetWorkArea() {
        SystemParametersInfo(SPI_SETWORKAREA, 0, IntPtr.Zero, SPIF_SENDCHANGE);
    }
}
'@ -Language CSharp

[void][Nx]::SetProcessDPIAware()

function Get-State {
    param([IntPtr]$H, [string]$Label)
    $r = New-Object Nx+RECT
    [void][Nx]::GetWindowRect($H, [ref]$r)
    [pscustomobject]@{
        Step     = $Label
        IsZoomed = [Nx]::IsZoomed($H)
        Left     = $r.Left
        Top      = $r.Top
        Width    = $r.Right - $r.Left
        Height   = $r.Bottom - $r.Top
    }
}

# ---------------------------------------------------------------- target region
$target = New-Object Nx+RECT
$target.Left = $X; $target.Top = $Y; $target.Right = $X + $W; $target.Bottom = $Y + $H

$hMon = [Nx]::MonitorFromRect([ref]$target, [Nx]::MONITOR_DEFAULTTONEAREST)
$mi = New-Object Nx+MONITORINFO
$mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
[void][Nx]::GetMonitorInfo($hMon, [ref]$mi)
$origWork = $mi.rcWork

"MONITOR   : ({0},{1})-({2},{3})" -f $mi.rcMonitor.Left, $mi.rcMonitor.Top, $mi.rcMonitor.Right, $mi.rcMonitor.Bottom
"WORK AREA : ({0},{1})-({2},{3})" -f $origWork.Left, $origWork.Top, $origWork.Right, $origWork.Bottom
"TARGET    : ({0},{1})-({2},{3})  [{4}x{5}]" -f $target.Left, $target.Top, $target.Right, $target.Bottom, $W, $H
"BORDER    : {0}px  (a real maximized window overhangs the screen edge by this)" -f ([Nx]::Border())
""

# ---------------------------------------------------------- isolated test Chrome
$profileDir = 'C:\Temp\zonemax-spike-profile'
$null = New-Item -ItemType Directory -Path $profileDir -Force
$proc = Start-Process -FilePath 'chrome.exe' -PassThru -ArgumentList @(
    "--user-data-dir=$profileDir"
    '--no-first-run'
    '--no-default-browser-check'
    '--new-window'
    '--window-size=700,500'
    '--window-position=300,300'
    'about:blank'
)

$hwnd = [IntPtr]::Zero
$deadline = 60
while ($deadline-- -gt 0 -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 250
    $wins = [Nx]::WindowsOf([uint32]$proc.Id, 'Chrome_WidgetWin_1')
    if ($wins.Count -gt 0) { $hwnd = $wins[0] }
}
if ($hwnd -eq [IntPtr]::Zero) { throw 'Could not find the test Chrome window.' }
Start-Sleep -Milliseconds 800

$results = @()
$results += Get-State -H $hwnd -Label '0. baseline (as launched)'

try {
    # =========================================================== METHOD A : workarea
    $shrunk = $target
    [void][Nx]::SystemParametersInfo([Nx]::SPI_SETWORKAREA, 0, [ref]$shrunk, 0)   # winIni=0 -> no broadcast

    if ([Nx]::IsZoomed($hwnd)) { [void][Nx]::ShowWindow($hwnd, [Nx]::SW_RESTORE); Start-Sleep -Milliseconds 150 }
    # Put the window inside the region first, so Windows maximizes it on the right monitor.
    [void][Nx]::SetWindowPos($hwnd, [IntPtr]::Zero, $X, $Y, $W, $H, ([Nx]::SWP_NOZORDER -bor [Nx]::SWP_NOACTIVATE))
    Start-Sleep -Milliseconds 150

    [void][Nx]::ShowWindow($hwnd, [Nx]::SW_MAXIMIZE)
    Start-Sleep -Milliseconds 400
    $results += Get-State -H $hwnd -Label 'A1. after SW_MAXIMIZE'

    $restore = $origWork
    [void][Nx]::SystemParametersInfo([Nx]::SPI_SETWORKAREA, 0, [ref]$restore, 0)  # restore, still no broadcast
    Start-Sleep -Milliseconds 400
    $results += Get-State -H $hwnd -Label 'A2. after workarea restore'

    Start-Sleep -Milliseconds 1500
    $results += Get-State -H $hwnd -Label 'A3. after 1.5s settle'

    # =========================================================== METHOD B : stylebit
    [void][Nx]::ShowWindow($hwnd, [Nx]::SW_RESTORE)
    Start-Sleep -Milliseconds 300

    $b = [Nx]::Border()
    $style = [Nx]::GetWindowLong($hwnd, [Nx]::GWL_STYLE)
    [void][Nx]::SetWindowLong($hwnd, [Nx]::GWL_STYLE, ($style -bor [Nx]::WS_MAXIMIZE))
    $flags = ([Nx]::SWP_NOZORDER -bor [Nx]::SWP_NOACTIVATE -bor [Nx]::SWP_FRAMECHANGED)
    [void][Nx]::SetWindowPos($hwnd, [IntPtr]::Zero, ($X - $b), ($Y - $b), ($W + 2*$b), ($H + 2*$b), $flags)
    Start-Sleep -Milliseconds 500
    $results += Get-State -H $hwnd -Label 'B1. after stylebit + SetWindowPos'

    Start-Sleep -Milliseconds 1500
    $results += Get-State -H $hwnd -Label 'B2. after 1.5s settle'
}
finally {
    $restore = $origWork
    [void][Nx]::SystemParametersInfo([Nx]::SPI_SETWORKAREA, 0, [ref]$restore, 0)
    [Nx]::ResetWorkArea()
}

""
"================ RESULTS ================"
$results | Format-Table -AutoSize | Out-String -Width 200

""
"Expected on SUCCESS: IsZoomed=True, and rect ~= ({0},{1}) {2}x{3} (+/- {4}px overhang)." -f $X, $Y, $W, $H, ([Nx]::Border())
"If the rect snaps to the FULL monitor width, the trick did not survive."
"If the rect snaps to 1440 or 2000 wide, DisplayFusion intercepted us."

if (-not $KeepWindow) {
    Start-Sleep -Milliseconds 500
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
    Get-Process chrome -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.StartTime -gt (Get-Date).AddMinutes(-2) } | Out-Null
    "`nTest Chrome closed."
}
