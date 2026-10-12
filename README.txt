HEATTRAY

v1.3 (2026-10-12)
Author: Andrew Reardon, andrewreardon@gmail.com

Shows in the system tray, next to the clock, whether your CPU is really
running slower than it can, so you know when heat is costing you
performance and when you can let it ride.


HOW IS THIS DIFFERENT FROM HWINFO?

HWiNFO is the right tool for finding out WHAT your CPU is doing. HeatTray
answers one question, at a glance: is this laptop running slower than it
should, and does it matter right now? Three things set it apart:

  * It tells you what a slowdown feels like. When the icon goes orange or
    red, the hover text adds a tongue-in-cheek line such as "Like a
    Pentium 4 2.4GHz from 2002": a feel for how bad "slow" is that a
    clock reading never gives you. It is an order-of-magnitude joke with a
    scale behind it (see THE READY RECKONER below).
  * It measures against your own workload, not a spec sheet. Calibrate
    once, with the laptop cool and your real load running, and 100 means
    "as fast as this load normally runs on this machine" (see CALIBRATE
    below). Rated and boost clocks cannot tell you that.
  * It boils it all down to one number: your speed as a % of normal. 98 is
    no real slowdown, 75 is a quarter slower, and a hot CPU that is still
    at full speed stays green. It is a verdict, not a dashboard. Windows'
    own throttle counters can report "no throttling" while a chip is pinned
    at its power cap, and HWiNFO's Average Effective Clock counts idle time
    (accurate, but a different question), so a lightly loaded CPU can look
    half-throttled while its working cores run at full speed. HeatTray
    reads the busy-core speed instead.

It also needs nothing but Windows. HeatTray is one plain C# file compiled
into one small exe, with no drivers, no admin rights and no other tools
(see HOW IT WORKS below). To build it yourself you need only the C#
compiler that is already part of Windows, run at user level: build.ps1, a
short script around that compiler's single command line. That suits
tightly controlled servers and laptops: nothing to install or approve, and
nothing to elevate. Rather not run someone else's exe? Read the source and
compile it yourself.


GETTING STARTED

  1. Double-click HeatTray.exe (in the dist folder).
  2. Don't see it? Click the small ^ arrow near the clock to show hidden
     tray icons.
  3. Calibrate it once for your workload (see CALIBRATE below).
  4. To close it, right-click the icon and choose Exit.


KEEPING THE ICON VISIBLE

  1. Right-click the taskbar, then choose Taskbar settings.
  2. Click Other system tray icons.
  3. Find HeatTray in the list and switch it On.


STARTING AUTOMATICALLY AT LOGIN

  1. Right-click HeatTray.exe and choose Create shortcut.
  2. Press Win + R, type shell:startup, and press Enter.
  3. Drag the shortcut into that folder. Delete it to turn this off.


CALIBRATE TO YOUR DEMO LOAD (DO THIS ONCE)

HeatTray cannot know what "full speed" means for your workload until you
show it. It measures your workload once, while the computer is cool, and
compares everything against that. ("Demo load" is the app's name for
whatever work you care about: a demo, a build, a render.)

  1. Let the laptop cool (idle on a desk, normal ventilation, on AC power
     in the power mode you demo in).
  2. Start your full demo workload.
  3. Right-click the icon, choose "Calibrate to demo load (5 min)...", OK.
  4. Keep the workload running while the icon shows CAL and a countdown.
     A notification (also recorded in Details) reports the result.

Notes:
  * The reference is the MEDIAN speed over the last 2 minutes of the 5,
    the speed your workload settles at. A cool CPU boosts harder for the
    first minutes, and comparing against that would leave a healthy demo
    orange.
  * If it reached 85 C or more during calibration you get a warning that
    the reference may be low: let it cool and run it again.
  * If you forgot to start the workload it fails with a message instead of
    storing a bad reference.
  * Calibrate again after changing hardware, power mode, AC vs battery, or
    what the demo workload does. Until you calibrate the icon is grey
    (showing the speed in GHz) and, while the CPU is working, the tooltip
    says "Needs calibration". To forget the reference, delete baseline.ini
    next to the exe.
  * The reference is stored in baseline.ini next to the exe. It belongs to
    one computer: do not copy it to another.


WHAT THE ICON SHOWS

One big number, so it can be read at a glance:

     98      speed: % of your reference (see CALIBRATE above)

