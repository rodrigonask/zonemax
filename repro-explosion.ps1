# repro-explosion.ps1 -- reproduce "maximized-in-zone Chrome explodes to full monitor" without
# touching the user's windows or ZoneMax's config. Uses an isolated throwaway Chrome and the raw
# SPI_SETWORKAREA trick directly (ZoneMax is in SINGLE mode and will classify the window's odd
# rect as deliberate and leave it alone).
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class W {
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")]
    public static extern bool SpiRect(int a, int u, ref RECT p, int w);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);
}
'@

function Get-Rect([IntPtr]$h) { $r = New-Object RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null; $r }
function Show-Rect([string]$label, [IntPtr]$h) {
    $r = Get-Rect $h
    "{0,-28} zoomed={1}  ({2},{3}) {4}x{5}" -f $label, [W]::IsZoomed($h), $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top)
}

# 1. throwaway Chrome
$profile = Join-Path $env:TEMP "zm-repro-profile"
$p = Start-Process "chrome.exe" -ArgumentList "--user-data-dir=`"$profile`"","--no-first-run","--new-window","about:blank" -PassThru
Start-Sleep 4
$hwnd = [IntPtr]::Zero
foreach ($proc in Get-Process chrome -ErrorAction SilentlyContinue) {
    if ($proc.MainWindowHandle -ne 0 -and $proc.CommandLine -like "*zm-repro-profile*") { $hwnd = $proc.MainWindowHandle }
}
if ($hwnd -eq [IntPtr]::Zero) {
    foreach ($proc in Get-Process chrome -ErrorAction SilentlyContinue) {
        $ci = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)" -ErrorAction SilentlyContinue).CommandLine
        if ($ci -like "*zm-repro-profile*" -and $proc.MainWindowHandle -ne 0) { $hwnd = $proc.MainWindowHandle }
    }
}
if ($hwnd -eq [IntPtr]::Zero) { throw "no throwaway Chrome window found" }
"hwnd = $hwnd"

# 2. truly maximize it into the LEFT HALF of the ultrawide via the work-area trick
$zone = New-Object RECT; $zone.Left=0; $zone.Top=0; $zone.Right=1376; $zone.Bottom=1440
$full = New-Object RECT; $full.Left=0; $full.Top=0; $full.Right=3440; $full.Bottom=1440
[W]::ShowWindow($hwnd, 9) | Out-Null       # SW_RESTORE first
Start-Sleep -Milliseconds 300
[W]::SpiRect(0x002F, 0, [ref]$zone, 0) | Out-Null   # SPI_SETWORKAREA, no broadcast
[W]::ShowWindow($hwnd, 3) | Out-Null                # SW_MAXIMIZE
Start-Sleep -Milliseconds 400
[W]::SpiRect(0x002F, 0, [ref]$full, 0) | Out-Null   # restore
Start-Sleep -Milliseconds 300
Show-Rect "parked in left-half" $hwnd

# 3. TEST A -- middle-click on the window (activation without ZoneMax pre-arm: single mode = no arm)
[W]::SetCursorPos(600, 600) | Out-Null
[W]::mouse_event(0x0020, 0, 0, 0, [IntPtr]::Zero)   # MIDDLEDOWN
[W]::mouse_event(0x0040, 0, 0, 0, [IntPtr]::Zero)   # MIDDLEUP
Start-Sleep -Milliseconds 800
Show-Rect "after MIDDLE-click" $hwnd

# re-park if it moved
$r = Get-Rect $hwnd
if (($r.Right - $r.Left) -gt 2000) {
    [W]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 300
    [W]::SpiRect(0x002F, 0, [ref]$zone, 0) | Out-Null
    [W]::ShowWindow($hwnd, 3) | Out-Null; Start-Sleep -Milliseconds 400
    [W]::SpiRect(0x002F, 0, [ref]$full, 0) | Out-Null; Start-Sleep -Milliseconds 300
    Show-Rect "re-parked" $hwnd
}

# 4. TEST B -- snip overlay (Win+Shift+S equivalent), then Esc
Start-Process "explorer.exe" "ms-screenclip:"
Start-Sleep 3
Show-Rect "with snip overlay OPEN" $hwnd
$sh = New-Object -ComObject WScript.Shell
$sh.SendKeys("{ESC}")
Start-Sleep 2
Show-Rect "after snip Esc" $hwnd

# 5. TEST C -- plain re-activation via SetForegroundWindow (Alt+Tab equivalent)
[W]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800
Show-Rect "after SetForeground" $hwnd

# cleanup
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Get-Process chrome -ErrorAction SilentlyContinue | ForEach-Object {
    $ci = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction SilentlyContinue).CommandLine
    if ($ci -like "*zm-repro-profile*") { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}
"done."
