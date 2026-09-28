// Pedal Gain Multi N — Meter window (v1.6)
//
// A separate, resizable window with large vertical meters for every channel
// plus the master pair. Opened from the METERS button on the machine's
// parameter panel; one window per machine.
//
//   • Reads its own meter slot (GUI message v4, slot 1), so it never steals
//     peaks from the parameter panel (slot 0, v3) — the panel is unchanged.
//   • Each meter shows RMS (solid, 300 ms integration) with the peak above it
//     (translucent), a 3 s peak-hold line, a clip light and numeric
//     peak-hold / RMS readouts. Master meters show TRUE PEAK. Clip lights
//     go out 2 s after the last over.
//   • PRE/POST switch for the channel meters; click anywhere on the meters (or
//     Clear) to reset holds and clip lights.
//   • Closes itself when its machine is deleted or the song changes.
//   • Paired with a pre-v1.6 machine it shows a notice instead of meters.
//
// Everything is drawn in one custom-rendered element for speed.
//
// v1.6.2 visual pass: neutral clip boxes when off, scale ticks drawn over the
// bars, tidier/colour-coded readouts, a distinct MASTER section, a tinted
// headroom zone above 0 dBFS, no crowded -50 label, M/S badges and dimming
// for channels not heard on the master, and redraw at the display rate.
// MeterScale (below) is shared with the parameter panel so both use the same
// scale shape and colours.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMultiN
{
    public sealed class MeterWindow : Window
    {
        // ── One window per machine ────────────────────────────────────────────
        static readonly Dictionary<IMachine, MeterWindow> open = new Dictionary<IMachine, MeterWindow>();

        public static void ShowFor(IMachine machine)
        {
            if (machine == null) return;

            MeterWindow w;
            if (open.TryGetValue(machine, out w))
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                return;
            }

            w = new MeterWindow(machine);
            open[machine] = w;
            w.Show();
        }

        // ── GUI message v4 (must match PedalGainMultiN.cpp) ───────────────────
        const int GUIMSG_GET_METERS_EX = 2;
        const int GUI_PROTOCOL_EX      = 4;
        const int SLOT                 = 1;      // 0 = parameter panel; 1 = this window
        const int MaxChannels          = 24;

        static readonly byte[] Request = BuildRequest();
        static byte[] BuildRequest()
        {
            var b = new byte[8];
            BitConverter.GetBytes(GUIMSG_GET_METERS_EX).CopyTo(b, 0);
            BitConverter.GetBytes(SLOT).CopyTo(b, 4);
            return b;
        }

        readonly IMachine        machine;
        readonly MeterBridge     bridge;
        bool running;             // subscribed to CompositionTarget.Rendering
        readonly TextBlock       prePostText;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double lastTick;

        bool everOk;              // at least one good reply received
        bool discardNext = true;  // first reply may hold peaks from long ago
        int  failures;            // consecutive failed polls after a good one
        IBuzz buzz;               // for "song changed" notifications

        MeterWindow(IMachine machine)
        {
            this.machine = machine;

            Title         = MachineName() + " — Meters";
            Width         = Math.Max(280, 70 + (DefaultChannels(machine) + 2) * 34);
            Height        = 380;
            MinWidth      = 250;   // buttons + three-line legend always fit
            MinHeight     = 220;
            ShowInTaskbar = false;
            Background    = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Keep the window with Buzz: owned by Buzz's main window, so it stays
            // above it and minimises with it. Buzz's main window is native, so
            // the owner is set through the HWND.
            try
            {
                var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (main != IntPtr.Zero)
                    new WindowInteropHelper(this).Owner = main;
            }
            catch { /* stand-alone window is fine too */ }

            // ── Toolbar ──
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(8, 6, 8, 2)
            };

            prePostText = MakeButtonText("PRE");
            var prePost = MakeButton(prePostText,
                "Channel meters: PRE = raw input, POST = after Mono, Mute, Volume and Pan", () =>
                {
                    bridge.PostFader = !bridge.PostFader;
                    prePostText.Text = bridge.PostFader ? "POST" : "PRE";
                    bridge.ResetChannels();
                });
            bar.Children.Add(prePost);

            var clear = MakeButton(MakeButtonText("CLEAR"), "Clear all peak holds and clip lights",
                                   () => bridge.ResetAll());
            clear.Margin = new Thickness(6, 0, 0, 0);
            bar.Children.Add(clear);

            // Legend: three short lines so it fits beside the buttons without
            // the window having to be widened.
            bar.Children.Add(new TextBlock
            {
                Text              = "solid = RMS (300 ms)\ntranslucent = peak\nmaster = true peak",
                Foreground        = new SolidColorBrush(Color.FromRgb(130, 130, 140)),
                FontFamily        = new FontFamily("Consolas"),
                FontSize          = 9,
                LineHeight        = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(10, 0, 0, 0)
            });

            bridge = new MeterBridge();

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(bridge);
            Content = dock;

            // ── Polling + redraw at the display rate (capped near 60 fps) ──
            CompositionTarget.Rendering += OnFrame;
            running = true;

            // ── Lifetime: close with the machine or the song ──
            try
            {
                if (machine.Graph != null)
                {
                    machine.Graph.MachineRemoved += OnMachineRemoved;
                    buzz = machine.Graph.Buzz;
                    if (buzz != null) buzz.PropertyChanged += OnBuzzPropertyChanged;
                }
            }
            catch { /* poll failures still close the window */ }

            Closed += (_, __) =>
            {
                Stop();
                open.Remove(machine);
                try
                {
                    if (machine.Graph != null) machine.Graph.MachineRemoved -= OnMachineRemoved;
                    if (buzz != null) buzz.PropertyChanged -= OnBuzzPropertyChanged;
                }
                catch { }
            };
        }

        // Solo/Mute track parameters, for the M/S badges and dimming.
        IParameter soloParam, muteParam;

        void ReadChannelStates(int n)
        {
            try
            {
                if (soloParam == null || muteParam == null)
                    foreach (var g in machine.ParameterGroups)
                        if (g != null && g.Type == ParameterGroupType.Track && g.Parameters != null)
                            foreach (var p in g.Parameters)
                            {
                                if (p?.Name == "Solo") soloParam = p;
                                if (p?.Name == "Mute") muteParam = p;
                            }

                for (int i = 0; i < n && i < MaxChannels; i++)
                {
                    bridge.Soloed[i] = soloParam != null && soloParam.GetValue(i) != 0;
                    bridge.Muted[i]  = muteParam != null && muteParam.GetValue(i) != 0;
                }
            }
            catch { /* badges are cosmetic */ }
        }

        string MachineName()
        {
            try { return string.IsNullOrEmpty(machine.Name) ? "Pedal Gain Multi N" : machine.Name; }
            catch { return "Pedal Gain Multi N"; }
        }

        static int DefaultChannels(IMachine m)
        {
            try
            {
                foreach (var g in m.ParameterGroups)
                    if (g != null && g.Type == ParameterGroupType.Track)
                        return Math.Max(1, Math.Min(MaxChannels, g.TrackCount));
            }
            catch { }
            return 6;
        }

        // CompositionTarget.Rendering is a static event: always unsubscribe,
        // or the window would be kept alive after closing.
        void Stop()
        {
            if (!running) return;
            CompositionTarget.Rendering -= OnFrame;
            running = false;
        }

        void OnFrame(object sender, EventArgs e)
        {
            if (clock.Elapsed.TotalSeconds - lastTick < 1.0 / 62) return;   // ~60 fps cap
            Poll();
        }

        void OnMachineRemoved(IMachine m)
        {
            if (ReferenceEquals(m, machine)) { Stop(); Close(); }
        }

        void OnBuzzPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Song") { Stop(); Close(); }
        }

        // ── Poll the machine and feed the bridge ──────────────────────────────
        void Poll()
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt  = now - lastTick;
            lastTick = now;
            if (dt < 0 || dt > 0.25) dt = 0.25;

            byte[] r = null;
            try { r = machine.SendGUIMessage(Request); }
            catch { r = null; }

            bool good = r != null && r.Length >= 8 && BitConverter.ToInt32(r, 0) == GUI_PROTOCOL_EX;
            int  n    = good ? BitConverter.ToInt32(r, 4) : 0;
            good = good && n >= 1 && n <= MaxChannels && r.Length >= 8 + 16 * n + 16;

            if (!good)
            {
                if (everOk)
                {
                    // Machine gone (or unreachable) — give it ~1 s, then close.
                    if (++failures > 60) { Stop(); Close(); }
                    bridge.Feed(null, 0, dt);
                }
                else
                {
                    bridge.Notice = "This meter window needs the Pedal Gain Multi N machine v1.6 or later.\n" +
                                    "Rebuild and deploy the native machine DLL.";
                    bridge.InvalidateVisual();
                }
                return;
            }

            everOk   = true;
            failures = 0;
            bridge.Notice = null;
            ReadChannelStates(n);

            if (discardNext) { discardNext = false; bridge.SetChannelCount(n); return; }

            bridge.Feed(r, n, dt);
        }

        // ── Toolbar helpers ───────────────────────────────────────────────────
        static TextBlock MakeButtonText(string text) => new TextBlock
        {
            Text                = text,
            FontFamily          = new FontFamily("Consolas"),
            FontSize            = 10,
            FontWeight          = FontWeights.Bold,
            Foreground          = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };

        static Border MakeButton(TextBlock content, string tooltip, Action onClick)
        {
            var b = new Border
            {
                MinWidth        = 48,
                Height          = 18,
                Padding         = new Thickness(6, 0, 6, 0),
                Background      = new SolidColorBrush(Color.FromRgb(52, 52, 58)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(90, 90, 98)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = tooltip,
                Child           = content
            };
            b.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
            return b;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MeterScale — scale shape and colours shared by the meter window and the
    // parameter panel, so both always agree.
    // ══════════════════════════════════════════════════════════════════════════
    static class MeterScale
    {
        // dB → 0..1 height, expanded near the top like a console meter bridge.
        // The window's scale runs to +3 dBFS (headroom for overs).
        static readonly double[] Db   = { -60, -50,  -40,  -30,  -24,  -18,  -12,   -9,   -6,   -3,    0,   3 };
        static readonly double[] Fr   = { 0.0, 0.06, 0.14, 0.25, 0.33, 0.43, 0.56, 0.64, 0.73, 0.84, 0.95, 1.0 };

        public static double Frac(double db)
        {
            if (db <= Db[0]) return 0;
            if (db >= Db[Db.Length - 1]) return 1;
            for (int i = 1; i < Db.Length; i++)
                if (db <= Db[i])
                    return Fr[i - 1] + (db - Db[i - 1]) / (Db[i] - Db[i - 1]) * (Fr[i] - Fr[i - 1]);
            return 1;
        }

        // Same shape, but ending at 0 dBFS (the panel's bars stop at 0 dB,
        // with the clip light at the end).
        public static double PanelFrac(double db) => Math.Min(1.0, Frac(db) / Frac(0));

        // Zone colours: green below -12, yellow -12..-3, red above -3 dBFS.
        public static readonly Color Green  = Color.FromRgb( 60, 200,  90);
        public static readonly Color Yellow = Color.FromRgb(230, 200,  50);
        public static readonly Color Red    = Color.FromRgb(235,  60,  45);
        public const double YellowFromDb = -12, RedFromDb = -3;

        // Hard-edged zone gradient along a line from `from` (−∞ end) to `to` (top end),
        // in absolute coordinates, so a bar's colours depend on level, not on its length.
        public static LinearGradientBrush ZoneBrush(Point from, Point to, Func<double, double> frac, byte alpha = 255)
        {
            var b = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint  = from,
                EndPoint    = to
            };
            double fy = frac(YellowFromDb), fr = frac(RedFromDb);
            Color g = Color.FromArgb(alpha, Green.R,  Green.G,  Green.B);
            Color y = Color.FromArgb(alpha, Yellow.R, Yellow.G, Yellow.B);
            Color r = Color.FromArgb(alpha, Red.R,    Red.G,    Red.B);
            b.GradientStops.Add(new GradientStop(g, 0));
            b.GradientStops.Add(new GradientStop(g, fy));
            b.GradientStops.Add(new GradientStop(y, fy));
            b.GradientStops.Add(new GradientStop(y, fr));
            b.GradientStops.Add(new GradientStop(r, fr));
            b.GradientStops.Add(new GradientStop(r, 1));
            b.Freeze();
            return b;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MeterBridge — draws every meter in one OnRender pass.
    // ══════════════════════════════════════════════════════════════════════════
    sealed class MeterBridge : FrameworkElement
    {
        const int    MaxChannels   = 24;
        const double DB_MIN        = -60.0;
        const double FALL_DB_PER_S = 20.0 / 1.7;   // IEC 60268-18 style fall-back
        const double HOLD_SECONDS  = 3.0;
        const double RMS_TAU       = 0.300;        // RMS integration time constant
        const double CLIP_SECONDS  = 2.0;          // clip light goes out 2 s after the last over

        public bool   PostFader;
        public string Notice;

        // Channel state for badges / dimming (fed by the window each frame).
        public readonly bool[] Soloed = new bool[MaxChannels];
        public readonly bool[] Muted  = new bool[MaxChannels];

        int channels = 6;

        // Meter state: channels 0..23, then master L (24) and R (25).
        readonly MeterState[] meters = new MeterState[MaxChannels + 2];

        public MeterBridge()
        {
            for (int i = 0; i < meters.Length; i++) meters[i] = new MeterState();
            ClipToBounds = true;
            Cursor  = Cursors.Hand;
            ToolTip = "Click to clear peak holds and clip lights";
            MouseLeftButtonDown += (_, e) => { ResetAll(); e.Handled = true; };
        }

        public void SetChannelCount(int n)
        {
            if (n != channels) { channels = n; InvalidateVisual(); }
        }

        public void ResetAll()      { foreach (var m in meters) m.Reset(); InvalidateVisual(); }
        public void ResetChannels() { for (int i = 0; i < MaxChannels; i++) meters[i].Reset(); InvalidateVisual(); }

        // r == null → no data this tick (meters fall).
        public void Feed(byte[] r, int n, double dt)
        {
            if (r != null) SetChannelCount(n);

            int o = 8;
            for (int i = 0; i < channels; i++, o += 16)
            {
                float pk = 0, ms = 0;
                if (r != null)
                {
                    pk = BitConverter.ToSingle(r, PostFader ? o + 4  : o);
                    ms = BitConverter.ToSingle(r, PostFader ? o + 12 : o + 8);
                }
                meters[i].Update(pk, ms, dt);
            }

            float tpL = 0, tpR = 0, msL = 0, msR = 0;
            if (r != null)
            {
                tpL = BitConverter.ToSingle(r, o);
                tpR = BitConverter.ToSingle(r, o + 4);
                msL = BitConverter.ToSingle(r, o + 8);
                msR = BitConverter.ToSingle(r, o + 12);
            }
            meters[MaxChannels].Update(tpL, msL, dt);
            meters[MaxChannels + 1].Update(tpR, msR, dt);

            InvalidateVisual();
        }

        // ── Per-meter ballistics ──────────────────────────────────────────────
        sealed class MeterState
        {
            public double PeakDb = DB_MIN, HoldDb = DB_MIN, MeanSq;
            double holdAge, clipAge;
            public bool Clipped;

            public void Reset() { HoldDb = DB_MIN; holdAge = 0; clipAge = 0; Clipped = false; }

            public void Update(float peakLin, float meanSq, double dt)
            {
                double pk   = peakLin > 1e-6f ? 20.0 * Math.Log10(peakLin) : DB_MIN;
                double fall = FALL_DB_PER_S * dt;

                PeakDb = Math.Max(pk, Math.Max(PeakDb - fall, DB_MIN));

                if (pk >= HoldDb) { HoldDb = pk; holdAge = 0; }
                else if ((holdAge += dt) > HOLD_SECONDS)
                    HoldDb = Math.Max(HoldDb - fall, DB_MIN);

                if (peakLin >= 1.0f) { Clipped = true; clipAge = 0; }
                else if (Clipped && (clipAge += dt) > CLIP_SECONDS) Clipped = false;

                // Exponential RMS integration of the mean square (τ = 300 ms).
                double a = 1.0 - Math.Exp(-dt / RMS_TAU);
                MeanSq += (meanSq - MeanSq) * a;
                if (MeanSq < 1e-12) MeanSq = 0;
            }

            public double RmsDb => MeanSq > 1e-12 ? 10.0 * Math.Log10(MeanSq) : DB_MIN;
        }

        // Scale ticks; -50 keeps its tick but not its label (too close to -60).
        static readonly int[] TickDb   = { 0, -3, -6, -9, -12, -18, -24, -30, -40, -50, -60 };
        static bool Labelled(int db) => db != -50;

        public static double Frac(double db) => MeterScale.Frac(db);

        // ── Drawing resources ─────────────────────────────────────────────────
        static readonly Brush TrackBrush   = Frozen(new SolidColorBrush(Color.FromRgb(38, 38, 44)));
        static readonly Brush MasterPanel  = Frozen(new SolidColorBrush(Color.FromRgb(33, 33, 40)));
        static readonly Brush TextBrush    = Frozen(new SolidColorBrush(Color.FromRgb(200, 200, 205)));
        static readonly Brush DimBrush     = Frozen(new SolidColorBrush(Color.FromRgb(120, 120, 130)));
        static readonly Brush FaintBrush   = Frozen(new SolidColorBrush(Color.FromRgb( 78,  78,  88)));
        static readonly Brush AmberBrush   = Frozen(new SolidColorBrush(Color.FromRgb(235, 180,  60)));
        static readonly Brush ClipOn       = Frozen(new SolidColorBrush(Color.FromRgb(235,  45,  35)));
        static readonly Brush ClipOff      = Frozen(new SolidColorBrush(Color.FromRgb( 46,  46,  52)));
        static readonly Pen   ClipOffPen   = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb( 90, 235,  60,  45)), 1));
        static readonly Brush HeadroomTint = Frozen(new SolidColorBrush(Color.FromArgb( 34, 235,  60,  45)));
        static readonly Brush DimOverlay   = Frozen(new SolidColorBrush(Color.FromArgb(150,  24,  24,  28)));
        static readonly Brush HoldBrush    = Frozen(new SolidColorBrush(Colors.White));
        static readonly Brush MuteBadge    = Frozen(new SolidColorBrush(Color.FromRgb(215,  55,  45)));
        static readonly Brush SoloBadge    = Frozen(new SolidColorBrush(Color.FromRgb(225, 175,  40)));
        static readonly Brush BadgeText    = Frozen(new SolidColorBrush(Color.FromRgb( 20,  20,  25)));
        static readonly Pen   TickPen      = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb( 48, 255, 255, 255)), 1));
        static readonly Pen   ZeroPen      = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(160, 255,  90,  80)), 1));
        static readonly Pen   DividerPen   = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb( 70, 255, 255, 255)), 1));
        static readonly Typeface Mono     = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface MonoBold = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold,   FontStretches.Normal);

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen   FrozenPen(Pen p) { p.Freeze(); return p; }

        LinearGradientBrush zoneBrush, zoneBrushDim;
        double zoneTop = -1, zoneBottom = -1;

        void EnsureZoneBrushes(double top, double bottom)
        {
            if (zoneBrush != null && top == zoneTop && bottom == zoneBottom) return;
            zoneTop = top; zoneBottom = bottom;
            zoneBrush    = MeterScale.ZoneBrush(new Point(0, bottom), new Point(0, top), MeterScale.Frac, 255);
            zoneBrushDim = MeterScale.ZoneBrush(new Point(0, bottom), new Point(0, top), MeterScale.Frac, 110);
        }

        FormattedText Text(string s, double size, Brush brush, bool bold = false)
        {
            double ppd = 1.0;
            try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     bold ? MonoBold : Mono, size, brush, ppd);
        }

        static string Db(double db) =>
            db <= DB_MIN + 0.5 ? "-∞" : (db > 0.05 ? "+" + db.ToString("F1", CultureInfo.InvariantCulture)
                                                   : db.ToString("F1", CultureInfo.InvariantCulture));

        // Is channel c heard on the master? (not muted, and not cut by someone else's solo)
        bool Audible(int c)
        {
            bool anySolo = false;
            for (int i = 0; i < channels; i++) if (Soloed[i]) { anySolo = true; break; }
            return !Muted[c] && (!anySolo || Soloed[c]);
        }

        // ── Render ────────────────────────────────────────────────────────────
        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));   // hit-testable

            if (Notice != null)
            {
                dc.DrawText(Text(Notice, 12, TextBrush), new Point(12, 12));
                return;
            }

            const double scaleW = 34, gap = 16, pad = 8;
            const double captionH = 12, readH = 28, clipH = 7, labelH = 15, badgeH = 12;

            int cols = channels + 2;
            double avail = W - pad * 2 - scaleW - gap;
            double colW  = Math.Max(10, Math.Min(52, avail / cols));
            double barW  = Math.Max(6, colW - 4);

            double top    = pad + captionH + readH + clipH + 3;
            double bottom = H - pad - labelH - badgeH;
            if (bottom - top < 40) return;
            double hgt = bottom - top;
            double y0  = bottom - Frac(0) * hgt;       // 0 dBFS line

            EnsureZoneBrushes(top, bottom);

            double chanLeft   = pad + scaleW;
            double masterLeft = chanLeft + channels * colW + gap;
            double right      = masterLeft + 2 * colW;
            Func<int, double> colX = c => (c < channels ? chanLeft + c * colW : masterLeft + (c - channels) * colW) + (colW - barW) / 2;

            // 1. Master section: panel, divider, caption.
            dc.DrawRectangle(MasterPanel, null, new Rect(masterLeft - 4, pad, 2 * colW + 8, H - 2 * pad));
            double divX = Math.Round(masterLeft - gap / 2) + 0.5;
            dc.DrawLine(DividerPen, new Point(divX, pad), new Point(divX, H - pad));
            var cap = Text("MASTER", 9, DimBrush, bold: true);
            dc.DrawText(cap, new Point(masterLeft + colW - cap.Width / 2, pad));

            // 2. Tracks + headroom tint.
            for (int c = 0; c < cols; c++)
            {
                double x = colX(c);
                dc.DrawRectangle(TrackBrush,   null, new Rect(x, top, barW, hgt));
                dc.DrawRectangle(HeadroomTint, null, new Rect(x, top, barW, Math.Max(0, y0 - top)));
            }

            // 3. Bars: peak (translucent) then RMS (solid); dim channels not heard on the master.
            for (int c = 0; c < cols; c++)
            {
                bool isMaster = c >= channels;
                var  m = meters[isMaster ? MaxChannels + (c - channels) : c];
                double x = colX(c);

                double yPk  = bottom - Frac(m.PeakDb) * hgt;
                double yRms = bottom - Frac(m.RmsDb)  * hgt;
                if (m.PeakDb > DB_MIN + 0.1) dc.DrawRectangle(zoneBrushDim, null, new Rect(x, yPk,  barW, bottom - yPk));
                if (m.RmsDb  > DB_MIN + 0.1) dc.DrawRectangle(zoneBrush,    null, new Rect(x, yRms, barW, bottom - yRms));

                if (!isMaster && !Audible(c))
                    dc.DrawRectangle(DimOverlay, null, new Rect(x, top, barW, hgt));
            }

            // 4. Scale ticks, drawn faintly OVER the bars; labels at the left.
            foreach (int t in TickDb)
            {
                double y = Math.Round(bottom - Frac(t) * hgt) + 0.5;
                dc.DrawLine(t == 0 ? ZeroPen : TickPen, new Point(chanLeft - 4, y), new Point(right, y));
                if (!Labelled(t)) continue;
                var ft = Text(t.ToString(CultureInfo.InvariantCulture), 10, DimBrush);
                dc.DrawText(ft, new Point(chanLeft - 7 - ft.Width, y - ft.Height / 2));
            }

            bool narrow = colW < 30;
            double fontPk  = narrow ? 8.5 : 10.5;
            double fontRms = narrow ? 7.5 : 9;

            // 5. Hold lines, clip lights, readouts, labels, badges.
            for (int c = 0; c < cols; c++)
            {
                bool isMaster = c >= channels;
                int  mi = isMaster ? MaxChannels + (c - channels) : c;
                var  m  = meters[mi];
                double x  = colX(c);
                double cx = x + barW / 2;

                if (m.HoldDb > DB_MIN + 0.5)
                {
                    double yH = Math.Round(bottom - Frac(m.HoldDb) * hgt);
                    dc.DrawRectangle(HoldBrush, null, new Rect(x, yH - 1, barW, 2));
                }

                // Clip light: neutral box with a faint red edge when off.
                var clipRect = new Rect(x + 0.5, top - clipH - 2 + 0.5, barW - 1, clipH - 1);
                if (m.Clipped) dc.DrawRectangle(ClipOn,  null,       clipRect);
                else           dc.DrawRectangle(ClipOff, ClipOffPen, clipRect);

                // Readouts: held peak (colour-coded) and current RMS (blank in silence).
                double ry = pad + captionH;
                bool silent = m.HoldDb <= DB_MIN + 0.5;
                Brush pkBrush = m.Clipped ? ClipOn : silent ? FaintBrush : m.HoldDb > -6 ? AmberBrush : TextBrush;
                var pkText = Text(Db(m.HoldDb), fontPk, pkBrush, bold: !silent);
                dc.DrawText(pkText, new Point(cx - pkText.Width / 2, ry));
                if (m.RmsDb > DB_MIN + 0.5)
                {
                    var rmsText = Text(Db(m.RmsDb), fontRms, DimBrush);
                    dc.DrawText(rmsText, new Point(cx - rmsText.Width / 2, ry + readH / 2 + 1));
                }

                // Label
                string label = isMaster ? (narrow ? "" : "Out ") + (mi == MaxChannels ? "L" : "R")
                                        : (narrow ? c.ToString(CultureInfo.InvariantCulture) : "In " + c);
                var lt = Text(label, narrow ? 9 : 10, isMaster ? TextBrush : (Audible(c) ? DimBrush : FaintBrush), bold: isMaster);
                dc.DrawText(lt, new Point(cx - lt.Width / 2, bottom + 3));

                // M / S badges under the label (channels only).
                if (!isMaster && (Muted[c] || Soloed[c]))
                {
                    const double bw = 11, bh = 10;
                    int count = (Muted[c] ? 1 : 0) + (Soloed[c] ? 1 : 0);
                    double bx = cx - (count * bw + (count - 1) * 2) / 2;
                    double by = bottom + labelH + 1;
                    if (Muted[c])  { DrawBadge(dc, "M", MuteBadge, bx, by, bw, bh); bx += bw + 2; }
                    if (Soloed[c]) { DrawBadge(dc, "S", SoloBadge, bx, by, bw, bh); }
                }
            }
        }

        void DrawBadge(DrawingContext dc, string letter, Brush bg, double x, double y, double w, double h)
        {
            dc.DrawRectangle(bg, null, new Rect(x, y, w, h));
            var t = Text(letter, 8, BadgeText, bold: true);
            dc.DrawText(t, new Point(x + w / 2 - t.Width / 2, y + h / 2 - t.Height / 2));
        }
    }
}
