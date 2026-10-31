# HeatTray

**v2.3** (2026-10-31) by Andrew Reardon

HeatTray puts one number and one colour in the system tray: how fast your CPU's working cores are running compared with the CPU's rated clock. `1.0` in green is normal. Orange or red means the CPU is slowed while it works, and the tooltip says whether heat or something else (the power mode, the charger) is the likely cause and what to try.

It needs only 64-bit Windows 10 or 11: no installer, no drivers, no admin rights. It collects and sends nothing. It reads Windows performance counters and writes only its own settings file (plus one entry under your own account's Run key if you tick **Start with Windows**).

Two slow readings, with different advice:

| Slow, **not hot**: check power mode and charger | Slow **and hot**: clear the vents |
|---|---|
| ![Orange 0.6 icon, tooltip: Speed 61%, Slower, not hot (77C), Plug in, check power mode](screenshots/HeatTray-screenshot_tray.png) | ![Orange 0.4 icon, tooltip: Speed 41%, Slower, and hot (89C), Clear vents, lift laptop](screenshots/HeatTray-screenshot_tray_hot.png) |

*Staged for the pictures: the laptop was idle, so the idle threshold was lowered and the speed scaled down to look slow, and the temperature in the second picture was raised. In the left picture the 77C is real.*

## Getting started

No installer: [download the latest release zip](https://github.com/andrewreardon-au/HeatTray/releases/latest) and extract it.

1. Double-click `dist\HeatTray.exe`.
2. If you don't see the icon, click the small **^** arrow near the clock to show hidden tray icons.
3. Give it about 15 seconds: the icon is grey while it takes its first readings.
4. To close it, right-click the icon > **Exit**.

![The right-click menu](screenshots/HeatTray-screenshot_menu.png)

**Keeping the icon visible:** right-click the taskbar > **Taskbar settings** > **Other system tray icons** > switch **HeatTray** on.

**Starting at login:** open Settings and tick **Start with Windows**. Untick it to stop. (Or put a shortcut to `HeatTray.exe` in the folder that **Win + R** > `shell:startup` opens, and delete it to turn autostart off. Use one or the other, not both.) If you move the folder on the same PC, open Settings and press OK so the entry points at the new place.

**Moving to another computer:** copy the folder and run `dist\HeatTray.exe`. Tick **Start with Windows** again there if you want it.

## What the icon shows

```
 0.9      speed: the busy cores' clock as a fraction of the CPU's rated clock
```

The number is how fast your CPU's working cores are running as a fraction of the CPU's rated clock, which is the speed Windows shows in **This PC > Properties** (for example 3.30 GHz). `1.0` is the rated speed, `0.7` is 70%, and anything above `1.0` is boost. The ratio is rounded to one decimal, except that a reading that is still orange or red is never rounded up to the limit: 69.6% under load shows `0.6`, so an orange icon never says `0.7`. The tooltip shows the percent.

The colour is about speed only, not temperature:

| Icon | Meaning | What to do |
|---|---|---|
| Green | The load is light (under the idle threshold, 30% CPU load by default), or the CPU is working at 70% of its rated speed or more. It stays green when the load dips, so it does not change colour every few seconds | Nothing. A hot CPU that is still at full speed is not being slowed |
| Orange | Slowed (under 70%) while the CPU is working. Settings and the command line call this colour amber | Fine for bursts. If it stays here and the CPU is hot, improve airflow. If it is cool, check the power mode and charger |
| Red | Heavily slowed (under 40%) while the CPU is working | Fix the cooling or the power mode now |
| Orange `HOT` | The load is light but the CPU is at 85 °C or more. It is a word, not a number, so it can't be mistaken for a slow reading | Something is trapping heat: clear the vents, lift the laptop |
| Grey number | Starting up: the first readings take about 15 seconds | Wait |
| Grey `?` | The CPU counters can't be read | Run `HeatTray.exe --diag` |

A brief dip is normal. What matters is when it stays low.

The text colours follow the Windows taskbar theme: bright shades on a dark taskbar, darker shades on a light one. If you change the theme the icon follows within a few seconds.

### What the icon can show

**Settings > Icon shows** has eight choices:

- **Speed ratio** (the default): `1.0`, `0.7`, `1.2`
- **Usage**: the CPU load, for example `23%`
- **Temperature**: for example `67C`, or `153F` if you chose Fahrenheit
- **Percent**: the same figure as the ratio as a whole number, for example `92`
- **GHz**: the current busy-core clock, for example `3.2`
- **Speed ratio + GHz**, **Usage + GHz** and **Percent + GHz**: two lines with smaller digits

The colour follows the speed verdict whichever number is shown, and the tooltip always has the percent and the GHz. **Settings > Temperature unit** switches every temperature you see between Celsius and Fahrenheit.

### Readings above and below 100%

100% is the CPU's *rated* (base) clock. Real life lands around it, not on it:

- A busy laptop often sits a little under it: power limits, battery, a power-saver mode, or heat.
- A lightly loaded CPU often boosts above it. Numbers over 100% are normal, and green.
- At light load the cores choose their own speed to save power, which can be well under or over the rated one. That says nothing about heat, so a light-load reading is never orange or red.

Only a big shortfall **under load** means something is holding the CPU back. That is what the orange and red thresholds look for.

### The tooltip

Hover over the icon. Line 1 is the speed as a percent, the current clock *of* the rated clock, and a trend arrow when there is one (`▲` rising, `►` steady, `▼` falling over about the last 5 minutes of load). The next lines give the verdict and what to do. Loaded and healthy:

```
Speed: 92% 3.0GHz of 3.3GHz
Load: Working hard, all fine
CPU: 68 C, usage 74%
```

Slow after heat (it has cooled to 73C, but the slowdown lingers). The CPU line is replaced by an action line, and the last line is the [ready reckoner](#the-ready-reckoner):

```
Speed: 18% 0.6GHz of 3.3GHz ▼
Too hot (now 73C, peak 96C)
Clear vents, lift laptop
Like a Pentium 4 2.4GHz from 2002
```

Light load:

```
Speed: 110% 3.6GHz of 3.3GHz
Load: Light load, core speed is variable
CPU: 52 C, usage 8%
```

The verdicts:

| Tooltip says | Meaning |
|---|---|
| Working hard, all fine | Loaded and at 70% or more of rated speed. Even if it is hot, you are not being slowed |
| Boosting, faster than rated | Loaded and above 100%: the CPU is boosting past its rated clock |
| Slower, and hot / Too hot, very slow (96C) | Slow **and** hot (85 °C or more): clear the vents, lift the laptop off soft surfaces, or reduce the load. "(now 73C, peak 96C)" means it is no longer that hot but was within the last 2 minutes: a heat slowdown can outlast the heat |
| Slower, not hot (52C) | Slow with no sign of heat (under 80 °C, and not hotter at any point in the last 2 minutes): plug in, check the Windows power mode, the charger wattage, or a firmware power cap |
| Slower, warm (80C) | Slow and warm (80 to 85 °C): could be heat or a power limit, so watch whether it gets hotter |
| Slower, no temp sensor | Slow, and this machine reports no temperature, so the cause is unknown: try the power mode, the charger and the vents |
| Light load, core speed is variable | The load is under the idle threshold, so there is nothing to judge |
| Hot at light load | The load is light but the CPU is at 85 °C or more: something is trapping heat |
| Starting up | The first readings after launch |

"Far slower" replaces "Slower" when the icon is red (and "Slower, and hot" becomes "Too hot, very slow"). The temperature in a slow verdict is the hottest reading of the last 2 minutes. When the heat has already eased, a slow verdict can end in "(now 60C, peak 82C)" in the same way as the hot ones.

### Details

Right-click the icon > **Details...** for the full breakdown: the temperature and its 2-minute peak, the verdict, how far below rated speed it is, the ready-reckoner line, the trend with GHz, the advice in a sentence, and the rated clock the percentage is taken from.

![The Details window](screenshots/HeatTray-screenshot_details.png)

*Staged in the same way as the tray picture at the top, so the numbers are an example.*

### The ready reckoner

When the icon is orange or red, the tooltip gets a last line, and the Details "Feels like" row names a CPU (it says "as intended" when nothing is slow): a tongue-in-cheek comparison of how your laptop is running right now with an old computer (100% of rated speed counts as a current laptop). It compares one core's speed only. It gives an order-of-magnitude feel for how bad a slowdown is, and it is not a benchmark.

| Speed (% of rated, approx.) | Feels like (and the year that CPU debuted) |
|---|---|
| 70 and up | as intended (no line shown) |
| 62-70 | a Core i5-4690 from 2014 |
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

The **Amber below** setting (the orange threshold) also decides when the line appears. It is not shown at light load or while starting up.

## Settings and command line

Click the icon, or right-click it > **Settings...**, to change what the icon shows, the temperature unit, whether it starts with Windows, the sample interval, the temperature that counts as "hot", the idle threshold and the speed thresholds (**Amber below** is the orange one). Changes apply immediately and are saved to `settings.ini` next to the exe. If a number is out of range, the box stays open, the hint at the bottom turns red and says which setting and what range it takes, and the cursor goes to that box.

![The Settings box](screenshots/HeatTray-screenshot_settings.png)

With Fahrenheit chosen, the "hot" box is in Fahrenheit too. The `--warn` option and `settings.ini` always use Celsius.

```
HeatTray 2.3: CPU heat slowdown indicator in the system tray

Usage: HeatTray.exe [options]

  -i <sec>,  --interval=<sec>  seconds between samples (default 5)
  --warn=<C>                   temperature that counts as hot, for the hint and the idle HOT warning (default 85)
  --gate=<pct>                 CPU load % below which it counts as idle (default 30)
  --amber=<pct>                amber when speed is below this % of rated (default 70)
  --red=<pct>                  red when speed is below this % of rated (default 40)
  --show=<ratio|usage|temp|percent|ghz|ratio-ghz|usage-ghz|both>
                               what the icon shows (default ratio)
  --diag                       print counter availability + 5 live samples, then exit
  -h, --help                   show this help and exit
  -v, --version                show version info and exit

With no options, the settings last saved from the tray's
Settings dialog (settings.ini next to the exe) are used.
Command-line options always win and are never persisted.
One instance per machine.
```

With no options it uses the saved settings. With any valid option given, the command line wins: `settings.ini` is ignored and never overwritten by flags, and changes you make in Settings during such a run apply until you close it and are not saved to `settings.ini`. The **Start with Windows** tick is the exception: it is a Windows setting, so it takes effect when you press OK, and it starts HeatTray without options. `--diag` prints which counters work on this machine, the taskbar theme it detected, what the icon is set to show, and five live samples. Run it first if something looks wrong.

## Limits

- It measures the outcome (slower clocks) and a coarse ACPI temperature, not the chip's internal power and thermal limits. It can tell you that the CPU is running slow, but not why: the hint is a rule of thumb from speed against temperature. Firmware power caps (sustained TDP limits), battery operation and Windows power modes (for example "Best power efficiency") all look the same as heat from here.
- The thresholds suit a typical laptop. On battery, or in a power-saving mode, a healthy CPU can run at 60 to 80% of its rated speed, so expect orange more often there, and the hint ("Plug in, check power mode") is then the right one. If your machine is orange all the time while performing normally, lower **Amber below** in Settings.
- Light load hides a slowdown. Below the idle threshold (30% CPU load by default) nothing is judged, so a firmware clamp that bites at light load shows green. The one exception is `HOT`: an idle CPU at 85 °C or more turns the icon orange.
- A heat slowdown can outlast the heat. After a firmware thermal clamp the CPU can stay at its lowest speed for 30 to 40 seconds while the temperature falls (seen on a laptop resting on a pillow: 96 °C, then about 0.5 GHz for 36 seconds until it had cooled to about 73 °C). So the hints use the hottest temperature of the last 2 minutes and say so (`Too hot (now 73C, peak 96C)`). A clamp that outlasts those 2 minutes can still read "Slower, not hot"; if it was hot a few minutes ago, treat it as cooling.
- The rated clock comes from the registry (`~MHz`, the speed Windows shows in This PC > Properties). If a machine does not report it, or reports a value outside 500 to 10,000 MHz, the GHz is left out and the percentages still work.
- Some machines (desktops, some VMs) have no thermal zone. Speed still works, and a slow reading then says "no temp sensor" because the cause is unknown. Where there is a sensor, it reports whole degrees only and updates slowly. It typically reads a few degrees different from the CPU core sensor (the one HWiNFO calls Tctl), so tune `--warn` for your hardware.
- On a non-English Windows the usual counter names (which Windows translates) do not exist, so HeatTray asks for the same counters by their English names through Windows' PDH interface, and `--diag` says when it had to. This was built and tested on an English Windows (the fallback is exercised there with a hidden test switch). If you see a grey `?` on another language, please [open an issue](https://github.com/andrewreardon-au/HeatTray/issues) with the `--diag` output.
- With an unusual taskbar colour (a very light accent colour, high contrast) the icon text can be harder to read. The light/dark choice follows the Windows system theme setting, and the outline helps.
- The exe in the release zip is unsigned. If SmartScreen blocks it after download, right-click the zip > Properties > **Unblock** before extracting (the same applies to a copy from a network share or USB drive), or rebuild locally with `build.ps1` (it uses the C# compiler that ships with Windows, no downloads).

## HeatTray and HWiNFO

HWiNFO is the tool for seeing in detail what your CPU is doing. HeatTray answers one question: is this laptop running slower than it should, and does it matter right now?

- **It says what a slowdown feels like.** When the icon goes orange or red, the tooltip adds a line such as *Like a Pentium 4 2.4GHz from 2002*, which gives a feel for how bad "slow" is in a way a clock reading does not. It is an order-of-magnitude joke with a scale behind it ([the ready reckoner](#the-ready-reckoner)).
- **There is nothing to set up.** The number is the busy cores' speed against the rated clock Windows already reports. No calibration, no learning period, no file to keep.
- **It reads the busy-core speed.** Windows' own throttle counters can report "no throttling" while a chip is pinned at its power cap. HWiNFO's *Average Effective Clock* counts idle time, which answers a different question, so a lightly loaded CPU can look half-throttled while its working cores run at full speed.

## How it works

HeatTray is one plain C# file compiled into one small exe, with no drivers and no other tools ([how it was built](#how-distheattrayexe-was-built)). It reads three standard Windows performance counters:

- `Processor Information\% Processor Performance`: how fast the cores run *while they are working*, relative to their rated speed (it can exceed 100% when boosting). GHz is this times the CPU's nominal clock.
- `Processor Information\% Processor Time`: how busy the CPU is (deliberately *not* `% Processor Utility`, which is itself scaled by clock speed and would hide a hard throttle by making a busy CPU look idle).
- `Thermal Zone Information\Temperature`: the hottest ACPI thermal zone.

Readings are smoothed over the last 3 samples (about 15 seconds).

## Upgrading

Exit the old HeatTray first (only one copy can run), then replace the exe.

**From v2.2 or earlier:** the icon now shows the speed ratio by default. If your icon showed Percent, it now shows Speed ratio; choose Percent in Settings to go back. Clicking the icon opens Settings, and Details is on the right-click menu.

**From v1.x:** calibration is gone, so the number now means something different: a fraction of the CPU's rated speed, not of your own demo load. Your old `baseline.ini` is no longer read and can be deleted. Your `settings.ini` still loads; if it holds the old default thresholds (amber 85, red 70) they move to the new defaults (70 and 40), and any other values you chose are kept. If you want calibration back, [v1.3](https://github.com/andrewreardon-au/HeatTray/releases/tag/v1.3) is still on the releases page.

## What's new in v2.3

- **The icon shows a speed ratio by default.** `1.0` is the rated clock, `0.6` is 60% of it, `1.2` is boosting. The colours have not changed: orange under 70% of rated and red under 40%, under load only. A reading that is still orange is never rounded up to the limit (69.6% shows `0.6`, or `69` in Percent).
- **More choices for the icon:** Usage (CPU load), Temperature, Speed ratio + GHz and Usage + GHz, besides Percent, GHz and Percent + GHz.
- **Fahrenheit.** Settings > Temperature unit switches the temperatures you see to F. `--warn` and `settings.ini` stay in Celsius.
- **Start with Windows:** a tick box in Settings. It adds one entry under your own account's Run key: no administrator rights, no shortcut.
- **Labelled tooltip lines:** Speed, Load and CPU, for example `CPU: 68 C, usage 74%`. A slow reading gets a verdict, an action and a "Like..." line instead of the CPU line.
- **Clicking the icon opens Settings** (it used to open Details, which is on the right-click menu).
- **A tidier Settings box,** split into Display and Thresholds.
- **Options are never saved by accident:** started with options such as `--gate=5`, pressing OK in Settings no longer overwrites your saved settings.
- **Non-English Windows:** if the usual counter names are missing, HeatTray reads the same counters by their English names. This is untested on a real localised Windows.
- **A rated clock outside 500 to 10,000 MHz is ignored,** so the GHz is left out instead of shown wrong.
- **About links to the project page,** and `-v` prints that address instead of an email.

## What's new in v2.2

- **Clearer advice.** When the CPU is slow and not hot, the action line reads *Plug in, check power mode*.

Older changes are in the [release notes](https://github.com/andrewreardon-au/HeatTray/releases).

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
| `store/`, `rendertest/` | Image-making scripts and a rendering test tool for developers (not in the release zip) |

## How `dist/HeatTray.exe` was built

To build it yourself you need only the C# compiler that is already part of Windows, run at user level. If you would rather not run a prebuilt exe, read the source and compile it:

```powershell
csc.exe /nologo /target:winexe /platform:x64 /out:dist\HeatTray.exe /win32icon:src\HeatTray.ico `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\HeatTray.cs
```

`csc.exe` is the one bundled with .NET Framework 4 (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). Or run `build.ps1`.

SHA-256 of the committed `dist/HeatTray.exe` (v2.3): `CDEC0E59AB22F96DB3B3CC0F437120F5EE7F68650486E06A884088DF91A8496B`

## License

MIT, see [LICENSE](LICENSE).
