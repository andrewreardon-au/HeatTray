# HeatTray

**v1.3** (2026-10-12) by Andrew Reardon, andrewreardon@gmail.com

Shows in the system tray whether your CPU is actually running slower than it can, so you know when heat is genuinely costing you performance and when you can let it ride.

![HeatTray in the system tray showing 73% in orange, with the hover tooltip](screenshots/HeatTray-screenshot_tray.png)

*An example of a slowed reading: the icon is orange and the tooltip says what it feels like. Staged for the picture: the laptop was idle, so the idle threshold was lowered (`--gate=5`) and a high reference was set to make it read slow.*

## How is this different from HWiNFO?

HWiNFO is the right tool for finding out *what* your CPU is doing. HeatTray answers one question, at a glance: **is this laptop running slower than it should, and does it matter right now?** Three things set it apart:

- It tells you what a slowdown feels like. When the icon goes orange or red, the tooltip adds a tongue-in-cheek line such as *Like a Pentium 4 2.4GHz from 2002*: a feel for how bad "slow" is that a clock reading never gives you. It is an order-of-magnitude joke with a scale behind it ([the ready reckoner](#the-ready-reckoner)).
- It measures against your own workload, not a spec sheet. Calibrate once, with the laptop cool and your real load running, and 100 means "as fast as this load normally runs on this machine" ([how](#calibrate-to-your-demo-load-do-this-once)). Rated and boost clocks can't tell you that.
- It boils it all down to one number: your speed as a % of normal. 98 is no real slowdown, 75 is a quarter slower, and a hot CPU that is still at full speed stays green. It is a verdict, not a dashboard. Windows' own throttle counters can report "no throttling" while a chip is pinned at its power cap, and HWiNFO's *Average Effective Clock* counts idle time (accurate, but a different question), so a lightly loaded CPU can look half-throttled while its working cores run at full speed. HeatTray reads the busy-core speed instead.

It also needs nothing but Windows. HeatTray is one plain C# file compiled into one small exe, with no drivers, no admin rights and no other tools ([how it works](#how-it-works)). To build it yourself you need only the C# compiler that is already part of Windows, run at user level: `build.ps1`, or the single `csc.exe` command in [How `dist/HeatTray.exe` was built](#how-distheattrayexe-was-built). That suits tightly controlled servers and laptops: nothing to install or approve, and nothing to elevate. Rather not run someone else's exe? Read the source and compile it yourself.

## Getting started

No installer: [download the latest release zip](https://github.com/andrewreardon-au/HeatTray/releases/latest), extract it, and run `dist\HeatTray.exe`.

1. Double-click `dist\HeatTray.exe`.
2. Don't see the icon? Click the small **^** arrow near the clock to show hidden tray icons.
3. [Calibrate it](#calibrate-to-your-demo-load-do-this-once) once for your workload.
4. To close it, right-click the icon > **Exit**.

![The right-click menu](screenshots/HeatTray-screenshot_menu.png)

### Keeping the icon visible

Right-click the taskbar > **Taskbar settings** > **Other system tray icons** > switch **HeatTray** on.

### Starting automatically at login

1. Right-click `HeatTray.exe` > **Create shortcut**.
2. Press **Win + R**, type `shell:startup` and press Enter.
3. Drag the shortcut into that folder. Delete it to turn autostart off.

## Calibrate to your demo load (do this once)

HeatTray can't know what "full speed" means for your workload until you show it. It measures the speed of *your* load once, while the laptop is cool, and compares everything against that. ("Demo load" is the app's name for whatever work you care about: a demo, a build, a render.)

1. Let the laptop cool (idle on a desk, normal ventilation, on AC power in the power mode you demo in).
2. Start your full demo load.
3. Right-click the icon > **Calibrate to demo load (5 min)...** > OK.
4. Keep the load running while the icon shows `CAL` and a countdown. A notification (also recorded in Details) reports the result.

Details worth knowing:

- The reference is the **median speed over the last 2 minutes** of the 5, i.e. the speed your load *settles* at. A cool CPU boosts harder for the first minutes, and comparing against that would leave a healthy steady demo permanently orange. The notification also shows the first-minute average so you can see how big that boost was.
- If it reached 85 °C or more during calibration, the notification warns that the reference may be low. Let it cool and run it again.
- It needs enough loaded readings (about 40% of what a full window holds). If you forgot to start the load, it fails with a message rather than storing a bad reference.
- Calibrate again after changing hardware, power mode, AC vs battery, or what the demo load does. Until you calibrate, the icon is grey (showing the speed in GHz) and, while the CPU is working, the tooltip says "Needs calibration". To forget the reference, delete `baseline.ini` next to the exe.
- The reference is stored in `baseline.ini` next to the exe. It is specific to one computer, so don't copy it to another.

## What the icon shows

One big number, so it can be read at a glance:

```
 98      speed: % of your reference (see "Calibrate" above)
```

The number is your CPU's busy-core speed as a % of the reference you calibrated from your own demo load: 100 = as fast as that load normally runs, and above 100 = faster than it ran during calibration. Lower means something (heat, a power limit, battery, a power mode) is holding it back. The colour of the number is about speed only, not temperature:

| Icon | Meaning |
|---|---|
| 🟢 Green | At or near the reference speed, or better (85%+ by default). Also shown while the CPU is idle and at least that fast, so the icon doesn't change colour every time the load dips |
| 🟠 Orange | Noticeably slowed (under 85%) while the CPU is working. Settings and the command line call this colour amber |
| 🔴 Red | Heavily slowed (under 70%) while the CPU is working |
| ⚪ Grey | No verdict. Either you haven't calibrated yet (there is nothing to take a percentage of, so the icon shows the speed in GHz), or the CPU is idle and its speed is under 85% of your reference. An idle CPU slows itself down on purpose, so an idle reading is never orange or red |
| 🔵 Cyan `CAL` / countdown | Calibration in progress |
| ⚪ Grey "?" | CPU counters unavailable: run `HeatTray.exe --diag` |

The text colours follow the Windows taskbar theme: bright shades on a dark taskbar, darker shades on a light one (and the outline flips from dark to light). If you change the Windows theme the icon follows within a couple of seconds.

You can change what the icon shows: **Settings > Icon shows** has **Percent** (the default), **GHz** (the current busy-core speed, e.g. `3.2`) or **Both** (percent over GHz on two lines, with smaller digits). The tooltip always has both. From the command line: `--show=percent|ghz|both`.

Hover over the icon for the full picture, e.g.

```
Speed 18% 0.6GHz ▼
74C | load 55%
Check cooling (peaked 96C)
Like a Pentium 4 2.4GHz from 2002
```

Line by line: speed, GHz and a trend arrow (▲ rising, ► steady, ▼ falling over about the last 5 minutes of load); temperature and CPU load; a hint; and, when the icon is orange or red, a tongue-in-cheek [ready reckoner](#the-ready-reckoner) line. The hints are:

| Hint | Meaning |
|---|---|
| OK | Running at normal speed. Even if it is hot, you are not being slowed, so carry on |
| Check cooling | Slow **and** hot (85 °C+): airflow, dust, surface, or reduce the load. "(peaked 96C)" means it is no longer that hot but was within the last 2 minutes: a heat slowdown can outlast the heat |
| Check power mode | Slow, with no sign of heat (under 80 °C, and not that hot at any point in the last 2 minutes; it is also what you get with no temperature sensor): check the Windows power mode, AC power / charger wattage, or a firmware power cap |
| Heat or power cap | Slow and warm (80-85 °C, now or within the last 2 minutes): could be either, so watch whether it gets hotter |
| Idle / Needs calibration | No verdict is being made: idle (the icon stays green, or goes grey if the speed is low) or not calibrated yet (grey GHz). See Grey above |

Click the icon (or right-click > **Details...**) for the full breakdown: the temperature and its 2-minute peak, estimated slowdown, the ready-reckoner feel, trend with GHz, the advice in a sentence, your reference and when it was set.

![The Details window](screenshots/HeatTray-screenshot_details.png)

*Staged in the same way as the tray picture at the top, so the numbers are an example.*

### Rules of thumb

| Speed shown | Roughly | What to do |
|---|---|---|
| 85 and up | Little or no slowdown | Ignore the temperature, let it ride |
| 70-85 | Mild to moderate | Fine for bursts; if it stays here **and** it's hot, improve airflow |
| Under 70 | Heavy | Address cooling (or the power mode) now |

A brief dip is normal. What matters is when it **stays** low.

### The ready reckoner

When the icon is orange or red, the tooltip gets a fourth line and Details gets a "Feels like" row: a tongue-in-cheek comparison of how your laptop is running right now with an old computer (your normal speed counts as a current laptop). It compares one core's speed only: an order-of-magnitude feel for *how bad is this*, not a benchmark.

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

The **Amber below** setting (the orange threshold) also decides when the line appears. It is not shown while the CPU is idle or you haven't calibrated yet.

## Settings and command line

Right-click the icon > **Settings...** (left-click opens **Details**) to change what the icon shows (percent, GHz or both), the sample interval, the temperature that counts as "hot", the idle threshold and the speed thresholds (**Amber below** is the orange one). Changes apply immediately and are saved to `settings.ini` next to the exe. If a number is out of range, the box stays open instead of closing: the hint at the bottom turns red and says which setting and what range it takes, and the cursor goes to that box.

![The Settings box](screenshots/HeatTray-screenshot_settings.png)

```
HeatTray.exe [options]

  -i <sec>, --interval=<sec>  seconds between samples (default 2)
  --warn=<C>                  temperature that counts as hot, for the hints and the
                              calibration warning (default 85)
  --gate=<pct>                CPU load % below which it counts as idle (default 30)
  --amber=<pct>               orange when speed is below this % of the reference (default 85)
  --red=<pct>                 red when speed is below this % of the reference (default 70)
  --show=<percent|ghz|both>   what the icon shows (default percent)
  --diag                      print which counters work and 5 live samples, then exit
  -h, --help                  show help and exit
  -v, --version               show version info and exit
```

With no options it uses the saved settings. With any valid option given, the command line wins and `settings.ini` is ignored (flags are never saved). `--diag` prints which counters work on this machine, the taskbar theme it detected, what the icon is set to show, plus five live samples. Run it first if something looks wrong on a colleague's PC.

## Limits worth knowing

- It measures the outcome (slower clocks) and a coarse ACPI temperature, not the chip's internal power and thermal limits. It can't tell you *why* the CPU is slow: the hint is a rule of thumb from speed vs temperature. Firmware power caps (sustained TDP limits), battery operation and Windows power modes (e.g. "Best power efficiency") all look identical to heat from here.
- The reference is only as good as the calibration: a different power mode, AC vs battery, or a demo load that changes shape will make the number read wrongly until you calibrate again. Calibrating while it heats up and throttles gives a low reference (you get a warning if it reached 85 °C).
- A heat slowdown can outlast the heat. After a firmware thermal clamp the CPU can stay pinned at its lowest speed for 30-40 seconds while the temperature falls back below 80 °C (seen on a laptop resting on a pillow: 96 °C, then ~0.5 GHz for 36+ seconds until it had cooled to ~73 °C). So the hints go by the hottest temperature of the last 2 minutes, not just the current one, and say so (`Check cooling (peaked 96C)`). A clamp that outlasts those 2 minutes can still read as "Check power mode"; if it was hot a few minutes ago, treat it as cooling.
- Until you calibrate there is no percentage to show, so the icon shows the speed in GHz (grey) and the tooltip's "Speed N%" is then a % of the CPU's *rated* speed, a different scale from the coloured readings (% of your reference).
- The light/dark choice follows the Windows system theme setting. With an unusual taskbar colour (a very light accent colour, high contrast) the icon text can be harder to read; the outline helps.
- Some machines (desktops, some VMs) don't expose a thermal zone. Speed still works, but with no temperature a slow reading always says Check power mode, so read it as "slow, cause unknown". Where there is a sensor, it reports whole degrees only and updates slowly.
- The Windows counter names are English-only: on a non-English Windows the counters fail to open and the icon shows a grey "?". `--diag` will say so.
- Windows' ACPI zone temperature typically reads a few degrees different from the CPU core sensor (the reading HWiNFO calls Tctl). Tune `--warn` for your hardware.
- A sleep/resume during a calibration can cut it short (it uses wall-clock time); just run it again.
- The exe is unsigned. If SmartScreen or IT policy blocks a copy from a network share or USB drive, right-click > Properties > **Unblock**, or rebuild locally with `build.ps1` (it uses the C# compiler that ships with Windows, no downloads).

## How it works

It reads three standard Windows performance counters:

- `Processor Information\% Processor Performance`: how fast the cores run *while they are working*, relative to their rated speed (it can exceed 100% when boosting). GHz is this times the CPU's nominal clock.
- `Processor Information\% Processor Time`: how busy the CPU is (deliberately *not* `% Processor Utility`, which is itself scaled by clock speed and would hide a hard throttle by making a busy CPU look idle).
- `Thermal Zone Information\Temperature`: the hottest ACPI thermal zone.

Readings are smoothed over the last 5 samples. Above the idle threshold (30% load) the speed is compared with the reference and coloured green, orange or red. Below it the same percentage is shown, green (or grey if it is low) and never orange or red.

## What's new in v1.3

- **Settings checks what you type.** A number that is out of range keeps the box open instead of closing it: the hint at the bottom turns red, says which setting and what range it takes, and the cursor goes to that box. Before, the box closed, threw away everything you had typed and showed one message listing every range.
- **One window at a time.** Clicking the icon again (or a menu item) while Details, Settings or About is open brings the open window to the front instead of stacking another copy on top.
- **The Details window is as tall as its text,** so there is nothing to scroll on a normal screen. On a screen too small to hold it all, it keeps its scrollbar.
- **A lighter red on dark taskbars,** easier to see (about 5:1 contrast against a dark taskbar, up from about 4:1). The red on a light taskbar is unchanged.
- Your calibration and saved settings carry over from earlier versions.

## What's in this repo

| Path | What it is |
|---|---|
| `src/HeatTray.cs` | The entire app: one plain C# file, no dependencies beyond .NET Framework |
| `src/HeatTray.ico` | The exe's embedded icon |
| `dist/HeatTray.exe` | Prebuilt, ready-to-run binary |
| `build.ps1` | Rebuilds `dist/HeatTray.exe` from source on your own machine |
| `make-icon.ps1` | Regenerates `src/HeatTray.ico` (only needed if you change the artwork) |
| `README.txt` | This guide as plain text, for the zip |
| `screenshots/` | The images in this README |
| `LICENSE` | MIT |

## How `dist/HeatTray.exe` was built

```powershell
csc.exe /nologo /target:winexe /platform:x64 /out:dist\HeatTray.exe /win32icon:src\HeatTray.ico `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\HeatTray.cs
```

`csc.exe` is the one bundled with .NET Framework 4 (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). Or just run `build.ps1`.

SHA-256 of the committed `dist/HeatTray.exe` (v1.3): `41EB94FFA1F29C4BD3C8EFAEBF8922B87671364627546029E99A4125AE045FE3`

## License

MIT, see [LICENSE](LICENSE).
