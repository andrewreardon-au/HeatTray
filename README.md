# Heat Tray

**v1.2** - 2026-10-09
**Author:** Andrew Reardon, andrewreardon@gmail.com

Shows, in the system tray, whether your CPU is actually running slower than it can - so you know when heat is genuinely costing you performance and when you can just let it ride.

## What the icon shows

The icon is two lines of text:

```
 87     <- speed: % of your reference (see "Calibrate" below)
 2.9    <- current busy-core speed in GHz
```

**Speed** is your CPU's busy-core speed as a % of the reference you calibrated from your own demo load (100 = as fast as that load normally runs). Lower means something - heat, a power limit, battery, a power mode - is holding it back. The colour of the number is about **speed only**, not temperature:

| Icon | Meaning |
|---|---|
| 🟢 Green | At or near the reference speed (85%+ by default) |
| 🟠 Orange | Noticeably slowed (under 85%) |
| 🔴 Red | Heavily slowed (under 70%) |
| ⚪ Grey | Nothing to compare with, so no green/orange/red. Either the CPU is idle (load under 30% - it slows itself down when idle, which is normal) or you haven't calibrated yet. The top number is then the raw speed as a % of the CPU's rated speed, not of your reference |
| 🔵 Cyan `CAL` / countdown | Calibration in progress |
| ⚪ Grey "?" | CPU counters unavailable - run `HeatTray.exe --diag` |

The text colours follow the Windows taskbar theme: bright shades on a dark taskbar, darker shades on a light one (and the outline flips from dark to light). If you change the Windows theme the icon follows within a couple of seconds.

**Hover** for the full picture, e.g.

```
Speed 18% 0.6GHz ▼
77C | load 55%
Check power mode
Like a Pentium 4 2.4GHz from 2002
```