The number is your CPU's speed (while it is working) as a percentage of
the reference you calibrated from your own demo workload. 100 = as fast as
that workload normally runs, and above 100 = faster than it ran during
calibration. Lower means something (heat, a power limit, battery, or a
power mode) is holding it back. The COLOUR is about speed only, not
temperature:

  Green         At or near the reference speed, or better (85% and up).
                Also shown while the CPU is idle and at least that fast,
                so the icon does not change colour every time the load
                dips.
  Orange        Noticeably slowed (under 85%) while the CPU is working
                (Settings and the command line call this colour amber)
  Red           Heavily slowed (under 70%) while the CPU is working
  Grey          No verdict. Either you have not calibrated yet (there is
                nothing to take a percentage of, so the icon shows the
                speed in GHz), or the CPU is idle and its speed is under
                85% of your reference. An idle CPU slows itself down on
                purpose, so an idle reading is never orange or red.
  Cyan CAL      Calibration in progress (with a countdown underneath)
  Grey ?        CPU counters not available (see TROUBLESHOOTING)

The text colours follow the Windows taskbar theme: bright shades on a dark
taskbar, darker shades on a light one. If you change the Windows theme the
icon follows within a couple of seconds.

You can change what the icon shows: right-click the icon, choose
Settings..., and pick "Icon shows": Percent (the default), GHz (the current
speed of the working cores, e.g. 3.2) or Both (percent over GHz on two
lines, with smaller digits). The hover text always has both.

Hover over the icon for the details, for example:

  Speed 18% 0.6GHz v
  74C | load 55%
  Check cooling (peaked 96C)
  Like a Pentium 4 2.4GHz from 2002

That is, line by line: speed, GHz and a trend arrow (up, steady or down
over about the last 5 minutes of load); temperature and CPU load; a hint;
and, when the icon is orange or red, a tongue-in-cheek "ready reckoner"
line (see THE READY RECKONER below). The hints are:

  OK                 Normal speed. Even if it is hot, you are not being
                     slowed, so carry on.
  Check cooling      Slow AND hot (85 C or more): airflow, dust, surface,
                     or reduce the load. "(peaked 96C)" means it is no
                     longer that hot but was within the last 2 minutes:
                     a heat slowdown can outlast the heat.
  Check power mode   Slow, with no sign of heat (under 80 C, and not that
                     hot at any point in the last 2 minutes; it is also
                     what you get with no temperature sensor): check the
                     Windows power mode, AC power / charger, or a firmware
                     power cap.
  Heat or power cap  Slow and warm (80-85 C, now or within the last 2
                     minutes): could be either.
  Idle, or           No verdict is being made: idle (the icon stays
  Needs calibration  green, or goes grey if the speed is low) or not
                     calibrated yet (grey GHz). See Grey above.

Click the icon for a full breakdown (temperature and its 2-minute peak,
slowdown, trend, advice, and your reference).

  Speed shown   Roughly                What to do
  85 and up     Little or no slowdown  Ignore the temperature, let it ride
  70-85         Mild to moderate       Fine for bursts; if it stays here
                                       AND it is hot, improve airflow
  Under 70      Heavy                  Address cooling (or the power
                                       mode) now

A brief dip is normal. What matters is when it STAYS low.


THE READY RECKONER

When the icon is orange or red, the tooltip gets a "Like a ..." line (and
Details gets a "Feels like" row): a tongue-in-cheek comparison of how your
laptop is running right now with an old computer (your normal speed counts
as a current laptop), and the year that CPU debuted. It compares one core's
speed only: an order-of-magnitude feel for "how bad is this", not a
benchmark.

  Speed (% of your normal)   Feels like
  85 and up                  as intended (no line shown)
  62-85                      a Core i5-4690 from 2014
  46-62                      a Core i5-2500K from 2011
  32-46                      a Core 2 Duo E8400 from 2008
  21-32                      a Core 2 Duo E6600 from 2006
  14-21                      a Pentium 4 2.4GHz from 2002
  8.5-14                     a Pentium 4 1.8GHz from 2001
  5-8.5                      a Pentium III 733 from 1999
  1.9-5                      a Pentium II 400 from 1998
  0.7-1.9                    a Pentium 166 from 1996
  0.34-0.7                   a Pentium 60 from 1993
  0.1-0.34                   a 486 DX2-66 from 1992
  0.014-0.1                  a 386DX-16 from 1985
  under 0.014                an abacus from 2400 BC

The "Amber below" setting (the orange threshold) also decides when the
line appears. It is not shown while the CPU is idle or not calibrated.


SETTINGS

Right-click the icon and choose Settings... to change what the icon shows
(percent, GHz or both), how often it samples, the "hot" temperature, the
idle threshold and the speed thresholds ("Amber below" is the orange one).
Changes apply immediately and are remembered (settings.ini next to the
exe). If a number is out of range, the box stays open instead of closing:
the hint at the bottom turns red and says which setting and what range it
takes, and the cursor goes to that box. A left-click on the icon opens
Details.


