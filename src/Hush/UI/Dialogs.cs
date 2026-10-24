using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hush.Ai;
using Hush.Audio;
using Hush.Core;

namespace Hush.UI
{
    static class Ui
    {
        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        public static TextBlock Label(string text, double size = 13, bool bold = false, bool muted = false) => new TextBlock
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = muted ? Res("Muted") : Res("Ink"), TextWrapping = TextWrapping.Wrap
        };

        public static FrameworkElement Row(string label, FrameworkElement control, string help = null)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Label(label));
            if (help != null) sp.Children.Add(Label(help, 11.5, muted: true));
            g.Children.Add(sp);
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(control);
            return g;
        }

        public static CheckBox Switch(string label, bool value, Action<bool> set, string help = null)
        {
            var c = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = value, Margin = new Thickness(0, 7, 0, 7) };
            var sp = new StackPanel();
            sp.Children.Add(Label(label));
            if (help != null) sp.Children.Add(Label(help, 11.5, muted: true));
            c.Content = sp;
            c.Checked += (s, e) => set(true); c.Unchecked += (s, e) => set(false);
            return c;
        }

        public static Slider Slider(double min, double max, double value, Action<double> set, double tick = 0)
        {
            var sl = new Slider { Minimum = min, Maximum = max, Value = value, MinWidth = 220 };
            if (tick > 0) { sl.TickFrequency = tick; sl.IsSnapToTickEnabled = true; }
            sl.ValueChanged += (s, e) => set(e.NewValue);
            return sl;
        }

        public static ComboBox Combo(System.Collections.IEnumerable items, object selected, Action<object> set)
        {
            var cb = new ComboBox { MinWidth = 220, Padding = new Thickness(8, 5, 8, 5) };
            foreach (var i in items) cb.Items.Add(i);
            cb.SelectedItem = selected;
            if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
            cb.SelectionChanged += (s, e) => set(cb.SelectedItem);
            return cb;
        }

        public static Window Dialog(Window w, string title, double width, double height)
        {
            w.Title = title; w.Width = width; w.Height = height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Background = Res("Paper");
            w.FontFamily = (FontFamily)Application.Current.FindResource("Ui");
            w.ShowInTaskbar = false; w.ResizeMode = ResizeMode.CanResize;
            w.SourceInitialized += (s, e) => Hush.Native.Win32.ExcludeFromCapture(w, App.Settings.HideMainWindowFromCapture);
            return w;
        }
    }

    // =====================================================================
    public sealed class SettingsWindow : Window
    {
        readonly Settings _s;
        readonly AiService _ai;
        VoicePipeline _meter;
        readonly DispatcherTimer _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        readonly ProgressBar _level = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        readonly TextBlock _levelText = Ui.Label("", 11.5, muted: true);

        public SettingsWindow(Settings s, AiService ai)
        {
            _s = s; _ai = ai;
            Ui.Dialog(this, "Settings", 640, 640);
            var tabs = new TabControl { Margin = new Thickness(18, 14, 18, 18), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            tabs.Items.Add(new TabItem { Header = "Prompter", Content = Page(PrompterPage()) });
            tabs.Items.Add(new TabItem { Header = "Voice", Content = Page(VoicePage()) });
            tabs.Items.Add(new TabItem { Header = "AI", Content = Page(AiPage()) });
            tabs.Items.Add(new TabItem { Header = "Privacy", Content = Page(PrivacyPage()) });
            Content = tabs;
            _meterTimer.Tick += (o, e) =>
            {
                if (_meter == null) return;
                _level.Value = _meter.Vad.Level;
                _level.Foreground = _meter.Vad.IsSpeaking ? Ui.Res("Accent") : Brushes.Gray;
                _levelText.Text = _meter.Vad.IsSpeaking ? "Speaking" : $"Listening ({_meter.Vad.LastDb:0} dB, threshold {_meter.Vad.ThresholdDb:0} dB)";
            };
            Closed += (o, e) => StopMeter();
        }

        static ScrollViewer Page(UIElement content) => new ScrollViewer { Content = new Border { Padding = new Thickness(6, 12, 12, 12), Child = content }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        StackPanel PrompterPage()
        {
            var p = new StackPanel();
            p.Children.Add(Ui.Row("Text size", Ui.Slider(14, 48, _s.FontSize, v => _s.FontSize = Math.Round(v), 1)));
            var colors = new[] { "#F4F5F8 White", "#FFE7A6 Warm", "#A8F0D0 Mint", "#A9D4FF Sky", "#FFB3C7 Rose" };
            var cur = colors.FirstOrDefault(c => c.StartsWith(_s.TextColor, StringComparison.OrdinalIgnoreCase)) ?? colors[0];
            p.Children.Add(Ui.Row("Text colour", Ui.Combo(colors, cur, o => _s.TextColor = ((string)o).Substring(0, 7))));
            var beams = new[] { "#5865FF Indigo", "#21C07A Green", "#FF8A3D Amber", "#E24C9A Pink", "#9AA3B5 Silver" };
            var bcur = beams.FirstOrDefault(c => c.StartsWith(_s.BeamColor, StringComparison.OrdinalIgnoreCase)) ?? beams[0];
            p.Children.Add(Ui.Row("Voice beam colour", Ui.Combo(beams, bcur, o => _s.BeamColor = ((string)o).Substring(0, 7))));
            p.Children.Add(Ui.Row("Countdown", Ui.Slider(0, 10, _s.CountdownSeconds, v => _s.CountdownSeconds = Math.Round(v), 1), "Seconds before the text starts"));
            var speeds = ScrollEngine.SpeedSteps.Select(v => v + " px/s").ToArray();
            p.Children.Add(Ui.Row("Default speed", Ui.Combo(speeds, speeds[_s.SpeedStep], o => _s.SpeedStep = Array.IndexOf(speeds, o)), "Ctrl+Alt+Left/Right while running"));
            p.Children.Add(Ui.Switch("Play one cue at a time", _s.CueMode, v => _s.CueMode = v, "Stops at the end of each cue; the pager moves on"));
            p.Children.Add(Ui.Switch("Hover to pause", _s.HoverToPause, v => _s.HoverToPause = v));
            p.Children.Add(Ui.Switch("Start as a floating window", _s.StartFloating, v => _s.StartFloating = v));
            var reset = new Button { Content = "Reset prompter size and position", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            reset.Click += (o, e) => { _s.NotchWidth = 560; _s.NotchHeight = 150; _s.FloatWidth = 620; _s.FloatHeight = 300; _s.FloatLeft = double.NaN; _s.FloatTop = double.NaN; };
            p.Children.Add(reset);
            return p;
        }

        StackPanel VoicePage()
        {
            var p = new StackPanel();
            var modes = new[] { "Voice: scroll while I speak", "Word follow: track my words (offline)", "Constant speed: no microphone" };
            int mi = _s.ScrollModeValue == ScrollMode.Voice ? 0 : _s.ScrollModeValue == ScrollMode.WordFollow ? 1 : 2;
            p.Children.Add(Ui.Row("Scrolling", Ui.Combo(modes, modes[mi], o =>
            {
                int i = Array.IndexOf(modes, o);
                _s.ScrollModeValue = i == 0 ? ScrollMode.Voice : i == 1 ? ScrollMode.WordFollow : ScrollMode.Continuous;
            })));
            var mics = new[] { "Default microphone" }.Concat(WaveInSource.Devices().Select(d => d.Name)).ToArray();
            p.Children.Add(Ui.Row("Microphone", Ui.Combo(mics, string.IsNullOrEmpty(_s.MicName) ? mics[0] : _s.MicName, o =>
            {
                _s.MicName = (string)o == mics[0] ? "" : (string)o;
                if (_meter != null) StartMeter();
            })));
            var test = new Button { Content = "Test microphone", HorizontalAlignment = HorizontalAlignment.Left };
            test.Click += (o, e) => { if (_meter == null) { StartMeter(); test.Content = "Stop test"; } else { StopMeter(); test.Content = "Test microphone"; } };
            var meterBox = new StackPanel(); meterBox.Children.Add(test); meterBox.Children.Add(_level); meterBox.Children.Add(_levelText);
            p.Children.Add(Ui.Row("Level", meterBox));
            p.Children.Add(Ui.Row("Sensitivity", Ui.Slider(0, 1, _s.Sensitivity, v => { _s.Sensitivity = v; if (_meter != null) _meter.Vad.Sensitivity = v; }), "Higher reacts to quieter speech"));
            p.Children.Add(Ui.Row("Pause before stopping", Ui.Slider(150, 1500, _s.PauseMs, v => { _s.PauseMs = Math.Round(v / 50) * 50; if (_meter != null) _meter.Vad.PauseMs = _s.PauseMs; }), "How long a silence stops the text"));
            p.Children.Add(Ui.Switch("Adaptive tempo", _s.AdaptiveTempo, v => _s.AdaptiveTempo = v, "Speeds up and slows down with your pace"));
            p.Children.Add(Ui.Switch("Noise suppression", _s.NoiseSuppression, v => _s.NoiseSuppression = v, "Filters hum and hiss before detecting speech"));
            p.Children.Add(Ui.Switch("Automatic gain boost", _s.AutoGain, v => _s.AutoGain = v, "Helps quiet microphones"));
            var langs = VoicePipeline.InstalledRecognizers().Select(c => c.Name).ToArray();
            if (langs.Length > 0)
                p.Children.Add(Ui.Row("Word follow language", Ui.Combo(langs, string.IsNullOrEmpty(_s.SpeechCulture) ? langs[0] : _s.SpeechCulture, o => _s.SpeechCulture = (string)o), "Installed Windows speech recognisers"));
            var cal = new Button { Content = "Recalibrate…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            cal.Click += (o, e) => { StopMeter(); new CalibrationWindow(_s) { Owner = this }.ShowDialog(); };
            p.Children.Add(cal);
            return p;
        }

        StackPanel AiPage()
        {
            var p = new StackPanel();
            p.Children.Add(Ui.Label("Hush picks the model from the task: the fast model for structural edits, the balanced one for rewriting, the deepest for coaching. Long scripts move up a tier.", 12, muted: true));
            var provs = new[] { "Auto", "ClaudeCode", "AnthropicApi", "Ollama" };
            var provNames = new[] { "Auto (Claude Code, then API key, then local)", "Claude Code login only", "API key only", "Local Ollama only" };
            p.Children.Add(Ui.Row("Provider", Ui.Combo(provNames, provNames[Math.Max(0, Array.IndexOf(provs, _s.AiProvider))], o => _s.AiProvider = provs[Array.IndexOf(provNames, o)])));
            var models = new[] { "Auto", "Quick", "Balanced", "Deep" };
            var modelNames = new[] { "Auto (by task)", "Always fast (Haiku)", "Always balanced (Sonnet)", "Always deepest (Opus)" };
            p.Children.Add(Ui.Row("Model", Ui.Combo(modelNames, modelNames[Math.Max(0, Array.IndexOf(models, _s.AiModel))], o => _s.AiModel = models[Array.IndexOf(modelNames, o)])));

            var cc = (ClaudeCodeProvider)_ai.Provider(ProviderKind.ClaudeCode);
            string exe = cc.FindExecutable();
            p.Children.Add(Ui.Row("Claude Code", Ui.Label(exe != null ? "Found. Requests use your Claude Code login." : "Not found. Install Claude Code and sign in once with \"claude\".", 12, muted: true)));

            var key = new PasswordBox { MinWidth = 220, Padding = new Thickness(6, 5, 6, 5) };
            var keyRow = new StackPanel();
            keyRow.Children.Add(key);
            var keyBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var save = new Button { Content = "Save key" }; var clear = new Button { Content = "Remove key", Margin = new Thickness(6, 0, 0, 0) };
            var keyState = Ui.Label(_s.HasApiKey ? "A key is saved (encrypted for your Windows account)." : "No key saved.", 11.5, muted: true);
            save.Click += (o, e) => { if (key.Password.Trim().Length > 10) { _s.SetApiKey(key.Password); key.Clear(); keyState.Text = "Saved, encrypted for your Windows account."; App.SaveSettings(); } };
            clear.Click += (o, e) => { _s.SetApiKey(null); keyState.Text = "No key saved."; App.SaveSettings(); };
            keyBtns.Children.Add(save); keyBtns.Children.Add(clear);
            keyRow.Children.Add(keyBtns); keyRow.Children.Add(keyState);
            p.Children.Add(Ui.Row("Claude API key", keyRow, "Optional fallback"));

            var ol = (OllamaProvider)_ai.Provider(ProviderKind.Ollama);
            var olModels = ol.ListModels();
            if (olModels.Count > 0)
                p.Children.Add(Ui.Row("Local model", Ui.Combo(olModels, string.IsNullOrEmpty(_s.OllamaModel) ? olModels[0] : _s.OllamaModel, o => _s.OllamaModel = (string)o), "Used offline through Ollama"));
            else
                p.Children.Add(Ui.Row("Local model", Ui.Label("Ollama is not running.", 12, muted: true)));

            var testBtn = new Button { Content = "Test AI", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            var testOut = Ui.Label("", 12, muted: true);
            testBtn.Click += async (o, e) =>
            {
                testBtn.IsEnabled = false; testOut.Text = "Asking…";
                try
                {
                    var r = await _ai.RunAsync(AiTask.Custom, "Hello and welcome to the show.", "Reply with the script unchanged.", CancellationToken.None);
                    testOut.Text = $"Works: {r.Provider} · {r.Model}" + (r.CostUsd.HasValue ? $" · ${r.CostUsd:0.0000}" : "");
                }
                catch (Exception ex) { testOut.Text = ex.Message; }
                finally { testBtn.IsEnabled = true; }
            };
            p.Children.Add(testBtn); p.Children.Add(testOut);
            return p;
        }

        StackPanel PrivacyPage()
        {
            var p = new StackPanel();
            p.Children.Add(Ui.Switch("Hide the prompter from screen sharing and screenshots", _s.HideFromCapture, v => _s.HideFromCapture = v, "Uses the Windows capture-exclusion flag"));
            p.Children.Add(Ui.Switch("Hide this main window from screen sharing too", _s.HideMainWindowFromCapture, v => _s.HideMainWindowFromCapture = v));
            p.Children.Add(Ui.Label("\nWhere your data lives", 13, bold: true));
            p.Children.Add(Ui.Label("Notes and settings: " + App.DataDir + "\nNothing is sent anywhere unless you use an AI action. Hush has no analytics, no account and no network listener.", 12, muted: true));
            var open = new Button { Content = "Open notes folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            open.Click += (o, e) => System.Diagnostics.Process.Start("explorer.exe", "\"" + System.IO.Path.Combine(App.DataDir, "notes") + "\"");
            p.Children.Add(open);
            return p;
        }

        void StartMeter()
        {
            StopMeter();
            try
            {
                _meter = new VoicePipeline();
                _meter.Vad.Sensitivity = _s.Sensitivity; _meter.Vad.PauseMs = _s.PauseMs; _meter.Vad.NoiseSuppression = _s.NoiseSuppression;
                _meter.Vad.AutoGain = _s.AutoGain; _meter.Vad.CalibratedNoiseDb = _s.CalibratedNoiseDb; _meter.Vad.CalibratedSpeechDb = _s.CalibratedSpeechDb;
                _meter.Start(new WaveInSource(WaveInSource.ByName(_s.MicName)));
                _meterTimer.Start();
            }
            catch (Exception ex) { _levelText.Text = ex.Message; _meter?.Dispose(); _meter = null; }
        }

        void StopMeter() { _meterTimer.Stop(); _meter?.Dispose(); _meter = null; _level.Value = 0; }
    }

    // =====================================================================
    /// <summary>First-run setup: measures room noise, then your speaking level and pace.</summary>
    public sealed class CalibrationWindow : Window
    {
        const string Sentence = "The quick brown fox jumps over the lazy dog, and then I keep talking at my normal speaking pace for a few more seconds.";
        readonly Settings _s;
        readonly TextBlock _step = Ui.Label("", 15, bold: true);
        readonly TextBlock _hint = Ui.Label("", 13, muted: true);
        readonly ProgressBar _level = new ProgressBar { Height = 8, Maximum = 1, Margin = new Thickness(0, 14, 0, 0) };
        readonly Button _go = new Button { Content = "Start", Style = (Style)Application.Current.FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        VoicePipeline _p;
        bool _done;

        public CalibrationWindow(Settings s)
        {
            _s = s;
            Ui.Dialog(this, "Set up your voice", 560, 400);
            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(Ui.Label("Voice setup", 24, bold: true));
            panel.Children.Add(new Border { Height = 8 });
            panel.Children.Add(_step); panel.Children.Add(_hint); panel.Children.Add(_level); panel.Children.Add(_go);
            var skip = new Button { Content = "Skip for now", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0) };
            skip.Click += (o, e) => { _s.Calibrated = true; App.SaveSettings(); Close(); };
            panel.Children.Add(skip);
            Content = panel;
            _step.Text = "Two short steps: stay quiet for three seconds, then read one sentence aloud.";
            _hint.Text = "Use the microphone you will present with. You can change it later in Settings.";
            _go.Click += async (o, e) => { if (_done) Close(); else await Run(); };
            Closed += (o, e) => _p?.Dispose();
        }

        async Task Run()
        {
            _go.IsEnabled = false;
            try
            {
                _p = new VoicePipeline();
                _p.Vad.NoiseSuppression = _s.NoiseSuppression; _p.Vad.AutoGain = _s.AutoGain;
                var noise = new System.Collections.Generic.List<double>();
                var speech = new System.Collections.Generic.List<double>();
                bool collectNoise = true;
                _p.Vad.Frame += (db, sp) => { lock (noise) { if (collectNoise) noise.Add(db); else speech.Add(db); } };
                _p.Start(new WaveInSource(WaveInSource.ByName(_s.MicName)));
                var meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                meter.Tick += (o, e) => _level.Value = _p?.Vad.Level ?? 0;
                meter.Start();

                _step.Text = "Step 1 of 2: stay quiet…"; _hint.Text = "Measuring the room.";
                await Task.Delay(3000);
                lock (noise) collectNoise = false;
                _p.Tempo.Reset();
                _step.Text = "Step 2 of 2: read this aloud at your normal pace"; _hint.Text = "“" + Sentence + "”";
                await Task.Delay(7000);
                meter.Stop();

                double noiseDb, speechDb;
                lock (noise)
                {
                    noiseDb = Percentile(noise, 0.5);
                    // Speech level: the loud part of what was heard, ignoring the gaps between words.
                    speechDb = Percentile(speech, 0.8);
                }
                double? rate = _p.Tempo.Rate;
                _p.Dispose(); _p = null;

                if (speechDb < noiseDb + 6)
                {
                    _step.Text = "That was too quiet to tell speech from the room.";
                    _hint.Text = "Check that the right microphone is selected in Settings, move closer, and try again.";
                    _go.Content = "Try again"; _go.IsEnabled = true;
                    return;
                }
                _s.CalibratedNoiseDb = Math.Round(noiseDb, 1);
                _s.CalibratedSpeechDb = Math.Round(speechDb, 1);
                if (rate.HasValue && rate > 2 && rate < 8) _s.ReferenceSyllableRate = Math.Round(rate.Value, 2);
                _s.Calibrated = true;
                App.SaveSettings();
                _step.Text = "Done.";
                _hint.Text = $"Room {noiseDb:0} dB, voice {speechDb:0} dB" + (rate.HasValue ? $", pace {rate:0.0} syllables a second." : ".") + " You can recalibrate any time in Settings.";
                _go.Content = "Close"; _go.IsEnabled = true;
                _done = true;
            }
            catch (Exception ex)
            {
                _step.Text = "The microphone could not be opened.";
                _hint.Text = ex.Message + " Check Windows Settings › Privacy › Microphone.";
                _go.Content = "Try again"; _go.IsEnabled = true;
            }
        }

        static double Percentile(System.Collections.Generic.List<double> v, double q)
        {
            if (v.Count == 0) return -100;
            var s = v.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        }
    }

    // =====================================================================
    /// <summary>Runs an AI task and shows the proposed text before anything changes.</summary>
    public sealed class AiWindow : Window
    {
        public string Accepted { get; private set; }
        public bool AsNewNote { get; private set; }
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public AiWindow(AiService ai, AiTask task, string script, string instruction)
        {
            Ui.Dialog(this, "AI", 920, 640);
            var plan = ai.Plan(task, script);
            string who = plan.Providers.Count > 0 ? ai.Provider(plan.Providers[0]).Name : "no provider";

            var root = new DockPanel { Margin = new Thickness(22) };
            var head = Ui.Label(TaskName(task), 20, bold: true);
            var sub = Ui.Label($"Asking {who} · {plan.Tier} model ({plan.Reason})…", 12, muted: true);
            var headPanel = new StackPanel(); headPanel.Children.Add(head); headPanel.Children.Add(sub);
            DockPanel.SetDock(headPanel, Dock.Top); root.Children.Add(headPanel);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = "Cancel" };
            var copy = new Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
            var asNew = new Button { Content = "Save as new note", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
            var replace = new Button { Content = "Replace note", Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
            buttons.Children.Add(cancel); buttons.Children.Add(copy);
            if (task != AiTask.Coach) { buttons.Children.Add(asNew); buttons.Children.Add(replace); }
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

            var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            var output = new TextBox { Style = (Style)Application.Current.FindResource("Field"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = (FontFamily)Application.Current.FindResource("Mono"), FontSize = 13, Text = "" };
            if (task == AiTask.Coach)
            {
                grid.Children.Add(output);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                var orig = new TextBox { Style = (Style)Application.Current.FindResource("Field"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = output.FontFamily, FontSize = 13, Text = script, Foreground = Ui.Res("Muted") };
                var l = new DockPanel(); var ll = Ui.Label("Now", 12, muted: true); DockPanel.SetDock(ll, Dock.Top); l.Children.Add(ll); l.Children.Add(orig);
                var r = new DockPanel(); var rl = Ui.Label("Proposed (you can edit it)", 12, muted: true); DockPanel.SetDock(rl, Dock.Top); r.Children.Add(rl); r.Children.Add(output);
                Grid.SetColumn(r, 2);
                grid.Children.Add(l); grid.Children.Add(r);
            }
            root.Children.Add(grid);
            Content = root;

            output.Text = "Working…";
            cancel.Click += (o, e) => { _cts.Cancel(); DialogResult = false; };
            copy.Click += (o, e) => { try { Clipboard.SetText(output.Text); } catch { } };
            replace.Click += (o, e) => { Accepted = output.Text; DialogResult = true; };
            asNew.Click += (o, e) => { Accepted = output.Text; AsNewNote = true; DialogResult = true; };
            Closed += (o, e) => _cts.Cancel();

            Loaded += async (o, e) =>
            {
                try
                {
                    var res = await ai.RunAsync(task, script, instruction, _cts.Token);
                    output.Text = res.Text;
                    sub.Text = $"{res.Provider} · {res.Model} · {res.Route}" + (res.CostUsd.HasValue && res.CostUsd > 0 ? $" · ${res.CostUsd:0.0000}" : "");
                    copy.IsEnabled = asNew.IsEnabled = replace.IsEnabled = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { output.Text = ex.Message; sub.Text = "Failed"; }
            };
        }

        static string TaskName(AiTask t)
        {
            switch (t)
            {
                case AiTask.SplitCues: return "Split into cues";
                case AiTask.Bullets: return "Bullet points";
                case AiTask.Speakable: return "Make it speakable";
                case AiTask.Shorten: return "Shorten by a third";
                case AiTask.Coach: return "Rehearsal coach";
                default: return "Custom edit";
            }
        }
    }

    // =====================================================================
    public static class Prompt
    {
        public static string Ask(Window owner, string title, string question)
        {
            var w = Ui.Dialog(new Window(), title, 520, 250);
            w.Owner = owner; w.ResizeMode = ResizeMode.NoResize;
            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(Ui.Label(question, 13));
            var box = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Margin = new Thickness(0, 12, 0, 0), MaxLength = 2000 };
            sp.Children.Add(box);
            var ok = new Button { Content = "Run", Style = (Style)Application.Current.FindResource("Primary"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            string result = null;
            ok.Click += (o, e) => { result = box.Text; w.DialogResult = true; };
            sp.Children.Add(ok);
            w.Content = sp;
            w.Loaded += (o, e) => box.Focus();
            return w.ShowDialog() == true ? result : null;
        }
    }
}
