using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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

    internal const string Version = "2.3";
    private const string VersionDate = "2026-10-31";
    private const string Author = "Andrew Reardon";
    private const string ProjectUrl = "https://github.com/andrewreardon-au/HeatTray";

    // ---- Tunables (settings.ini / command line) ----
    private static int IntervalMs = 5000;
    private static double WarnC = 85;      // temp >= this counts as hot: changes the hint, and an idle CPU this hot shows HOT
    private static double GateUtil = 30;   // CPU load % below which we call it "idle" and don't judge speed
    private static double AmberPct = 70;   // loaded speed (% of the rated clock) below this: amber
    private static double RedPct = 40;     // ... below this: red
    // Test seam (hidden, not in -h): scales the perf counter so an idle machine can be staged in a slow state.
    private static double PerfScale = 1.0;
    private static double TempOffset = 0;      // hidden test seam (--tempoffset): added to the thermal reading, for README screenshots
    private const int ShowPercent = 0, ShowGhz = 1, ShowBoth = 2, ShowRatio = 3, ShowRatioGhz = 4, ShowTemp = 5, ShowUsage = 6, ShowUsageGhz = 7;
    private static int IconShows = ShowRatio;   // what the icon says: ratio, usage, temperature, percent, GHz, or two-line combos
    private static bool TempF = false;          // show temperatures in Fahrenheit (storage and CLI stay Celsius)

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

    // The same three readings through PDH with ENGLISH counter names. PerformanceCounter takes the
    // category and counter names as the local language spells them, so on a non-English Windows it
    // throws and the icon would stay a grey "?". These are only set when that route failed (or with
    // the hidden --forcepdh, which lets an English machine exercise this path).
    private static PdhCounter _pPerf, _pUtil, _pTemps;
    private static int _pTempCount;
    private static bool ForcePdh;

    private const uint PdhFmtDouble = 0x200;
    private const uint PdhMoreData = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_VALUE value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_VALUE { public uint CStatus; public double Value; }

    // PDH_FMT_COUNTERVALUE_ITEM: the instance name, then the same status + value pair.
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_ITEM { public IntPtr Name; public uint CStatus; public double Value; }

    // One PDH counter that reads like PerformanceCounter.NextValue(): the constructor takes the
    // priming sample, each NextValue() is the value over the time since the one before. A path with
    // a (*) instance gives every instance through NextValues().
    private sealed class PdhCounter
    {
        private IntPtr _query, _counter;

        public PdhCounter(string englishPath)
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) throw new InvalidOperationException("PdhOpenQuery");
            if (PdhAddEnglishCounter(_query, englishPath, IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                throw new InvalidOperationException("PdhAddEnglishCounter " + englishPath);
            }
            PdhCollectQueryData(_query);
        }

        private static bool GoodStatus(uint s) { return s == 0 || s == 1; }   // PDH_CSTATUS_VALID_DATA, _NEW_DATA

        public double NextValue()
        {
            if (PdhCollectQueryData(_query) != 0) throw new InvalidOperationException("PdhCollectQueryData");
            uint type;
            PDH_VALUE v;
            if (PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out type, out v) != 0 || !GoodStatus(v.CStatus))
                throw new InvalidOperationException("PdhGetFormattedCounterValue");
            return v.Value;
        }

        public double[] NextValues()
        {
            if (PdhCollectQueryData(_query) != 0) throw new InvalidOperationException("PdhCollectQueryData");
            uint size = 0, count = 0;
            uint r = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
            if (r != PdhMoreData) return new double[0];   // no instances (or an error): nothing to read
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, ref count, buf) != 0) return new double[0];
                var list = new List<double>();
                int itemSize = Marshal.SizeOf(typeof(PDH_ITEM));
                for (int i = 0; i < (int)count; i++)
                {
                    var item = (PDH_ITEM)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + (long)i * itemSize), typeof(PDH_ITEM));
                    if (GoodStatus(item.CStatus)) list.Add(item.Value);
                }
                return list.ToArray();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

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

        ConfigPath = SettingsFilePath();

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
        var settingsItem = menu.Items.Add("Settings...");
        settingsItem.Click += (s, e) => ShowSettingsDialog();
        var detailsItem = menu.Items.Add("Details...");
        detailsItem.Click += (s, e) => ShowDetailsDialog();
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
                ShowSettingsDialog();
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
            if (ForcePdh) throw new InvalidOperationException();
            _cPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cPerf.NextValue();
        }
        catch { _cPerf = null; }

        try
        {
            if (ForcePdh) throw new InvalidOperationException();
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
                if (ForcePdh) throw new InvalidOperationException();
                _cUtil = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cUtil.NextValue();
                _utilSource = "Processor % Processor Time (fallback)";
            }
            catch { _cUtil = null; }
        }

        // On a non-English Windows the names above do not exist, so the same counters are asked for by their English names.
        if (_cPerf == null)
        {
            try { _pPerf = new PdhCounter(@"\Processor Information(_Total)\% Processor Performance"); }
            catch { _pPerf = null; }
        }
        if (_cUtil == null)
        {
            try
            {
                _pUtil = new PdhCounter(@"\Processor Information(_Total)\% Processor Time");
                _utilSource = "% Processor Time (PDH, English names)";
            }
            catch
            {
                try
                {
                    _pUtil = new PdhCounter(@"\Processor(_Total)\% Processor Time");
                    _utilSource = "Processor % Processor Time (PDH, English names, fallback)";
                }
                catch { _pUtil = null; }
            }
        }

        try
        {
            if (ForcePdh) throw new InvalidOperationException();
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

        if (_cTemps.Count == 0)
        {
            try
            {
                var z = new PdhCounter(@"\Thermal Zone Information(*)\Temperature");
                int n = z.NextValues().Length;
                if (n > 0) { _pTemps = z; _pTempCount = n; }
            }
            catch { _pTemps = null; }
        }

        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                _ratedMhz = RatedMhzFrom(key == null ? null : key.GetValue("~MHz"));
            }
        }
        catch { }
    }

    // The rated clock from the registry value, or 0 (unknown) when it is missing or not believable: every
    // percentage and GHz figure is scaled by it, so a junk value (0, a few MHz, a typo) must not be used.
    // No CPU is rated under 500 MHz or over 10 GHz.
    private static double RatedMhzFrom(object v)
    {
        try
        {
            if (v == null) return 0;
            double m = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return (m >= 500 && m <= 10000) ? m : 0;
        }
        catch { return 0; }
    }

    private static bool HavePerfCounter() { return _cPerf != null || _pPerf != null; }
    private static bool HaveUtilCounter() { return _cUtil != null || _pUtil != null; }
    private static double ReadPerf() { return _cPerf != null ? _cPerf.NextValue() : _pPerf.NextValue(); }
    private static double ReadUtil() { return _cUtil != null ? _cUtil.NextValue() : _pUtil.NextValue(); }
    private static int ThermalZoneCount() { return _cTemps.Count + _pTempCount; }

    // Hottest thermal zone in deg C, or NaN. The counter reports Kelvin.
    private static double ReadTempC()
    {
        double best = double.NaN;
        foreach (var c in _cTemps)
        {
            try { best = HotterC(best, c.NextValue()); }
            catch { }
        }
        if (_pTemps != null)
        {
            try { foreach (double k in _pTemps.NextValues()) best = HotterC(best, k); }
            catch { }
        }
        return best;
    }

    // The hotter of best (deg C or NaN) and a reading in Kelvin; readings outside 200-500 K are ignored.
    private static double HotterC(double best, double kelvin)
    {
        if (kelvin > 200 && kelvin < 500)
        {
            double t = kelvin - 273.15;
            if (double.IsNaN(best) || t > best) return t;
        }
        return best;
    }

    // ------------------------------------------------------------------
    // Sampling and judgement
    // ------------------------------------------------------------------

    private static void Sample()
    {
        if (!HavePerfCounter() || !HaveUtilCounter())
        {
            SetTooltip("Cannot read speed\nRight-click, Details");
            ApplyIcon("?", null, Tone.Neutral, Tone.Neutral);
            return;
        }

        try
        {
            _lastPerf = ReadPerf() * PerfScale;
            _lastUtil = ReadUtil();
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
        if (!double.IsNaN(_lastTempC)) _lastTempC += TempOffset;
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

    // Temperature display helpers. Internals (sensors, config files, --warn CLI) stay in Celsius;
    // only the UI is converted when TempF is on. "F" takes an upper-case suffix to match "C".
    private static double CToDisplay(double c) { return TempF ? c * 9.0 / 5.0 + 32.0 : c; }
    private static double DisplayToC(double d) { return TempF ? (d - 32.0) * 5.0 / 9.0 : d; }
    private static string TempUnit() { return TempF ? "F" : "C"; }
    private static string FmtTemp(double c)
    {
        if (double.IsNaN(c)) return "?";
        return string.Format(CultureInfo.InvariantCulture, "{0:0}{1}", CToDisplay(c), TempUnit());
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
        return " (peaked " + FmtTemp(peak) + ")";
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
                    ? "Slow, and it was hot within the last 2 minutes (peak " + FmtTemp(HeatTempC()) + "): a heat slowdown can outlast the heat. Act on cooling (airflow, dust, surface) or reduce the workload."
                    : "Slow AND hot: act on cooling (airflow, dust, surface) or reduce the workload.";
            case "Cause unknown":
                return "Slow, and this machine reports no temperature, so the cause is unknown. Check the power mode, AC power / charger wattage or a firmware power cap, and make sure the vents are clear.";
            case "Check power mode":
                return "Slow but not hot, so this isn't heat. Check the power mode, AC power / charger wattage, or a firmware power cap.";
            case "Heat or power cap":
                return eased
                    ? "Slow, and it was warm within the last 2 minutes (peak " + FmtTemp(HeatTempC()) + "): could be heat or a power limit. Watch whether it gets hotter; check power mode too."
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
        string cpuTemp = double.IsNaN(_lastTempC)
            ? "no temp sensor"
            : string.Format(CultureInfo.InvariantCulture, "{0:0} {1}", CToDisplay(_lastTempC), TempUnit());
        string cpuLine = string.Format(CultureInfo.InvariantCulture, "CPU: {0}, usage {1:0}%", cpuTemp, _smoothUtil);

        if (!_ready)
        {
            // The smoothing window is not full yet: no verdict, just the live reading in grey,
            // formatted the same way as the full icon (ratio / temp / percent / GHz) so startup
            // doesn't flash a disconnected number.
            SetTooltip("Starting up\nMeasuring CPU speed\nReady in seconds");
            int pctNow = (int)Math.Round(Math.Min(_lastPerf, 999));
            double gNow = Ghz(_lastPerf);
            string rNow = (_lastPerf / 100.0).ToString("0.0", CultureInfo.InvariantCulture);
            string tNow = double.IsNaN(_lastTempC) ? "?" : FmtTemp(_lastTempC);
            string uNow = ((int)Math.Round(Math.Min(_lastUtil, 999))).ToString(CultureInfo.InvariantCulture) + "%";
            string sTop, sBottom;
            IconLines(IconShows, pctNow.ToString(CultureInfo.InvariantCulture),
                double.IsNaN(gNow) ? null : gNow.ToString("0.0", CultureInfo.InvariantCulture),
                rNow, tNow, uNow, out sTop, out sBottom);
            ApplyIcon(sTop, sBottom, Tone.Neutral, Tone.Neutral);
            return;
        }

        // Speed is judged only under load: busy-core speed as a % of the rated clock, coloured
        // green / orange / red. Idle, the cores choose their own speed (it can be well under or
        // over the rated one), so the same figure is shown but there is nothing to judge: green,
        // unless the CPU is hot even though it is idle.
        int shown = PctShown(_smoothPerf, _loaded);
        double g = Ghz(_smoothPerf);
        string ghzText = double.IsNaN(g) ? "" : string.Format(" {0:0.0}GHz of {1:0.0}GHz", g, _ratedMhz / 1000.0);
        string arrow = "";
        if (_loaded && !double.IsNaN(TrendPct()))
        {
            int td = TrendDir();
            arrow = td > 0 ? " \u25B2" : (td < 0 ? " \u25BC" : " \u25BA");
        }
        bool hotIdle = !_loaded && !double.IsNaN(_lastTempC) && _lastTempC >= WarnC;

        // Three labelled lines (Speed / Load / CPU), short so the taskbar does not wrap them.
        // The slow case swaps the CPU line for an action line and adds Reckoner's "Like" tag: the
        // verdict already carries the temperature, and with the CPU line too the text passes the
        // shell's 127 characters and SetTooltip would cut the "Like" line (and its year).
        string tip = string.Format("Speed: {0}%{1}{2}\n", shown, ghzText, arrow);
        if (!_loaded)
        {
            tip += "Load: " + (hotIdle ? "Hot at light load\nClear vents, lift laptop\n"
                                       : "Light load, core speed is variable\n") + cpuLine;
        }
        else if (_speedPct >= AmberPct)
        {
            tip += "Load: " + (_speedPct > 100 ? "Boosting, faster than rated" : "Working hard, all fine") + "\n" + cpuLine;
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
            string ratioText = RatioShown(_smoothPerf, _loaded).ToString("0.0", CultureInfo.InvariantCulture);
            string tempIcon = double.IsNaN(_lastTempC) ? "?" : FmtTemp(_lastTempC);
            string usageIcon = ((int)Math.Round(Math.Min(_smoothUtil, 999))).ToString(CultureInfo.InvariantCulture) + "%";
            IconLines(IconShows, shown.ToString(CultureInfo.InvariantCulture),
                double.IsNaN(g) ? null : g.ToString("0.0", CultureInfo.InvariantCulture),
                ratioText, tempIcon, usageIcon, out top, out bottom);
        }
        ApplyIcon(top, bottom, tone, tone);
    }

    // The figure shown for a speed reading. Under load a reading below a colour limit must not round UP to it:
    // 69.6 is orange, and "70%" or "0.7" next to an orange icon reads as a healthy value. Such a reading is
    // shown one step below the limit (69, 0.6) instead. Idle readings are always green, so they round normally.
    private static int PctShown(double perf, bool loaded)
    {
        int r = (int)Math.Round(Math.Min(perf, 999));
        if (loaded)
        {
            foreach (double limit in new[] { AmberPct, RedPct })
            {
                if (perf < limit && r >= limit) r = (int)Math.Ceiling(limit) - 1;
            }
        }
        return r;
    }

    private static double RatioShown(double perf, bool loaded)
    {
        int tenths = (int)Math.Round(Math.Min(perf, 999) / 10.0, MidpointRounding.AwayFromZero);
        if (loaded)
        {
            foreach (double limit in new[] { AmberPct, RedPct })
            {
                if (perf < limit && tenths * 10 >= limit) tenths = (int)Math.Ceiling(limit / 10.0) - 1;
            }
        }
        return tenths / 10.0;
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
        action = hint == "Check power mode" ? "Plug in, check power mode" : (hot ? "Clear vents, lift laptop" : "Cooling or power mode?");
        if (PeakNote().Length > 0)
        {
            // The heat has already eased but the slowdown outlasts it.
            string what = hot ? (red ? "Too hot" : "Hot") : slower;
            verdict = double.IsNaN(now) ? what + " (peak " + FmtTemp(peak) + ")"
                                        : what + " (now " + FmtTemp(now) + ", peak " + FmtTemp(peak) + ")";
            return;
        }
        string temp = " (" + FmtTemp(peak) + ")";
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
    private static void IconLines(int show, string pctText, string ghzText, string ratioText, string tempText, string usageText, out string top, out string bottom)
    {
        bottom = null;
        if (show == ShowRatio) { top = ratioText; return; }
        if (show == ShowUsage) { top = usageText; return; }
        if (show == ShowTemp) { top = tempText; return; }
        if (ghzText != null && show == ShowGhz) { top = ghzText; return; }
        if (show == ShowRatioGhz)
        {
            top = ratioText;
            if (ghzText != null) bottom = ghzText;
            return;
        }
        if (show == ShowUsageGhz)
        {
            top = usageText;
            if (ghzText != null) bottom = ghzText;
            return;
        }
        top = pctText;
        if (ghzText != null && show == ShowBoth) bottom = ghzText;
    }

    private static string ShowName(int show)
    {
        switch (show)
        {
            case ShowGhz: return "ghz";
            case ShowBoth: return "both";
            case ShowRatio: return "ratio";
            case ShowRatioGhz: return "ratio-ghz";
            case ShowTemp: return "temp";
            case ShowUsage: return "usage";
            case ShowUsageGhz: return "usage-ghz";
            default: return "percent";
        }
    }

    // -1 when the text is not one of the choices.
    private static int ParseShow(string s)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "percent": case "pct": return ShowPercent;
            case "ghz": return ShowGhz;
            case "both": case "percent-ghz": case "percent+ghz": return ShowBoth;
            case "ratio": return ShowRatio;
            case "ratio-ghz": case "ratio+ghz": return ShowRatioGhz;
            case "temp": case "temperature": return ShowTemp;
            case "usage": return ShowUsage;
            case "usage-ghz": case "usage+ghz": return ShowUsageGhz;
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
        double warnMin = TempF ? 86 : 30, warnMax = TempF ? 248 : 120;
        if (!TryNum(warn.Trim(), warnMin, warnMax, out v))
            return "Count as hot at: enter a temperature from " + ((int)warnMin) + " to " + ((int)warnMax) + " " + TempUnit() + ".";
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
            else if (string.Equals(arg, "--forcepdh", StringComparison.OrdinalIgnoreCase))
            {
                // hidden test seam: read the counters through PDH by English name even on an English Windows
                ForcePdh = true;
            }
            else if (MatchOpt(args, ref i, null, "perfscale", out val))
            {
                // hidden test seam: no _anyCli, so it does not change which settings are loaded
                if (TryNum(val, 0.01, 3, out d)) PerfScale = d;
            }
            else if (MatchOpt(args, ref i, null, "tempoffset", out val))
            {
                // hidden test seam, like --perfscale: degrees added to the temperature, so a screenshot can show the hot case
                if (TryNum(val, -50, 80, out d)) TempOffset = d;
            }
        }
        return true;
    }

    private static string UsageText()
    {
        return "HeatTray " + Version + ": CPU heat slowdown indicator in the system tray\r\n" +
               "\r\n" +
               "Usage: HeatTray.exe [options]\r\n" +
               "\r\n" +
               "  -i <sec>,  --interval=<sec>  seconds between samples (default 5)\r\n" +
               "  --warn=<C>                   temperature that counts as hot, for the hint and the idle HOT warning (default 85)\r\n" +
               "  --gate=<pct>                 CPU load % below which it counts as idle (default 30)\r\n" +
               "  --amber=<pct>                amber when speed is below this % of rated (default 70)\r\n" +
               "  --red=<pct>                  red when speed is below this % of rated (default 40)\r\n" +
               "  --show=<ratio|usage|temp|percent|ghz|ratio-ghz|usage-ghz|both>\r\n" +
               "                               what the icon shows (default ratio)\r\n" +
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
               Author + "\r\n" + ProjectUrl;
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
        sb.AppendLine("Counter '% Processor Performance': " + (_cPerf != null ? "OK" : (_pPerf != null ? "OK (read through PDH by its English name: the local-language counter names did not work)" : "MISSING (speed cannot be read; the performance counters may be switched off)")));
        sb.AppendLine("Counter load: " + (HaveUtilCounter() ? "OK (" + _utilSource + ")" : "MISSING"));
        sb.AppendLine("Thermal zones: " + ThermalZoneCount() + (ThermalZoneCount() == 0 ? " (none exposed; temperature will be unavailable, speed still works)" : (_cTemps.Count == 0 ? " (through PDH, English names)" : "")));
        sb.AppendLine("Taskbar theme: " + (IsLightTaskbar() ? "light" : "dark") + " (sets the icon text colours)");
        sb.AppendLine("Icon shows: " + ShowName(IconShows) + " (Settings, or --show=ratio|usage|temp|percent|ghz|ratio-ghz|usage-ghz|both)");
        sb.AppendLine("Temperature unit: " + (TempF ? "Fahrenheit (F)" : "Celsius (C)") + " (Settings; --warn is always in C)");
        sb.AppendLine();
        sb.AppendLine("Live samples (1s apart):");
        sb.AppendLine("  perf%   load%   temp");
        for (int i = 0; i < 5; i++)
        {
            System.Threading.Thread.Sleep(1000);
            try
            {
                double p = HavePerfCounter() ? ReadPerf() : double.NaN;
                double u = HaveUtilCounter() ? ReadUtil() : double.NaN;
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
            form.ClientSize = new Size(330, 185);

            var iconBox = new PictureBox
            {
                Left = 15, Top = 18, Width = 32, Height = 32,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap()
            };

            // Three controls so the project address can be a link: the text above it, the link, the text below it.
            var label = new Label
            {
                Left = 62, Top = 15, Width = 255, Height = 75,
                Text = "HeatTray " + Version + " (" + VersionDate + ")\n\n" +
                       "CPU heat slowdown indicator in the system tray.\n\n" +
                       Author
            };
            var projectLink = new LinkLabel { Left = 62, Top = 90, Width = 255, Height = 15, Text = ProjectUrl.Replace("https://", "") };
            projectLink.LinkClicked += (s, e) =>
            {
                try { Process.Start(ProjectUrl); }
                catch { }
            };
            var helpLabel = new Label { Left = 62, Top = 105, Width = 255, Height = 30, Text = "\nCommand-line options: HeatTray.exe -h" };

            var okButton = new Button { Text = "OK", Left = 235, Top = 150, Width = 80, DialogResult = DialogResult.OK };

            form.Controls.Add(iconBox);
            form.Controls.Add(label);
            form.Controls.Add(projectLink);
            form.Controls.Add(helpLabel);
            form.Controls.Add(okButton);
            form.AcceptButton = okButton;
            form.CancelButton = okButton;
            // Nothing else asks Windows to activate the dialog after a tray click, and it often declines
            // on its own (the click came from the overflow flyout, say): ask once it is on screen.
            form.Shown += (s, e) => form.Activate();
            form.ActiveControl = okButton;   // not the link: Enter on a focused link opens the browser instead of closing the box
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
        if (!HavePerfCounter() || !HaveUtilCounter())
        {
            Row(sb, "Status:", "HeatTray cannot read the CPU speed from Windows. This usually means the performance counters are switched off or damaged. Run HeatTray.exe --diag from a terminal to see which counter is missing.");
        }
        else
        {
            double ghz = Ghz(_lastPerf);
            Row(sb, "CPU load:", string.Format("{0:0}%", _lastUtil));
            Row(sb, "Busy-core speed:", string.Format("{0:0}% of rated{1}", _lastPerf,
                double.IsNaN(ghz) ? "" : string.Format(" (~{0:0.0} GHz)", ghz)));
            double peakT = HeatTempC();
            Row(sb, "Temperature:", double.IsNaN(_lastTempC) ? "not exposed by this machine"
                : FmtTemp(_lastTempC) + (peakT - _lastTempC >= 1 ? " (hottest in the last 2 minutes: " + FmtTemp(peakT) + ")" : ""));
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
                    Row(sb, "Status:", "Hot at light load (" + FmtTemp(_lastTempC) + "). Something is trapping heat: lift the laptop onto a hard, flat surface and clear the vents. The icon shows HOT in amber.");
                }
                else
                {
                    Row(sb, "Status:", string.Format("Light load (below {0:0}% CPU load, so speed is not checked). With little to do, core speed varies on its own to save power, so the figure can be well below or above the rated speed and says little about heat. The icon stays green, and shows HOT in amber if the CPU is {1} or more while idle.", GateUtil, FmtTemp(WarnC)));
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
        Row(sb, "HOT:", "amber word on the icon when the CPU is " + FmtTemp(WarnC) + " or more at light load");
        sb.AppendLine();
        sb.AppendLine("HOW TO READ IT");
        Row(sb, "Speed near 100%:", "ignore the temperature, carry on.");
        Row(sb, "Slow, >= " + FmtTemp(WarnC) + ":", "act on cooling or reduce the workload.");
        Row(sb, "Slow, < " + FmtTemp(WarnC - 5) + ":", "not heat: check the power mode / AC power.");
        Row(sb, "No sensor:", "if this machine reports no temperature, a slow reading says the cause is unknown: check the power mode, the charger and the vents.");
        Row(sb, "Hints use:", "the hottest temperature of the last 2 minutes, not just now: a heat slowdown can outlast the heat.");
        Row(sb, "Icon:", "your busy-core speed as a ratio of the CPU's rated clock (1.0 is rated, 0.7 is 70%), or CPU usage, temperature, a percent or GHz: choose in Settings. Grey only while HeatTray is starting.");
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder packageFullName);

    // True when running from an installed MSIX package (a Microsoft Store build). The install folder is
    // then read-only and per-user files belong in LocalAppData, and "start at login" is a package setting
    // that Windows owns rather than a Run-key entry. Same code as PingTray.
    private static bool IsPackaged()
    {
        try
        {
            int length = 0;
            const int AppModelErrorNoPackage = 15700;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Packaged: %LOCALAPPDATA%\HeatTray\settings.ini (the package folder cannot be written to). Otherwise
    // next to the exe, so the unzipped folder stays portable.
    private static string SettingsFilePath()
    {
        if (IsPackaged())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeatTray", "settings.ini");
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
    }

    // "Start with Windows" for the unzipped build: a per-user Run-key entry that launches this exe with no
    // options, so it resumes the settings saved from the Settings box (same as PingTray).
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "HeatTray";

    private static bool GetRunAtLogin()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                // Any entry counts, even one left pointing at an old folder after the exe was moved:
                // OK then rewrites it to this exe (ticked) or removes it (unticked).
                return k != null && k.GetValue(RunValueName) != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void SetRunAtLogin(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // Best-effort, like saving the settings: a locked-down profile just keeps it off.
        }
    }

    private static void LoadSettings()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            bool sawAmber = false, sawRed = false, sawShow = false;
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
                else if (key == "show") { int s = ParseShow(value); if (s >= 0) { IconShows = s; sawShow = true; } }
                else if (key == "interval" && TryNum(value, 1, 60, out d)) IntervalMs = (int)(d * 1000);
                else if (key == "warn" && TryNum(value, 30, 120, out d)) WarnC = d;
                else if (key == "gate" && TryNum(value, 5, 80, out d)) GateUtil = d;
                else if (key == "amber" && TryNum(value, 10, 100, out d)) { AmberPct = d; sawAmber = true; }
                else if (key == "red" && TryNum(value, 10, 100, out d)) { RedPct = d; sawRed = true; }
                else if (key == "tempunit") { string u = value.Trim().ToLowerInvariant(); if (u == "f" || u == "fahrenheit") TempF = true; else if (u == "c" || u == "celsius") TempF = false; }
            }
            // v1.x measured speed against a calibrated reference and saved 85 / 70 as its defaults. In a file
            // without a v= line those two exact values mean "never changed": move them to the v2 defaults (the
            // scale is now % of rated). Any other pair was a deliberate choice and is kept, and so is 85 / 70 in a
            // v2 file (SaveSettings writes v=2), which someone chose on purpose.
            if (fileVersion < 2 && sawAmber && sawRed && AmberPct == 85 && RedPct == 70) { AmberPct = 70; RedPct = 40; }
            // v3 adds the Ratio display. Pre-v3 files that still carry the old "percent" default
            // (or had no show= line) are migrated to the new Ratio default on first launch.
            if (fileVersion < 3 && (!sawShow || IconShows == ShowPercent)) IconShows = ShowRatio;
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
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllLines(ConfigPath, new[]
            {
                "v=3",
                "interval=" + (IntervalMs / 1000),
                "warn=" + WarnC.ToString(CultureInfo.InvariantCulture),
                "gate=" + GateUtil.ToString(CultureInfo.InvariantCulture),
                "amber=" + AmberPct.ToString(CultureInfo.InvariantCulture),
                "red=" + RedPct.ToString(CultureInfo.InvariantCulture),
                "show=" + ShowName(IconShows),
                "tempunit=" + (TempF ? "f" : "c")
            });
        }
        catch
        {
            // Best-effort; not critical if this fails (e.g. read-only folder).
        }
    }

    // Rows share the same right edge (260 + 70 = 330) as the combos above (210 + 120 = 330), so
    // every input lines up on the right regardless of width.
    private static TextBox AddRow(Form form, string label, string value, int top, string name)
    {
        form.Controls.Add(new Label { Text = label, Left = 15, Top = top + 3, Width = 240 });
        var box = new TextBox { Name = name, Text = value, Left = 260, Top = top, Width = 70, TextAlign = HorizontalAlignment.Right };
        form.Controls.Add(box);
        return box;
    }

    // Same row style, but hosted inside a GroupBox (coordinates are relative to the group).
    // Label at x=10, text box right-edged at x=315 so numbers in the two groups look aligned.
    private static TextBox AddRowIn(GroupBox group, string label, string value, int top, string name)
    {
        group.Controls.Add(new Label { Text = label, Left = 10, Top = top + 3, Width = 230 });
        var box = new TextBox { Name = name, Text = value, Left = 245, Top = top, Width = 70, TextAlign = HorizontalAlignment.Right };
        group.Controls.Add(box);
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
            form.Font = SystemFonts.MessageBoxFont;    // Segoe UI, matches the rest of Windows
            form.ClientSize = new Size(360, 436);

            // Inside a GroupBox, child coordinates are relative to the group box, not the form.
            // Each row: label left=10 width=170; input right-edged at x=305 (box inner width ~ 320).

            // DISPLAY
            var displayGroup = new GroupBox { Text = "Display", Left = 15, Top = 10, Width = 330, Height = 90 };
            form.Controls.Add(displayGroup);

            displayGroup.Controls.Add(new Label { Text = "Icon shows:", Left = 10, Top = 28, Width = 150 });
            var showBox = new ComboBox { Left = 180, Top = 25, Width = 135, DropDownStyle = ComboBoxStyle.DropDownList };
            // Dropdown order (Ratio first); the parallel array maps ComboBox index -> Show* constant.
            int[] showMap = new[] { ShowRatio, ShowUsage, ShowTemp, ShowPercent, ShowGhz, ShowRatioGhz, ShowUsageGhz, ShowBoth };
            showBox.Items.AddRange(new object[] { "Speed ratio", "Usage", "Temperature", "Percent", "GHz", "Speed ratio + GHz", "Usage + GHz", "Percent + GHz" });
            showBox.SelectedIndex = Math.Max(0, Array.IndexOf(showMap, IconShows));
            displayGroup.Controls.Add(showBox);

            displayGroup.Controls.Add(new Label { Text = "Temperature unit:", Left = 10, Top = 60, Width = 150 });
            var unitBox = new ComboBox { Left = 180, Top = 57, Width = 135, DropDownStyle = ComboBoxStyle.DropDownList };
            unitBox.Items.AddRange(new object[] { "Celsius (C)", "Fahrenheit (F)" });
            unitBox.SelectedIndex = TempF ? 1 : 0;
            displayGroup.Controls.Add(unitBox);

            // THRESHOLDS
            var thresholdsGroup = new GroupBox { Text = "Thresholds", Left = 15, Top = 110, Width = 330, Height = 185 };
            form.Controls.Add(thresholdsGroup);

            var intervalBox = AddRowIn(thresholdsGroup, "Sample every (sec):",
                (IntervalMs / 1000).ToString(CultureInfo.InvariantCulture), 25, "intervalBox");
            var warnBox = AddRowIn(thresholdsGroup, "Count as hot at (" + TempUnit() + "):",
                ((int)Math.Round(CToDisplay(WarnC))).ToString(CultureInfo.InvariantCulture), 55, "warnBox");
            var gateBox = AddRowIn(thresholdsGroup, "Idle below CPU load (%):",
                GateUtil.ToString(CultureInfo.InvariantCulture), 85, "gateBox");
            var amberBox = AddRowIn(thresholdsGroup, "Amber below (% of rated):",
                AmberPct.ToString(CultureInfo.InvariantCulture), 115, "amberBox");
            var redBox = AddRowIn(thresholdsGroup, "Red below (% of rated):",
                RedPct.ToString(CultureInfo.InvariantCulture), 145, "redBox");

            // Switching the unit while the dialog is open relabels the hot box AND converts whatever
            // is currently typed, so the number keeps its meaning (85 C becomes 185 F, not stays 85 F).
            // The unit is only the dialog's until OK: Cancel must leave the running app (tooltip, icon) as it was.
            bool unitF = TempF;
            Label warnLabel = null;
            foreach (Control c in thresholdsGroup.Controls) { var l = c as Label; if (l != null && l.Text.StartsWith("Count as hot at ")) { warnLabel = l; break; } }
            unitBox.SelectedIndexChanged += (s, e) =>
            {
                bool nowF = unitBox.SelectedIndex == 1;
                if (nowF == unitF) return;
                double parsed;
                if (TryNum(warnBox.Text.Trim(), -100, 500, out parsed))
                {
                    double asC = unitF ? (parsed - 32.0) * 5.0 / 9.0 : parsed;
                    double shown = nowF ? asC * 9.0 / 5.0 + 32.0 : asC;
                    warnBox.Text = ((int)Math.Round(shown)).ToString(CultureInfo.InvariantCulture);
                }
                unitF = nowF;
                if (warnLabel != null) warnLabel.Text = "Count as hot at (" + (unitF ? "F" : "C") + "):";
            };

            // Start at login. The unzipped build owns this (a Run-key entry, applied with OK); in a Store
            // build Windows owns it, so there is a link to the page where it is switched instead.
            CheckBox startupBox = null;
            if (IsPackaged())
            {
                var startupLink = new LinkLabel { Text = "Start with Windows: change in Windows Settings", Left = 15, Top = 305, Width = 330, Height = 20 };
                startupLink.LinkClicked += (s, e) =>
                {
                    try { Process.Start("ms-settings:startupapps"); }
                    catch { }
                };
                form.Controls.Add(startupLink);
            }
            else
            {
                startupBox = new CheckBox { Text = "Start with Windows", Left = 15, Top = 303, Width = 330, Height = 22, Checked = GetRunAtLogin() };
                form.Controls.Add(startupBox);
            }

            // The hint, which turns into the message when OK finds something wrong (see FormClosing below).
            var hint = new Label
            {
                Name = "hint",
                Text = "Amber and red only trigger under load, below these percentages of the CPU's rated clock. \"Hot\" affects the hints and the HOT warning only.",
                Left = 15, Top = 333, Width = 330, Height = 56, ForeColor = SystemColors.GrayText
            };
            form.Controls.Add(hint);

            var okButton = new Button { Text = "OK", Left = 177, Top = 398, Width = 80, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 265, Top = 398, Width = 80, DialogResult = DialogResult.Cancel };
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
                bool shownF = TempF;
                TempF = unitF;   // SettingsProblem judges the hot box in the unit the dialog is showing
                string problem;
                try { problem = SettingsProblem(intervalBox.Text, warnBox.Text, gateBox.Text, amberBox.Text, redBox.Text, out field); }
                finally { TempF = shownF; }
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
            bool appF = TempF;
            TempF = unitF;   // the unit chosen in the dialog takes effect only now, on OK
            double warnMin = TempF ? 86 : 30, warnMax = TempF ? 248 : 120;
            bool ok = TryNum(intervalBox.Text.Trim(), 1, 60, out interval) &&
                      TryNum(warnBox.Text.Trim(), warnMin, warnMax, out warn) &&
                      TryNum(gateBox.Text.Trim(), 5, 80, out gate) &&
                      TryNum(amberBox.Text.Trim(), 10, 100, out amber) &&
                      TryNum(redBox.Text.Trim(), 10, 100, out red);

            if (!ok || red > amber)
            {
                TempF = appF;
                return;
            }

            IntervalMs = (int)(interval * 1000);
            WarnC = DisplayToC(warn); GateUtil = gate; AmberPct = amber; RedPct = red;
            IconShows = showMap[showBox.SelectedIndex];
            _timer.Interval = IntervalMs;
            if (startupBox != null) SetRunAtLogin(startupBox.Checked);
            // Started with options: the command line described this run and settings.ini was not read,
            // so writing it now would replace the saved settings with this run's.
            if (!_anyCli) SaveSettings();
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

    // Two lines of text (the bottom one optional) filling an icon at the shell's current
    // tray slot size. Rendering at this size (not 32x32 to be downsampled) keeps the digits -
    // especially a decimal point - crisp.
    private static Icon RenderIcon(string top, string bottom, Tone topTone, Tone bottomTone, bool light)
    {
        int S = Math.Max(16, Math.Min(48, SystemInformation.SmallIconSize.Width));
        Color topColor = ToneColor(topTone, light), bottomColor = ToneColor(bottomTone, light);
        // The halo is the opposite of the text: dark on a dark taskbar, light on a light one.
        Color halo = light ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(190, 15, 15, 15);
        using (var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // Margins and gap scale with the icon so the two-line layout stays balanced at any DPI.
                float mx = S / 32f, pad = S / 32f * 2f, gap = S / 32f * 1.5f;
                if (bottom == null)
                {
                    DrawFit(g, top, topColor, halo, new RectangleF(mx, pad, S - 2 * mx, S - 2 * pad));
                }
                else
                {
                    float lineH = (S - 2 * (S / 32f * 0.75f) - gap) / 2f;
                    float y0 = S / 32f * 0.75f;
                    DrawFit(g, top, topColor, halo, new RectangleF(mx, y0, S - 2 * mx, lineH));
                    DrawFit(g, bottom, bottomColor, halo, new RectangleF(mx, y0 + lineH + gap, S - 2 * mx, lineH));
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
    private static GraphicsPath TightText(string text)
    {
        var path = new GraphicsPath();
        FontStyle style = FontStyle.Regular;
        FontFamily family;
        try { family = new FontFamily("Segoe UI Semibold"); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); style = FontStyle.Bold; }
        using (family)
        {
            float x = 0;
            foreach (char ch in text)
            {
                using (var glyph = new GraphicsPath())
                {
                    glyph.AddString(ch.ToString(), family, (int)style, 100f, new PointF(0, 0), StringFormat.GenericTypographic);
                    RectangleF cb = glyph.GetBounds();
                    if (cb.Width <= 0) continue;
                    using (var m = new Matrix())
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

    // Draws text as an outline path scaled to fill the box, with a thin halo so it reads on any
    // taskbar. Ink-tight packing keeps the decimal point visible even at small icon sizes.
    private static void DrawFit(Graphics g, string text, Color color, Color halo, RectangleF box)
    {
        if (string.IsNullOrEmpty(text)) return;
        using (var path = TightText(text))
        {
            RectangleF b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            RectangleF r = b;
            if (text.Length == 2 && (char.IsDigit(text[0]) || text[0] == '<') && char.IsDigit(text[1]))
                using (var rp = TightText("88")) r = rp.GetBounds();
            float fitW = box.Width / r.Width, fitH = box.Height / r.Height;
            float sy = Math.Min(fitH, fitW / MinSqueeze);
            float sx = Math.Min(Math.Min(sy, fitW), box.Width / b.Width);
            using (var m = new Matrix())
            {
                m.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
                m.Scale(sx, sy);
                m.Translate(-(b.X + b.Width / 2f), -(r.Y + r.Height / 2f));
                path.Transform(m);
            }
            float stroke = Math.Max(1.25f, box.Height * 0.08f);
            if (halo.A > 0)
            {
                using (var pen = new Pen(halo, stroke))
                {
                    pen.LineJoin = LineJoin.Round;
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
