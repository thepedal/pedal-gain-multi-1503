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
        const int GUI_PROTOCOL_VERSION = 2;   // v2: + channel count
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

        // Latest meter snapshot from the native side (1.0 == 0 dBFS).
        readonly float[] meterIn = new float[MaxChannels];
        float meterL, meterR;
        bool  meterLinkOk = true;

        public IMachine Machine
        {
            get => imachine;
            set
            {
                imachine = value;
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
        readonly Rectangle[] inBars        = new Rectangle[MaxChannels];
        readonly Rectangle[] inPeakLines   = new Rectangle[MaxChannels];
        readonly TextBlock[] inDbTexts     = new TextBlock[MaxChannels];
        readonly float[]     inHoldDb      = new float[MaxChannels];
        readonly int[]       inHoldFrames  = new int[MaxChannels];

        // Output widgets
        Rectangle barL,  barR;
        Rectangle peakL, peakR;
        TextBlock dbTextL, dbTextR;
        float holdDbL = DB_MIN, holdDbR = DB_MIN;
        int   holdFramesL,      holdFramesR;

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
        const int   HOLD_FRAMES = 90;   // ~3 s at 33 ms/frame

        // ── Cached, frozen brushes ───────────────────────────────────────────
        static readonly Brush TrackBrush     = Freeze(new SolidColorBrush(Color.FromRgb(34,  34,  38)));
        static readonly Brush PeakBrush      = Freeze(new SolidColorBrush(Colors.White));
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
            Loaded   += (_, __) => { meterLinkOk = true; timer.Start(); };
            Unloaded += (_, __) => timer.Stop();
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

            (barL, peakL, dbTextL) = AddOutputRow(root, "L", grad);
            (barR, peakR, dbTextR) = AddOutputRow(root, "R", grad);

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

                // Col 3 — bar canvas + peak-hold line.
                var (canvas, bar, peak) = BuildBarCanvas(levelBrush);
                Grid.SetColumn(canvas, 3);
                grid.Children.Add(canvas);
                inBars[i]      = bar;
                inPeakLines[i] = peak;

                // Col 4 — dB readout.
                var db = RowReadout();
                Grid.SetColumn(db, 4);
                grid.Children.Add(db);
                inDbTexts[i] = db;

                inHoldDb[i]     = DB_MIN;
                inHoldFrames[i] = 0;

                // Col 5 — channel fader (Volume), col 6 — balance (Pan).
                var vol = new MiniFader(FADER_W - 8, H, bipolar: false, fill: FaderBrush,
                                        param: () => volumeParam, track: track);
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
                Text              = "OUT (MASTER)",
                FontFamily        = Mono,
                FontSize          = 8,
                Foreground        = SectionColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hdr, 2);
            grid.Children.Add(hdr);

            // Col 5 — master fader (Gain, output 0 only).
            masterFader = new MiniFader(FADER_W - 8, H, bipolar: false, fill: FaderBrush,
                                        param: () => gainParam, track: 0);
            Grid.SetColumn(masterFader.Root, 5);
            grid.Children.Add(masterFader.Root);

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

        // Build the bar canvas used by every meter row.
        static (Canvas canvas, Rectangle bar, Rectangle peak)
            BuildBarCanvas(Brush fill)
        {
            var canvas = new Canvas { Width = W, Height = H, ClipToBounds = true };

            var track = new Rectangle
            {
                Width = W, Height = H, Fill = TrackBrush, RadiusX = 1.5, RadiusY = 1.5
            };
            canvas.Children.Add(track);

            var bar = new Rectangle
            {
                Width = 0, Height = H, Fill = fill, RadiusX = 1.5, RadiusY = 1.5
            };
            Canvas.SetLeft(bar, 0);
            Canvas.SetTop(bar, 0);
            canvas.Children.Add(bar);

            var peak = new Rectangle
            {
                Width = 2, Height = H, Fill = PeakBrush, Opacity = 0
            };
            Canvas.SetTop(peak, 0);
            canvas.Children.Add(peak);

            return (canvas, bar, peak);
        }

        // Helper used for the output L / R rows — no toggle buttons, same
        // grid so the bar column still aligns pixel-for-pixel with the
        // input rows.
        (Rectangle bar, Rectangle peak, TextBlock db)
            AddOutputRow(Panel parent, string label, Brush fill)
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            grid.Children.Add(RowLabel(label, col: 2));

            var (canvas, bar, peak) = BuildBarCanvas(fill);
            Grid.SetColumn(canvas, 3);
            grid.Children.Add(canvas);

            var db = RowReadout();
            Grid.SetColumn(db, 4);
            grid.Children.Add(db);

            parent.Children.Add(grid);
            return (bar, peak, db);
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
            readonly Rectangle fill;
            readonly Rectangle defaultTick;
            int shownValue = int.MinValue;

            public MiniFader(double width, double height, bool bipolar, Brush fill,
                             Func<IParameter> param, int track)
            {
                this.width   = width;
                this.bipolar = bipolar;
                this.param   = param;
                this.track   = track;

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
                    int step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 1 : 5;
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
                Set(p.MinValue + (int)Math.Round(frac * (p.MaxValue - p.MinValue)));
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
                p.MaxValue > p.MinValue ? (double)(v - p.MinValue) / (p.MaxValue - p.MinValue) * width : 0;

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
                               "\nDrag to set · double-click to reset · wheel ±5 (Ctrl ±1)";
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
            if (imachine == null || !meterLinkOk) return;

            byte[] r;
            try { r = imachine.SendGUIMessage(MeterRequest); }
            catch { meterLinkOk = false; return; }

            if (r == null || r.Length < 8) return;
            if (BitConverter.ToInt32(r, 0) != GUI_PROTOCOL_VERSION) { meterLinkOk = false; return; }

            int channels = BitConverter.ToInt32(r, 4);
            if (channels < 1 || channels > MaxChannels) return;
            if (r.Length < 8 + 4 * (channels + 2)) return;

            if (channels != shownChannels)
                BuildInputRows(channels);

            int o = 8;
            for (int i = 0; i < channels; i++, o += 4)
                meterIn[i] = BitConverter.ToSingle(r, o);
            meterL = BitConverter.ToSingle(r, o); o += 4;
            meterR = BitConverter.ToSingle(r, o);
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
        // peak (UpdateHold tracks its value in dB, not linear).
        static string FormatDb(float db) =>
            db <= DB_MIN + 0.5f ? "-∞" : $"{db:F1}";

        void SetBar(Rectangle bar, float lin)
        {
            bar.Width = Clamp(Norm(LinToDb(lin)) * W, 0f, W);
        }

        void UpdateHold(ref float holdDb, ref int frames, float currentDb, Rectangle line)
        {
            if (currentDb >= holdDb) { holdDb = currentDb; frames = 0; }
            else if (++frames > HOLD_FRAMES)
                holdDb = Math.Max(holdDb - 0.4f, DB_MIN);

            Canvas.SetLeft(line, Clamp(Norm(holdDb) * W - 1f, 0f, W - 2f));
            line.Opacity = holdDb > DB_MIN + 0.5f ? 1.0 : 0.0;
        }

        // ── Timer tick ───────────────────────────────────────────────────────
        void Tick(object sender, EventArgs e)
        {
            if (imachine == null) return;
            if (soloParam == null) CacheParameters();

            PollMeters();

            // Per-input meters + solo / mute button state.
            for (int i = 0; i < shownChannels; i++)
            {
                float v = meterIn[i];
                SetBar(inBars[i], v);
                UpdateHold(ref inHoldDb[i], ref inHoldFrames[i], LinToDb(v), inPeakLines[i]);
                inDbTexts[i].Text = FormatDb(inHoldDb[i]);

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

            // Stereo output meter — hold-synchronized readout.
            float l = meterL;
            float r = meterR;

            SetBar(barL, l);
            UpdateHold(ref holdDbL, ref holdFramesL, LinToDb(l), peakL);
            dbTextL.Text = FormatDb(holdDbL);

            SetBar(barR, r);
            UpdateHold(ref holdDbR, ref holdFramesR, LinToDb(r), peakR);
            dbTextR.Text = FormatDb(holdDbR);
        }
    }
}
