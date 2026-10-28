using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// Embedded VERSIONINFO (Explorer's Properties > Details). Kept in this file
// so the single-source-file build stays true; versions derive from the same
// Version const that -v and the About dialog show.
[assembly: AssemblyTitle("HeatTray")]
[assembly: AssemblyProduct("HeatTray")]
[assembly: AssemblyDescription("CPU heat slowdown indicator in the system tray")]
[assembly: AssemblyCopyright("(c) 2026 Andrew Reardon")]
[assembly: AssemblyVersion(HeatTray.Version + ".0.0")]
[assembly: AssemblyFileVersion(HeatTray.Version + ".0.0")]

internal static class HeatTray
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;
    private const ushort KEY_EVENT = 1;
    private const ushort VK_RETURN = 0x0D;
    private const uint FILE_TYPE_CHAR = 2;

    internal const string Version = "2.1";
    private const string VersionDate = "2026-10-28";
    private const string Author = "Andrew Reardon";
    private const string AuthorEmail = "andrewreardon@gmail.com";

    // ---- Tunables (settings.ini / command line) ----
    private static int IntervalMs = 5000;
    private static double WarnC = 85;      // temp >= this counts as hot: changes the hint, and an idle CPU this hot shows HOT
    private static double GateUtil = 30;   // CPU load % below which we call it "idle" and don't judge speed
    private static double AmberPct = 70;   // loaded speed (% of the rated clock) below this: amber
    private static double RedPct = 40;     // ... below this: red
    // Test seam (hidden, not in -h): scales the perf counter so an idle machine can be staged in a slow state.
    private static double PerfScale = 1.0;
    private const int ShowPercent = 0, ShowGhz = 1, ShowBoth = 2;
    private static int IconShows = ShowPercent;   // what the icon says: speed %, GHz, or both (two lines)

    // Command-line options always win: if any tunable was passed, settings.ini
    // is not loaded at all (defaults + flags only), and flags are never
    // persisted - only Settings-dialog edits write settings.ini.
    private static bool _anyCli;

    private const int SmoothN = 3;         // samples averaged before judging (15 s at the default 5 s interval)

    private static string ConfigPath;

    private static NotifyIcon _notifyIcon;
    private static Icon _currentIcon;
    private static Timer _timer;

    // ---- Counters (all optional; the app degrades gracefully) ----
    private static PerformanceCounter _cPerf;
    private static PerformanceCounter _cUtil;
    private static readonly List<PerformanceCounter> _cTemps = new List<PerformanceCounter>();
    private static string _utilSource = "none";
    private static double _ratedMhz;

    // ---- Live state ----
    private static readonly Queue<double> _perfRing = new Queue<double>();
    private static readonly Queue<double> _utilRing = new Queue<double>();
    private static double _lastPerf, _lastUtil, _lastTempC = double.NaN;
    private static double _smoothPerf, _smoothUtil;
    private static double _speedPct = -1;          // busy-core speed (% of rated) while loaded; -1 = idle / no data
    private static bool _ready;                   // the smoothing window is full (otherwise the app is still starting up)
    private static bool _loaded;                  // CPU load is above the idle gate
    // Smoothed busy-core speed (% of rated) for recent loaded samples, to show a trend.
    private static readonly Queue<double> _trendRing = new Queue<double>();
    private const int TrendMax = 60;      // ~5 min at the default 5 s interval
    private const int TrendEnds = 6;      // compare the newest vs oldest ~30 s of the window
    private const int TrendMin = 24;      // need ~2 min of loaded history before showing a trend

    // Recent temperature readings (UTC time, deg C), so the hint can look back and not
    // only at now: a firmware thermal clamp can hold the CPU slow for 30-40 s after
    // the heat has gone (see HeatTempC).
    private const int HeatMemorySec = 120;
    private static readonly Queue<KeyValuePair<DateTime, double>> _tempRing = new Queue<KeyValuePair<DateTime, double>>();

    [STAThread]
    private static void Main(string[] args)
    {
        bool diag;
        if (!ParseArgs(args, out diag))
        {
            return;
        }

        ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        if (!_anyCli)
        {
            LoadSettings();
        }

        InitCounters();

        if (diag)
        {
            PrintOrShow(RunDiag());
            return;
        }

        bool createdNew;
        var mutex = new System.Threading.Mutex(true, "Global\\HeatTray_SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "HeatTray is already running. Look for its icon in the tray (or under the ^ overflow arrow near the clock).",
                // MessageBoxIcon.None: an icon would play the system notification sound.
                "HeatTray", MessageBoxButtons.OK, MessageBoxIcon.None);
            return;
        }

        Application.EnableVisualStyles();

        var menu = new ContextMenuStrip();
        var detailsItem = menu.Items.Add("Details...");
        detailsItem.Click += (s, e) => ShowDetailsDialog();
        var settingsItem = menu.Items.Add("Settings...");
        settingsItem.Click += (s, e) => ShowSettingsDialog();
        var aboutItem = menu.Items.Add("About HeatTray...");
        aboutItem.Click += (s, e) => ShowAboutDialog();
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (s, e) => Application.Exit();

        _notifyIcon = new NotifyIcon();
        // Placeholder until the first sample paints a number.
        _notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _notifyIcon.Text = "HeatTray: ...";
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Visible = true;
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowDetailsDialog();
            }
        };

        _timer = new Timer();
        _timer.Interval = IntervalMs;
        _timer.Tick += (s, e) => Sample();
        _timer.Start();

        Application.ApplicationExit += (s, e) =>
        {
            _timer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_currentIcon != null)
            {
                var h = _currentIcon.Handle;
                _currentIcon.Dispose();
                DestroyIcon(h);
            }
            GC.KeepAlive(mutex);
        };

        Application.Run();
    }

    // ------------------------------------------------------------------
    // Counters
    // ------------------------------------------------------------------

    private static void InitCounters()
    {
        try
        {
            _cPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cPerf.NextValue();
        }
        catch { _cPerf = null; }

        try
        {
            // Deliberately NOT "% Processor Utility": that is scaled by clock
            // speed (~ Time x Performance), so a hard throttle would push the
            // load reading under the idle gate and hide itself.
            _cUtil = new PerformanceCounter("Processor Information", "% Processor Time", "_Total");
            _cUtil.NextValue();
            _utilSource = "% Processor Time";
        }
        catch
        {
            try
            {
                _cUtil = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cUtil.NextValue();
                _utilSource = "Processor % Processor Time (fallback)";
            }
            catch { _cUtil = null; }
        }

        try
        {
            var cat = new PerformanceCounterCategory("Thermal Zone Information");
            foreach (string inst in cat.GetInstanceNames())
            {
                try
                {
                    var c = new PerformanceCounter("Thermal Zone Information", "Temperature", inst);
                    c.NextValue();
                    _cTemps.Add(c);
                }
                catch { }
            }
        }
        catch { }

        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                object v = key == null ? null : key.GetValue("~MHz");
                if (v != null) _ratedMhz = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
        }
        catch { }
    }

    // Hottest thermal zone in deg C, or NaN. The counter reports Kelvin.
    private static double ReadTempC()
    {
        double best = double.NaN;
        foreach (var c in _cTemps)
        {
            try
            {
                double k = c.NextValue();
                if (k > 200 && k < 500)
                {
                    double t = k - 273.15;
                    if (double.IsNaN(best) || t > best) best = t;
                }
            }
            catch { }
        }
        return best;
    }

    // ------------------------------------------------------------------
    // Sampling and judgement
    // ------------------------------------------------------------------

    private static void Sample()
    {
        if (_cPerf == null || _cUtil == null)
        {
            SetTooltip("Cannot read speed\nClick for how to fix");
            ApplyIcon("?", null, Tone.Neutral, Tone.Neutral);
            return;
        }

        try
        {
            _lastPerf = _cPerf.NextValue() * PerfScale;
            _lastUtil = _cUtil.NextValue();
        }
        catch
        {
            return; // transient counter hiccup (e.g. resume from sleep); try again next tick
        }
        if (_lastPerf <= 0 || _lastPerf > 400)
        {
            return; // implausible reading (counter glitch); skip this tick
        }
        _lastTempC = ReadTempC();
        NoteTemp(_lastTempC);

        _perfRing.Enqueue(_lastPerf);
        _utilRing.Enqueue(_lastUtil);
        while (_perfRing.Count > SmoothN) { _perfRing.Dequeue(); _utilRing.Dequeue(); }

        _smoothPerf = Average(_perfRing);
        _smoothUtil = Average(_utilRing);

        bool ringFull = _perfRing.Count >= SmoothN;
        bool loaded = ringFull && _smoothUtil >= GateUtil && _smoothPerf > 0;

        _ready = ringFull;
        _loaded = loaded;
        // Busy-core speed as a % of the rated clock (the counter is already that), judged only under
        // load: an idle CPU chooses its own speed, so there is nothing to compare. Not capped: above 100 is boost.
        _speedPct = loaded ? _smoothPerf : -1;

        // Only fully-loaded smoothing windows feed the trend: the ramp up from idle
        // averages idle readings in and showed a fake "rising" (+479%).
        bool steady = loaded;
        foreach (double u in _utilRing) { if (u < GateUtil) steady = false; }
        if (steady)
        {
            _trendRing.Enqueue(_smoothPerf);
            while (_trendRing.Count > TrendMax) _trendRing.Dequeue();
        }
        else if (!loaded)
        {
            _trendRing.Clear();   // idle gaps would pollute the trend
        }

        Render();
    }

    // Busy-core speed in GHz (NaN when the rated clock is unknown).
    private static double Ghz(double perfPct)
    {
        return _ratedMhz > 0 ? perfPct / 100.0 * _ratedMhz / 1000.0 : double.NaN;
    }

    // Change in busy-core speed over the loaded history window, in % of the
    // older value; NaN until there is enough history. Load-shape changes move
    // it too, so it is a hint, not a verdict.
    private static double TrendPct()
    {
        if (_trendRing.Count < TrendMin) return double.NaN;
        double[] a = _trendRing.ToArray();
        double oldAvg = 0, newAvg = 0;
        for (int i = 0; i < TrendEnds; i++) { oldAvg += a[i]; newAvg += a[a.Length - 1 - i]; }
        oldAvg /= TrendEnds; newAvg /= TrendEnds;
        return oldAvg > 0 ? (newAvg - oldAvg) / oldAvg * 100.0 : double.NaN;
    }

    // 1 rising, 0 steady, -1 falling, 0 when unknown.
    private static int TrendDir()
    {
        double t = TrendPct();
        if (double.IsNaN(t)) return 0;
        return t >= 3 ? 1 : (t <= -3 ? -1 : 0);
    }

    // Keeps the last HeatMemorySec seconds of temperature readings.
    private static void NoteTemp(double tempC)
    {
        DateTime now = DateTime.UtcNow;
        if (!double.IsNaN(tempC)) _tempRing.Enqueue(new KeyValuePair<DateTime, double>(now, tempC));
        // Abs: also drops readings stamped in the future (the clock was stepped back).
        while (_tempRing.Count > 0 && Math.Abs((now - _tempRing.Peek().Key).TotalSeconds) > HeatMemorySec) _tempRing.Dequeue();
    }

    // The temperature the hints go by: the hottest reading of the last HeatMemorySec
    // seconds, or now if that is hotter (NaN with no reading at all). Going by the
    // instantaneous value blamed the power mode for a heat slowdown that outlasted the
    // heat: after a firmware thermal clamp the CPU stayed pinned at its lowest speed
    // for 30-40 s while the temperature fell from 96 C to 73 C.
    private static double HeatTempC()
    {
        double peak = _lastTempC;
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-HeatMemorySec);
        foreach (KeyValuePair<DateTime, double> kv in _tempRing)
        {
            if (kv.Key >= cutoff && (double.IsNaN(peak) || kv.Value > peak)) peak = kv.Value;
        }
        return peak;
    }

    // Cause hint for a slow reading when the temperature is t (NaN = unknown).
    // Slow + hot -> cooling; slow + cool -> power mode / AC / firmware cap; no sensor -> unknown.
    private static string HintFor(double t)
    {
        if (double.IsNaN(t)) return "Cause unknown";
        if (t < WarnC - 5) return "Check power mode";
        return t >= WarnC ? "Check cooling" : "Heat or power cap";
    }

    // Short cause hint; null when the speed isn't being judged (idle / starting).
    private static string AdviceShort()
    {
        if (_speedPct < 0) return null;
        if (_speedPct >= AmberPct) return "OK";
        return HintFor(HeatTempC());
    }

    // " (peaked 96C)" when a slow reading is being put down to heat that has already
    // eased, i.e. the hint differs from what the current temperature alone would give.
    private static string PeakNote()
    {
        double peak = HeatTempC();
        if (_speedPct < 0 || _speedPct >= AmberPct || double.IsNaN(peak) || HintFor(peak) == HintFor(_lastTempC)) return "";
        return string.Format(" (peaked {0:0}C)", peak);
    }

    private static string AdviceLong()
    {
        bool eased = PeakNote().Length > 0;   // the hint rests on heat that has already eased
        switch (AdviceShort())
        {
            case "OK":
                return (!double.IsNaN(_lastTempC) && _lastTempC >= WarnC)
                    ? "Hot, but still running at a healthy speed, so no action is needed."
                    : (_speedPct > 100 ? "Boosting above its rated speed. Carry on." : "Running at a healthy speed. Carry on.");
            case "Check cooling":
                return eased
                    ? string.Format("Slow, and it was hot within the last 2 minutes (peak {0:0}C): a heat slowdown can outlast the heat. Act on cooling (airflow, dust, surface) or reduce the workload.", HeatTempC())
                    : "Slow AND hot: act on cooling (airflow, dust, surface) or reduce the workload.";
            case "Cause unknown":
                return "Slow, and this machine reports no temperature, so the cause is unknown. Check the power mode, AC power / charger wattage or a firmware power cap, and make sure the vents are clear.";
            case "Check power mode":
                return "Slow but not hot, so this isn't heat. Check the power mode, AC power / charger wattage, or a firmware power cap.";
            case "Heat or power cap":
                return eased
                    ? string.Format("Slow, and it was warm within the last 2 minutes (peak {0:0}C): could be heat or a power limit. Watch whether it gets hotter; check power mode too.", HeatTempC())
                    : "Slow and warm: could be heat or a power limit. Watch whether it gets hotter; check power mode too.";
        }
        return null;
    }

    // Ready reckoner (all Intel, at the user's request, except the abacus): roughly which vintage of CPU the current speed feels like, if the
    // rated speed (100%) is taken as a current laptop. Rough single-thread comparison, order of
    // magnitude only: for fun, not a benchmark.
    // Centres are % of rated; the nearest one (on a log scale) wins.
    private static readonly double[] ReckonPct = { 70, 55, 38, 27, 17, 11, 6.5, 3.5, 1.0, 0.45, 0.25, 0.04, 0.005 };
    private static readonly string[] ReckonName = {
        "a Core i5-4690", "a Core i5-2500K", "a Core 2 Duo E8400", "a Core 2 Duo E6600",
        "a Pentium 4 2.4GHz", "a Pentium 4 1.8GHz", "a Pentium III 733", "a Pentium II 400",
        "a Pentium 166", "a Pentium 60", "a 486 DX2-66", "a 386DX-16", "an abacus" };
    // The year each of those CPUs debuted (same order).
    private static readonly string[] ReckonYear = {
        "2014", "2011", "2008", "2006", "2002", "2001", "1999", "1998", "1996", "1993", "1992", "1985", "2400 BC" };

    // Null at or above the amber threshold (nothing to joke about) or when not under load.
    private static string Reckoner(double pct)
    {
        if (pct < 0 || pct >= AmberPct) return null;
        double p = Math.Max(pct, 0.01);
        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < ReckonPct.Length; i++)
        {
            double dist = Math.Abs(Math.Log(p / ReckonPct[i]));
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        // No-break spaces keep "from <year>" together if the shell has to wrap the line.
        string nb = ((char)0xA0).ToString();
        return ReckonName[best] + " from" + nb + ReckonYear[best].Replace(" ", nb);
    }

    private static double Average(IEnumerable<double> xs)
    {
        double sum = 0; int n = 0;
        foreach (double x in xs) { sum += x; n++; }
        return n == 0 ? 0 : sum / n;
    }

    private static void Render()
    {
        string tempText = double.IsNaN(_lastTempC) ? "no temp" : string.Format("{0:0}C", _lastTempC);
        string tempLoad = tempText + " | " + string.Format("load {0:0}%", _smoothUtil);

        if (!_ready)
        {
            // The smoothing window is not full yet: no verdict, just the live reading in grey.
            SetTooltip("Starting up\nMeasuring CPU speed\nReady in seconds");
            ApplyIcon(((int)Math.Round(Math.Min(_lastPerf, 999))).ToString(CultureInfo.InvariantCulture), null, Tone.Neutral, Tone.Neutral);
            return;
        }

        // Speed is judged only under load: busy-core speed as a % of the rated clock, coloured
        // green / orange / red. Idle, the cores choose their own speed (it can be well under or
        // over the rated one), so the same figure is shown but there is nothing to judge: green,
        // unless the CPU is hot even though it is idle.
        int shown = (int)Math.Round(Math.Min(_smoothPerf, 999));
        double g = Ghz(_smoothPerf);
        string ghzText = double.IsNaN(g) ? "" : string.Format(" {0:0.0}GHz of {1:0.0}GHz", g, _ratedMhz / 1000.0);
        string arrow = "";
        if (_loaded && !double.IsNaN(TrendPct()))
        {
            int td = TrendDir();
            arrow = td > 0 ? " \u25B2" : (td < 0 ? " \u25BC" : " \u25BA");
        }
        bool hotIdle = !_loaded && !double.IsNaN(_lastTempC) && _lastTempC >= WarnC;

        // Short lines, not one long one: the taskbar wraps long tray tooltips. Line 1 is always the speed.
        string tip = string.Format("Speed {0}%{1}{2}\n", shown, ghzText, arrow);
        if (!_loaded)
        {
            tip += hotIdle ? "Hot at light load\nClear vents, lift laptop\n" + tempLoad
                           : "Light load: core speed is variable\n" + tempLoad;
        }
        else if (_speedPct >= AmberPct)
        {
            tip += (_speedPct > 100 ? "Boosting: faster than rated" : "Working hard, all fine") + "\n" + tempLoad;
        }
        else
        {
            string verdict, action;
            SlowLines(out verdict, out action);
            tip += verdict + "\n" + action;
            string like = Reckoner(_speedPct);
            if (like != null) tip += "\nLike " + like;
        }
        SetTooltip(tip);

        // Colour is about speed only (is heat actually slowing me down?); the temperature
        // is in the tooltip. Both lines share the colour so the icon reads as one signal.
        Tone tone = IconTone(_ready, _loaded, hotIdle, _speedPct);
        string top, bottom;
        if (hotIdle)
        {
            top = "HOT"; bottom = null;   // a word, so amber cannot be read as a slow speed
        }
        else
        {
            IconLines(IconShows, shown.ToString(CultureInfo.InvariantCulture),
                double.IsNaN(g) ? null : g.ToString("0.0", CultureInfo.InvariantCulture), out top, out bottom);
        }
        ApplyIcon(top, bottom, tone, tone);
    }

    // The verdict (tooltip line 2) and the action (line 3) for a loaded reading below the amber line.
    private static void SlowLines(out string verdict, out string action)
    {
        bool red = _speedPct < RedPct;
        double now = _lastTempC, peak = HeatTempC();
        string hint = HintFor(peak);
        string slower = red ? "Far slower" : "Slower";
        if (hint == "Cause unknown")
        {
            verdict = slower + ", no temp sensor";
            action = "Try power mode, vents";
            return;
        }
        bool hot = hint == "Check cooling";
        action = hint == "Check power mode" ? "Plug in, power mode" : (hot ? "Clear vents, lift laptop" : "Cooling or power mode?");
        if (PeakNote().Length > 0)
        {
            // The heat has already eased but the slowdown outlasts it.
            string what = hot ? (red ? "Too hot" : "Hot") : slower;
            verdict = double.IsNaN(now) ? string.Format("{0} (peak {1:0}C)", what, peak)
                                        : string.Format("{0} (now {1:0}C, peak {2:0}C)", what, now, peak);
            return;
        }
        string temp = string.Format(" ({0:0}C)", peak);
        verdict = hot ? (red ? "Too hot, very slow" : "Slower, and hot") + temp
                      : (hint == "Check power mode" ? slower + ", not hot" : slower + ", warm") + temp;
    }

    // Colour of the icon. Starting (no full window yet): neutral. Idle: green, or amber when the CPU
    // is hot while idle. Under load: the speed verdict (green / orange / red).
    private static Tone IconTone(bool ready, bool loaded, bool hotIdle, double speedPct)
    {
        if (!ready) return Tone.Neutral;
        if (!loaded) return hotIdle ? Tone.Warn : Tone.Good;
        return speedPct < RedPct ? Tone.Bad : (speedPct < AmberPct ? Tone.Warn : Tone.Good);
    }

    // The icon's one or two lines for the chosen display; bottom is null for a single big
    // number. With no rated clock there is no GHz, so it falls back to the percent.
    private static void IconLines(int show, string pctText, string ghzText, out string top, out string bottom)
    {
        bottom = null;
        if (ghzText != null && show == ShowGhz) { top = ghzText; return; }
        top = pctText;
        if (ghzText != null && show == ShowBoth) bottom = ghzText;
    }

    private static string ShowName(int show)
    {
        return show == ShowGhz ? "ghz" : (show == ShowBoth ? "both" : "percent");
    }

    // -1 when the text is not one of the choices.
    private static int ParseShow(string s)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "percent": case "pct": return ShowPercent;
            case "ghz": return ShowGhz;
            case "both": return ShowBoth;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // CLI
    // ------------------------------------------------------------------

    private static bool MatchOpt(string[] args, ref int i, string shortName, string longName, out string value)
    {
        value = null;
        string a = args[i];
        if (longName != null && a.StartsWith("--" + longName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = a.Substring(a.IndexOf('=') + 1).Trim();
            return true;
        }
        if (shortName != null && a.StartsWith("-" + shortName + "=", StringComparison.Ordinal))
        {
            value = a.Substring(a.IndexOf('=') + 1).Trim();
            return true;
        }
        if (shortName != null && a == "-" + shortName && i + 1 < args.Length)
        {
            value = args[++i].Trim();
            return true;
        }
        if (longName != null && string.Equals(a, "--" + longName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            value = args[++i].Trim();
            return true;
        }
        return false;
    }

    private static bool TryNum(string s, double min, double max, out double v)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;
    }

    // What is wrong with the entries in the Settings dialog, in plain words, or null if all are fine.
    // field says which box the cursor belongs in: 0 interval, 1 hot temperature, 2 idle gate, 3 amber,
    // 4 red. The first problem, top to bottom, is the one reported.
    private static string SettingsProblem(string interval, string warn, string gate, string amber, string red, out int field)
    {
        double v, a, r;
        field = 0;
        if (!TryNum(interval.Trim(), 1, 60, out v)) return "Sample every: enter a number of seconds from 1 to 60.";
        field = 1;
        if (!TryNum(warn.Trim(), 30, 120, out v)) return "Count as hot at: enter a temperature from 30 to 120 C.";
        field = 2;
        if (!TryNum(gate.Trim(), 5, 80, out v)) return "Idle below CPU load: enter a percentage from 5 to 80.";
        field = 3;
        if (!TryNum(amber.Trim(), 10, 100, out a)) return "Amber below: enter a percentage from 10 to 100.";
        field = 4;
        if (!TryNum(red.Trim(), 10, 100, out r)) return "Red below: enter a percentage from 10 to 100.";
        if (r > a) return "Red below must not be higher than Amber below.";
        return null;
    }

    // Returns false if the process should exit without starting the tray
    // icon (-h/-v handled and printed).
    private static bool ParseArgs(string[] args, out bool diag)
    {
        diag = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string val;
            double d;

            if (arg == "-h" || arg == "-?" || arg == "/?" ||
                string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintOrShow(UsageText());
                return false;
            }
            else if (arg == "-v" || string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase))
            {
                PrintOrShow(VersionText());
                return false;
            }
            else if (string.Equals(arg, "--diag", StringComparison.OrdinalIgnoreCase))
            {
                diag = true;
            }
            else if (MatchOpt(args, ref i, "i", "interval", out val))
            {
                if (TryNum(val, 1, 60, out d)) { IntervalMs = (int)(d * 1000); _anyCli = true; }
            }
            else if (MatchOpt(args, ref i, null, "warn", out val))
            {
                if (TryNum(val, 30, 120, out d)) { WarnC = d; _anyCli = true; }
            }
            else if (MatchOpt(args, ref i, null, "gate", out val))
            {
                if (TryNum(val, 5, 80, out d)) { GateUtil = d; _anyCli = true; }
            }
            else if (MatchOpt(args, ref i, null, "amber", out val))
            {
                if (TryNum(val, 10, 100, out d)) { AmberPct = d; _anyCli = true; }
            }
            else if (MatchOpt(args, ref i, null, "red", out val))
            {
                if (TryNum(val, 10, 100, out d)) { RedPct = d; _anyCli = true; }
            }
            else if (MatchOpt(args, ref i, null, "show", out val))
            {
                int s = ParseShow(val);
                if (s >= 0) { IconShows = s; _anyCli = true; }
            }
            else if (MatchOpt(args, ref i, null, "perfscale", out val))
            {
                // hidden test seam: no _anyCli, so it does not change which settings are loaded
                if (TryNum(val, 0.01, 3, out d)) PerfScale = d;
            }
        }
        return true;
    }

    private static string UsageText()
    {
        return "HeatTray " + Version + " - CPU heat slowdown indicator in the system tray\r\n" +
               "\r\n" +
               "Usage: HeatTray.exe [options]\r\n" +
               "\r\n" +
               "  -i <sec>,  --interval=<sec>  seconds between samples (default 5)\r\n" +
               "  --warn=<C>                   temperature that counts as hot, for the hint and the idle HOT warning (default 85)\r\n" +
               "  --gate=<pct>                 CPU load % below which it counts as idle (default 30)\r\n" +
               "  --amber=<pct>                amber when speed is below this % of rated (default 70)\r\n" +
               "  --red=<pct>                  red when speed is below this % of rated (default 40)\r\n" +
               "  --show=<percent|ghz|both>    what the icon shows (default percent)\r\n" +
               "  --diag                       print counter availability + 5 live samples, then exit\r\n" +
               "  -h, --help                   show this help and exit\r\n" +
               "  -v, --version                show version info and exit\r\n" +
               "\r\n" +
               "With no options, the settings last saved from the tray's\r\n" +
               "Settings dialog (settings.ini next to the exe) are used.\r\n" +
               "Command-line options always win and are never persisted.\r\n" +
               "One instance per machine.";
    }

    private static string VersionText()
    {
        return "HeatTray " + Version + " (" + VersionDate + ")\r\n" +
               "CPU heat slowdown indicator in the system tray.\r\n" +
               Author + " <" + AuthorEmail + ">";
    }

    // Compiled as /target:winexe, so there is no console by default. When
    // run from a terminal, attach to it so -h/-v/--diag behave like a normal
    // CLI tool; when stdout is a pipe/file, the inherited handle already
    // works. Only if neither exists (double-clicked with a flag) show a dialog.
    private static void PrintOrShow(string text)
    {
        bool attached = AttachConsole(ATTACH_PARENT_PROCESS);
        IntPtr stdout = GetStdHandle(STD_OUTPUT_HANDLE);

        if (attached || (stdout != IntPtr.Zero && stdout != new IntPtr(-1)))
        {
            try
            {
                using (var writer = new StreamWriter(Console.OpenStandardOutput()))
                {
                    if (attached)
                    {
                        writer.WriteLine();
                    }
                    writer.WriteLine(text);
                }

                if (attached && GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) == FILE_TYPE_CHAR)
                {
                    // The shell isn't waiting on this GUI-subsystem process,
                    // so its prompt printed before our output. Feed one
                    // Enter into the console input buffer so it redraws.
                    var rec = new INPUT_RECORD();
                    rec.EventType = KEY_EVENT;
                    rec.KeyEvent.bKeyDown = true;
                    rec.KeyEvent.wRepeatCount = 1;
                    rec.KeyEvent.wVirtualKeyCode = VK_RETURN;
                    rec.KeyEvent.UnicodeChar = '\r';
                    uint written;
                    WriteConsoleInput(GetStdHandle(STD_INPUT_HANDLE), new[] { rec }, 1, out written);
                }
                return;
            }
            catch
            {
                // Console/pipe write failed; we were in a CLI context, so exit quietly.
            }
            return;
        }

        MessageBox.Show(text, "HeatTray " + Version, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string RunDiag()
    {
        var sb = new StringBuilder();
        sb.AppendLine("HeatTray " + Version + " --diag");
        sb.AppendLine("Rated CPU speed (registry ~MHz): " + (_ratedMhz > 0 ? _ratedMhz.ToString("0") + " MHz" : "unknown"));
        sb.AppendLine("Counter '% Processor Performance': " + (_cPerf != null ? "OK" : "MISSING (needs an English-language Windows; speed cannot be read)"));
        sb.AppendLine("Counter load: " + (_cUtil != null ? "OK (" + _utilSource + ")" : "MISSING"));
        sb.AppendLine("Thermal zones: " + _cTemps.Count + (_cTemps.Count == 0 ? " (none exposed; temperature will be unavailable, speed still works)" : ""));
        sb.AppendLine("Taskbar theme: " + (IsLightTaskbar() ? "light" : "dark") + " (sets the icon text colours)");
        sb.AppendLine("Icon shows: " + ShowName(IconShows) + " (Settings, or --show=percent|ghz|both)");
        sb.AppendLine();
        sb.AppendLine("Live samples (1s apart):");
        sb.AppendLine("  perf%   load%   temp");
        for (int i = 0; i < 5; i++)
        {
            System.Threading.Thread.Sleep(1000);
            try
            {
                double p = _cPerf == null ? double.NaN : _cPerf.NextValue();
                double u = _cUtil == null ? double.NaN : _cUtil.NextValue();
                double t = ReadTempC();
                sb.AppendLine(string.Format("  {0,5:0}   {1,5:0}   {2}", p, u, double.IsNaN(t) ? "n/a" : t.ToString("0.0") + " C"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("  error: " + ex.GetType().Name);
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Dialogs
    // ------------------------------------------------------------------

    // The dialogs are modal, but the tray icon and its menu keep answering while one is open, so a second
    // click used to stack another copy on top of the first: they open at the same spot, so it looked like
    // one dialog until the top one was closed. Bring the open one forward instead. Returns true if a
    // dialog with this name was already open. A dialog opened from inside another (Settings from the menu
    // while Details is open) sits above it and the first one is disabled, so the one to bring forward
    // is the one opened last - the one that takes input - not the dialog that was asked for.
    private static bool ActivateOpenDialog(string name)
    {
        Form top = DialogToActivate(name);
        if (top == null) return false;
        if (top.WindowState == FormWindowState.Minimized) top.WindowState = FormWindowState.Normal;
        top.Activate();
        return true;
    }

    // The open dialog a request for the dialog called name should bring forward: the innermost one if a
    // dialog of that name is already open, otherwise null (the caller creates it).
    private static Form DialogToActivate(string name)
    {
        bool isOpen = false;
        foreach (Form f in Application.OpenForms)
        {
            if (f.Name == name) isOpen = true;
        }
        return isOpen ? Application.OpenForms[Application.OpenForms.Count - 1] : null;
    }

    // Deliberately not a MessageBox: any MessageBox with an icon plays the
    // system notification sound, which is jarring for a plain About box.
    private static void ShowAboutDialog()
    {
        if (ActivateOpenDialog("about")) return;
        using (var form = new Form())
        {
            form.Name = "about";
            form.Text = "About HeatTray";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.ClientSize = new Size(340, 185);

            var iconBox = new PictureBox
            {
                Left = 15, Top = 18, Width = 32, Height = 32,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap()
            };

            var label = new Label
            {
                Left = 62, Top = 15, Width = 265, Height = 125,
                Text = "HeatTray " + Version + " (" + VersionDate + ")\n\n" +
                       "CPU heat slowdown indicator in the system tray.\n\n" +
                       Author + "\n" + AuthorEmail + "\n\n" +
                       "Command-line options: HeatTray.exe -h"
            };

            var okButton = new Button { Text = "OK", Left = 245, Top = 150, Width = 80, DialogResult = DialogResult.OK };

            form.Controls.Add(iconBox);
            form.Controls.Add(label);
            form.Controls.Add(okButton);
            form.AcceptButton = okButton;
            form.CancelButton = okButton;
            form.ShowDialog();
        }
    }

    private const int DetailsWidth = 82;   // characters per line in the Details box

    // "  Label:   value", with the value wrapped onto lines that hang under it.
    private static void Row(StringBuilder sb, string label, string value)
    {
        string head = ("  " + label).PadRight(24);
        string pad = new string(' ', head.Length);
        string line = head;
        bool first = true;
        foreach (string word in value.Split(' '))
        {
            if (!first && line.Length + 1 + word.Length > DetailsWidth)
            {
                sb.AppendLine(line);
                line = pad + word;
            }
            else
            {
                line += (first ? "" : " ") + word;
            }
            first = false;
        }
        sb.AppendLine(line);
    }

    // Plain paragraph wrapped to the box width.
    private static void Para(StringBuilder sb, string text)
    {
        string line = "";
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > DetailsWidth)
            {
                sb.AppendLine(line);
                line = word;
            }
            else
            {
                line += (line.Length > 0 ? " " : "") + word;
            }
        }
        if (line.Length > 0) sb.AppendLine(line);
    }

    private static string DetailsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("NOW");
        if (_cPerf == null || _cUtil == null)
        {
            Row(sb, "Status:", "HeatTray cannot read the CPU speed from Windows. This usually means the performance counters are switched off or Windows is not set to English. Run HeatTray.exe --diag from a terminal to see which counter is missing.");
        }
        else
        {
            double ghz = Ghz(_lastPerf);
            Row(sb, "CPU load:", string.Format("{0:0}%", _lastUtil));
            Row(sb, "Busy-core speed:", string.Format("{0:0}% of rated{1}", _lastPerf,
                double.IsNaN(ghz) ? "" : string.Format(" (~{0:0.0} GHz)", ghz)));
            double peakT = HeatTempC();
            Row(sb, "Temperature:", double.IsNaN(_lastTempC) ? "not exposed by this machine"
                : string.Format("{0:0} C{1}", _lastTempC,
                    peakT - _lastTempC >= 1 ? string.Format(" (hottest in the last 2 minutes: {0:0} C)", peakT) : ""));
            if (_speedPct >= 0)
            {
                Row(sb, "Verdict:", string.Format("{0}: {1:0}% of rated speed",
                    _speedPct < RedPct ? "Red" : (_speedPct < AmberPct ? "Amber" : "Green"), _speedPct));
                Row(sb, "Below rated speed:", string.Format("~{0:0}%", Math.Max(0, 100 - _speedPct)));
                string like = Reckoner(_speedPct);
                Row(sb, "Feels like:", like == null
                    ? "as intended."
                    : string.Format("{0} (~{1:0.#}x slower than rated)", like, 100 / Math.Max(_speedPct, 1)));
                double tp = TrendPct();
                if (double.IsNaN(tp))
                {
                    Row(sb, "Trend (~5 min):", "building (needs ~2 min of steady load)");
                }
                else
                {
                    double oldG = Ghz(_trendRing.ToArray()[0]), newG = Ghz(_smoothPerf);
                    Row(sb, "Trend (~5 min):", string.Format("{0} ({1:+0;-0;0}%{2})",
                        tp >= 3 ? "rising" : (tp <= -3 ? "falling" : "steady"), tp,
                        double.IsNaN(oldG) ? "" : string.Format(", ~{0:0.0} -> ~{1:0.0} GHz", oldG, newG)));
                }
                string adv = AdviceLong();
                if (adv != null) Row(sb, "Advice:", adv);
            }
            else if (_ready)
            {
                if (!double.IsNaN(_lastTempC) && _lastTempC >= WarnC)
                {
                    Row(sb, "Status:", string.Format("Hot at light load ({0:0} C). Something is trapping heat: lift the laptop onto a hard, flat surface and clear the vents. The icon shows HOT in amber.", _lastTempC));
                }
                else
                {
                    Row(sb, "Status:", string.Format("Light load (below {0:0}% CPU load, so speed is not checked). With little to do, core speed varies on its own to save power, so the figure can be well below or above the rated speed and says little about heat. The icon stays green, and shows HOT in amber if the CPU is {1:0} C or more while idle.", GateUtil, WarnC));
                }
            }
            else
            {
                Row(sb, "Status:", string.Format("Starting up: needs about {0:0} seconds of readings before it can show a verdict.", SmoothN * IntervalMs / 1000.0));
            }
        }
        sb.AppendLine();
        sb.AppendLine("RATED SPEED");
        Row(sb, "Rated clock:", _ratedMhz > 0
            ? string.Format("{0:0.00} GHz ({1:0} MHz), about the speed Windows shows in This PC > Properties. 100% means the busy cores run at that clock.", _ratedMhz / 1000.0, _ratedMhz)
            : "not reported by this machine, so GHz is not shown. The percentages still work.");
        sb.AppendLine();
        sb.AppendLine("THRESHOLDS (the icon colour follows speed only)");
        Row(sb, "Green:", string.Format("light load, or working at {0:0}% of rated speed or more (above 100% is boost)", AmberPct));
        Row(sb, "Amber:", string.Format("under load, below {0:0}% of rated speed", AmberPct));
        Row(sb, "Red:", string.Format("under load, below {0:0}% of rated speed", RedPct));
        Row(sb, "HOT:", string.Format("amber word on the icon when the CPU is {0:0} C or more at light load", WarnC));
        sb.AppendLine();
        sb.AppendLine("HOW TO READ IT");
        Row(sb, "Speed near 100%:", "ignore the temperature, carry on.");
        Row(sb, string.Format("Slow, >= {0:0} C:", WarnC), "act on cooling or reduce the workload.");
        Row(sb, string.Format("Slow, < {0:0} C:", WarnC - 5), "not heat: check the power mode / AC power.");
        Row(sb, "No sensor:", "if this machine reports no temperature, a slow reading says the cause is unknown: check the power mode, the charger and the vents.");
        Row(sb, "Hints use:", "the hottest temperature of the last 2 minutes, not just now: a heat slowdown can outlast the heat.");
        Row(sb, "Icon:", "your busy-core speed as a % of the CPU's rated clock, or GHz, or both: choose in Settings. Grey only while HeatTray is starting.");
        Row(sb, "Trend:", "the arrow in the tooltip and the Trend line above.");
        Row(sb, "Feels like:", "a tongue-in-cheek comparison of how your laptop is running right now with an old computer (100% of rated speed counts as a current laptop). Order of magnitude only, not a benchmark.");
        sb.AppendLine();
        Para(sb, "Why not 100%? The rated clock is the CPU's base speed. A busy laptop often sits a little under it (power limits, battery, heat), a lightly loaded one often boosts above it, and an idle one chooses its own speed. Only a big shortfall under load means something is holding it back.");
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    // The Details window is as tall as its text, so nothing needs scrolling. Only a screen too small for the whole text (a low-resolution or heavily
    // zoomed display) keeps the scrollbar, and the box is then as tall as fits, in whole lines so no line is cut in half.
    private const int DetailsBoxPad = 6;   // the box's 3-D border (2 x 2 px) and a little air

    // Height of the Details text box for this many lines of this height, and whether it still has to scroll. availableHeight is the most it may take.
    private static int DetailsBoxHeight(int lines, int lineHeight, int availableHeight, out bool scrolls)
    {
        if (lineHeight < 1) lineHeight = 1;
        int need = lines * lineHeight + DetailsBoxPad;
        scrolls = need > availableHeight;
        if (!scrolls) return need;
        return Math.Max((availableHeight - DetailsBoxPad) / lineHeight, 3) * lineHeight + DetailsBoxPad;
    }

    private static void ShowDetailsDialog()
    {
        ShowDetailsWindow(Screen.FromPoint(Control.MousePosition).WorkingArea.Height);
    }

    // availableHeight: the height of the working area (the screen without the taskbar) of the screen the window opens on. A parameter so a test
    // can ask what happens on a small screen.
    private static void ShowDetailsWindow(int availableHeight)
    {
        if (ActivateOpenDialog("details")) return;
        using (var form = new Form())
        {
            form.Name = "details";
            form.Text = "HeatTray";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.ClientSize = new Size(660, 520);
            int frame = form.Height - form.ClientSize.Height;   // title bar and borders, known once the client size is set

            string text = DetailsText();
            var box = new TextBox
            {
                Name = "detailsBox",
                Left = 12, Top = 12, Width = 636,
                Multiline = true, ReadOnly = true,
                Font = new Font("Consolas", 9f),
                Text = text,
                BackColor = SystemColors.Window
            };
            // The box counts the empty last line after the final line break too. The line height is the one the box itself draws with (GDI), 1 px
            // less than Font.Height here.
            int lines = text.Split('\n').Length;
            int lineHeight = TextRenderer.MeasureText("Xg", box.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
            bool scrolls;
            // what the box may take: the screen minus the title bar and borders, the margins and the button row (12 + 10 + 23 + 15), and 20 px of air
            box.Height = DetailsBoxHeight(lines, lineHeight, availableHeight - frame - (12 + 10 + 23 + 15) - 20, out scrolls);
            box.ScrollBars = scrolls ? ScrollBars.Vertical : ScrollBars.None;
            int buttonTop = box.Bottom + 10;
            form.ClientSize = new Size(660, buttonTop + 23 + 15);

            var copyButton = new Button { Text = "Copy", Left = 478, Top = buttonTop, Width = 80 };
            copyButton.Click += (s, e) => { try { Clipboard.SetText(box.Text); } catch { } };
            var okButton = new Button { Text = "Close", Left = 568, Top = buttonTop, Width = 80, DialogResult = DialogResult.OK };

            form.Controls.Add(box);
            form.Controls.Add(copyButton);
            form.Controls.Add(okButton);
            form.AcceptButton = okButton;
            form.CancelButton = okButton;
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            form.ShowDialog();
        }
    }

    private static void LoadSettings()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            bool sawAmber = false, sawRed = false;
            int fileVersion = 0;   // v1.x files have no v= line
            foreach (string rawLine in File.ReadAllLines(ConfigPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int idx = line.IndexOf('=');
                if (idx < 0) continue;

                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string value = line.Substring(idx + 1).Trim();
                double d;

                if (key == "v") { int.TryParse(value, out fileVersion); }
                else if (key == "show") { int s = ParseShow(value); if (s >= 0) IconShows = s; }
                else if (key == "interval" && TryNum(value, 1, 60, out d)) IntervalMs = (int)(d * 1000);
                else if (key == "warn" && TryNum(value, 30, 120, out d)) WarnC = d;
                else if (key == "gate" && TryNum(value, 5, 80, out d)) GateUtil = d;
                else if (key == "amber" && TryNum(value, 10, 100, out d)) { AmberPct = d; sawAmber = true; }
                else if (key == "red" && TryNum(value, 10, 100, out d)) { RedPct = d; sawRed = true; }
            }
            // v1.x measured speed against a calibrated reference and saved 85 / 70 as its defaults. In a file
            // without a v= line those two exact values mean "never changed": move them to the v2 defaults (the
            // scale is now % of rated). Any other pair was a deliberate choice and is kept, and so is 85 / 70 in a
            // v2 file (SaveSettings writes v=2), which someone chose on purpose.
            if (fileVersion < 2 && sawAmber && sawRed && AmberPct == 85 && RedPct == 70) { AmberPct = 70; RedPct = 40; }
        }
        catch
        {
            // Malformed or unreadable config: keep whatever was already resolved.
        }
    }

    private static void SaveSettings()
    {
        try
        {
            File.WriteAllLines(ConfigPath, new[]
            {
                "v=2",
                "interval=" + (IntervalMs / 1000),
                "warn=" + WarnC.ToString(CultureInfo.InvariantCulture),
                "gate=" + GateUtil.ToString(CultureInfo.InvariantCulture),
                "amber=" + AmberPct.ToString(CultureInfo.InvariantCulture),
                "red=" + RedPct.ToString(CultureInfo.InvariantCulture),
                "show=" + ShowName(IconShows)
            });
        }
        catch
        {
            // Best-effort; not critical if this fails (e.g. read-only folder).
        }
    }

    private static TextBox AddRow(Form form, string label, string value, int top, string name)
    {
        form.Controls.Add(new Label { Text = label, Left = 15, Top = top + 3, Width = 215 });
        var box = new TextBox { Name = name, Text = value, Left = 240, Top = top, Width = 70 };
        form.Controls.Add(box);
        return box;
    }

    private static void ShowSettingsDialog()
    {
        if (ActivateOpenDialog("settings")) return;
        using (var form = new Form())
        {
            form.Name = "settings";
            form.Text = "HeatTray Settings";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ClientSize = new Size(325, 297);
            // The inputs are 70 px wide: the "Icon shows" drop-down needs about 67 px to show "Percent" in full.

            form.Controls.Add(new Label { Text = "Icon shows:", Left = 15, Top = 18, Width = 215 });
            var showBox = new ComboBox { Left = 240, Top = 15, Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
            showBox.Items.AddRange(new object[] { "Percent", "GHz", "Both" });   // index = ShowPercent / ShowGhz / ShowBoth
            showBox.SelectedIndex = IconShows;
            form.Controls.Add(showBox);

            var intervalBox = AddRow(form, "Sample every (sec):", (IntervalMs / 1000).ToString(CultureInfo.InvariantCulture), 47, "intervalBox");
            var warnBox = AddRow(form, "Count as hot at (C):", WarnC.ToString(CultureInfo.InvariantCulture), 79, "warnBox");
            var gateBox = AddRow(form, "Idle below CPU load (%):", GateUtil.ToString(CultureInfo.InvariantCulture), 111, "gateBox");
            var amberBox = AddRow(form, "Amber below (% of rated):", AmberPct.ToString(CultureInfo.InvariantCulture), 143, "amberBox");
            var redBox = AddRow(form, "Red below (% of rated):", RedPct.ToString(CultureInfo.InvariantCulture), 175, "redBox");

            // The hint, which turns into the message when OK finds something wrong (see FormClosing below).
            var hint = new Label
            {
                Name = "hint",
                Text = "Green at light load or when healthy; amber or red only under load, below these % of the CPU's rated clock. \"Hot\" just changes the hint and the HOT warning.",
                Left = 15, Top = 212, Width = 295, Height = 48, ForeColor = SystemColors.GrayText
            };
            form.Controls.Add(hint);

            var okButton = new Button { Text = "OK", Left = 120, Top = 262, Width = 80, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 210, Top = 262, Width = 80, DialogResult = DialogResult.Cancel };
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            // OK is checked before the dialog closes: if something is wrong the dialog stays open, says what
            // in the hint's place (in red) and puts the cursor in that box. (It used to close first, throw
            // away everything typed and show one message listing every range.) Editing any box brings the
            // hint back.
            string hintText = hint.Text;
            form.FormClosing += (s, e) =>
            {
                if (form.DialogResult != DialogResult.OK) return;
                int field;
                string problem = SettingsProblem(intervalBox.Text, warnBox.Text, gateBox.Text, amberBox.Text, redBox.Text, out field);
                if (problem == null) return;
                e.Cancel = true;
                hint.Text = problem;
                hint.ForeColor = Color.Firebrick;
                TextBox bad = field == 0 ? intervalBox : (field == 1 ? warnBox : (field == 2 ? gateBox : (field == 3 ? amberBox : redBox)));
                bad.Focus();
                bad.SelectAll();
            };
            EventHandler edited = (s, e) =>
            {
                hint.Text = hintText;
                hint.ForeColor = SystemColors.GrayText;
            };
            foreach (TextBox box in new[] { intervalBox, warnBox, gateBox, amberBox, redBox }) box.TextChanged += edited;

            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            // Only an OK with nothing wrong gets here (FormClosing above), so all of this parses; the
            // check is a guard, not a second set of rules.
            double interval = 0, warn = 0, gate = 0, amber = 0, red = 0;
            bool ok = TryNum(intervalBox.Text.Trim(), 1, 60, out interval) &&
                      TryNum(warnBox.Text.Trim(), 30, 120, out warn) &&
                      TryNum(gateBox.Text.Trim(), 5, 80, out gate) &&
                      TryNum(amberBox.Text.Trim(), 10, 100, out amber) &&
                      TryNum(redBox.Text.Trim(), 10, 100, out red);

            if (!ok || red > amber)
            {
                return;
            }

            IntervalMs = (int)(interval * 1000);
            WarnC = warn; GateUtil = gate; AmberPct = amber; RedPct = red;
            IconShows = showBox.SelectedIndex;
            _timer.Interval = IntervalMs;
            SaveSettings();
            Sample();
        }
    }

    // ------------------------------------------------------------------
    // Icon plumbing (same approach as PingTray)
    // ------------------------------------------------------------------

    private static void SetTooltip(string text)
    {
        // NotifyIcon.Text throws at 64 characters (each newline counts as one) although the
        // shell takes 127. For longer text set the private field and refresh the icon (the
        // usual workaround); if that ever fails, fall back to truncating.
        if (text.Length <= 63)
        {
            _notifyIcon.Text = text;
            return;
        }
        if (text.Length > 127) text = text.Substring(0, 124) + "...";
        try
        {
            typeof(NotifyIcon).GetField("text", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_notifyIcon, text);
            if ((bool)typeof(NotifyIcon).GetField("added", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_notifyIcon))
            {
                typeof(NotifyIcon).GetMethod("UpdateIcon", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_notifyIcon, new object[] { true });
            }
        }
        catch
        {
            _notifyIcon.Text = text.Substring(0, 60) + "...";
        }
    }

    // What a line of the icon is saying; each tone has a bright colour for a dark
    // taskbar and a darker one for a light taskbar (see ToneColor).
    private enum Tone { Neutral, Good, Warn, Bad }

    private static Color ToneColor(Tone tone, bool light)
    {
        switch (tone)
        {
            case Tone.Good: return light ? Color.FromArgb(0, 130, 0) : Color.LimeGreen;
            case Tone.Warn: return light ? Color.FromArgb(205, 100, 0) : Color.Orange;
            // Bad: pure Color.Red was only 3.9:1 on a maroon accent taskbar; this lighter red is 5.0:1
            // there (4.1 -> 5.2 on Windows dark grey) and still clearly red.
            case Tone.Bad: return light ? Color.FromArgb(200, 0, 0) : Color.FromArgb(255, 85, 85);
            default: return light ? Color.FromArgb(35, 35, 35) : Color.Gainsboro;
        }
    }

    // True when the Windows taskbar / tray is using the light theme (the system
    // theme, not the apps theme). Dark when the setting is missing or unreadable.
    private static bool IsLightTaskbar()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = (k == null) ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyIcon(string top, string bottom, Tone topTone, Tone bottomTone)
    {
        // Read the theme on every redraw (cheap) so a Windows theme switch shows within a sample.
        Icon newIcon = RenderIcon(top, bottom, topTone, bottomTone, IsLightTaskbar());
        Icon oldIcon = _currentIcon;
        _currentIcon = newIcon;
        _notifyIcon.Icon = newIcon;

        if (oldIcon != null)
        {
            var h = oldIcon.Handle;
            oldIcon.Dispose();
            DestroyIcon(h);
        }
    }

    // Two lines of text (the bottom one optional) filling a 32x32 icon, like
    // the clock / language indicator beside it.
    private static Icon RenderIcon(string top, string bottom, Tone topTone, Tone bottomTone, bool light)
    {
        const int S = 32;
        Color topColor = ToneColor(topTone, light), bottomColor = ToneColor(bottomTone, light);
        // The halo is the opposite of the text: dark on a dark taskbar, light on a light one.
        Color halo = light ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(190, 15, 15, 15);
        using (var bmp = new Bitmap(S, S))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (bottom == null)
                {
                    DrawFit(g, top, topColor, halo, new RectangleF(1, 2, S - 2, S - 4));
                }
                else
                {
                    // Each line gets 14.5 of the 32 px (0.75 px margins), with a 1.5 px gap.
                    DrawFit(g, top, topColor, halo, new RectangleF(1, 0.75f, S - 2, 14.5f));
                    DrawFit(g, bottom, bottomColor, halo, new RectangleF(1, 16.75f, S - 2, 14.5f));
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
    }

    // Digits are never stretched taller than 1 / MinSqueeze of their natural shape; InkGap is the
    // space between characters, in the 100-unit em they are laid out in.
    private const float MinSqueeze = 0.85f;
    private const float InkGap = 9f;

    // The text as one outline path. Each character is placed by its ink, not by its advance
    // width, so a narrow "1" takes little room and "111" needs far less squeezing to fit.
    private static System.Drawing.Drawing2D.GraphicsPath TightText(string text)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        FontStyle style = FontStyle.Regular;
        FontFamily family;
        try { family = new FontFamily("Segoe UI Semibold"); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); style = FontStyle.Bold; }
        using (family)
        {
            float x = 0;
            foreach (char ch in text)
            {
                using (var glyph = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    glyph.AddString(ch.ToString(), family, (int)style, 100f, new PointF(0, 0), StringFormat.GenericTypographic);
                    RectangleF cb = glyph.GetBounds();
                    if (cb.Width <= 0) continue;
                    using (var m = new System.Drawing.Drawing2D.Matrix())
                    {
                        m.Translate(x - cb.X, 0);
                        glyph.Transform(m);
                    }
                    path.AddPath(glyph, false);
                    x += cb.Width + InkGap;
                }
            }
        }
        return path;
    }

    // Draws text as an outline path scaled to fill the box (digits as tall as the box allows),
    // with a thin halo (none if the halo colour is transparent) so it reads on any taskbar.
    private static void DrawFit(Graphics g, string text, Color color, Color halo, RectangleF box)
    {
        using (var path = TightText(text))
        {
            RectangleF b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            // Two digits (and "<1") all share the scale of "88", so 11 and 88 have the same height and stroke
            // weight (fitted on their own, narrow numbers came out bigger and bolder). A wide pair
            // (a 4 is wider than an 8) is squeezed by its own width rather than clipped. Anything
            // else is fitted on its own bounds.
            RectangleF r = b;
            if (text.Length == 2 && (char.IsDigit(text[0]) || text[0] == '<') && char.IsDigit(text[1]))
                using (var rp = TightText("88")) r = rp.GetBounds();
            // Fill the box height; if the text is too wide, squeeze it horizontally (never
            // below MinSqueeze) and only then shrink it.
            float fitW = box.Width / r.Width, fitH = box.Height / r.Height;
            float sy = Math.Min(fitH, fitW / MinSqueeze);
            float sx = Math.Min(Math.Min(sy, fitW), box.Width / b.Width);
            using (var m = new System.Drawing.Drawing2D.Matrix())
            {
                m.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
                m.Scale(sx, sy);
                m.Translate(-(b.X + b.Width / 2f), -(r.Y + r.Height / 2f));
                path.Transform(m);
            }
            if (halo.A > 0)
            {
                using (var pen = new Pen(halo, 2f))
                {
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawPath(pen, path);
                }
            }
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }
    }
}
