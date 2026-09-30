// SysMonitor - a tiny native Windows system-tray monitor.
// Shows CPU %, GPU %, RAM used/total and live network speed.
// Targets .NET Framework 4.8 (present on all Win10/11) -> single native .exe, no runtime install.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysMonitor
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "SysMonitor_SingleInstance_9f2a", out created))
            {
                if (!created) return; // already running
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                    Application.Run();
            }
        }
    }

    // ---- Live metric values shared between sampler and UI ----
    class GpuStat
    {
        public string Name;
        public float Pct;
    }

    class Stats
    {
        public float Cpu;       // %
        public List<GpuStat> Gpus = new List<GpuStat>();
        public double RamUsedGB;
        public double RamTotalGB;
        public float RamPct;    // %
        public double DownMbps;
        public double UpMbps;
        public string NetName = "Network";
        public double DiskReadMBps;
        public double DiskWriteMBps;
    }

    class TrayApp : IDisposable
    {
        readonly NotifyIcon _tray;
        readonly System.Windows.Forms.Timer _timer;
        readonly Flyout _flyout;
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _startupItem;

        // Samplers
        PerformanceCounter _cpu;
        PerformanceCounter _diskRead, _diskWrite;

        class GpuGroup
        {
            public string Name;
            public List<PerformanceCounter> Counters = new List<PerformanceCounter>();
        }
        List<GpuGroup> _gpuGroups;
        DateTime _gpuRefresh = DateTime.MinValue;

        long _prevRecv, _prevSent;
        DateTime _prevNet = DateTime.MinValue;

        readonly Stats _stats = new Stats();
        Icon _lastIcon;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "SysMonitor";

        public TrayApp()
        {
            try { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpu.NextValue(); }
            catch { _cpu = null; }

            try { _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total"); _diskRead.NextValue(); }
            catch { _diskRead = null; }
            try { _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total"); _diskWrite.NextValue(); }
            catch { _diskWrite = null; }

            _flyout = new Flyout(_stats);

            _startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem("Show monitor", null, (s, e) => ShowFlyout()));
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Exit()));
            _menu.Opening += (s, e) => _startupItem.Checked = IsStartupEnabled();

            _tray = new NotifyIcon
            {
                Text = "System Monitor",
                Visible = true,
                ContextMenuStrip = _menu,
                Icon = SystemIcons.Application
            };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Sample();
            _timer.Start();
            Sample();

            ShowFlyout(); // stay visible on screen from launch; only the panel's "x" hides it
        }

        // ---------- Sampling ----------
        void Sample()
        {
            try { _stats.Cpu = _cpu != null ? Clamp(_cpu.NextValue()) : 0; } catch { _stats.Cpu = 0; }
            SampleGpus();
            ReadRam();
            ReadNet();
            ReadDisk();

            UpdateTrayIcon();
            // NotifyIcon.Text has a hard 63-char limit in .NET Framework (throws if exceeded) -
            // keep the hover tooltip short; the click-open panel already shows full detail.
            string gpuText = _stats.Gpus.Count > 0
                ? string.Join(" ", _stats.Gpus.Select(g => g.Name + " " + g.Pct.ToString("0") + "%"))
                : "n/a";
            string tip = string.Format("CPU {0:0}%  GPU {1}  RAM {2:0}%",
                _stats.Cpu, gpuText, _stats.RamPct);
            try { _tray.Text = Truncate(tip, 63); } catch { try { _tray.Text = "System Monitor"; } catch { } }

            _flyout.SyncLayout();
            if (_flyout.Visible) _flyout.Refresh();
        }

        void ReadDisk()
        {
            try { _stats.DiskReadMBps = _diskRead != null ? _diskRead.NextValue() / 1048576.0 : 0; }
            catch { _stats.DiskReadMBps = 0; }
            try { _stats.DiskWriteMBps = _diskWrite != null ? _diskWrite.NextValue() / 1048576.0 : 0; }
            catch { _stats.DiskWriteMBps = 0; }
        }

        void ReadRam()
        {
            var m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m))
            {
                double total = m.ullTotalPhys / 1073741824.0;
                double avail = m.ullAvailPhys / 1073741824.0;
                _stats.RamTotalGB = total;
                _stats.RamUsedGB = total - avail;
                _stats.RamPct = total > 0 ? (float)((total - avail) / total * 100.0) : 0;
            }
        }

        void ReadNet()
        {
            long recv = 0, sent = 0;
            string wifi = null, active = null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var t = ni.NetworkInterfaceType;
                    if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                    var s = ni.GetIPv4Statistics();
                    recv += s.BytesReceived;
                    sent += s.BytesSent;
                    if (t == NetworkInterfaceType.Wireless80211 && wifi == null) wifi = ni.Name;
                    if (active == null) active = ni.Name;
                }
            }
            catch { }
            _stats.NetName = wifi ?? active ?? "Network";

            var now = DateTime.UtcNow;
            if (_prevNet != DateTime.MinValue)
            {
                double secs = (now - _prevNet).TotalSeconds;
                if (secs > 0)
                {
                    _stats.DownMbps = Math.Max(0, (recv - _prevRecv)) * 8.0 / 1e6 / secs;
                    _stats.UpMbps = Math.Max(0, (sent - _prevSent)) * 8.0 / 1e6 / secs;
                }
            }
            _prevRecv = recv; _prevSent = sent; _prevNet = now;
        }

        // Reports each physical GPU separately (matches Task Manager), instead of summing all GPUs
        // into one misleading number. GPU Engine perf-counter instances are grouped by the adapter's
        // LUID (embedded in the instance name), resolved to a friendly name via the D3DKMT adapter
        // API (the same low-level mechanism Task Manager itself uses). Ghost/stale LUIDs left behind
        // by driver resets (which no longer resolve to a live adapter) are silently dropped.
        void SampleGpus()
        {
            try
            {
                if (_gpuGroups == null || (DateTime.Now - _gpuRefresh).TotalSeconds > 8)
                    RebuildGpuGroups();

                var result = new List<GpuStat>();
                foreach (var grp in _gpuGroups)
                {
                    float sum = 0;
                    foreach (var c in grp.Counters) { try { sum += c.NextValue(); } catch { } }
                    result.Add(new GpuStat { Name = grp.Name, Pct = Clamp(sum) });
                }
                _stats.Gpus = result;
            }
            catch { _stats.Gpus = new List<GpuStat>(); }
        }

        static readonly Regex LuidRegex = new Regex(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)", RegexOptions.Compiled);
        static readonly Dictionary<string, string> _luidNameCache = new Dictionary<string, string>();

        void RebuildGpuGroups()
        {
            var byKey = new Dictionary<string, GpuGroup>();
            var ordered = new List<GpuGroup>();
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var m = LuidRegex.Match(inst);
                    if (!m.Success) continue;
                    string key = m.Groups[1].Value.ToUpperInvariant() + "_" + m.Groups[2].Value.ToUpperInvariant();

                    GpuGroup grp;
                    if (!byKey.TryGetValue(key, out grp))
                    {
                        string name = ResolveGpuName(key,
                            int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber),
                            uint.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber));
                        if (name == null) continue; // stale/ghost adapter - skip entirely, don't show a mystery "GPU" row
                        grp = new GpuGroup { Name = name };
                        byKey[key] = grp;
                        ordered.Add(grp);
                    }
                    try { grp.Counters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst)); }
                    catch { }
                }
            }
            catch { }

            // stable, readable order
            ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            if (_gpuGroups != null)
                foreach (var g in _gpuGroups) foreach (var c in g.Counters) { try { c.Dispose(); } catch { } }
            _gpuGroups = ordered;
            _gpuRefresh = DateTime.Now;
            foreach (var g in _gpuGroups) foreach (var c in g.Counters) { try { c.NextValue(); } catch { } }
        }

        // Resolves a LUID to a friendly GPU name via D3DKMT; returns null if the adapter no longer
        // exists (stale performance-counter instance left over from a driver reset). Cached per LUID.
        static string ResolveGpuName(string cacheKey, int high, uint low)
        {
            string cached;
            if (_luidNameCache.TryGetValue(cacheKey, out cached)) return cached;

            string result = null;
            var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = new LUID { HighPart = high, LowPart = low } };
            if (D3DKMTOpenAdapterFromLuid(ref open) == 0)
            {
                try
                {
                    int size = Marshal.SizeOf(typeof(D3DKMT_ADAPTERREGISTRYINFO));
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        var q = new D3DKMT_QUERYADAPTERINFO
                        {
                            hAdapter = open.hAdapter,
                            Type = KMTQAITYPE_ADAPTERREGISTRYINFO,
                            pPrivateDriverData = buf,
                            PrivateDriverDataSize = (uint)size
                        };
                        if (D3DKMTQueryAdapterInfo(ref q) == 0)
                        {
                            var info = (D3DKMT_ADAPTERREGISTRYINFO)Marshal.PtrToStructure(buf, typeof(D3DKMT_ADAPTERREGISTRYINFO));
                            result = ShortGpuName(info.AdapterString);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
            _luidNameCache[cacheKey] = result; // cache the negative result too, so ghost LUIDs aren't retried every 8s
            return result;
        }

        static string ShortGpuName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "GPU";
            string n = fullName;
            if (n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return "NVIDIA";
            if (n.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) return "Intel";
            if (n.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0) return "AMD";
            int sp = n.IndexOf(' ');
            return sp > 0 ? n.Substring(0, sp) : n;
        }

        // ---------- D3DKMT interop (minimal: resolve a LUID to its adapter's friendly name) ----------
        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public uint Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct D3DKMT_ADAPTERREGISTRYINFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
        }

        const uint KMTQAITYPE_ADAPTERREGISTRYINFO = 8;

        [DllImport("gdi32.dll")]
        static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID pData);
        [DllImport("gdi32.dll")]
        static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER pData);
        [DllImport("gdi32.dll")]
        static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO pData);

        // ---------- Tray icon rendering ----------
        void UpdateTrayIcon()
        {
            int size = 32;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                Color c = LoadColor(_stats.Cpu);
                using (var bg = new SolidBrush(Color.FromArgb(235, 28, 30, 38)))
                    g.FillRectangle(bg, 0, 0, size, size);
                // bottom load bar
                using (var bar = new SolidBrush(c))
                    g.FillRectangle(bar, 0, size - 4, (int)(size * _stats.Cpu / 100f), 4);

                string txt = ((int)Math.Round(_stats.Cpu)).ToString();
                float fs = txt.Length >= 3 ? 15f : 19f;
                using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(Color.White))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(txt, f, br, new RectangleF(0, -2, size, size - 3), sf);

                IntPtr h = bmp.GetHicon();
                try
                {
                    var icon = Icon.FromHandle(h);
                    _tray.Icon = icon;
                    if (_lastIcon != null) _lastIcon.Dispose();
                    _lastIcon = icon;
                }
                finally { DestroyIcon(h); }
            }
        }

        // ---------- Flyout control ----------
        void ToggleFlyout() { if (_flyout.Visible) _flyout.Hide(); else ShowFlyout(); }
        void ShowFlyout() { _flyout.Refresh(); _flyout.ShowNearTray(); }

        // ---------- Startup registry ----------
        bool IsStartupEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }
        void ToggleStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (IsStartupEnabled()) k.DeleteValue(RunName, false);
                    else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception ex) { MessageBox.Show("Could not change startup setting:\n" + ex.Message); }
        }

        void Exit() { _tray.Visible = false; Application.Exit(); }

        // ---------- helpers ----------
        static float Clamp(float v) { return v < 0 ? 0 : (v > 100 ? 100 : v); }
        static string Truncate(string s, int max) { return s.Length <= max ? s : s.Substring(0, max); }
        public static Color LoadColor(float pct)
        {
            if (pct >= 85) return Color.FromArgb(239, 68, 68);   // red
            if (pct >= 60) return Color.FromArgb(245, 158, 11);  // amber
            return Color.FromArgb(34, 197, 94);                  // green
        }

        public void Dispose()
        {
            _timer.Dispose();
            _tray.Dispose();
            _flyout.Dispose();
            if (_cpu != null) _cpu.Dispose();
            if (_diskRead != null) _diskRead.Dispose();
            if (_diskWrite != null) _diskWrite.Dispose();
            if (_gpuGroups != null) foreach (var g in _gpuGroups) foreach (var c in g.Counters) c.Dispose();
            if (_lastIcon != null) _lastIcon.Dispose();
        }

        // ---------- P/Invoke ----------
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }
    }

    // Borderless dark flyout drawn above the tray.
    class Flyout : Form
    {
        readonly Stats _s;
        static readonly Color Bg = Color.FromArgb(24, 26, 34);
        static readonly Color Card = Color.FromArgb(34, 37, 48);
        static readonly Color TextDim = Color.FromArgb(150, 156, 170);
        static readonly Color TextMain = Color.FromArgb(236, 238, 244);

        Rectangle _closeBtn;
        bool _dragging;
        Point _dragOffset;

        public Flyout(Stats s)
        {
            _s = s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Width = 280;
            Height = 250;
            DoubleBuffered = true;
            BackColor = Bg;
            _closeBtn = new Rectangle(Width - 30, 10, 20, 20);

            MouseDown += Flyout_MouseDown;
            MouseMove += Flyout_MouseMove;
            MouseUp += Flyout_MouseUp;
        }

        // Stays on-screen always; only hides via the close ("x") button.
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE - never steals focus from other apps
                return cp;
            }
        }

        public void ShowNearTray()
        {
            if (!Visible)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Left = wa.Right - Width - 12;
                Top = wa.Bottom - Height - 12;
            }
            Show();
        }

        void Flyout_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_closeBtn.Contains(e.Location)) { Hide(); return; }
            _dragging = true;
            _dragOffset = e.Location;
        }
        void Flyout_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                var p = PointToScreen(e.Location);
                Left = p.X - _dragOffset.X;
                Top = p.Y - _dragOffset.Y;
            }
        }
        void Flyout_MouseUp(object sender, MouseEventArgs e) { _dragging = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, ClientRectangle);
            using (var border = new Pen(Color.FromArgb(60, 64, 78)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            using (var title = new Font("Segoe UI Semibold", 11f))
            using (var tb = new SolidBrush(TextMain))
                g.DrawString("System Monitor", title, tb, 16, 12);

            // close ("x") button
            using (var closeBg = new SolidBrush(Color.FromArgb(50, 54, 66)))
                g.FillEllipse(closeBg, _closeBtn);
            using (var closePen = new Pen(TextDim, 1.6f))
            {
                int m = 6;
                g.DrawLine(closePen, _closeBtn.Left + m, _closeBtn.Top + m, _closeBtn.Right - m, _closeBtn.Bottom - m);
                g.DrawLine(closePen, _closeBtn.Right - m, _closeBtn.Top + m, _closeBtn.Left + m, _closeBtn.Bottom - m);
            }

            int y = 46;
            DrawMetric(g, "CPU", _s.Cpu, string.Format("{0:0}%", _s.Cpu), ref y);

            if (_s.Gpus.Count == 0)
            {
                DrawMetric(g, "GPU", 0, "n/a", ref y);
            }
            else
            {
                foreach (var gpu in _s.Gpus)
                    DrawMetric(g, "GPU (" + gpu.Name + ")", gpu.Pct, string.Format("{0:0}%", gpu.Pct), ref y);
            }

            DrawMetric(g, "RAM", _s.RamPct,
                string.Format("{0:0.0} / {1:0.0} GB", _s.RamUsedGB, _s.RamTotalGB), ref y);

            DrawTextRow(g, _s.NetName, string.Format("↓ {0:0.0}   ↑ {1:0.0} Mbps", _s.DownMbps, _s.UpMbps), ref y);
            DrawTextRow(g, "Disk", string.Format("R {0:0.0}   W {1:0.0} MB/s", _s.DiskReadMBps, _s.DiskWriteMBps), ref y);
        }

        // Recomputes the panel height to fit the current number of metric rows (GPU count
        // varies per machine). Called before painting, never from inside OnPaint itself.
        public void SyncLayout()
        {
            int rows = 1 /*CPU*/ + Math.Max(1, _s.Gpus.Count) /*GPU(s)*/ + 1 /*RAM*/;
            int height = 46 + rows * 44 + 2 * 30 + 18; // title offset + bar rows + net/disk text rows + bottom pad
            if (Height != height) Height = height;
            _closeBtn = new Rectangle(Width - 30, 10, 20, 20);
        }

        void DrawTextRow(Graphics g, string label, string value, ref int y)
        {
            using (var lf = new Font("Segoe UI", 9.5f))
            using (var vf = new Font("Segoe UI Semibold", 9.5f))
            using (var ld = new SolidBrush(TextDim))
            using (var lv = new SolidBrush(TextMain))
            {
                g.DrawString(label, lf, ld, 16, y + 2);
                var sz = g.MeasureString(value, vf);
                g.DrawString(value, vf, lv, Width - 16 - sz.Width, y + 2);
            }
            y += 30;
        }

        void DrawMetric(Graphics g, string label, float pct, string value, ref int y)
        {
            using (var lf = new Font("Segoe UI", 9.5f))
            using (var vf = new Font("Segoe UI Semibold", 9.5f))
            using (var ld = new SolidBrush(TextDim))
            using (var lv = new SolidBrush(TextMain))
            {
                g.DrawString(label, lf, ld, 16, y);
                var sz = g.MeasureString(value, vf);
                g.DrawString(value, vf, lv, Width - 16 - sz.Width, y);
            }
            int barY = y + 22, barX = 16, barW = Width - 32, barH = 8;
            using (var track = new SolidBrush(Card))
                FillRound(g, track, barX, barY, barW, barH, 4);
            int w = (int)(barW * Math.Max(0, Math.Min(100, pct)) / 100f);
            if (w > 0)
                using (var fill = new SolidBrush(TrayApp.LoadColor(pct)))
                    FillRound(g, fill, barX, barY, Math.Max(w, 4), barH, 4);
            y += 44;
        }

        static void FillRound(Graphics g, Brush b, int x, int y, int w, int h, int r)
        {
            using (var p = new GraphicsPath())
            {
                int d = r * 2;
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }
    }
}