Line by line: speed, GHz and a trend arrow (▲ rising, ► steady, ▼ falling over about the last 5 minutes of load); temperature and CPU load; a hint; and, when the icon is orange or red, a tongue-in-cheek [ready reckoner](#the-ready-reckoner) line. The tooltip is short lines on purpose: the Windows taskbar wraps long tray tooltips, so one long line wraps badly. The hints are:

| Hint | Meaning |
|---|---|
| OK | Running at normal speed. Even if it is hot, you are not being slowed - carry on |
| Check cooling | Slow **and** hot (85 °C+) - airflow, dust, surface, or reduce the load |
| Check power mode | Slow but **not** hot (under 80 °C) - not heat: check the Windows power mode, AC power / charger wattage, or a firmware power cap |
| Heat or power cap | Slow and warm (80-85 °C) - could be either; watch whether it gets hotter |
| Idle / Needs calibration | The icon is grey: no comparison is being made (see Grey above) |

Click the icon (or right-click → **Details...**) for the full breakdown: estimated slowdown, the ready-reckoner feel, trend with GHz, the advice in a sentence, your reference and when it was set.

### Rules of thumb

| Speed shown | Roughly | What to do |
|---|---|---|
| 90-100 | Little or no slowdown | Ignore the temperature, let it ride |
| 70-90 | Mild to moderate | Fine for bursts; if it stays here **and** it's hot, improve airflow |
| Under 70 | Heavy | Address cooling (or the power mode) now |

A brief dip is normal. What matters is when it **stays** low.

### The ready reckoner

When the icon is orange or red, the tooltip gets a fourth line and Details gets a "Feels like" row saying roughly which vintage of CPU your laptop is performing like right now, counting your normal speed as a current laptop. It is tongue-in-cheek and single-thread only: an order-of-magnitude feel for *how bad is this*, not a benchmark.

| Speed (% of your normal, approx.) | Feels like (and the year that CPU debuted) |
|---|---|
| 85 and up | as intended (no line shown) |
| 62-85 | a Core i5-4690 from 2014 |
| 46-62 | a Core i5-2500K from 2011 |
| 32-46 | a Core 2 Duo E8400 from 2008 |
| 21-32 | a Core 2 Duo E6600 from 2006 |
| 14-21 | a Pentium 4 2.4GHz from 2002 |
| 8.5-14 | a Pentium 4 1.8GHz from 2001 |
| 5-8.5 | a Pentium III 733 from 1999 |
| 1.9-5 | a Pentium II 400 from 1998 |
| 0.7-1.9 | a Pentium 166 from 1996 |
| 0.34-0.7 | a Pentium 60 from 1993 |
| 0.1-0.34 | a 486 DX2-66 from 1992 |
| 0.014-0.1 | a 386DX-16 from 1985 |
| under 0.014 | an abacus from 2400 BC |

The "orange below" threshold in Settings also decides when the line appears. It is not shown while the icon is grey (idle or not calibrated).

## Calibrate to your demo load (do this once)

HeatTray can't know what "full speed" means for your workload until you show it. It measures the speed of **your** load once, while the laptop is cool, and compares everything against that.

1. Let the laptop cool (idle on a desk, normal ventilation, on AC power in the power mode you demo in).
2. Start your full demo load.
3. Right-click the icon → **Calibrate to demo load (5 min)...** → OK.
4. Keep the load running while the icon shows `CAL` and a countdown. A notification (also recorded in Details) reports the result.

Details worth knowing:

- The reference is the **median speed over the last 2 minutes** of the 5, i.e. the speed your load *settles* at. A cool CPU boosts harder for the first minutes; comparing against that would leave a healthy steady demo permanently orange. The notification also shows the first-minute average so you can see how big that boost was.
- If it reached 85 °C+ during calibration the notification warns that the reference may be low - let it cool and run it again.
- It needs enough loaded readings (about 40% of what a full window holds); if you forgot to start the load it fails with a message rather than storing a bad reference.
- Calibrate again after changing hardware, power mode, AC vs battery, or what the demo load does. Until you calibrate, the icon is grey and the tooltip says "Needs calibration". To forget the reference, delete `baseline.ini` next to the exe.
- The reference is stored in `baseline.ini` next to the exe. It is specific to one computer - don't copy it to another.

## How it works (and why it's dependency-free)

It reads three standard Windows performance counters - no drivers, no admin rights, no other tools:

- `Processor Information → % Processor Performance` - how fast the cores run *while they are working*, relative to their rated speed (can exceed 100% when boosting). GHz is this times the CPU's nominal clock.
- `Processor Information → % Processor Time` - how busy the CPU is (deliberately *not* `% Processor Utility`, which is itself scaled by clock speed and would hide a hard throttle by making a busy CPU look idle).
- `Thermal Zone Information → Temperature` - the hottest ACPI thermal zone.

Readings are smoothed over the last 5 samples. Above the idle threshold (30% load) the speed is compared with the reference.

## What's in this repo

| Path | What it is |
|---|---|
| `src/HeatTray.cs` | The entire app - one plain C# file, no dependencies beyond .NET Framework |
| `src/HeatTray.ico` | The exe's embedded icon |
| `dist/HeatTray.exe` | Prebuilt, ready-to-run binary |
| `build.ps1` | Rebuilds `dist/HeatTray.exe` from source on your own machine |
| `make-icon.ps1` | Regenerates `src/HeatTray.ico` (only needed if you change the artwork) |

## Getting started

1. Double-click `dist\HeatTray.exe`.
2. Don't see it? Click the small **^** arrow near the clock to show hidden tray icons.
3. Calibrate (above).
4. To close it, right-click the icon → **Exit**.

To pin it visibly: right-click the taskbar → **Taskbar settings** → **Other system tray icons** → toggle **HeatTray** on.

## Starting automatically on login

1. Right-click `HeatTray.exe` → **Create shortcut**.
2. Press **Win + R**, type `shell:startup`, press Enter.
3. Drag the shortcut into that folder. Delete it to turn autostart off.

## Settings and command line

Right-click the icon → **Settings...** (left-click opens **Details**) to change the sample interval, the idle threshold, the speed thresholds and the temperature that counts as "hot". Changes apply immediately and are saved to `settings.ini` next to the exe.

```
HeatTray.exe [-i <sec>] [--warn=<C>] [--gate=<pct>]
             [--amber=<pct>] [--red=<pct>] [--diag] [-h] [-v]
```

`--warn` is the temperature that counts as "hot", used for the hints and the calibration warning (default 85). With no options it uses the saved settings. With any option given, the command line wins and `settings.ini` is ignored (flags are never saved). `--diag` prints which counters work on this machine, the taskbar theme it detected, plus five live samples - run it first if something looks wrong on a colleague's PC.

## Limits worth knowing

- It measures the **outcome** (slower clocks) and a coarse ACPI temperature, not the chip's internal power/thermal limits. It can't tell you *why* the CPU is slow - the hint is a rule of thumb from speed vs temperature. Firmware power caps (sustained TDP limits), **battery operation and Windows power modes (e.g. "Best power efficiency") all look identical to heat** from here.
- The reference is only as good as the calibration: a different power mode, AC vs battery, or a demo load that changes shape will make the number read wrongly until you calibrate again. Calibrating while it heats up and throttles gives a low reference (you get a warning if it reached 85 °C).
- **A heat slowdown can outlast the heat.** After a firmware thermal clamp the CPU can stay pinned at its lowest speed for 30-40 seconds while the temperature falls back below 80 °C (seen on a laptop resting on a pillow: 96 °C, then ~0.5 GHz for 36+ seconds until it had cooled to ~73 °C). In that window the hint can say "Check power mode" even though heat caused it. If it was hot a minute ago, treat it as cooling.
- Grey readings (idle or not calibrated) show the raw % of the CPU's rated speed, which is a different scale from the coloured readings (% of your reference).
- The light/dark choice follows the Windows system theme setting. With an unusual taskbar colour (a very light accent colour, high contrast) the icon text can be harder to read; the outline helps.
- Some machines (desktops, some VMs) don't expose a thermal zone; speed still works, and the hints just can't use temperature. `Thermal Zone Information` also only has 1 K resolution and updates slowly.
- The Windows counter names are **English-only**: on a non-English Windows the counters fail to open and the icon shows a grey "?". `--diag` will say so.
- Windows' ACPI zone temperature typically reads a few degrees different from the CPU core sensor (Tctl). Tune `--warn` for your hardware.
- A sleep/resume during a calibration can cut it short (it uses wall-clock time); just run it again.
- Unsigned exe: if SmartScreen or IT policy blocks a copy from a network share/USB, right-click → Properties → **Unblock**, or rebuild locally with `build.ps1` (uses the C# compiler that ships with Windows - no downloads).

## How `dist/HeatTray.exe` was built

```powershell
csc.exe /nologo /target:winexe /platform:x64 /out:dist\HeatTray.exe /win32icon:src\HeatTray.ico `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\HeatTray.cs
```

`csc.exe` is the one bundled with .NET Framework 4 (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). Or just run `build.ps1`.

SHA-256 of the committed `dist/HeatTray.exe` (v1.2): `B7155EC9A60508FF068B691DCB5F8FB750A41647064FF29E94A7525696D99ECE`

## License

MIT - see [LICENSE](LICENSE).