COMMAND-LINE OPTIONS

  -i <sec>, --interval=<sec>  seconds between samples (default 2)
  --warn=<C>                  temperature that counts as hot, for the
                              hints and the calibration warning
                              (default 85)
  --gate=<pct>                CPU load below which it counts as idle
                              (default 30)
  --amber=<pct>               orange below this speed % (default 85)
  --red=<pct>                 red below this speed % (default 70)
  --show=<percent|ghz|both>   what the icon shows (default percent)
  --diag                      show which counters work + live samples
  -h, --help                  show help and exit
  -v, --version               show version info and exit

Started with no options, it uses the saved settings. With any valid
option given, the command line wins and the saved file is ignored.


TROUBLESHOOTING

Open a terminal in the dist folder and run:   HeatTray.exe --diag

That lists which Windows counters are available on this computer, the
taskbar theme it detected (light or dark), what the icon is set to show,
and prints five live readings. Send that output along if something looks
off.

  * Grey "?"        The CPU counters could not be opened. HeatTray needs an
                    English-language Windows for the counter names.
  * "no temp"       This computer does not expose a temperature sensor to
                    Windows. Speed still works, but with no temperature a
                    slow reading always says Check power mode: read it as
                    "slow, cause unknown".
  * Temperature     Windows' reading is often a few degrees different from
                    the CPU's own sensor. Adjust the "hot" temperature in
                    Settings to suit.
  * Grey, "Needs calibration"   You have not calibrated yet (see above).


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

A heat slowdown can outlast the heat. After a firmware thermal clamp the
CPU can stay pinned at its lowest speed for 30-40 seconds while the
temperature falls back below 80 C (seen on a laptop resting on a pillow:
96 C, then about 0.5 GHz for 36+ seconds until it had cooled to about
73 C). So the hints go by the hottest temperature of the last 2 minutes,
not just the current one, and say so ("Check cooling (peaked 96C)"). A
clamp that outlasts those 2 minutes can still read as "Check power mode";
if it was hot a few minutes ago, treat it as cooling.

Until you calibrate there is no percentage to show, so the icon shows the
speed in GHz (grey), and the percentage in the hover text is then a
percentage of the CPU's RATED speed, a different scale from the coloured
readings (a percentage of your reference).

The light/dark choice follows the Windows system theme setting. With an
unusual taskbar colour (a very light accent colour, high contrast) the icon
text can be harder to read; the outline helps.

A sleep or resume during a calibration can cut it short; just run it
again.

If Windows blocks the exe because it was copied from a network share or USB
drive, right-click it, choose Properties, and tick Unblock. Or rebuild it on
the machine with build.ps1 (uses the compiler that ships with Windows).


HOW IT WORKS

It reads three standard Windows performance counters:

  * Processor Information\% Processor Performance: how fast the cores run
    while they are working, relative to their rated speed (it can exceed
    100% when boosting). GHz is this times the CPU's nominal clock.
  * Processor Information\% Processor Time: how busy the CPU is
    (deliberately not % Processor Utility, which is itself scaled by clock
    speed and would hide a hard throttle by making a busy CPU look idle).
  * Thermal Zone Information\Temperature: the hottest ACPI thermal zone.

Readings are smoothed over the last 5 samples. Above the idle threshold
(30% load) the speed is compared with the reference and coloured green,
orange or red. Below it the same percentage is shown, green (or grey if it
is low) and never orange or red.


WHAT'S NEW IN v1.3

  * Settings checks what you type. A number that is out of range keeps the
    box open instead of closing it: the hint at the bottom turns red, says
    which setting and what range it takes, and the cursor goes to that
    box. Before, the box closed, threw away everything you had typed and
    showed one message listing every range.
  * One window at a time. Clicking the icon again (or a menu item) while
    Details, Settings or About is open brings the open window to the front
    instead of stacking another copy on top.
  * The Details window is as tall as its text, so there is nothing to
    scroll on a normal screen. On a screen too small to hold it all, it
    keeps its scrollbar.
  * A lighter red on dark taskbars, easier to see (about 5:1 contrast
    against a dark taskbar, up from about 4:1). The red on a light taskbar
    is unchanged.
  * Your calibration and saved settings carry over from earlier versions.


MOVING TO ANOTHER COMPUTER

Copy the whole HeatTray folder anywhere and run dist\HeatTray.exe. There is
nothing else to install. Do not copy baseline.ini to a different machine;
calibrate on each one.

Right-click the tray icon and choose About HeatTray... for version and
contact details.


LICENSE

MIT. See the LICENSE file.
