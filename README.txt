HEAT TRAY

v1.2 -- 2026-10-09
Author: Andrew Reardon, andrewreardon@gmail.com

Shows, in the system tray next to the clock, whether your CPU is really
running slower than it can -- so you know when heat is costing you
performance and when you can let it ride.


WHAT THE ICON SHOWS

The icon is two lines of text:

     87      speed: % of your reference (see CALIBRATE below)
     2.9     current speed of the working cores, in GHz

The top number is your CPU's speed (while it is working) as a percentage
of the reference you calibrated from your own demo workload. 100 = as fast
as that workload normally runs; lower means something -- heat, a power
limit, battery, or a power mode -- is holding it back. The COLOUR is about
speed only, not temperature:

  Green         At or near the reference speed (85% and up)
  Orange        Noticeably slowed (under 85%)
  Red           Heavily slowed (under 70%)
  Grey          Not judged: the CPU is idle (load under 30%), or you have
                not calibrated yet. The top number is then the raw speed
                as a percentage of the CPU's rated speed.
  Cyan CAL      Calibration in progress (with a countdown underneath)
  Grey ?        CPU counters not available (see TROUBLESHOOTING)

Hover over the icon for the details, for example:

  Speed 87% 2.9GHz v | 91C | load 62% | Check cooling

That is: speed, GHz, trend arrow (up, steady or down over about the last
5 minutes of load), temperature, CPU load, and a hint:

  OK                 Normal speed. Even if hot, you are not being slowed.
  Check cooling      Slow AND hot (85 C+): airflow, dust, surface, or
                     reduce the load.
  Check power mode   Slow but NOT hot (under 80 C): not heat. Check the
                     Windows power mode, AC power / charger, or a
                     firmware power cap.
  Heat or power cap  Slow and warm (80-85 C): could be either.
  Idle / Needs calibration   Not judged (see above).

Click the icon for a full breakdown (slowdown, trend, advice, and your
reference).

  90-100   Little or no slowdown   -> ignore the temperature, let it ride
  70-90    Mild to moderate        -> fine for bursts; improve airflow if
                                      it stays here AND it is hot
  under 70 Heavy                   -> sort out cooling or power mode now

A brief dip is normal. What matters is when it STAYS low.


CALIBRATE TO YOUR DEMO LOAD (DO THIS ONCE)

HeatTray cannot know what "full speed" means for your workload until you
show it. It measures your workload once, while the computer is cool, and
judges everything against that.

  1. Let the laptop cool (idle on a desk, normal ventilation, on AC power
     in the power mode you demo in).
  2. Start your full demo workload.
  3. Right-click the icon, choose "Calibrate to demo load (5 min)...", OK.
  4. Keep the workload running while the icon shows CAL and a countdown.
     A notification (also recorded in Details) reports the result.

Notes:
  * The reference is the MEDIAN speed over the last 2 minutes of the 5 --
    the speed your workload settles at. A cool CPU boosts harder for the
    first minutes; judging against that would leave a healthy demo orange.
  * If it reached 85 C or more during calibration you get a warning that
    the reference may be low: let it cool and run it again.
  * If you forgot to start the workload it fails with a message instead of
    storing a bad reference.
  * Calibrate again after changing hardware, power mode, AC vs battery, or
    what the demo workload does. Until you calibrate the icon is grey and
    the tooltip says "Needs calibration". To forget the reference, delete
    baseline.ini next to the exe.
  * The reference is stored in baseline.ini next to the exe. It belongs to
    one computer: do not copy it to another.


GETTING STARTED

  1. Double-click HeatTray.exe (in the dist folder).
  2. Don't see it? Click the small ^ arrow near the clock to show hidden
     tray icons.
  3. Calibrate (above).
  4. To close it, right-click the icon and choose Exit.


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
samples, the "hot" temperature, the idle threshold and the speed
thresholds. Changes apply immediately and are remembered (settings.ini
next to the exe).


COMMAND-LINE OPTIONS

  -i <sec>, --interval=<sec>  seconds between samples (default 2)
  --warn=<C>                  temperature that counts as hot, for the hints and
                              the calibration warning (default 85)
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
                    Windows. Speed still works; the hints just cannot use
                    temperature.
  * Temperature     Windows' reading is often a few degrees different from
                    the CPU's own sensor. Adjust the "hot" temperature in
                    Settings to suit.
  * Grey, "Needs calibration"   You have not calibrated yet (above).


LIMITS

HeatTray measures the OUTCOME (slower clocks) and a coarse temperature. It
cannot see inside the CPU's own power and thermal limits, so it can tell
you THAT the CPU is running slow and how hot the machine is, but not
for certain whether heat, a firmware power cap, running on battery, or a
Windows power mode such as "Best power efficiency" is the cause. The hint
is a rule of thumb. Check the power mode before blaming the cooling.

The reference is only as good as the calibration: a different power mode,
AC vs battery, or a workload that changes shape will make the number read
wrongly until you calibrate again. Calibrating while it heats up and
throttles gives a low reference (you get a warning if it reached 85 C).

Grey readings (idle or not calibrated) show the raw percentage of the
CPU's rated speed, which is a different scale from the judged number.

A sleep or resume during a calibration can cut it short; just run it
again.

If Windows blocks the exe because it was copied from a network share or USB
drive, right-click it, choose Properties, and tick Unblock. Or rebuild it on
the machine with build.ps1 (uses the compiler that ships with Windows).


MOVING TO ANOTHER COMPUTER

Copy the whole HeatTray folder anywhere and run dist\HeatTray.exe -- nothing
else to install. Do not copy baseline.ini to a different machine; calibrate
on each one.

Right-click the tray icon and choose About HeatTray... for version and
contact details.
