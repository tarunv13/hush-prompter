using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Hush.Audio;
using Hush.Core;
using Hush.Native;

namespace Hush.UI
{
    /// <summary>Numbers gathered during a run, for the rehearsal coach.</summary>
    public sealed class RehearsalStats
    {
        public DateTime Started;
        public double TotalSeconds, SpeakingSeconds;
        public int Pauses, Words, SpeedChanges;
        public double AverageTempo;
        public Dictionary<string, double> SecondsPerCue = new Dictionary<string, double>();

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Total time: {TotalSeconds:0} s; time speaking: {SpeakingSeconds:0} s; pauses: {Pauses}.");
            if (SpeakingSeconds > 5) sb.AppendLine($"Average pace: about {Words / (SpeakingSeconds / 60.0):0} words per minute of speech (script has {Words} words).");
            sb.AppendLine($"Average tempo relative to calibration: {AverageTempo:0.00}. Speed changes during the run: {SpeedChanges}.");
            foreach (var kv in SecondsPerCue) sb.AppendLine($"Section \"{kv.Key}\": {kv.Value:0} s.");
            return sb.ToString();
        }
    }

    public sealed class PrompterWindow : Window
    {
        public ScrollEngine Engine { get; } = new ScrollEngine();
        public bool Floating { get; private set; }
        public int CueIndex { get; private set; }
        public int CueCount => _cues.Count;
        public RehearsalStats Stats { get; } = new RehearsalStats();
        public string StatusText { get; private set; } = "";

        /// <summary>Raised once when a voice run hears nothing for eight seconds.</summary>
        public event Action<string> SilenceHint;
        public event Action Stopped;

        readonly Settings _s;
        readonly VoicePipeline _voice;
        readonly List<Cue> _cues;
        readonly List<int> _cueStarts = new List<int>();
        WordAligner _aligner;

        // Visual tree
        readonly Grid _root = new Grid();
        readonly Grid _panel = new Grid();
        readonly Path _notchShape = new Path { Fill = Brushes.Black };
        readonly Border _floatShape = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1) };
        readonly Canvas _viewport = new Canvas { ClipToBounds = true };
        readonly TextBox _text = new TextBox();
        readonly TranslateTransform _shift = new TranslateTransform();
        readonly Ellipse _beam = new Ellipse { IsHitTestVisible = false };
        readonly ScaleTransform _beamScale = new ScaleTransform(1, 0.4);
        readonly TextBlock _countdown = new TextBlock();
        readonly Grid _controls = new Grid { Opacity = 0 };
        readonly Button _pauseBtn, _stopBtn, _modeBtn;
        readonly TextBlock _toast = new TextBlock();
        readonly Border _endMark = new Border();
        readonly StackPanel _pager = new StackPanel { Orientation = Orientation.Horizontal };
        readonly Button _pagerLabel, _prevBtn, _nextBtn;
        readonly Popup _cueList = new Popup { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom };
        readonly Thumb _bottomThumb = new Thumb(), _leftThumb = new Thumb(), _rightThumb = new Thumb(), _cornerThumb = new Thumb();

        readonly Stopwatch _clock = new Stopwatch();
        TimeSpan _last;
        double _tempoSum; long _tempoN;
        bool _silenceWarned, _closing;
        const double Ear = 12, Pad = 18;

        public PrompterWindow(Settings settings, VoicePipeline voice, List<Cue> cues, bool floating, ScrollMode mode)
        {
            _s = settings; _voice = voice; _cues = cues.Count > 0 ? cues : new List<Cue> { new Cue { Title = "Script", Text = "" } };
            Floating = floating;

            Title = "Hush prompter";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            Engine.Mode = mode;
            Engine.SetSpeedStep(_s.SpeedStep);

            // Text
            _text.IsReadOnly = true; _text.Focusable = false; _text.IsHitTestVisible = false; _text.IsTabStop = false;
            _text.BorderThickness = new Thickness(0); _text.Background = Brushes.Transparent; _text.Padding = new Thickness(0);
            _text.TextWrapping = TextWrapping.Wrap; _text.TextAlignment = TextAlignment.Center;
            _text.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
            _text.RenderTransform = _shift;
            _text.Template = BareTextTemplate();
            _viewport.Children.Add(_text);
            // Voice beam: a soft arc of light rising from the bottom edge.
            _beam.RenderTransform = _beamScale; _beam.RenderTransformOrigin = new Point(0.5, 1);
            _beam.VerticalAlignment = VerticalAlignment.Bottom; _beam.HorizontalAlignment = HorizontalAlignment.Center;

            _countdown.Foreground = new LinearGradientBrush(Color.FromRgb(120, 120, 128), Colors.White, 90);
            _countdown.FontFamily = (FontFamily)Application.Current.FindResource("Ui");
            _countdown.FontWeight = FontWeights.Light;
            _countdown.HorizontalAlignment = HorizontalAlignment.Center; _countdown.VerticalAlignment = VerticalAlignment.Center;
            _countdown.IsHitTestVisible = false;

            _endMark.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            _endMark.CornerRadius = new CornerRadius(8); _endMark.Padding = new Thickness(10, 2, 10, 3);
            _endMark.HorizontalAlignment = HorizontalAlignment.Center; _endMark.VerticalAlignment = VerticalAlignment.Bottom;
            _endMark.Margin = new Thickness(0, 0, 0, 8); _endMark.Visibility = Visibility.Collapsed; _endMark.IsHitTestVisible = false;
            _endMark.Child = new TextBlock { Text = "End", Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), FontSize = 11, FontFamily = (FontFamily)Application.Current.FindResource("Ui") };

            _toast.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            _toast.FontFamily = (FontFamily)Application.Current.FindResource("Ui"); _toast.FontSize = 11;
            _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Bottom;
            _toast.Margin = new Thickness(0, 0, 0, 6); _toast.Opacity = 0; _toast.IsHitTestVisible = false;

            // Controls that appear on hover
            _pauseBtn = RoundButton("", "Pause or resume (Ctrl+Alt+P)", (s, e) => TogglePause());
            _stopBtn = RoundButton("", "Stop (Esc or Ctrl+Alt+Q)", (s, e) => Close());
            _modeBtn = RoundButton("", "Switch notch / floating", (s, e) => SetFloating(!Floating));
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            left.Children.Add(_pauseBtn);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            right.Children.Add(_modeBtn); right.Children.Add(_stopBtn);
            _modeBtn.Margin = new Thickness(0, 0, 6, 0);
            _controls.Children.Add(left); _controls.Children.Add(right);

            // Cue pager
            _prevBtn = PagerButton("", "Previous cue (Ctrl+Alt+Page Up)", (s, e) => GoToCue(CueIndex - 1));
            _nextBtn = PagerButton("", "Next cue (Ctrl+Alt+Page Down)", (s, e) => GoToCue(CueIndex + 1));
            _pagerLabel = PagerButton("1/1", "Show all cues", (s, e) => OpenCueList());
            _pagerLabel.Width = double.NaN; _pagerLabel.Padding = new Thickness(10, 0, 10, 0);
            _pagerLabel.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
            _pager.Children.Add(_prevBtn); _pager.Children.Add(_pagerLabel); _pager.Children.Add(_nextBtn);
            var pagerShell = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 18, 19, 23)), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(4), Child = _pager, HorizontalAlignment = HorizontalAlignment.Center,
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            _pagerShell = pagerShell;
            _cueList.PlacementTarget = pagerShell;

            foreach (var t in new[] { _bottomThumb, _leftThumb, _rightThumb, _cornerThumb })
            {
                t.Template = InvisibleThumb();
                t.DragDelta += OnResize;
                t.DragCompleted += (s, e) => SaveGeometry();
            }
            _bottomThumb.Cursor = Cursors.SizeNS; _bottomThumb.Height = 8; _bottomThumb.VerticalAlignment = VerticalAlignment.Bottom;
            _leftThumb.Cursor = Cursors.SizeWE; _leftThumb.Width = 8; _leftThumb.HorizontalAlignment = HorizontalAlignment.Left;
            _rightThumb.Cursor = Cursors.SizeWE; _rightThumb.Width = 8; _rightThumb.HorizontalAlignment = HorizontalAlignment.Right;
            _cornerThumb.Cursor = Cursors.SizeNWSE; _cornerThumb.Width = 16; _cornerThumb.Height = 16;
            _cornerThumb.HorizontalAlignment = HorizontalAlignment.Right; _cornerThumb.VerticalAlignment = VerticalAlignment.Bottom;

            _panel.Children.Add(_notchShape);
            _panel.Children.Add(_floatShape);
            _panel.Children.Add(_beam);
            _panel.Children.Add(_viewport);
            _panel.Children.Add(_countdown);
            _panel.Children.Add(_endMark);
            _panel.Children.Add(_toast);
            _panel.Children.Add(_controls);
            _panel.Children.Add(_bottomThumb); _panel.Children.Add(_leftThumb); _panel.Children.Add(_rightThumb); _panel.Children.Add(_cornerThumb);
            _panel.MouseEnter += (s, e) => { if (_s.HoverToPause) Engine.HoverPaused = true; FadeControls(true); };
            _panel.MouseLeave += (s, e) => { Engine.HoverPaused = false; FadeControls(false); };
            _panel.MouseWheel += (s, e) => { Engine.Nudge(-e.Delta * 0.35); e.Handled = true; };
            _panel.MouseLeftButtonDown += (s, e) => { if (Floating && e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); SaveGeometry(); } catch { } } };

            _root.Children.Add(_panel);
            _root.Children.Add(pagerShell);
            Content = _root;

            KeyDown += OnKey;
            SourceInitialized += (s, e) =>
            {
                Win32.MakeToolWindow(this, true);
                Win32.ExcludeFromCapture(this, _s.HideFromCapture);
            };
            Loaded += (s, e) => { ApplyLayout(); LoadText(); Engine.StartCountdown(_s.CountdownSeconds); Stats.Started = DateTime.Now; _clock.Start(); CompositionTarget.Rendering += OnFrame; };
            Closing += (s, e) => { _closing = true; CompositionTarget.Rendering -= OnFrame; FinishStats(); };
            Closed += (s, e) => Stopped?.Invoke();

            if (_voice != null) _voice.WordsHeard += OnWordsHeard;
        }

        readonly Border _pagerShell;

        // ---------------- layout ----------------

        public void SetFloating(bool floating)
        {
            if (Floating == floating) return;
            Floating = floating;
            _s.StartFloating = floating;
            ApplyLayout();
            Dispatcher.BeginInvoke(new Action(() => { Relayout(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void ApplyLayout()
        {
            var mon = Win32.MonitorOf(this);
            _text.Foreground = new SolidColorBrush(ParseColor(_s.TextColor, Colors.White));
            _text.FontSize = _s.FontSize;
            TextBlock.SetLineHeight(_text, Math.Round(_s.FontSize * 1.45));
            TextBlock.SetLineStackingStrategy(_text, LineStackingStrategy.BlockLineHeight);
            var beamColor = ParseColor(_s.BeamColor, Color.FromRgb(88, 101, 255));
            _beam.Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, beamColor.R, beamColor.G, beamColor.B), 0.55),
                new GradientStop(Color.FromArgb(230, beamColor.R, beamColor.G, beamColor.B), 0.78),
                new GradientStop(Color.FromArgb(0, beamColor.R, beamColor.G, beamColor.B), 1.0)
            });

            double w, h;
            if (!Floating)
            {
                w = _s.NotchWidth; h = _s.NotchHeight;
                Width = Math.Min(mon.Bounds.Width, w + 40);
                Height = h + 280;
                Left = mon.Bounds.Left + (mon.Bounds.Width - Width) / 2;
                Top = mon.Bounds.Top;
                _panel.Width = w; _panel.Height = h;
                _panel.HorizontalAlignment = HorizontalAlignment.Center; _panel.VerticalAlignment = VerticalAlignment.Top;
                _notchShape.Visibility = Visibility.Visible; _floatShape.Visibility = Visibility.Collapsed;
                _notchShape.Data = NotchGeometry(w, h);
                _panel.Clip = NotchGeometry(w, h);
                _viewport.Margin = new Thickness(Ear + Pad, 4, Ear + Pad, 4);
                _controls.Margin = new Thickness(Ear + 8, 8, Ear + 8, 0);
                _pagerShell.VerticalAlignment = VerticalAlignment.Top;
                _pagerShell.Margin = new Thickness(0, h + 8, 0, 0);
                _leftThumb.Margin = new Thickness(Ear, 0, 0, 0); _rightThumb.Margin = new Thickness(0, 0, Ear, 0);
                _cornerThumb.Visibility = Visibility.Collapsed;
                _beam.Width = w * 0.62; _beam.Height = h * 1.1; _beam.Margin = new Thickness(0, 0, 0, -h * 0.62);
            }
            else
            {
                w = _s.FloatWidth; h = _s.FloatHeight;
                Width = w; Height = h + 52;
                double l = double.IsNaN(_s.FloatLeft) ? mon.Work.Left + (mon.Work.Width - w) / 2 : _s.FloatLeft;
                double t = double.IsNaN(_s.FloatTop) ? mon.Work.Top + 80 : _s.FloatTop;
                Left = Math.Max(mon.Bounds.Left - w + 80, Math.Min(l, mon.Bounds.Right - 80));
                Top = Math.Max(mon.Bounds.Top, Math.Min(t, mon.Bounds.Bottom - 80));
                _panel.Width = w; _panel.Height = h;
                _panel.HorizontalAlignment = HorizontalAlignment.Stretch; _panel.VerticalAlignment = VerticalAlignment.Top;
                _notchShape.Visibility = Visibility.Collapsed; _floatShape.Visibility = Visibility.Visible;
                _floatShape.Background = new LinearGradientBrush(Color.FromArgb(240, 34, 36, 42), Color.FromArgb(240, 20, 21, 25), 90);
                _floatShape.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
                _panel.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 18, 18);
                _viewport.Margin = new Thickness(Pad + 30, 10, Pad + 30, 10);
                _controls.Margin = new Thickness(12, 12, 12, 0);
                _pagerShell.VerticalAlignment = VerticalAlignment.Top;
                _pagerShell.Margin = new Thickness(0, h + 8, 0, 0);
                _leftThumb.Margin = new Thickness(0); _rightThumb.Margin = new Thickness(0);
                _cornerThumb.Visibility = Visibility.Visible;
                _beam.Width = w * 0.62; _beam.Height = h * 0.8; _beam.Margin = new Thickness(0, 0, 0, -h * 0.48);
            }
            _modeBtn.Content = Glyph(Floating ? "" : "");
            _countdown.FontSize = Math.Max(28, h * 0.55);
            _pagerShell.Visibility = _cues.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePager();
        }

        /// <summary>The notch: square shoulders at the top edge, curved ears, rounded bottom corners.</summary>
        static Geometry NotchGeometry(double w, double h)
        {
            double r = Math.Min(24, h / 3);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(w, 0), false, false);
                c.QuadraticBezierTo(new Point(w - Ear, 0), new Point(w - Ear, Ear), false, true);
                c.LineTo(new Point(w - Ear, h - r), false, false);
                c.ArcTo(new Point(w - Ear - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, true);
                c.LineTo(new Point(Ear + r, h), false, false);
                c.ArcTo(new Point(Ear, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, true);
                c.LineTo(new Point(Ear, Ear), false, false);
                c.QuadraticBezierTo(new Point(Ear, 0), new Point(0, 0), false, true);
            }
            g.Freeze();
            return g;
        }

        // ---------------- text ----------------

        void LoadText()
        {
            _cueStarts.Clear();
            string text;
            if (_s.CueMode)
            {
                text = _cues[CueIndex].Text;
                _cueStarts.Add(0);
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in _cues)
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    _cueStarts.Add(sb.Length);
                    sb.Append(c.Text);
                }
                text = sb.ToString();
            }
            _text.Text = text.Length == 0 ? "This note is empty." : text;
            _aligner = new WordAligner(_text.Text);
            if (Engine.Mode == ScrollMode.WordFollow && _voice != null)
            {
                if (!_voice.StartFollowing(_aligner.Vocabulary(), _s.SpeechCulture))
                {
                    Engine.Mode = ScrollMode.Voice;
                    ShowToast("Word follow unavailable: using voice mode");
                }
            }
            Dispatcher.BeginInvoke(new Action(Relayout), System.Windows.Threading.DispatcherPriority.Loaded);
            Stats.Words = ScriptParser.WordCount(_text.Text);
        }

        double ViewportHeight => Math.Max(20, _panel.Height - _viewport.Margin.Top - _viewport.Margin.Bottom);
        double StartY => ViewportHeight * 0.24;

        void Relayout()
        {
            double vw = Math.Max(40, _panel.Width - _viewport.Margin.Left - _viewport.Margin.Right);
            _viewport.Width = vw; _viewport.Height = ViewportHeight;
            // Fade the top and bottom edges of the visible area. Absolute mapping: a relative brush
            // would follow the scrolled text's full height and drift as it moves.
            double vh = ViewportHeight;
            _viewport.OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), Math.Min(0.3, 22 / vh)),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), Math.Max(0.7, 1 - 26 / vh)),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0)
            }, new Point(0, 0), new Point(0, vh)) { MappingMode = BrushMappingMode.Absolute };
            _text.Width = vw;
            _text.UpdateLayout();
            // The last line's exact top, so the final line (and the last cue) can reach the reading line.
            double lastTop = YOfChar(_text.Text.Length - 1);
            if (lastTop <= 0) lastTop = Math.Max(0, _text.ActualHeight - _text.FontSize * 1.35);
            Engine.SetBounds(0, lastTop);
            UpdateTransform();
        }

        void UpdateTransform() => _shift.Y = Math.Round(StartY - Engine.Offset, 1);

        // ---------------- cues ----------------

        public void GoToCue(int index)
        {
            if (index < 0 || index >= _cues.Count) return;
            CueIndex = index;
            if (_s.CueMode)
            {
                Engine.JumpTo(0);
                LoadText();
            }
            else
            {
                Engine.JumpTo(YOfChar(_cueStarts[index]));
                _aligner?.Reset(WordIndexAtChar(_cueStarts[index]));
            }
            UpdatePager();
        }

        double YOfChar(int charIndex)
        {
            if (charIndex <= 0 || _text.Text.Length == 0) return 0;
            try
            {
                var r = _text.GetRectFromCharacterIndex(Math.Min(charIndex, _text.Text.Length - 1));
                return r.IsEmpty ? 0 : r.Top;
            }
            catch { return 0; }
        }

        int WordIndexAtChar(int ch)
        {
            if (_aligner == null) return 0;
            for (int i = 0; i < _aligner.Words.Count; i++) if (_aligner.Words[i].CharIndex >= ch) return i;
            return _aligner.Words.Count;
        }

        void UpdatePager()
        {
            if (!_s.CueMode && _cueStarts.Count == _cues.Count && _cues.Count > 1)
            {
                // In continuous mode the current cue is the last one whose start has scrolled past the reading line.
                int idx = 0;
                for (int i = 0; i < _cueStarts.Count; i++) if (YOfChar(_cueStarts[i]) <= Engine.Offset + 2) idx = i;
                CueIndex = idx;
            }
            _pagerLabel.Content = (CueIndex + 1) + "/" + _cues.Count;
            _prevBtn.IsEnabled = CueIndex > 0;
            _nextBtn.IsEnabled = CueIndex < _cues.Count - 1;
        }

        void OpenCueList()
        {
            var list = new StackPanel { Margin = new Thickness(4) };
            for (int i = 0; i < _cues.Count; i++)
            {
                int k = i;
                var b = new Button
                {
                    Content = $"{i + 1}  {_cues[i].Title}", HorizontalContentAlignment = HorizontalAlignment.Left,
                    Foreground = i == CueIndex ? Brushes.Black : new SolidColorBrush(Color.FromRgb(230, 232, 238)),
                    Background = i == CueIndex ? new SolidColorBrush(Color.FromRgb(240, 236, 220)) : Brushes.Transparent,
                    BorderThickness = new Thickness(0), Padding = new Thickness(10, 6, 14, 6), Margin = new Thickness(0, 1, 0, 1),
                    FontFamily = (FontFamily)Application.Current.FindResource("Mono"), FontSize = 13, Focusable = false, MinWidth = 220
                };
                b.Template = CueRowTemplate();
                b.Click += (s, e) => { _cueList.IsOpen = false; GoToCue(k); };
                list.Children.Add(b);
            }
            _cueList.Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 22, 23, 28)), CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), BorderThickness = new Thickness(1),
                Child = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            };
            _cueList.VerticalOffset = 6;
            _cueList.IsOpen = true;
        }

        // ---------------- frame loop ----------------

        void OnFrame(object sender, EventArgs e)
        {
            if (_closing) return;
            var now = _clock.Elapsed;
            double dt = (now - _last).TotalSeconds;
            _last = now;

            var vad = _voice?.Vad;
            if (Engine.Mode != ScrollMode.Continuous && vad != null)
            {
                Engine.Speaking = vad.IsSpeaking;
                Engine.TempoFactor = _s.AdaptiveTempo ? _voice.Tempo.Factor : 1.0;
            }
            Engine.Tick(dt);
            UpdateTransform();

            // Beam: height and brightness follow loudness; dim when paused.
            double level = vad?.Level ?? (Engine.Mode == ScrollMode.Continuous && !Engine.Paused ? 0.35 : 0);
            double target = Engine.Paused ? 0.15 : 0.25 + level * 0.9;
            _beamScale.ScaleY += (target - _beamScale.ScaleY) * Math.Min(1, dt * 10);
            _beam.Opacity = Math.Min(1, 0.15 + level * 1.3) * (Engine.Paused ? 0.4 : 1);

            if (Engine.CountdownRemaining > 0)
            {
                _countdown.Visibility = Visibility.Visible;
                _countdown.Text = Math.Ceiling(Engine.CountdownRemaining).ToString();
                double frac = Engine.CountdownRemaining - Math.Floor(Engine.CountdownRemaining);
                _countdown.Opacity = 0.35 + 0.65 * frac;
                _viewport.Opacity = 0.15;
            }
            else
            {
                _countdown.Visibility = Visibility.Collapsed;
                _viewport.Opacity = Math.Min(1, _viewport.Opacity + dt * 3);
            }

            _endMark.Visibility = Engine.AtEnd && Engine.CountdownRemaining <= 0 && Engine.MaxOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_endMark.Visibility == Visibility.Visible)
                ((TextBlock)_endMark.Child).Text = _s.CueMode && CueIndex < _cues.Count - 1 ? "End of cue · Ctrl+Alt+PgDn for next" : "End";

            _pauseBtn.Content = Glyph(Engine.UserPaused ? "" : "");

            // Statistics
            if (Engine.CountdownRemaining <= 0)
            {
                string cue = _cues[Math.Min(CueIndex, _cues.Count - 1)].Title ?? "";
                Stats.SecondsPerCue.TryGetValue(cue, out double sec);
                Stats.SecondsPerCue[cue] = sec + dt;
                _tempoSum += Engine.TempoFactor; _tempoN++;
            }
            if (((long)now.TotalMilliseconds / 250) != ((long)(now.TotalMilliseconds - dt * 1000) / 250)) UpdatePager();

            // Silence hint: a voice run that hears nothing for 8 s probably has the wrong microphone.
            if (!_silenceWarned && _voice != null && Engine.Mode != ScrollMode.Continuous && !Engine.Paused && _voice.SecondsWithoutSpeech > 8)
            {
                _silenceWarned = true;
                string name = _voice.Source?.DeviceName ?? "the microphone";
                ShowToast("Hearing nothing from " + name);
                SilenceHint?.Invoke(name);
            }

            StatusText = $"{Engine.Mode} · {Engine.Speed:0} px/s" + (Engine.Paused ? " · paused" : "") + (vad != null && vad.IsSpeaking ? " · speaking" : "");
        }

        void OnWordsHeard(string phrase)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing || _aligner == null || Engine.Mode != ScrollMode.WordFollow) return;
                if (_aligner.Feed(phrase))
                    Engine.FollowTarget = Math.Min(Engine.MaxOffset, YOfChar(_aligner.CursorCharIndex));
            }));
        }

        void FinishStats()
        {
            Stats.TotalSeconds = _clock.Elapsed.TotalSeconds;
            Stats.SpeakingSeconds = _voice?.SpeakingSeconds ?? 0;
            Stats.Pauses = _voice?.PauseCount ?? 0;
            Stats.AverageTempo = _tempoN > 0 ? _tempoSum / _tempoN : 1;
            if (_voice != null) _voice.WordsHeard -= OnWordsHeard;
        }

        // ---------------- input ----------------

        public void TogglePause() { Engine.UserPaused = !Engine.UserPaused; ShowToast(Engine.UserPaused ? "Paused" : "Resumed"); }

        public void Faster() { Engine.Faster(); _s.SpeedStep = Engine.SpeedStep; Stats.SpeedChanges++; ShowToast($"Speed {Engine.Speed:0} px/s"); }
        public void Slower() { Engine.Slower(); _s.SpeedStep = Engine.SpeedStep; Stats.SpeedChanges++; ShowToast($"Speed {Engine.Speed:0} px/s"); }
        public void LineBack() => Engine.Nudge(-_text.FontSize * 1.35);
        public void LineForward() => Engine.Nudge(_text.FontSize * 1.35);

        void OnKey(object sender, KeyEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Space: TogglePause(); break;
                case Key.Right when shift: Faster(); break;
                case Key.Left when shift: Slower(); break;
                case Key.Up: LineBack(); break;
                case Key.Down: LineForward(); break;
                case Key.PageUp: GoToCue(CueIndex - 1); break;
                case Key.PageDown: GoToCue(CueIndex + 1); break;
                default: return;
            }
            e.Handled = true;
        }

        void OnResize(object sender, DragDeltaEventArgs e)
        {
            if (!Floating)
            {
                if (sender == _bottomThumb) _s.NotchHeight = Math.Max(70, Math.Min(700, _s.NotchHeight + e.VerticalChange));
                if (sender == _rightThumb) _s.NotchWidth = Math.Max(280, Math.Min(1600, _s.NotchWidth + e.HorizontalChange * 2));
                if (sender == _leftThumb) _s.NotchWidth = Math.Max(280, Math.Min(1600, _s.NotchWidth - e.HorizontalChange * 2));
            }
            else
            {
                if (sender == _bottomThumb || sender == _cornerThumb) _s.FloatHeight = Math.Max(120, Math.Min(2000, _s.FloatHeight + e.VerticalChange));
                if (sender == _rightThumb || sender == _cornerThumb) _s.FloatWidth = Math.Max(260, Math.Min(3000, _s.FloatWidth + e.HorizontalChange));
                if (sender == _leftThumb)
                {
                    double nw = Math.Max(260, Math.Min(3000, _s.FloatWidth - e.HorizontalChange));
                    _s.FloatLeft = Left + (_s.FloatWidth - nw); _s.FloatWidth = nw;
                }
            }
            ApplyLayout();
            Relayout();
        }

        void SaveGeometry()
        {
            if (Floating) { _s.FloatLeft = Left; _s.FloatTop = Top; }
            App.SaveSettings();
        }

        public void ApplySettings()
        {
            Engine.Mode = _s.ScrollModeValue;
            Win32.ExcludeFromCapture(this, _s.HideFromCapture);
            ApplyLayout();
            Relayout();
        }

        void ShowToast(string text)
        {
            _toast.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2100))));
            _toast.BeginAnimation(OpacityProperty, a);
        }

        void FadeControls(bool show)
        {
            _controls.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 300)));
        }

        // ---------------- small builders ----------------

        static Button RoundButton(string glyph, string tip, RoutedEventHandler click)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("PrompterButton"), Content = Glyph(glyph), ToolTip = tip };
            b.Click += click;
            return b;
        }

        static Button PagerButton(string content, string tip, RoutedEventHandler click)
        {
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("PrompterButton"), ToolTip = tip, Height = 26, Width = 28,
                Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(230, 232, 238)), FontSize = 12
            };
            b.Content = content.Length == 1 && content[0] > '' ? (object)Glyph(content) : content;
            b.Click += click;
            return b;
        }

        static TextBlock Glyph(string g) => new TextBlock { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 240)) };

        static ControlTemplate BareTextTemplate()
        {
            var t = new ControlTemplate(typeof(TextBox));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            sv.SetValue(FocusableProperty, false);
            t.VisualTree = sv;
            return t;
        }

        static ControlTemplate InvisibleThumb()
        {
            var t = new ControlTemplate(typeof(Thumb));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            t.VisualTree = b;
            return t;
        }

        static ControlTemplate CueRowTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            b.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            t.VisualTree = b;
            return t;
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
        }
    }
}
