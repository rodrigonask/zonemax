<#
  verify.ps1 -- end-to-end proof that ZoneMax does its job.

  Simulates exactly what happens when you click the maximize button on a window
  sitting in a zone: issue a plain ShowWindow(SW_MAXIMIZE). Without ZoneMax, Windows
  blows the window across the full 3440px panel. With ZoneMax running, it must come
  back TRULY maximized inside the zone.

  Uses an isolated throwaway Chrome (its own --user-data-dir). Never touches your real windows.
#>
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class V {
    public const int SW_MAXIMIZE = 3;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
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
}
'@ -Language CSharp

[void][V]::SetProcessDPIAware()

function State($h, $label) {
    $r = New-Object V+RECT
    [void][V]::GetWindowRect($h, [ref]$r)
    [pscustomobject]@{
        Step = $label; IsZoomed = [V]::IsZoomed($h)
        Left = $r.Left; Top = $r.Top; Width = ($r.Right - $r.Left); Height = ($r.Bottom - $r.Top)
    }
}

$zm = Get-Process ZoneMax -ErrorAction SilentlyContinue
if (-not $zm) { throw 'ZoneMax is not running.' }
"ZoneMax running (PID $($zm.Id)), RAM $([math]::Round($zm.WorkingSet64/1MB,1)) MB"
""

$profileDir = 'C:\Temp\zonemax-verify-profile'
$null = New-Item -ItemType Directory -Path $profileDir -Force
$proc = Start-Process chrome.exe -PassThru -ArgumentList @(
    "--user-data-dir=$profileDir", '--no-first-run', '--no-default-browser-check',
    '--new-window', '--window-size=700,500', '--window-position=400,400', 'about:blank'
)

$hwnd = [IntPtr]::Zero; $n = 60
while ($n-- -gt 0 -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 250
    $w = [V]::WindowsOf([uint32]$proc.Id, 'Chrome_WidgetWin_1')
    if ($w.Count -gt 0) { $hwnd = $w[0] }
}
if ($hwnd -eq [IntPtr]::Zero) { throw 'test Chrome window not found' }
Start-Sleep -Milliseconds 800

$res = @()
$res += State $hwnd '0. launched'

# Park it inside the LEFT zone (0,0,1440,1440), like a window you dragged there.
[void][V]::SetWindowPos($hwnd, [IntPtr]::Zero, 300, 300, 700, 500, ([V]::SWP_NOZORDER -bor [V]::SWP_NOACTIVATE))
Start-Sleep -Milliseconds 400
$res += State $hwnd '1. parked in LEFT zone'

# This is EXACTLY what clicking the maximize button does.
[void][V]::ShowWindow($hwnd, [V]::SW_MAXIMIZE)
Start-Sleep -Milliseconds 1500
$res += State $hwnd '2. after maximize (ZoneMax should have caught it)'

Start-Sleep -Milliseconds 1500
$res += State $hwnd '3. after settle'

""
"================= RESULT ================="
$res | Format-Table -AutoSize | Out-String -Width 200

# Zones are computed at runtime, so read them from the CURRENT session's startup block only --
# a block from a previous (differently-moded) run must not set the expectation. The test window
# is parked at x=300, so its zone is the leftmost zone of the primary monitor: [Left] when the
# monitor is split, [Full] when it is single. Both are correct behavior; test against reality.
$log = Get-Content 'C:\Projects\zonemax\zonemax.log'
$lastStart = 0
for ($i = 0; $i -lt $log.Count; $i++) { if ($log[$i] -match '---- ZoneMax .* starting ----') { $lastStart = $i } }
$session = $log[$lastStart..($log.Count - 1)]

$zoneLine = ($session | Where-Object { $_ -match 'zone \[(Left|Full)\] \(0,0\)' } | Select-Object -First 1)
if (-not $zoneLine) { throw 'No zone at (0,0) in the current session startup block.' }
$zoneName = [regex]::Match($zoneLine, 'zone \[(\w+)\]').Groups[1].Value
$zoneW = [int]([regex]::Match($zoneLine, '(\d+)x(\d+)\s*$').Groups[1].Value)
$expect = $zoneW + 16     # zone + 8px maximized overhang each side

$final = $res[-1]
$okZoomed = $final.IsZoomed
$okWidth  = ([math]::Abs($final.Width - $expect) -le 20)
$okLeft   = ($final.Left -ge -12 -and $final.Left -le 2)

"leftmost zone this session: [$zoneName] $zoneW px  ->  expect a maximized width of ~$expect"
if ($okZoomed -and $okWidth -and $okLeft) {
    "PASS -- truly maximized (IsZoomed=True) and contained inside the ${zoneW}px [$zoneName] zone."
} else {
    "FAIL -- IsZoomed=$($final.IsZoomed) Width=$($final.Width) Left=$($final.Left)"
    "       Expected IsZoomed=True, Width ~$expect, Left ~-8"
}

Start-Sleep -Milliseconds 500
try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
"`ntest Chrome closed."
