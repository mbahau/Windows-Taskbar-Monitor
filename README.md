# Windows Taskbar Monitor

A tiny, fast Windows tray app that shows live **CPU %**, **GPU % (per physical GPU)**, **RAM used/total**, **disk read/write speed**, and **network (Wi‑Fi/Ethernet) speed** right next to your clock.

- **Native .exe, ~20 KB.** No installer, no runtime, no .NET SDK needed on the target PC (uses .NET Framework 4.8, built into Windows 10/11).
- **Lightweight**: ~25–30 MB RAM, near-zero CPU overhead, 1-second refresh via native Windows performance counters.
- **Multi-GPU aware**: correctly separates integrated + dedicated GPUs (e.g. Intel iGPU + NVIDIA/AMD dGPU) using the same low-level adapter API Task Manager itself uses — no "combined into one bogus number" problem.
- **Persistent panel**: click the tray icon for a dark, color-coded panel (green/amber/red by load) that stays on screen until you close it — drag it anywhere, close with the "×".
- **Right-click** for a menu: *Show monitor*, *Start with Windows* (toggle), *Exit*.
- **Auto-start on boot** via a per-user registry Run key — no admin rights required.

## Quick install (this PC)

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

This builds `SysMonitor.exe` (if not already built) and turns on auto-start. The app launches immediately — look in the tray (may be under the `^` overflow arrow the first time; drag it out to keep it always visible).

## Share with someone else

Just copy this whole folder (or zip it) — everything needed is here:

- `SysMonitor.exe` — the app itself. **Just double-click it to run** — no install needed to try it.
- `SysMonitor.cs`, `app.manifest`, `build.cmd` — source, in case they want to rebuild it (e.g. on a machine without this exact exe, or to audit/modify the code).
- `Install.ps1` — one-command setup: builds (if needed) + enables auto-start.

To have it start automatically on their PC too, they should run `Install.ps1` (or manually add a shortcut to `shell:startup`).

## How it works

- **CPU** — `Processor(_Total)\% Processor Time` performance counter.
- **GPU** — reads `GPU Engine\Utilization Percentage` for all `engtype_3D` instances, grouped **per physical adapter** by the LUID embedded in each instance's name. Each LUID is resolved to a friendly name (e.g. "NVIDIA", "Intel") via the low-level `D3DKMTOpenAdapterFromLuid` / `D3DKMTQueryAdapterInfo` API — the same mechanism Task Manager itself uses. Stale/ghost LUIDs left behind by a driver reset (no longer resolvable) are silently dropped rather than shown as a mystery extra "GPU".
- **RAM** — `GlobalMemoryStatusEx` (physical memory used vs. total).
- **Disk** — `PhysicalDisk(_Total)\Disk Read Bytes/sec` and `...Write Bytes/sec` counters, converted to MB/s.
- **Network** — delta of bytes sent/received across active adapters each second, converted to Mbps; the Wi‑Fi adapter is preferred for the displayed name when present.
- **Auto-start** — writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SysMonitor` pointing at the exe (per-user, no admin needed, easy to remove via the tray menu toggle or `regedit`).

## Notes for anyone downloading this

- The exe isn't code-signed (no certificate), so Windows SmartScreen may show *"Windows protected your PC"* on first run — click **More info → Run anyway**. This is normal for small unsigned tools, not a detection of anything malicious; read the source yourself, it's ~700 lines of plain C#.
- Windows 11 often hides newly-added tray icons in the collapsed "Other icons" flyout by default. If you don't see it next to the clock, go to **Settings → Personalization → Taskbar → Other system tray icons** and toggle it on — one-time, per PC.

## Uninstall

Right-click the tray icon → **Start with Windows** to untick it, then **Exit**. Delete the folder — nothing else is written to your system (no services, no scheduled tasks, no admin-level hooks).

## Rebuild from source

Requires only what's already on any Windows 10/11 PC (the C# compiler shipped with .NET Framework 4.8):

```bat
build.cmd
```
