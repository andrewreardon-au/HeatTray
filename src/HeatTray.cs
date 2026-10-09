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

// Embedded VERSIONINFO (Explorer's Properties â†’ Details). Kept in this file
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

    internal const string Version = "1.2";
    private const string VersionDate = "2026-10-09";
    private const string Author = "Andrew Reardon";
    private const string AuthorEmail = "andrewreardon@gmail.com";

    // ---- Tunables (settings.ini / command line) ----
    private static int IntervalMs = 2000;
    private static double WarnC = 85;      // temp >= this: amber
    private static double GateUtil = 30;   // CPU load % below which we call it "idle" and don't judge speed
    private static double AmberPct = 85;   // speed (% of the calibrated reference) below this: amber
    private static double RedPct = 70;     // ... below this: red
    private const int ShowPercent = 0, ShowGhz = 1, ShowBoth = 2;
    private static int IconShows = ShowPercent;   // what the icon says: speed %, GHz, or both (two lines)

    // Command-line options always win: if any tunable was passed, settings.ini
    // is not loaded at all (defaults + flags only), and flags are never
    // persisted - only Settings-dialog edits write settings.ini.
    private static bool _anyCli;

    private const int SmoothN = 5;         // samples averaged before judging (10s at the default 2s interval)
    private const int CalSeconds = 300;       // calibration window

    private static string ConfigPath;
    private static string BaselinePath;

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
    private static double _speedPct = -1;          // -1 = not judged (idle / no data)
    private static bool _calibrated;              // speed is being judged against the reference
    private static bool _loaded;                  // CPU load is above the idle gate
    // Smoothed busy-core speed (% of rated) for recent loaded samples, to show a trend.
    private static readonly Queue<double> _trendRing = new Queue<double>();
    private const int TrendMax = 150;     // ~5 min at the default 2 s interval
    private const int TrendEnds = 15;     // compare the newest vs oldest ~30 s of the window
    private const int TrendMin = 60;      // need ~2 min of loaded history before showing a trend

    // Recent temperature readings (UTC time, deg C), so the hint can look back and not
    // only at now: a firmware thermal clamp can hold the CPU slow for 30-40 s after
    // the heat has gone (see HeatTempC).
    private const int HeatMemorySec = 120;
    private static readonly Queue<KeyValuePair<DateTime, double>> _tempRing = new Queue<KeyValuePair<DateTime, double>>();

    // The reference: busy-core speed (% of rated) of the user's own demo load,
    // measured once while the machine was cool (see StartCalibration).
    private static double _refPct;                // 0 = none yet
    private static string _refDate = "";
    private static double _refMaxTemp = double.NaN;
    private static int _refSamples;
    private static string _lastCalResult = "";    // last calibration outcome (balloons can be suppressed by Focus Assist)

    // Calibration in progress
    private static bool _calActive;
    private static DateTime _calStart;
    private static readonly List<double> _calSamples = new List<double>();
    private static double _calMaxTemp = double.NaN;

    [STAThread]
    private static void Main(string[] args)
    {
        bool diag;
        if (!ParseArgs(args, out diag))
        {
            return;
        }

        ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        BaselinePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "baseline.ini");

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
                "HeatTray is already running - look for its icon in the tray (or under the ^ overflow arrow near the clock).",
                // MessageBoxIcon.None: an icon would play the system notification sound.
                "HeatTray", MessageBoxButtons.OK, MessageBoxIcon.None);
            return;
        }

        LoadBaseline();
        Application.EnableVisualStyles();

        var menu = new ContextMenuStrip();
        var detailsItem = menu.Items.Add("Details...");
        detailsItem.Click += (s, e) => ShowDetailsDialog();
        var settingsItem = menu.Items.Add("Settings...");
        settingsItem.Click += (s, e) => ShowSettingsDialog();
        var calItem = menu.Items.Add("Calibrate to demo load (5 min)...");
        calItem.Click += (s, e) => StartCalibration();
        menu.Opening += (s, e) => { calItem.Text = _calActive ? "Cancel calibration" : "Calibrate to demo load (5 min)..."; };
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

    // Hottest thermal zone in Â°C, or NaN. The counter reports Kelvin.
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
            SetTooltip("HeatTray: no CPU counters\nrun HeatTray.exe --diag");
            ApplyIcon("?", null, Tone.Neutral, Tone.Neutral);
            return;
        }

        try
        {
            _lastPerf = _cPerf.NextValue();
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

        _loaded = loaded;
        _speedPct = -1;
        _calibrated = false;

        if (_calActive && !double.IsNaN(_lastTempC) && (double.IsNaN(_calMaxTemp) || _lastTempC > _calMaxTemp))
        {
            _calMaxTemp = _lastTempC;
        }

        if (loaded)
        {
            if (_calActive) _calSamples.Add(_smoothPerf);
            if (_refPct > 0)
            {
                _speedPct = _smoothPerf / _refPct * 100.0;   // not capped: above 100 = faster than the calibration run
                _calibrated = true;
            }
        }

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

        if (_calActive && (DateTime.Now - _calStart).TotalSeconds >= CalSeconds)
        {
            FinishCalibration();
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
    // Slow + hot -> cooling; slow + cool -> power mode / AC / firmware cap.
    private static string HintFor(double t)
    {
        if (double.IsNaN(t) || t < WarnC - 5) return "Check power mode";
        return t >= WarnC ? "Check cooling" : "Heat or power cap";
    }

    // Short cause hint; null when speed isn't judged.
    private static string AdviceShort()
    {
        if (_speedPct < 0 || !_calibrated) return null;
        if (_speedPct >= AmberPct) return "OK";
        return HintFor(HeatTempC());
    }

    // " (peaked 96C)" when a slow reading is being put down to heat that has already
    // eased, i.e. the hint differs from what the current temperature alone would give.
    private static string PeakNote()
    {
        double peak = HeatTempC();
        if (_speedPct < 0 || !_calibrated || _speedPct >= AmberPct || double.IsNaN(peak) || HintFor(peak) == HintFor(_lastTempC)) return "";
        return string.Format(" (peaked {0:0}C)", peak);
    }

    private static string AdviceLong()
    {
        bool eased = PeakNote().Length > 0;   // the hint rests on heat that has already eased
        switch (AdviceShort())
        {
            case "OK":
                return (!double.IsNaN(_lastTempC) && _lastTempC >= WarnC)
                    ? "Hot, but still holding normal speed - no action needed."
                    : "Running at normal speed - carry on.";
            case "Check cooling":
                return eased
                    ? string.Format("Slow, and it was hot within the last 2 minutes (peak {0:0}C): a heat slowdown can outlast the heat. Act on cooling (airflow, dust, surface) or reduce the workload.", HeatTempC())
                    : "Slow AND hot - act on cooling (airflow, dust, surface) or reduce the workload.";
            case "Check power mode":
                return "Slow but not hot - this isn't heat. Check the power mode, AC power / charger wattage, or a firmware power cap.";
            case "Heat or power cap":
                return eased
                    ? string.Format("Slow, and it was warm within the last 2 minutes (peak {0:0}C) - could be heat or a power limit. Watch whether it gets hotter; check power mode too.", HeatTempC())
                    : "Slow and warm - could be heat or a power limit. Watch whether it gets hotter; check power mode too.";
        }
        return null;
    }

    // Ready reckoner (all Intel, at the user's request, except the abacus): roughly which vintage of CPU the current speed feels like, if this
    // machine's normal speed (the reference, 100%) is taken as a current laptop. Rough
    // single-thread comparison, order of magnitude only: for fun, not a benchmark.
    // Centres are % of normal; the nearest one (on a log scale) wins.
    private static readonly double[] ReckonPct = { 70, 55, 38, 27, 17, 11, 6.5, 3.5, 1.0, 0.45, 0.25, 0.04, 0.005 };
    private static readonly string[] ReckonName = {
        "a Core i5-4690", "a Core i5-2500K", "a Core 2 Duo E8400", "a Core 2 Duo E6600",
        "a Pentium 4 2.4GHz", "a Pentium 4 1.8GHz", "a Pentium III 733", "a Pentium II 400",
        "a Pentium 166", "a Pentium 60", "a 486 DX2-66", "a 386DX-16", "an abacus" };
    // The year each of those CPUs debuted (same order).
    private static readonly string[] ReckonYear = {
        "2014", "2011", "2008", "2006", "2002", "2001", "1999", "1998", "1996", "1993", "1992", "1985", "2400 BC" };

    // Null at or above the amber threshold (nothing to joke about) or when not judged.
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
        string loadText = string.Format("load {0:0}%", _smoothUtil);

        if (_calActive)
        {
            int left = Math.Max(0, CalSeconds - (int)(DateTime.Now - _calStart).TotalSeconds);
            string mmss = string.Format("{0}:{1:00}", left / 60, left % 60);
            SetTooltip(string.Format("Calibrating {0} left\n{1} | {2}\n{3} readings", mmss, tempText, loadText, _calSamples.Count));
            ApplyIcon("CAL", mmss, Tone.Cal, Tone.Cal);
            return;
        }

        // Judged = loaded and a reference exists: the speed is compared with the reference
        // (the user's own demo load) and coloured green / orange / red. The same figure is
        // shown while idle (see IconTone), so the icon stays steady instead of flipping to
        // grey. With no reference there is nothing to compare with, so the raw busy-core
        // speed as % of rated is shown (in grey) with a tag.
        bool judged = _speedPct >= 0;
        bool haveRef = _refPct > 0;
        double vsRef = judged ? _speedPct : (haveRef ? _smoothPerf / _refPct * 100.0 : -1);
        double shownPct = vsRef >= 0 ? vsRef : _smoothPerf;
        int shown = (int)Math.Round(Math.Min(shownPct, 999));
        double g = Ghz(_smoothPerf);

        string tag;
        string arrow = "";
        if (judged)
        {
            tag = AdviceShort() + PeakNote();
            if (!double.IsNaN(TrendPct()))
            {
                int td = TrendDir();
                arrow = td > 0 ? " \u25B2" : (td < 0 ? " \u25BC" : " \u25BA");
            }
        }
        else
        {
            tag = (_loaded && _refPct <= 0) ? "Needs calibration" : "Idle";
        }
        string ghzText = double.IsNaN(g) ? "" : string.Format(" {0:0.0}GHz", g);
        // Short lines, not one long one: the taskbar wraps long tray tooltips (the old
        // one-liner, ~54 characters, wrapped at 125% scaling).
        string tip = string.Format("Speed {0}%{1}{2}\n{3} | {4}\n{5}", shown, ghzText, arrow, tempText, loadText, tag);
        string like = judged ? Reckoner(_speedPct) : null;
        if (like != null) tip += "\nLike " + like;
        SetTooltip(tip);

        // Colour is about speed only (is heat actually slowing me down?); the temperature
        // is in the tooltip. Both lines share the colour so the icon reads as one signal.
        Tone tone = IconTone(judged, haveRef, vsRef);
        string top, bottom;
        IconLines(IconShows, haveRef, shown.ToString(CultureInfo.InvariantCulture),
            double.IsNaN(g) ? null : g.ToString("0.0", CultureInfo.InvariantCulture), out top, out bottom);
        ApplyIcon(top, bottom, tone, tone);
    }

    // Colour of the icon. Under load: the speed verdict (green / orange / red). Idle: only
    // green (speed at or above the orange threshold) or grey, because a CPU slows itself down
    // on purpose when idle, so orange or red would be a false alarm. No reference: grey.
    private static Tone IconTone(bool judged, bool haveRef, double vsRefPct)
    {
        if (judged) return vsRefPct < RedPct ? Tone.Bad : (vsRefPct < AmberPct ? Tone.Warn : Tone.Good);
        return (haveRef && vsRefPct >= AmberPct) ? Tone.Good : Tone.Neutral;
    }

    // The icon's one or two lines for the chosen display; bottom is null for a single big
    // number. With no reference there is no percentage to show, so it is the GHz on its own.
    private static void IconLines(int show, bool haveRef, string pctText, string ghzText, out string top, out string bottom)
    {
        bottom = null;
        if (ghzText != null && (!haveRef || show == ShowGhz)) { top = ghzText; return; }
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
    // Reference: calibration + persistence
    // ------------------------------------------------------------------

    private static void StartCalibration()
    {
        if (_cPerf == null || _cUtil == null) return;   // no counters: nothing to calibrate
        if (_calActive)
        {
            _calActive = false;      // menu item reads "Cancel calibration" while active
            _calSamples.Clear();
            Render();
            return;
        }
        var r = MessageBox.Show(
            "Calibrate to your demo load\n\n" +
            "1. Let the laptop cool (idle, on a desk).\n" +
            "2. Start your full demo load.\n" +
            "3. Click OK.\n\n" +
            "HeatTray then watches for 5 minutes (readings count while CPU load is above " + GateUtil.ToString("0") +
            "%) and stores that speed as your reference. Keep the load running until the icon stops showing CAL.",
            "HeatTray", MessageBoxButtons.OKCancel, MessageBoxIcon.None);
        if (r != DialogResult.OK) return;
        _calActive = true;
        _calStart = DateTime.Now;
        _calSamples.Clear();
        _calMaxTemp = double.NaN;
        Render();
    }

    private static void FinishCalibration()
    {
        _calActive = false;
        int n = _calSamples.Count;
        // ~40% of the readings a full window would hold at the current interval.
        int need = Math.Max(10, (int)(CalSeconds * 1000L / IntervalMs * 0.4));
        if (n < need)
        {
            _calSamples.Clear();
            _lastCalResult = string.Format("{0}: failed - only {1} loaded readings (need {2}).", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), n, need);
            _notifyIcon.ShowBalloonTip(8000, "HeatTray calibration failed",
                string.Format("Only {0} loaded readings in 5 minutes (need {1}). Start the demo load first, then calibrate again.", n, need),
                ToolTipIcon.None);
            return;
        }
        // Reference = median of the LAST 40% of the window (the settled speed). A cool
        // start usually boosts / draws extra power for the first minutes, which is
        // not what the demo sustains; judging against it would sit permanently amber.
        double[] all = _calSamples.ToArray();
        _calSamples.Clear();
        int tailStart = (int)(n * 0.6);
        double[] tail = new double[n - tailStart];
        Array.Copy(all, tailStart, tail, 0, tail.Length);
        double firstAvg = 0;
        int firstN = Math.Max(1, n / 5);
        for (int i = 0; i < firstN; i++) firstAvg += all[i];
        firstAvg /= firstN;
        Array.Sort(tail);
        _refPct = tail[tail.Length / 2];
        _refDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        _refMaxTemp = _calMaxTemp;
        _refSamples = n;
        SaveBaseline();

        double g = Ghz(_refPct);
        string msg = string.Format("Reference set: {0:0}% of rated{1} (settled speed over the last 2 min; first minute averaged {2:0}%).",
            _refPct, double.IsNaN(g) ? "" : string.Format(" (~{0:0.0} GHz)", g), firstAvg);
        if (!double.IsNaN(_refMaxTemp) && _refMaxTemp >= WarnC)
        {
            msg += string.Format(" It reached {0:0} C during calibration, so the reference may be low - let it cool and run it again.", _refMaxTemp);
        }
        _lastCalResult = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ": " + msg;
        _notifyIcon.ShowBalloonTip(10000, "HeatTray calibrated", msg, ToolTipIcon.None);
    }

    private static void LoadBaseline()
    {
        try
        {
            if (!File.Exists(BaselinePath)) return;
            int version = 0, samples = 0;
            double r = 0, maxTemp = double.NaN;
            string date = "";
            foreach (string rawLine in File.ReadAllLines(BaselinePath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int idx = line.IndexOf('=');
                if (idx < 0) continue;
                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string val = line.Substring(idx + 1).Trim();
                if (key == "version") int.TryParse(val, out version);
                else if (key == "ref") double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out r);
                else if (key == "maxtemp") double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out maxTemp);
                else if (key == "samples") int.TryParse(val, out samples);
                else if (key == "date") date = val;
            }
            // Older formats (v1 best-ever, v2 per-band histograms) don't carry a reference.
            if (version == 3 && r >= 10 && r <= 400)
            {
                _refPct = r;
                _refDate = date.Length > 40 ? date.Substring(0, 40) : date;
                _refMaxTemp = maxTemp;
                _refSamples = samples;
            }
        }
        catch { }
    }

    private static void SaveBaseline()
    {
        try
        {
            if (_refPct <= 0) return;
            var lines = new List<string>();
            lines.Add("# HeatTray reference: busy-core speed (% of rated) of your demo load, measured while calibrating.");
            lines.Add("# Delete this file to forget it (calibrate again from the tray menu).");
            lines.Add("version=3");
            lines.Add(string.Format(CultureInfo.InvariantCulture, "ref={0:0.0}", _refPct));
            lines.Add("date=" + _refDate);
            if (!double.IsNaN(_refMaxTemp)) lines.Add(string.Format(CultureInfo.InvariantCulture, "maxtemp={0:0.0}", _refMaxTemp));
            lines.Add("samples=" + _refSamples.ToString(CultureInfo.InvariantCulture));
            File.WriteAllLines(BaselinePath, lines.ToArray());
        }
        catch { }
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
        }
        return true;
    }

    private static string UsageText()
    {
        return "HeatTray " + Version + " - CPU heat slowdown indicator in the system tray\r\n" +
               "\r\n" +
               "Usage: HeatTray.exe [options]\r\n" +
               "\r\n" +
               "  -i <sec>,  --interval=<sec>  seconds between samples (default 2)\r\n" +
               "  --warn=<C>                   temperature that counts as hot, for the hint (default 85)\r\n" +
               "  --gate=<pct>                 CPU load % below which it counts as idle (default 30)\r\n" +
               "  --amber=<pct>                amber when speed is below this % of reference (default 85)\r\n" +
               "  --red=<pct>                  red when speed is below this % of reference (default 70)\r\n" +
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

    // Deliberately not a MessageBox: any MessageBox with an icon plays the
    // system notification sound, which is jarring for a plain About box.
    private static void ShowAboutDialog()
    {
        using (var form = new Form())
        {
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
            Row(sb, "", "CPU counters unavailable. Run HeatTray.exe --diag from a terminal.");
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
                Row(sb, "Speed vs reference:", string.Format("{0:0}%", _speedPct));
                Row(sb, "Estimated slowdown:", string.Format("~{0:0}%", Math.Max(0, 100 - _speedPct)));
                string like = Reckoner(_speedPct);
                Row(sb, "Feels like:", like == null
                    ? "as intended."
                    : string.Format("{0} (~{1:0.#}x slower than normal)", like, 100 / Math.Max(_speedPct, 1)));
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
            else if (_calActive)
            {
                Row(sb, "Status:", "Calibrating - keep the demo load running until the icon stops showing CAL.");
            }
            else if (_loaded)
            {
                Row(sb, "Status:", "Under load but no reference yet - right-click the icon > Calibrate to demo load...");
            }
            else if (_refPct > 0)
            {
                Row(sb, "Speed vs reference:", string.Format("{0:0}% (idle, so no verdict)", _smoothPerf / _refPct * 100.0));
                Row(sb, "Status:", string.Format("Idle (load below {0:0}%). A CPU slows itself down when idle, so a low speed is normal: the icon stays green while the speed is at least {1:0}% of your reference, goes grey below that, and is never orange or red.", GateUtil, AmberPct));
            }
            else
            {
                Row(sb, "Status:", "Idle (load below " + GateUtil.ToString("0") + "%), and no reference yet - right-click the icon > Calibrate to demo load...");
            }
        }
        sb.AppendLine();
        sb.AppendLine("REFERENCE (your demo load, measured while cool)");
        if (_refPct > 0)
        {
            double rg = Ghz(_refPct);
            Row(sb, "Speed:", string.Format("{0:0}% of rated{1}", _refPct,
                double.IsNaN(rg) ? "" : string.Format(" (~{0:0.0} GHz)", rg)));
            Row(sb, "Set:", _refDate);
            Row(sb, "From:", string.Format("{0} readings{1}", _refSamples,
                double.IsNaN(_refMaxTemp) ? "" : string.Format(", peak temperature {0:0} C", _refMaxTemp)));
        }
        else
        {
            Row(sb, "", "none yet - right-click the icon > Calibrate to demo load...");
        }
        if (_lastCalResult.Length > 0) Row(sb, "Last calibration:", _lastCalResult);
        sb.AppendLine();
        sb.AppendLine("THRESHOLDS (the icon colour follows speed only)");
        Row(sb, "Amber:", string.Format("speed below {0:0}% of reference", AmberPct));
        Row(sb, "Red:", string.Format("speed below {0:0}% of reference", RedPct));
        sb.AppendLine();
        sb.AppendLine("HOW TO READ IT");
        Row(sb, "Speed near 100%:", "ignore the temperature, carry on.");
        Row(sb, string.Format("Slow, >= {0:0} C:", WarnC), "act on cooling or reduce the workload.");
        Row(sb, string.Format("Slow, < {0:0} C:", WarnC - 5), "not heat - check the power mode / AC power.");
        Row(sb, "Hints use:", "the hottest temperature of the last 2 minutes, not just now: a heat slowdown can outlast the heat.");
        Row(sb, "Icon:", "your speed as a % of your normal (100 = as fast as your calibration run, more = faster), or GHz, or both: choose in Settings. Grey GHz = no reference yet.");
        Row(sb, "Trend:", "the arrow in the tooltip and the Trend line above.");
        Row(sb, "Feels like:", "a tongue-in-cheek comparison that treats your normal speed as a current laptop. Order of magnitude only - not a benchmark.");
        sb.AppendLine();
        Para(sb, "The reference is the median busy-core speed over the last 2 minutes of a 5-minute calibration run (the settled speed, not the initial boost). Calibrate again after changing the hardware, power mode or what your demo load does.");
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static void ShowDetailsDialog()
    {
        using (var form = new Form())
        {
            form.Text = "HeatTray";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.ClientSize = new Size(660, 520);

            var box = new TextBox
            {
                Left = 12, Top = 12, Width = 636, Height = 460,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                Text = DetailsText(),
                BackColor = SystemColors.Window
            };
            var copyButton = new Button { Text = "Copy", Left = 478, Top = 482, Width = 80 };
            copyButton.Click += (s, e) => { try { Clipboard.SetText(box.Text); } catch { } };
            var okButton = new Button { Text = "Close", Left = 568, Top = 482, Width = 80, DialogResult = DialogResult.OK };

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

            foreach (string rawLine in File.ReadAllLines(ConfigPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int idx = line.IndexOf('=');
                if (idx < 0) continue;

                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string value = line.Substring(idx + 1).Trim();
                double d;

                if (key == "show") { int s = ParseShow(value); if (s >= 0) IconShows = s; }
                else if (key == "interval" && TryNum(value, 1, 60, out d)) IntervalMs = (int)(d * 1000);
                else if (key == "warn" && TryNum(value, 30, 120, out d)) WarnC = d;
                else if (key == "gate" && TryNum(value, 5, 80, out d)) GateUtil = d;
                else if (key == "amber" && TryNum(value, 10, 100, out d)) AmberPct = d;
                else if (key == "red" && TryNum(value, 10, 100, out d)) RedPct = d;
            }
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

    private static TextBox AddRow(Form form, string label, string value, int top)
    {
        form.Controls.Add(new Label { Text = label, Left = 15, Top = top + 3, Width = 215 });
        var box = new TextBox { Text = value, Left = 240, Top = top, Width = 80 };
        form.Controls.Add(box);
        return box;
    }

    private static void ShowSettingsDialog()
    {
        using (var form = new Form())
        {
            form.Text = "HeatTray Settings";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ClientSize = new Size(335, 297);

            form.Controls.Add(new Label { Text = "Icon shows:", Left = 15, Top = 18, Width = 215 });
            var showBox = new ComboBox { Left = 240, Top = 15, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            showBox.Items.AddRange(new object[] { "Percent", "GHz", "Both" });   // index = ShowPercent / ShowGhz / ShowBoth
            showBox.SelectedIndex = IconShows;
            form.Controls.Add(showBox);

            var intervalBox = AddRow(form, "Sample every (sec):", (IntervalMs / 1000).ToString(CultureInfo.InvariantCulture), 47);
            var warnBox = AddRow(form, "Count as hot at (C):", WarnC.ToString(CultureInfo.InvariantCulture), 79);
            var gateBox = AddRow(form, "Idle below CPU load (%):", GateUtil.ToString(CultureInfo.InvariantCulture), 111);
            var amberBox = AddRow(form, "Amber below (% of reference):", AmberPct.ToString(CultureInfo.InvariantCulture), 143);
            var redBox = AddRow(form, "Red below (% of reference):", RedPct.ToString(CultureInfo.InvariantCulture), 175);

            form.Controls.Add(new Label
            {
                Text = "The icon colour follows speed only. \"Hot\" just changes the hint: slow and hot says Check cooling, slow and cool says Check power mode.",
                Left = 15, Top = 212, Width = 305, Height = 48, ForeColor = SystemColors.GrayText
            });

            var okButton = new Button { Text = "OK", Left = 130, Top = 262, Width = 80, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 220, Top = 262, Width = 80, DialogResult = DialogResult.Cancel };
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            double interval = 0, warn = 0, gate = 0, amber = 0, red = 0;
            bool ok = TryNum(intervalBox.Text.Trim(), 1, 60, out interval) &&
                      TryNum(warnBox.Text.Trim(), 30, 120, out warn) &&
                      TryNum(gateBox.Text.Trim(), 5, 80, out gate) &&
                      TryNum(amberBox.Text.Trim(), 10, 100, out amber) &&
                      TryNum(redBox.Text.Trim(), 10, 100, out red);

            if (!ok || red > amber)
            {
                MessageBox.Show("Check the values: interval 1-60 s, hot temperature 30-120 C, idle gate 5-80%, speeds 10-100% (red not above amber).",
                    "HeatTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    private enum Tone { Neutral, Good, Warn, Bad, Cal }

    private static Color ToneColor(Tone tone, bool light)
    {
        switch (tone)
        {
            case Tone.Good: return light ? Color.FromArgb(0, 130, 0) : Color.LimeGreen;
            case Tone.Warn: return light ? Color.FromArgb(205, 100, 0) : Color.Orange;
            case Tone.Bad: return light ? Color.FromArgb(200, 0, 0) : Color.Red;
            case Tone.Cal: return light ? Color.FromArgb(0, 120, 170) : Color.Cyan;
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

    // Draws text as an outline path scaled to fill the box (digits as tall as
    // the box allows), with a thin halo so it reads on any taskbar.
    private static void DrawFit(Graphics g, string text, Color color, Color halo, RectangleF box)
    {
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            path.AddString(text, new FontFamily("Segoe UI"), (int)FontStyle.Bold, 100f, new PointF(0, 0), StringFormat.GenericTypographic);
            RectangleF b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            // Fill the box height; squeeze horizontally (to at most 60%) only when the
            // text is too wide, rather than shrinking it, so "100" stays as tall as "92".
            float fitW = box.Width / b.Width, fitH = box.Height / b.Height;
            float sy = Math.Min(fitH, fitW / 0.6f);
            float sx = Math.Min(sy, fitW);
            using (var m = new System.Drawing.Drawing2D.Matrix())
            {
                m.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
                m.Scale(sx, sy);
                m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                path.Transform(m);
            }
            using (var pen = new Pen(halo, 2f))
            using (var brush = new SolidBrush(color))
            {
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                g.DrawPath(pen, path);
                g.FillPath(brush, path);
            }
        }
    }
}
