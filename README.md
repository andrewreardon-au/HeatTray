# Heat Tray

**v1.1** - 2026-10-08
**Author:** Andrew Reardon, andrewreardon@gmail.com

Shows, as a number in the system tray, how much slower your CPU is running than it normally can - so you know when heat is genuinely costing you performance and when you can just let it ride.

## What the number means

The number is your CPU's **busy-core speed as a % of its normal speed at the same load, learned while the machine was not hot**. 100 = as fast as it normally runs; lower = something (heat, a power limit, battery, a power mode) is holding it back.

| Icon | Meaning |
|---|---|
| 🟢 Green | Running at or near full speed, and not hot |
| 🟠 Orange | Noticeably slowed, or getting hot (85 °C+ by default) |
| 🔴 Red | Heavily slowed, or very hot (92 °C+ by default) |
| ⚪ Light-grey number | Idle (CPU load under 30%) - shows the **temperature** in °C, speed isn't judged (tooltip says "Idle"). Also shown, in an alarm colour if hot, while a load band has no cool baseline yet (tooltip says "Learning") |
| ⚪ Light-grey "?" | CPU counters unavailable - run `HeatTray.exe --diag` |

Hover for the exact numbers, e.g. `Speed 87% | 91C | load 62%`. Click the icon (or right-click → **Details...**) for the full breakdown, including an estimated slowdown (100 - speed).

### Rules of thumb

| Speed shown | Roughly | What to do |
|---|---|---|
| 90-100 | Little or no slowdown | Let it ride |
| 70-90 | Mild to moderate | Fine for bursts; improve airflow if it stays here |
| Under 70 | Heavy | Address cooling now (vents, stand, lower the load) |

A brief dip is normal boost behaviour. What matters is when it **stays** low while the temperature sits at the top of its range.

## How it works (and why it's dependency-free)

It reads three standard Windows performance counters - no drivers, no admin rights, no other tools:

- `Processor Information → % Processor Performance` - how fast the cores run *while they are working*, relative to their rated speed (can exceed 100% when boosting).
- `Processor Information → % Processor Time` - how busy the CPU is (deliberately *not* `% Processor Utility`, which is itself scaled by clock speed and would hide a hard throttle by making a busy CPU look idle).
- `Thermal Zone Information → Temperature` - the hottest ACPI thermal zone.

Because a heavy all-core load is naturally slower than a light one, it learns a speed baseline **per load band** (30-45%, 45-60%, 60-75%, 75-90%, 90%+) and judges you against the band you're in.

The baseline is the **75th percentile of the speeds seen in that band while the temperature was below the amber threshold** (85 °C by default). That does two things: boost bursts can't inflate it (it isn't the all-time maximum), and a long hot session can't erode it (hot samples are never learned). The first minute or so at a given load shows "Learning baseline" in the tooltip.

### Get an honest baseline

The baseline learns only from samples taken *below the amber temperature*, so it can't learn "hot" as normal - but a band that has only ever been seen hot has no baseline at all and just shows "Learning (needs cool)". For a meaningful number:

1. Start with the laptop cool and well ventilated.
2. Right-click the icon → **Reset baseline...**
3. Run **the heavy load you actually want to be warned about** (e.g. your full demo load) for the first few minutes, so the high-load bands learn what "full speed" is while the machine is still cool.

Baselining only on light everyday work teaches only the low bands; the heavy bands would then learn "hot" the first time you run your demo.

After that, the number is "% of how fast this machine can run this kind of load".

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
3. To close it, right-click the icon → **Exit**.

To pin it visibly: right-click the taskbar → **Taskbar settings** → **Other system tray icons** → toggle **HeatTray** on.

## Starting automatically on login

1. Right-click `HeatTray.exe` → **Create shortcut**.
2. Press **Win + R**, type `shell:startup`, press Enter.
3. Drag the shortcut into that folder. Delete it to turn autostart off.

## Settings and command line

Right-click the icon → **Settings...** (left-click opens **Details**) to change the sample interval, the amber/red temperatures, the idle threshold and the speed thresholds. Changes apply immediately and are saved to `settings.ini` next to the exe.

```
HeatTray.exe [-i <sec>] [--warn=<C>] [--hot=<C>] [--gate=<pct>]
             [--amber=<pct>] [--red=<pct>] [--diag] [-h] [-v]
```

With no options it uses the saved settings. With any option given, the command line wins and `settings.ini` is ignored (flags are never saved). `--diag` prints which counters work on this machine plus five live samples - run it first if something looks wrong on a colleague's PC.

## Limits worth knowing

- It measures the **outcome** (slower clocks) and a coarse ACPI temperature, not the chip's internal power/thermal limits. It can't tell you *why* the CPU is slow - only that it is, and how hot the laptop is. Firmware power caps (sustained TDP limits), **battery operation and Windows power modes (e.g. "Best power efficiency") all look identical to heat** from here - check those before blaming the cooling.
- Workloads inside one load band differ (a few busy cores boost higher than many busy cores at the same total load), so the number can wobble by ~10% with workload shape even when cool. Read the trend with the temperature, not a single value. If it looks permanently off, **Reset baseline...**.
- Some machines (desktops, some VMs) don't expose a thermal zone; the icon then judges on speed alone. `Thermal Zone Information` also only has 1 K resolution and updates slowly.
- The Windows counter names are **English-only**: on a non-English Windows the counters fail to open and the icon shows a grey "?". `--diag` will say so.
- Windows' ACPI zone temperature typically reads a few degrees different from the CPU core sensor (Tctl). Tune `--warn` / `--hot` for your hardware.
- Unsigned exe: if SmartScreen or IT policy blocks a copy from a network share/USB, right-click → Properties → **Unblock**, or rebuild locally with `build.ps1` (uses the C# compiler that ships with Windows - no downloads).

## How `dist/HeatTray.exe` was built

```powershell
csc.exe /nologo /target:winexe /platform:x64 /out:dist\HeatTray.exe /win32icon:src\HeatTray.ico `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\HeatTray.cs
```

`csc.exe` is the one bundled with .NET Framework 4 (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). Or just run `build.ps1`.

SHA-256 of the committed `dist/HeatTray.exe`: *(fill in with `Get-FileHash dist\HeatTray.exe` when committing a release build)*

## License

MIT - see [LICENSE](LICENSE).
