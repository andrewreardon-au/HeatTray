HEAT TRAY

v1.1 -- 2026-10-08
Author: Andrew Reardon, andrewreardon@gmail.com

Shows, as a number in the system tray next to the clock, how much slower
your CPU is running than it normally can -- so you know when heat is really
costing you performance and when you can let it ride.


WHAT THE NUMBER MEANS

The number is your CPU's speed (while it is working) as a percentage of its
normal speed at the same workload, learned while the computer was not hot.
100 = as fast as it normally runs; lower means something -- heat, a power
limit, battery, or a power mode -- is holding it back.

  Green         Full speed or close, and not hot
  Orange        Noticeably slowed, or getting hot (85 C and up)
  Red           Heavily slowed, or very hot (92 C and up)
  Light-grey    Idle -- shows the temperature in C, speed is not judged
  number        (the tooltip says "Idle"). The same number appears, in
                orange or red if hot, while a load level has no cool
                baseline yet (the tooltip says "Learning")
  Light-grey ?  CPU counters not available (see TROUBLESHOOTING)

Hover over the icon for the exact numbers. Click it for a full breakdown,
including an estimated slowdown (100 minus the number).

  90-100   Little or no slowdown   -> let it ride
  70-90    Mild to moderate        -> fine for bursts; improve airflow if
                                      it stays here
  under 70 Heavy                   -> sort out cooling now (clear vents,
                                      use a stand, reduce the load)

A brief dip is normal. What matters is when it STAYS low while the
temperature is at the top of its range.


GETTING STARTED

  1. Double-click HeatTray.exe (in the dist folder).
  2. Don't see it? Click the small ^ arrow near the clock to show hidden
     tray icons.
  3. To close it, right-click the icon and choose Exit.


GETTING AN HONEST BASELINE

HeatTray learns "normal speed" only from moments when the temperature is
below the orange threshold (85 C by default), so it cannot learn "hot" as
normal -- but a load level that has only ever been seen hot has no
baseline and just shows the temperature while it learns. For a meaningful
number:

  1. Start with the laptop cool and well ventilated.
  2. Right-click the icon and choose Reset baseline...
  3. Run the HEAVY workload you want to be warned about (for example
     your full demo load) for the first few minutes, so the heavy-load
     bands learn what full speed is while the computer is still cool.

Baselining only on light everyday work teaches only the light bands; the
heavy bands would then learn "hot" the first time you run your demo.

It keeps separate baselines for five load bands (light to heavy), because
a heavy load is naturally slower than a light one. For the first minute at a
given load the tooltip says "Learning baseline".


KEEPING IT ALWAYS VISIBLE

  1. Right-click the taskbar, then choose Taskbar settings.
  2. Click Other system tray icons.
  3. Find HeatTray in the list and toggle it On.


STARTING AUTOMATICALLY ON LOGIN

  1. Right-click HeatTray.exe and choose Create shortcut.
  2. Press Win + R, type shell:startup, and hit Enter.
  3. Drag the shortcut into that folder. Delete it to turn this off.


SETTINGS

Right-click the icon and choose Settings... to change how often it
samples, the temperatures for orange and red, the idle threshold, and the
speed thresholds. Changes apply immediately and are remembered
(settings.ini next to the exe).


COMMAND-LINE OPTIONS

  -i <sec>, --interval=<sec>  seconds between samples (default 2)
  --warn=<C>                  orange at this temperature (default 85)
  --hot=<C>                   red at this temperature (default 92)
  --gate=<pct>                CPU load below which it counts as idle (30)
  --amber=<pct>               orange below this speed % (default 85)
  --red=<pct>                 red below this speed % (default 70)
  --diag                      show which counters work + live samples
  -h, --help                  show help and exit
  -v, --version               show version info and exit

Started with no options, it uses the saved settings. With any option
given, the command line wins and the saved file is ignored.


TROUBLESHOOTING

Open a terminal in the dist folder and run:   HeatTray.exe --diag

That lists which Windows counters are available on this computer and
prints five live readings. Send that output along if something looks off.

  * Grey "?"        The CPU counters could not be opened. HeatTray needs an
                    English-language Windows for the counter names.
  * "no temp"       This computer does not expose a temperature sensor to
                    Windows. Speed still works; colour then depends on
                    speed alone.
  * Temperature     Windows' reading is often a few degrees different from
                    the CPU's own sensor. Adjust the orange/red
                    temperatures in Settings to suit.


LIMITS

HeatTray measures the OUTCOME (slower clocks) and a coarse temperature. It
cannot see inside the CPU's own power and thermal limits, so it can tell
you THAT the CPU is running slow and how hot the machine is, but not
whether heat, a firmware power cap, running on battery, or a Windows
power mode such as "Best power efficiency" is the cause. Check those
before blaming the cooling.

Different workloads at the same total load can run at different speeds (a
few busy cores boost higher than many), so the number can wobble by about
10% even when cool. Read the trend together with the temperature. If it
looks permanently off, use Reset baseline...

If Windows blocks the exe because it was copied from a network share or USB
drive, right-click it, choose Properties, and tick Unblock. Or rebuild it on
the machine with build.ps1 (uses the compiler that ships with Windows).


MOVING TO ANOTHER COMPUTER

Copy the whole HeatTray folder anywhere and run dist\HeatTray.exe -- nothing
else to install. The learned baseline is per computer (baseline.ini); do
not copy it to a different machine, and reset the baseline on each one.

Right-click the tray icon and choose About HeatTray... for version and
contact details.
