// Pedal Gain Multi N — Machine GUI (code-only WPF, no XAML)
//
// Companion GUI DLL for the NATIVE machine "Pedal Gain Multi N" (Buzz 1503).
// Buzz loads "<machine dll name>.GUI.dll" from the gear folder and
// instantiates the exported IMachineGUIFactory.
//
// Differences from the managed Pedal Gain Multi GUI:
//   • There is no managed machine object to read — meters come from the C++
//     side via IMachine.SendGUIMessage (→ CMachineInterfaceEx::HandleGUIMessage),
//     polled on the same 33 ms timer. Protocol is defined in PedalGainMultiN.cpp.
//   • Button states are read from the parameters themselves (IParameter.GetValue)
//     instead of machine.Solo[] / machine.InMute[].
//   • Targets net48 (Buzz 1503's .NET Framework 4 CLR): no Math.Clamp /
//     MathF, which don't exist on .NET Framework.
//   • The timer restarts on Loaded, so reopening the parameter window works.
//   • v1.2: channels are TRACKS (1..24). Solo/Mute are track parameters
//     (value per track); rows follow the track count reported in every
//     meter reply (protocol v2). [−]/[+] in the IN header remove/add a track.
//   • v1.3: per-row Volume fader and Pan (balance) control, master Gain
//     fader on the OUT row. All bound to parameters; the machine smooths
//     every level change.
//   • v1.3.1: per-row MO (Mono) toggle.
//   • v1.4: VOL and master faders are dB faders with a console taper
//     (unity at 75 % of travel); wheel steps are 1 dB / 0.5 dB.
//   • v1.5: meters get exact peaks since the last poll (protocol v3); IEC-
//     style fall (≈ 11.8 dB/s) timed by the clock; 3 s peak hold; latching
//     clip lights (click any meter to clear); PRE/POST channel metering;
//     the master shows true peak.
//   • v1.6: METERS button on the OUT row opens the separate meter window
//     (PedalGainMultiNMeterWindow.cs). The panel itself is unchanged.
//   • v1.6.1: on opening, widens Buzz's parameter window if it is too narrow
//     to show the whole panel (Buzz 1503 opens it at a fixed width).
//     Clip lights go out 2 s after the last over instead of latching.
//
// Renders a compact meter stack at the top of the parameters window:
//
//   IN
//   [M][S] 1 ████████████░░░░░░░░  -12.3 dB
//   [M][S] 2 ██████░░░░░░░░░░░░░░  -18.1 dB
//   [M][S] 3 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [M][S] 4 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [M][S] 5 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [M][S] 6 ░░░░░░░░░░░░░░░░░░░░      -∞
//   ──────────────────────────────
//   [M]    OUT
//          L ██████████░░░░░░░░░░  -14.2 dB
//          R ██████████░░░░░░░░░░  -14.5 dB
//                     -48 -24 -12 -6 -3 0
//
// [M] mute buttons (red when on) toggle the Mute / Mute{N} switch
// parameters; [S] solo buttons (amber when on) toggle Solo{N} switch
// parameters. The per-input [M]s align vertically with the output [M]
// — all mutes stack in column 0. Per-input mutes ramp at the same rate
// as the output mute (set by the global Inertia parameter, 0–500 ms,
// default 25) and follow the DAW convention of mute beating solo.
//
// All parameter writes go through IParameter.SetValue so the GUI, params
// window, pattern editor, undo and save/load stay in sync.
//
// Meter ballistics run on the audio thread (see mi::MultiWork in
// PedalGainMultiN.cpp); this GUI polls the values on a 33 ms timer.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMultiN
{
    public class PedalGainMultiNGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalGainMultiNGUI();
    }

    public class PedalGainMultiNGUI : UserControl, IMachineGUI
    {
        // Must match MaxChannels / DefaultChannels in PedalGainMultiN.cpp.
        // The live count (= track count) comes from the machine with every
        // meter reply; the rows are rebuilt when it changes.
        const int MaxChannels     = 24;
        const int DefaultChannels = 6;
        int shownChannels;

        // ── GUI message protocol (must match PedalGainMultiN.cpp) ────────────
        const int GUIMSG_GET_METERS    = 1;
        const int GUI_PROTOCOL_VERSION = 3;   // v3: peaks since last poll, pre+post per channel, master true peak
        static readonly byte[] MeterRequest = BitConverter.GetBytes(GUIMSG_GET_METERS);

        IMachine imachine;

        // Cached parameter handles — looked up once per Machine assignment.
        // Solo/Mute are TRACK parameters: one IParameter, value per track
        // (track k = channel k). Master Mute is a global parameter.
        IParameterGroup trackGroup;
        IParameter soloParam, muteParam, volumeParam, panParam, monoParam, masterMuteParam, gainParam;

        // Per-row faders (rebuilt with the rows) + the master fader.
        readonly MiniFader[] volFaders = new MiniFader[MaxChannels];
        readonly MiniFader[] panFaders = new MiniFader[MaxChannels];
        MiniFader masterFader;

        // Peaks since the previous poll from the native side (1.0 == 0 dBFS).
        readonly float[] prePeak  = new float[MaxChannels];
        readonly float[] postPeak = new float[MaxChannels];
        float tpPeakL, tpPeakR;
        bool  meterLinkOk = true;
        bool  discardNextPoll = true;    // first reply after (re)open may hold stale peaks
        bool  postFader;                 // channel meters: false = PRE, true = POST
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double lastTickSeconds;

        public IMachine Machine
        {
            get => imachine;
            set
            {
                imachine = value;
                discardNextPoll = true;
                CacheParameters();
            }
        }

        readonly DispatcherTimer timer;

        // Per-input widgets
        readonly Border[]    inMuteButtons = new Border[MaxChannels];
        readonly TextBlock[] inMuteLabels  = new TextBlock[MaxChannels];
        readonly Border[]    soloButtons   = new Border[MaxChannels];
        readonly TextBlock[] soloLabels    = new TextBlock[MaxChannels];
        readonly Border[]    monoButtons   = new Border[MaxChannels];
        readonly TextBlock[] monoLabels    = new TextBlock[MaxChannels];
        readonly MeterView[] inMeters      = new MeterView[MaxChannels];

        // Output (master, true-peak) meters
        MeterView outMeterL, outMeterR;

        // PRE/POST switch label in the IN header
        TextBlock prePostLabel;

        // Mute widgets (single button gating the whole output)
        Border    muteButton;
        TextBlock muteLabel;

        // ── Layout constants ─────────────────────────────────────────────────
        const float MUTE_W    = 14f;   // per-input M button column
        const float SOLO_W    = 14f;   // per-input S button column
        const float LABEL_W   = 18f;   // room for two-digit channel numbers
        const float W         = 200f;
        const float READOUT_W = 46f;
        const float FADER_W   = 70f;   // channel Volume / master Gain fader column
        const float PAN_W     = 38f;   // channel Pan column
        const float MONO_W    = 26f;   // channel Mono toggle column
        const float H         = 9f;
        const float DB_MIN    = -60f;

        // Ballistics: IEC 60268-18 style digital peak meter — instant attack,
        // fall 20 dB in ~1.7 s (≈ 11.8 dB/s). Peak hold: 3 s, then falls at
        // the same rate. Clip lights come on at >= 0 dBFS and go out 2 s after
        // the last over (or immediately when a meter is clicked).
        const double FALL_DB_PER_S = 20.0 / 1.7;
        const double HOLD_SECONDS  = 3.0;
        const double CLIP_SECONDS  = 2.0;

        // ── Cached, frozen brushes ───────────────────────────────────────────
        static readonly Brush TrackBrush     = Freeze(new SolidColorBrush(Color.FromRgb(34,  34,  38)));
        static readonly Brush PeakBrush      = Freeze(new SolidColorBrush(Colors.White));
        static readonly Brush ClipBrush      = Freeze(new SolidColorBrush(Color.FromRgb(235,  45,  35)));
        static readonly Brush LabelColor     = Freeze(new SolidColorBrush(Color.FromRgb(170, 170, 180)));
        static readonly Brush ScaleColor     = Freeze(new SolidColorBrush(Color.FromRgb(95,  95, 105)));
        static readonly Brush SectionColor   = Freeze(new SolidColorBrush(Color.FromRgb(120, 120, 135)));
        static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(60,  60,  68)));

        // Solo button — off state uses the track background so it reads as a
        // "recessed" button; on state lights up amber with dark text.
        static readonly Brush SoloOffBg   = Freeze(new SolidColorBrush(Color.FromRgb(44,  44,  50)));
        static readonly Brush SoloOffFg   = Freeze(new SolidColorBrush(Color.FromRgb(130, 130, 140)));
        static readonly Brush SoloOnBg    = Freeze(new SolidColorBrush(Color.FromRgb(235, 185,  40)));
        static readonly Brush SoloOnFg    = Freeze(new SolidColorBrush(Color.FromRgb(20,  20,  25)));
        static readonly Brush SoloBorder  = Freeze(new SolidColorBrush(Color.FromRgb(70,  70,  80)));

        // Mute uses the same "off" recessed look but red when active —
        // standard DAW colour convention (solo = yellow, mute = red).
        static readonly Brush MuteOnBg    = Freeze(new SolidColorBrush(Color.FromRgb(215,  55,  45)));
        static readonly Brush MuteOnFg    = Freeze(new SolidColorBrush(Color.FromRgb(245, 245, 245)));

        // Mono — teal when on, so it can't be confused with mute (red) or solo (amber).
        static readonly Brush MonoOnBg    = Freeze(new SolidColorBrush(Color.FromRgb( 40, 160, 160)));
        static readonly Brush MonoOnFg    = Freeze(new SolidColorBrush(Color.FromRgb( 20,  20,  25)));

        static readonly FontFamily Mono = new FontFamily("Consolas");

        // Fader fill — cool blue so it doesn't read as a level meter.
        static readonly Brush FaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(70, 130, 200)));

        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        // Green → yellow → red gradient with break points at -12 dB and -3 dB.
        static LinearGradientBrush LevelGradient()
        {
            float yp = Norm(-12f), rp = Norm(-3f);
            var b = new LinearGradientBrush
                { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            b.GradientStops.Add(new GradientStop(Color.FromRgb( 30, 175,  55), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb( 30, 175,  55), yp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(205, 185,   0), yp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(205, 185,   0), rp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(215,  45,  30), rp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(215,  45,  30), 1.0));
            b.Freeze();
            return b;
        }

        // ── Construction ─────────────────────────────────────────────────────
        public PedalGainMultiNGUI()
        {
            BuildUI();
            timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)   // ~30 fps
            };
            timer.Tick += Tick;
            Loaded   += (_, __) =>
            {
                meterLinkOk = true; discardNextPoll = true; timer.Start();
                // After Buzz has finished sizing its parameter window, widen it
                // if the panel doesn't fit (Buzz 1503 opens it at a fixed width).
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(FitHostWindow));
            };
            Unloaded += (_, __) => timer.Stop();
        }

        // Widen the hosting parameter window so the whole panel is visible.
        // Only ever grows the window; a window that is already wide enough
        // (or that the user has made wider) is left alone.
        void FitHostWindow()
        {
            try
            {
                var win = Window.GetWindow(this);
                var client = win?.Content as FrameworkElement;
                if (win == null || client == null) return;

                // Natural (unclipped) width of the panel.
                Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double need = DesiredSize.Width;
                InvalidateMeasure();   // let the normal layout pass run again

                // Where the panel's right edge would be, in the window's client area.
                Point left  = TranslatePoint(new Point(0, 0), client);
                double right = left.X + need;
                double spare = Math.Min(Math.Max(left.X, 4), 16);   // keep a margin like the left one

                double deficit = right + spare - client.ActualWidth;
                if (deficit > 0.5)
                    win.Width = win.ActualWidth + Math.Ceiling(deficit);
            }
            catch { /* sizing is cosmetic — never break the panel over it */ }
        }

        // ── Layout helpers ───────────────────────────────────────────────────

        // Every row shares this 5-column grid:
        //   col 0 — per-input mute button [M] (empty on output L/R and scale rows;
        //           the output mute button also lives here on its own header row)
        //   col 1 — per-input solo button [S] (empty everywhere except input rows)
        //   col 2 — label
        //   col 3 — bar area (W px, the pixel-aligned meter column)
        //   col 4 — dB readout
        static Grid MakeRowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MUTE_W)    });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SOLO_W)    });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LABEL_W)   });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W)         });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(READOUT_W) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FADER_W)   });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PAN_W)     });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MONO_W)    });
            return g;
        }

        void BuildUI()
        {
            var root = new StackPanel { Margin = new Thickness(6, 6, 6, 4) };
            var grad = LevelGradient();

            root.Children.Add(BuildInHeaderRow());

            // Input rows live in their own panel so they can be rebuilt when
            // the track (= channel) count changes.
            inRowsPanel = new StackPanel();
            root.Children.Add(inRowsPanel);
            levelBrush = grad;
            BuildInputRows(DefaultChannels);

            // Thin separator — spans the bar area only for visual symmetry.
            root.Children.Add(new Rectangle
            {
                Height              = 1,
                Fill                = SeparatorBrush,
                Margin              = new Thickness(MUTE_W + SOLO_W + LABEL_W, 5, READOUT_W + FADER_W + PAN_W + MONO_W, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            // OUT header row: [M] mute button + "OUT" label, aligned to the
            // same 4-column grid as the input rows so the mute button sits
            // directly under the column of solo buttons above.
            root.Children.Add(BuildOutHeaderRow());

            outMeterL = AddOutputRow(root, "L", grad);
            outMeterR = AddOutputRow(root, "R", grad);

            root.Children.Add(MakeScaleRow());

            Content  = root;
            MinWidth = MUTE_W + SOLO_W + LABEL_W + W + READOUT_W + FADER_W + PAN_W + MONO_W + 12;
        }

        StackPanel inRowsPanel;
        Brush      levelBrush;

        // (Re)build the per-input rows for `count` channels.
        void BuildInputRows(int count)
        {
            if (count < 1) count = 1;
            if (count > MaxChannels) count = MaxChannels;

            inRowsPanel.Children.Clear();
            for (int i = 0; i < count; i++)
            {
                var grid = MakeRowGrid();
                grid.Margin = new Thickness(0, 1, 0, 1);

                // Col 0 — per-input mute button.
                int track = i;
                var (mbtn, mlbl) = MakeToggleButton(
                    letter:    "M",
                    tooltip:   $"Mute In {i} on the master mix and its direct out (fade time = Inertia)",
                    onClick:   () => ToggleTrackParameter(muteParam, track),
                    onBg:      MuteOnBg,
                    onFg:      MuteOnFg);
                Grid.SetColumn(mbtn, 0);
                grid.Children.Add(mbtn);
                inMuteButtons[i] = mbtn;
                inMuteLabels[i]  = mlbl;

                // Col 1 — solo button.
                var (sbtn, slbl) = MakeToggleButton(
                    letter:    "S",
                    tooltip:   $"Solo In {i} on the master mix",
                    onClick:   () => ToggleTrackParameter(soloParam, track),
                    onBg:      SoloOnBg,
                    onFg:      SoloOnFg);
                Grid.SetColumn(sbtn, 1);
                grid.Children.Add(sbtn);
                soloButtons[i] = sbtn;
                soloLabels[i]  = slbl;

                // Col 2 — input number label.
                // 0-based: row i = track i = input plug In i (as Buzz numbers them).
                var num = RowLabel(i.ToString(), col: 2);
                num.ToolTip = $"In {i} (track {i}) — its direct out is output plug {i + 1} (Direct In {i}); plug 0 is the master mix";
                grid.Children.Add(num);

                // Col 3 — meter bar (+ hold line, clip light); col 4 — held-peak readout.
                var meter = BuildMeter(levelBrush);
                Grid.SetColumn(meter.Canvas, 3);
                grid.Children.Add(meter.Canvas);
                Grid.SetColumn(meter.Readout, 4);
                grid.Children.Add(meter.Readout);
                inMeters[i] = meter;

                // Col 5 — channel fader (Volume), col 6 — balance (Pan).
                var vol = LevelFader(FADER_W - 8, H, () => volumeParam, track);
                Grid.SetColumn(vol.Root, 5);
                grid.Children.Add(vol.Root);
                volFaders[i] = vol;

                var pan = new MiniFader(PAN_W - 8, H, bipolar: true, fill: FaderBrush,
                                        param: () => panParam, track: track);
                Grid.SetColumn(pan.Root, 6);
                grid.Children.Add(pan.Root);
                panFaders[i] = pan;

                // Col 7 — Mono toggle.
                var (obtn, olbl) = MakeToggleButton(
                    letter:  "MO",
                    tooltip: $"Mono: sum In {i} to (L+R)/2 before its fader and pan",
                    onClick: () => ToggleTrackParameter(monoParam, track),
                    onBg:    MonoOnBg,
                    onFg:    MonoOnFg,
                    width:   MONO_W - 6);
                obtn.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(obtn, 7);
                grid.Children.Add(obtn);
                monoButtons[i] = obtn;
                monoLabels[i]  = olbl;

                inRowsPanel.Children.Add(grid);
            }

            shownChannels = count;
        }

        // IN header: "IN" label + [−] [+] channel buttons at the right of the bar column.
        UIElement BuildInHeaderRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            var hdr = SectionHeader("IN");
            Grid.SetColumn(hdr, 0);
            Grid.SetColumnSpan(hdr, 3);
            grid.Children.Add(hdr);

            var buttons = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(MakeActionButton("−", "Remove the last channel (track)", () => ChangeChannelCount(-1)));
            buttons.Children.Add(MakeActionButton("+", "Add a channel (track), up to 24",  () => ChangeChannelCount(+1)));
            Grid.SetColumn(buttons, 3);
            grid.Children.Add(buttons);

            // Col 4 — PRE/POST switch for the channel meters.
            prePostLabel = new TextBlock
            {
                Text                = "PRE",
                FontFamily          = Mono,
                FontSize            = 8,
                FontWeight          = FontWeights.Bold,
                Foreground          = SoloOffFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            var prePost = new Border
            {
                Width           = READOUT_W - 10,
                Height          = H + 2,
                Margin          = new Thickness(6, 0, 0, 0),
                Background      = SoloOffBg,
                BorderBrush     = SoloBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = "Channel meters: PRE = raw input, POST = after Mono, Mute, Volume and Pan",
                Child           = prePostLabel
            };
            prePost.MouseLeftButtonDown += (_, e) =>
            {
                postFader = !postFader;
                prePostLabel.Text = postFader ? "POST" : "PRE";
                for (int i = 0; i < MaxChannels; i++) inMeters[i]?.Reset();
                e.Handled = true;
            };
            Grid.SetColumn(prePost, 4);
            grid.Children.Add(prePost);

            var volHdr = SectionHeader("VOL");
            ((FrameworkElement)volHdr).Margin = new Thickness(6, 1, 0, 1);
            Grid.SetColumn(volHdr, 5);
            grid.Children.Add(volHdr);

            var panHdr = SectionHeader("PAN");
            ((FrameworkElement)panHdr).Margin = new Thickness(6, 1, 0, 1);
            Grid.SetColumn(panHdr, 6);
            grid.Children.Add(panHdr);

            var monoHdr = SectionHeader("MONO");
            ((FrameworkElement)monoHdr).Margin = new Thickness(4, 1, 0, 1);
            Grid.SetColumn(monoHdr, 7);
            grid.Children.Add(monoHdr);

            return grid;
        }

        UIElement MakeActionButton(string glyph, string tooltip, Action onClick)
        {
            var btn = new Border
            {
                Width           = SOLO_W,
                Height          = H + 2,
                Margin          = new Thickness(2, 0, 0, 0),
                Background      = SoloOffBg,
                BorderBrush     = SoloBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = tooltip,
                Child           = new TextBlock
                {
                    Text                = glyph,
                    FontFamily          = Mono,
                    FontSize            = 10,
                    FontWeight          = FontWeights.Bold,
                    Foreground          = SoloOffFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            };
            btn.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
            return btn;
        }

        static UIElement SectionHeader(string text)
        {
            return new TextBlock
            {
                Text       = text,
                FontFamily = Mono,
                FontSize   = 8,
                Foreground = SectionColor,
                Margin     = new Thickness(0, 1, 0, 1)
            };
        }

        // OUT section header row: mute button in col 0 + "OUT" label in col 2.
        // The mute button stays in col 0 so it aligns vertically with the
        // column of per-input [M] buttons above — visually all the mutes
        // stack in the same column.
        UIElement BuildOutHeaderRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            var (btn, btnLbl) = MakeToggleButton(
                letter:    "M",
                tooltip:   "Mute the master output (fade time = Inertia). Direct outs are not affected.",
                onClick:   () => ToggleGlobalParameter(masterMuteParam),
                onBg:      MuteOnBg,
                onFg:      MuteOnFg);
            Grid.SetColumn(btn, 0);
            grid.Children.Add(btn);
            muteButton = btn;
            muteLabel  = btnLbl;

            // The "OUT" label lives in col 2, styled to match the IN header.
            var hdr = new TextBlock
            {
                Text              = "OUT (MASTER, TRUE PEAK)",
                FontFamily        = Mono,
                FontSize          = 8,
                Foreground        = SectionColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hdr, 2);
            grid.Children.Add(hdr);

            // Col 5 — master fader (Gain, output 0 only).
            masterFader = LevelFader(FADER_W - 8, H, () => gainParam, 0);
            Grid.SetColumn(masterFader.Root, 5);
            grid.Children.Add(masterFader.Root);

            // Cols 6–7 — open the separate meter window (v1.6).
            var metersText = new TextBlock
            {
                Text                = "METERS",
                FontFamily          = Mono,
                FontSize            = 8,
                FontWeight          = FontWeights.Bold,
                Foreground          = SoloOffFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            var meters = new Border
            {
                Height          = H + 2,
                Margin          = new Thickness(6, 0, 2, 0),
                Background      = SoloOffBg,
                BorderBrush     = SoloBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = "Open the large meter window",
                Child           = metersText
            };
            meters.MouseLeftButtonDown += (_, e) => { MeterWindow.ShowFor(imachine); e.Handled = true; };
            Grid.SetColumn(meters, 6);
            Grid.SetColumnSpan(meters, 2);
            grid.Children.Add(meters);

            return grid;
        }

        static TextBlock RowLabel(string text, int col)
        {
            var lbl = new TextBlock
            {
                Text              = text,
                FontFamily        = Mono,
                FontSize          = 10,
                Foreground        = LabelColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, col);
            return lbl;
        }

        static TextBlock RowReadout() => new TextBlock
        {
            Text              = "-∞",
            Width             = READOUT_W - 2,
            TextAlignment     = TextAlignment.Right,
            FontFamily        = Mono,
            FontSize          = 10,
            Foreground        = LabelColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0)
        };

        // Build one meter: bar canvas (track, level bar, hold line, clip light)
        // plus its readout. Clicking any meter clears every hold and clip light.
        MeterView BuildMeter(Brush fill)
        {
            var canvas = new Canvas
            {
                Width = W, Height = H, ClipToBounds = true,
                Cursor = Cursors.Hand,
                ToolTip = "Click to clear peak holds and clip lights"
            };

            canvas.Children.Add(new Rectangle
            {
                Width = W, Height = H, Fill = TrackBrush, RadiusX = 1.5, RadiusY = 1.5
            });

            var bar = new Rectangle
            {
                Width = 0, Height = H, Fill = fill, RadiusX = 1.5, RadiusY = 1.5
            };
            Canvas.SetLeft(bar, 0);
            Canvas.SetTop(bar, 0);
            canvas.Children.Add(bar);

            var hold = new Rectangle { Width = 2, Height = H, Fill = PeakBrush, Opacity = 0 };
            Canvas.SetTop(hold, 0);
            canvas.Children.Add(hold);

            // Clip light: a red cap at the 0 dBFS end of the bar.
            var clip = new Rectangle { Width = 4, Height = H, Fill = ClipBrush, Opacity = 0 };
            Canvas.SetLeft(clip, W - 4);
            Canvas.SetTop(clip, 0);
            canvas.Children.Add(clip);

            canvas.MouseLeftButtonDown += (_, e) => { ResetAllMeters(); e.Handled = true; };

            return new MeterView(canvas, bar, hold, clip, RowReadout());
        }

        void ResetAllMeters()
        {
            for (int i = 0; i < MaxChannels; i++)
                inMeters[i]?.Reset();
            outMeterL?.Reset();
            outMeterR?.Reset();
        }

        // Output L / R rows — same grid so the bar column aligns with the inputs.
        MeterView AddOutputRow(Panel parent, string label, Brush fill)
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            var lbl = RowLabel(label, col: 2);
            lbl.ToolTip = "Master output, true peak (4x oversampled)";
            grid.Children.Add(lbl);

            var meter = BuildMeter(fill);
            Grid.SetColumn(meter.Canvas, 3);
            grid.Children.Add(meter.Canvas);
            Grid.SetColumn(meter.Readout, 4);
            grid.Children.Add(meter.Readout);

            parent.Children.Add(grid);
            return meter;
        }

        // ── MeterView — one meter's widgets + ballistics ──────────────────────
        // Fed with the exact peak since the previous poll; handles fall-back,
        // peak hold and the clip light (off 2 s after the last over) using real elapsed time.
        sealed class MeterView
        {
            public readonly Canvas    Canvas;
            public readonly TextBlock Readout;
            readonly Rectangle bar, hold, clip;

            double levelDb = DB_MIN, holdDb = DB_MIN, holdAge, clipAge;
            bool   clipped;
            string shownText;
            bool   shownClip;

            public MeterView(Canvas canvas, Rectangle bar, Rectangle hold, Rectangle clip, TextBlock readout)
            {
                Canvas = canvas; this.bar = bar; this.hold = hold; this.clip = clip; Readout = readout;
            }

            public void Reset()
            {
                holdDb  = DB_MIN;
                holdAge = 0;
                clipAge = 0;
                clipped = false;
            }

            public void Update(float peakLin, double dt)
            {
                double pk = LinToDb(peakLin);
                double fall = FALL_DB_PER_S * dt;

                levelDb = Math.Max(pk, Math.Max(levelDb - fall, DB_MIN));

                if (pk >= holdDb) { holdDb = pk; holdAge = 0; }
                else if ((holdAge += dt) > HOLD_SECONDS)
                    holdDb = Math.Max(holdDb - fall, DB_MIN);

                // Clip light: on at an over, off CLIP_SECONDS after the last one.
                if (peakLin >= 1.0f) { clipped = true; clipAge = 0; }
                else if (clipped && (clipAge += dt) > CLIP_SECONDS) clipped = false;

                bar.Width = Clamp(Norm((float)levelDb) * W, 0f, W);
                Canvas.SetLeft(hold, Clamp(Norm((float)holdDb) * W - 1f, 0f, W - 2f));
                hold.Opacity = holdDb > DB_MIN + 0.5 ? 1.0 : 0.0;
                clip.Opacity = clipped ? 1.0 : 0.0;

                string text = FormatDb((float)holdDb);
                if (text != shownText) { Readout.Text = text; shownText = text; }
                if (clipped != shownClip)
                {
                    Readout.Foreground = clipped ? ClipBrush : LabelColor;
                    shownClip = clipped;
                }
            }
        }

        // Scale row re-uses MakeRowGrid so column 3 matches the bar widths
        // pixel-for-pixel. Cols 0 / 1 / 2 / 4 stay empty.
        UIElement MakeScaleRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 2, 0, 0);

            var canvas = new Canvas { Width = W, Height = 11 };
            Grid.SetColumn(canvas, 3);

            int[] marks = { -48, -36, -24, -12, -6, -3, 0 };
            foreach (int db in marks)
            {
                var t = new TextBlock
                {
                    Text       = db.ToString(),
                    FontFamily = Mono,
                    FontSize   = 8,
                    Foreground = ScaleColor
                };
                // Rough centering offset by text length.
                double halfW = db <= -10 ? 7 : db < 0 ? 5 : 2;
                double x     = Norm(db) * W - halfW;
                Canvas.SetLeft(t, x);
                canvas.Children.Add(t);
            }

            grid.Children.Add(canvas);
            return grid;
        }

        // ── Toggle buttons (used by both solo and mute) ──────────────────────

        (Border border, TextBlock label)
            MakeToggleButton(string letter, string tooltip, Action onClick, Brush onBg, Brush onFg,
                             double width = SOLO_W - 2)
        {
            var text = new TextBlock
            {
                Text                = letter,
                FontFamily          = Mono,
                FontSize            = 9,
                FontWeight          = FontWeights.Bold,
                Foreground          = SoloOffFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };

            var btn = new Border
            {
                // MUTE_W == SOLO_W, so the same button width works in both
                // columns. Two-pixel padding keeps the buttons visually
                // separated when they sit side-by-side in an input row.
                Width               = width,
                Height              = H,
                Background          = SoloOffBg,
                BorderBrush         = SoloBorder,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(2),
                Child               = text,
                Cursor              = Cursors.Hand,
                ToolTip             = tooltip,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                // Stash the on-colours on the button itself so the tick can
                // recolour without needing a parallel data structure.
                Tag                 = new ToggleColors(onBg, onFg)
            };

            btn.MouseLeftButtonDown += (_, e) =>
            {
                onClick();
                e.Handled = true;
            };

            return (btn, text);
        }

        // ── Fader taper (Gain / Volume) ──────────────────────────────────────
        // Parameter codes: 0 = -inf, c = (c - 161) × 0.5 dB, 181 = +10 dB.
        // Travel follows a console-style curve: unity at 75 %, -20 dB at 42 %,
        // -40 dB at 20 %, so the useful mixing range gets most of the fader.
        static class Taper
        {
            const int    UNITY = 161, MAX = 181;
            const double STEP_DB = 0.5, MIN_FRAC = 0.01;   // below MIN_FRAC → -inf

            static readonly double[] Db   = { -80, -60, -40, -30, -20, -10,  -5,    0,    5,   10 };
            static readonly double[] Frac = { 0.02, 0.08, 0.20, 0.30, 0.42, 0.60, 0.68, 0.75, 0.87, 1.00 };

            public static double ToFrac(int code)
            {
                if (code <= 0) return 0;
                if (code > MAX) code = MAX;
                double db = (code - UNITY) * STEP_DB;
                for (int i = 1; i < Db.Length; i++)
                    if (db <= Db[i])
                        return Frac[i - 1] + (db - Db[i - 1]) / (Db[i] - Db[i - 1]) * (Frac[i] - Frac[i - 1]);
                return 1;
            }

            public static int FromFrac(double f)
            {
                if (f < MIN_FRAC) return 0;
                double db;
                if (f <= Frac[0]) db = Db[0];
                else
                {
                    db = Db[Db.Length - 1];
                    for (int i = 1; i < Frac.Length; i++)
                        if (f <= Frac[i])
                        {
                            db = Db[i - 1] + (f - Frac[i - 1]) / (Frac[i] - Frac[i - 1]) * (Db[i] - Db[i - 1]);
                            break;
                        }
                }
                int code = UNITY + (int)Math.Round(db / STEP_DB);
                return code < 1 ? 1 : (code > MAX ? MAX : code);
            }
        }

        static MiniFader LevelFader(double width, double height, Func<IParameter> param, int track) =>
            new MiniFader(width, height, bipolar: false, fill: FaderBrush, param: param, track: track,
                          toFrac: Taper.ToFrac, fromFrac: Taper.FromFrac,
                          wheelStep: 2, fineStep: 1, hint: "wheel ±1 dB (Ctrl ±0.5 dB)");

        // ── MiniFader — compact horizontal fader bound to one parameter/track ──
        //
        //   drag            set value (the parameter is the source of truth)
        //   double-click    reset to the parameter's default (unity / centre)
        //   mouse wheel     ±5 steps, ±1 with Ctrl
        //   tooltip         the machine's own DescribeValue text (dB, L/C/R)
        //
        // Bipolar faders (Pan) fill from the centre towards the value.
        sealed class MiniFader
        {
            public readonly Canvas Root;

            readonly Func<IParameter> param;
            readonly int       track;
            readonly bool      bipolar;
            readonly double    width;
            readonly Func<int, double> toFrac;     // value → 0..1 travel (null = linear)
            readonly Func<double, int> fromFrac;   // 0..1 travel → value
            readonly int       wheelStep, fineStep;
            readonly string    hint;
            readonly Rectangle fill;
            readonly Rectangle defaultTick;
            int shownValue = int.MinValue;

            public MiniFader(double width, double height, bool bipolar, Brush fill,
                             Func<IParameter> param, int track,
                             Func<int, double> toFrac = null, Func<double, int> fromFrac = null,
                             int wheelStep = 5, int fineStep = 1,
                             string hint = "wheel ±5 (Ctrl ±1)")
            {
                this.width     = width;
                this.bipolar   = bipolar;
                this.param     = param;
                this.track     = track;
                this.toFrac    = toFrac;
                this.fromFrac  = fromFrac;
                this.wheelStep = wheelStep;
                this.fineStep  = fineStep;
                this.hint      = hint;

                Root = new Canvas
                {
                    Width               = width,
                    Height              = height,
                    Margin              = new Thickness(6, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Background          = TrackBrush,
                    ClipToBounds        = true,
                    Cursor              = Cursors.Hand
                };

                this.fill = new Rectangle { Height = height, Width = 0, Fill = fill };
                Root.Children.Add(this.fill);

                // Marks the default (unity for Volume/Gain, centre for Pan).
                defaultTick = new Rectangle { Width = 1, Height = height, Fill = ScaleColor };
                Root.Children.Add(defaultTick);

                Root.MouseLeftButtonDown += (_, e) =>
                {
                    var p = param();
                    if (p == null) return;
                    if (e.ClickCount == 2)
                        Set(p.DefValue);
                    else
                    {
                        Root.CaptureMouse();
                        SetFromX(e.GetPosition(Root).X);
                    }
                    e.Handled = true;
                };
                Root.MouseMove += (_, e) =>
                {
                    if (Root.IsMouseCaptured)
                        SetFromX(e.GetPosition(Root).X);
                };
                Root.MouseLeftButtonUp += (_, e) =>
                {
                    if (Root.IsMouseCaptured)
                        Root.ReleaseMouseCapture();
                };
                Root.MouseWheel += (_, e) =>
                {
                    var p = param();
                    if (p == null) return;
                    int step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? this.fineStep : this.wheelStep;
                    Set(p.GetValue(track) + (e.Delta > 0 ? step : -step));
                    e.Handled = true;
                };
            }

            void SetFromX(double x)
            {
                var p = param();
                if (p == null) return;
                double frac = x / width;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                Set(fromFrac != null
                    ? fromFrac(frac)
                    : p.MinValue + (int)Math.Round(frac * (p.MaxValue - p.MinValue)));
            }

            void Set(int v)
            {
                var p = param();
                if (p == null) return;
                if (v < p.MinValue) v = p.MinValue;
                if (v > p.MaxValue) v = p.MaxValue;
                if (v != p.GetValue(track))
                    p.SetValue(track, v);
                Refresh();
            }

            double XOf(IParameter p, int v) =>
                toFrac != null ? toFrac(v) * width
                : p.MaxValue > p.MinValue ? (double)(v - p.MinValue) / (p.MaxValue - p.MinValue) * width : 0;

            public void Refresh()
            {
                var p = param();
                if (p == null) return;

                int v = p.GetValue(track);
                if (v == shownValue) return;
                shownValue = v;

                double x    = XOf(p, v);
                double xDef = XOf(p, p.DefValue);

                if (bipolar)
                {
                    Canvas.SetLeft(fill, Math.Min(x, xDef));
                    fill.Width = Math.Max(Math.Abs(x - xDef), 1);
                }
                else
                {
                    Canvas.SetLeft(fill, 0);
                    fill.Width = x;
                }
                Canvas.SetLeft(defaultTick, Math.Min(xDef, width - 1));

                string desc = null;
                try { desc = p.DescribeValue(v); } catch { }
                Root.ToolTip = $"{p.Name}: {(string.IsNullOrEmpty(desc) ? v.ToString() : desc)}" +
                               "\nDrag to set · double-click to reset · " + hint;
            }
        }

        // Holds the per-button "on" colours so RefreshToggleVisual can recolour
        // any button uniformly. Off colours are shared (SoloOffBg / SoloOffFg).
        sealed class ToggleColors
        {
            public readonly Brush OnBg, OnFg;
            public ToggleColors(Brush onBg, Brush onFg) { OnBg = onBg; OnFg = onFg; }
        }

        static void RefreshToggleVisual(Border btn, TextBlock label, bool on)
        {
            var c = (ToggleColors)btn.Tag;
            btn.Background   = on ? c.OnBg : SoloOffBg;
            label.Foreground = on ? c.OnFg : SoloOffFg;
        }

        // Find a parameter by name in the group of the given type (null if absent).
        IParameter FindParameter(ParameterGroupType type, string name, out IParameterGroup group)
        {
            group = null;
            if (imachine?.ParameterGroups == null) return null;

            foreach (var pg in imachine.ParameterGroups)
            {
                if (pg?.Parameters == null || pg.Type != type) continue;
                foreach (var p in pg.Parameters)
                    if (p?.Name == name) { group = pg; return p; }
            }
            return null;
        }

        void CacheParameters()
        {
            IParameterGroup unused;
            soloParam       = FindParameter(ParameterGroupType.Track,  "Solo", out trackGroup);
            muteParam       = FindParameter(ParameterGroupType.Track,  "Mute", out unused);
            volumeParam     = FindParameter(ParameterGroupType.Track,  "Volume", out unused);
            panParam        = FindParameter(ParameterGroupType.Track,  "Pan", out unused);
            monoParam       = FindParameter(ParameterGroupType.Track,  "Mono", out unused);
            masterMuteParam = FindParameter(ParameterGroupType.Global, "Master Mute", out unused);
            gainParam       = FindParameter(ParameterGroupType.Global, "Gain", out unused);
            meterLinkOk     = true;
        }

        // Flip a switch through the host parameter API so the GUI, parameter
        // window, pattern editor, undo and save/load stay consistent — for a
        // native machine this becomes a control change delivered to Tick().
        static void ToggleTrackParameter(IParameter p, int track)
        {
            if (p == null) return;
            p.SetValue(track, p.GetValue(track) == 0 ? 1 : 0);
        }

        static void ToggleGlobalParameter(IParameter p) => ToggleTrackParameter(p, 0);

        static bool IsOn(IParameter p, int track) => p != null && p.GetValue(track) != 0;

        // Add/remove a channel = add/remove a track. The host then calls the
        // machine's SetNumTracks, which updates the plugs; the next meter
        // reply carries the new count and the rows are rebuilt.
        void ChangeChannelCount(int delta)
        {
            if (trackGroup == null) CacheParameters();
            if (trackGroup == null) return;

            int n = trackGroup.TrackCount + delta;
            if (n < 1 || n > MaxChannels) return;
            trackGroup.TrackCount = n;
        }

        // Ask the native machine for the current meter values. Any failure
        // (old DLL, machine being deleted, host without GUI messaging) just
        // freezes the meters; it never throws into the dispatcher.
        void PollMeters()
        {
            for (int i = 0; i < MaxChannels; i++) { prePeak[i] = 0; postPeak[i] = 0; }
            tpPeakL = tpPeakR = 0;

            if (imachine == null || !meterLinkOk) return;

            byte[] r;
            try { r = imachine.SendGUIMessage(MeterRequest); }
            catch { meterLinkOk = false; return; }

            if (r == null || r.Length < 8) return;
            if (BitConverter.ToInt32(r, 0) != GUI_PROTOCOL_VERSION) { meterLinkOk = false; return; }

            int channels = BitConverter.ToInt32(r, 4);
            if (channels < 1 || channels > MaxChannels) return;
            if (r.Length < 8 + 4 * (2 * channels + 2)) return;

            if (channels != shownChannels)
                BuildInputRows(channels);

            // Reading resets the machine's peaks. The first reply after the
            // window opens can hold peaks from long ago, so drop it.
            if (discardNextPoll) { discardNextPoll = false; return; }

            int o = 8;
            for (int i = 0; i < channels; i++, o += 8)
            {
                prePeak[i]  = BitConverter.ToSingle(r, o);
                postPeak[i] = BitConverter.ToSingle(r, o + 4);
            }
            tpPeakL = BitConverter.ToSingle(r, o);
            tpPeakR = BitConverter.ToSingle(r, o + 4);
        }

        // ── Meter math ───────────────────────────────────────────────────────

        // net48 has neither Math.Clamp nor MathF.
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        // Map a dBFS value to a 0→1 position (used by both bars and scale).
        static float Norm(float db) =>
            Clamp((db - DB_MIN) / -DB_MIN, 0f, 1f);

        static float LinToDb(float lin) =>
            lin < 1e-6f ? DB_MIN : 20f * (float)Math.Log10(lin);

        // Format a dB value for the meter readout. -∞ is shown when the
        // value is at or below the noise floor. Used to display the held
        // peak (the hold is tracked in dB, not linear).
        static string FormatDb(float db) =>
            db <= DB_MIN + 0.5f ? "-∞" : (db > 0.05f ? $"+{db:F1}" : $"{db:F1}");

        // ── Timer tick ───────────────────────────────────────────────────────
        void Tick(object sender, EventArgs e)
        {
            if (imachine == null) return;
            if (soloParam == null) CacheParameters();

            PollMeters();

            double now = clock.Elapsed.TotalSeconds;
            double dt  = now - lastTickSeconds;
            lastTickSeconds = now;
            if (dt < 0 || dt > 0.25) dt = 0.25;   // window hidden / timer stalled

            // Per-input meters + solo / mute button state.
            for (int i = 0; i < shownChannels; i++)
            {
                inMeters[i]?.Update(postFader ? postPeak[i] : prePeak[i], dt);

                // Refresh toggle buttons — the parameters are the source of
                // truth (click handler, pattern editor, song load all land there).
                RefreshToggleVisual(inMuteButtons[i], inMuteLabels[i], IsOn(muteParam, i));
                RefreshToggleVisual(soloButtons[i],   soloLabels[i],   IsOn(soloParam, i));
                RefreshToggleVisual(monoButtons[i],   monoLabels[i],   IsOn(monoParam, i));
            }

            // Mute button — same source-of-truth pattern.
            RefreshToggleVisual(muteButton, muteLabel, IsOn(masterMuteParam, 0));

            // Faders follow their parameters (pattern playback, param window, undo).
            for (int i = 0; i < shownChannels; i++)
            {
                volFaders[i]?.Refresh();
                panFaders[i]?.Refresh();
            }
            masterFader?.Refresh();

            // Master output — true peak.
            outMeterL?.Update(tpPeakL, dt);
            outMeterR?.Update(tpPeakR, dt);
        }
    }
}
