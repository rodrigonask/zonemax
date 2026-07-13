# ZoneMax

**Real Windows maximize inside a region of one monitor.** Split an ultrawide into zones and get windows that are *genuinely maximized* in each zone — `IsZoomed() == true`, no resize border, tabs flush at the top edge.

A single ~50KB executable. No installer, no dependencies, no .NET SDK — it compiles with the C# compiler that ships inside Windows.

## The problem nobody else fixes

Every window manager in this category — FancyZones, DisplayFusion, MaxTo, all of them — *fakes* zone maximizing. They resize a normal window to fill the zone. The window looks maximized, but it's in the `normal` state, which means Windows keeps a **live resize border at the top edge**. Slam your mouse to the top of the screen to hit a browser tab and you grab the resize handle instead. Every time. There is no setting that fixes it, because it's an OS constraint: Windows has exactly one maximize target per physical monitor — the monitor's work area.

## The trick

ZoneMax bypasses the constraint instead of faking around it:

1. Temporarily shrink the monitor's work area to the target zone (`SystemParametersInfo(SPI_SETWORKAREA)` with **no broadcast**, so nothing else re-layouts)
2. Issue a genuine `ShowWindow(SW_MAXIMIZE)` — Windows maximizes the window to the work area, i.e. the zone
3. Silently restore the work area

The window comes out **truly maximized** inside the zone and survives the restore. Windows itself removes the resize border and lets the window overhang the zone edges by the standard 8px, exactly like a normal maximized window. Fitts' law works again.

## Usage

Zones are derived, not configured. Each monitor has a mode — `single` (one zone: behaves exactly like plain Windows) or `split` (two zones) — and you drive it with keys ZoneMax takes over from the shell:

| Key | Action |
|---|---|
| `Win+Up` | Collapse this monitor to ONE zone, maximize the window across it |
| `Win+Left/Right` | Split the monitor (activating the split if needed), truly maximize the window in that half. Already there? Continue onto the neighbouring monitor, like native Snap |
| `Win+Down` | Restore, then minimize |
| `Win+Shift+Left/Right` | Throw the window onto the next monitor |
| `Shift` + maximize click | Bypass zones, fill the whole monitor |
| Drag to an outer screen edge | Truly maximize in the zone you dropped it on (the edge between two monitors is not an edge — drag across it freely) |

Clicking a window's own maximize button maximizes it into its zone. The split ratio (50/50, 40/60, 35/65, …) is set from the tray icon.

**Design law: ZoneMax is invisible.** No overlays, no flashes, no notifications — windows simply end up maximized where they belong. The only visual it ever shows is a 2-second zone preview when you change the split ratio from the tray, because you need to see where the boundary landed.

## Build

```powershell
.\build.ps1
```

That's it. It uses `csc.exe` from the .NET Framework that ships in every Windows install (which is also why the code is C# 5 — no string interpolation, no `?.`). Output is `ZoneMax.exe`. Run it; it adds itself to startup (tray menu to opt out) and lives in the tray.

`verify.ps1` is an end-to-end proof: it launches an isolated throwaway Chrome, parks it in a zone, issues a plain maximize, and asserts the window came back truly maximized (`IsZoomed=True`) inside the zone.

## The landmines (or: why nobody ships this)

The trick is three API calls. Keeping it stable cost every lesson below, each of which was a real, user-visible bug. If you're building anything in this space, this list is the actual value of this repo:

- **`GetWindowPlacement().rcNormalPosition` lies to you.** It's reported in workspace coordinates relative to the primary monitor's work-area origin — which this program *moves*. Track window↔zone assignment yourself.
- **Never read the work area back from Windows and call it truth.** A crash while the work area is shrunk leaves the shrunken value behind, and `SPI_SETWORKAREA` with a NULL rect is a silent no-op, not a reset. Derive the true work area from the monitor rect minus what the taskbar actually reserves, and self-heal on startup.
- **Never leave the work area armed.** It's a global per-monitor setting. Chrome re-computes its maximized bounds against the *current* work area on **every mouse activation** — so the work area must be armed to a window's zone *synchronously in the mouse hook, before the app receives the click*, and restored moments later. Armed asynchronously, Chrome wins the race on every single click.
- **Chrome re-maximizes on EVERY foreground arrival, not just clicks** (proven with a raw `SetForegroundWindow` repro — no click, instant explosion to the full monitor). Clicks are covered by the mouse hook, but screenshot overlays (Win+Shift+S) hand focus back programmatically, so the keyboard hook must pre-arm the focused window's zone the moment it sees the snip combo — with a TTL long enough to survive the whole snip. Activation paths you can't see coming (Alt+Tab onto a zoned window) are caught after the fact by autofix.
- **Windows silently uninstalls low-level hooks.** Exceed `LowLevelHooksTimeout` (~300ms) once on the hook thread and your keyboard hook is gone — no error, no event. Native Snap quietly takes over and users report "it only does 50/50 now." Keep the hook thread free of all real work, and run a watchdog that compares hook heartbeats against `GetLastInputInfo` and reinstalls dead hooks.
- **`RegisterHotKey` cannot take Win+Arrow** (error 1409 — the shell owns them, even with Snap disabled). Take them below the shell with `WH_KEYBOARD_LL`, and inject an unassigned virtual key so swallowing the arrow doesn't pop the Start menu.
- **Windows Snap must stay ON.** "Drag a maximized window to un-maximize it" *is* Aero Snap. Turn Snap off and every truly-maximized window — i.e. every window this program touches — becomes undraggable. Bonus: `SPI_SETWINARRANGING` takes its boolean in `uiParam`, not `pvParam` as documented; called MSDN's way it returns success and does nothing.
- **HWNDs are recycled.** Any per-window state keyed by handle must also store the owning PID, or a brand-new window inherits a dead window's zone.
- Also featured: per-monitor-V2 DPI awareness (system-aware breaks the moment two monitors have different scales), UIPI (elevated windows must fall through to native Snap — you can't move them and you've already eaten the keystroke), and maximized windows overhanging their zone by 7px into the neighbouring monitor.

## Requirements

Windows 10 1703+ / Windows 11, .NET Framework 4.x (in-box). Built and daily-driven on Windows 11 with a 3440×1440 ultrawide + mixed-DPI laptop panel. One machine's worth of testing — file issues.

## License

MIT © [Rodrigo Nask](https://github.com/rodrigonask)
