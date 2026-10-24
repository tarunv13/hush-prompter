using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hush.Ai;
using Hush.Audio;
using Hush.Core;
using Hush.Native;

namespace Hush.UI
{
    public sealed class MainWindow : Window
    {
        readonly NoteStore _store;
        readonly AiService _ai;
        readonly VoicePipeline _voice = new VoicePipeline();
        HotkeyManager _hotkeys;
        PrompterWindow _prompter;
        RehearsalStats _lastStats;
        Note _current;
        bool _loading;
        readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        readonly DispatcherTimer _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        // Controls
        readonly TextBox _search = new TextBox();
        readonly ListBox _list = new ListBox();
        readonly TextBox _title = new TextBox();
        readonly TextBox _body = new TextBox();
        readonly TextBlock _meta = new TextBlock();
        readonly TextBlock _status = new TextBlock();
        readonly Border _toast = new Border();
        readonly TextBlock _toastText = new TextBlock();
        readonly Button _notchBtn, _floatBtn, _aiBtn, _settingsBtn;

        public MainWindow()
        {
            _store = new NoteStore(Path.Combine(App.DataDir, "notes"));
            _ai = AiService.CreateDefault(() => App.Settings, Path.Combine(App.LocalDir, "ai-sandbox"));

            Title = "Hush";
            Width = 1100; Height = 720; MinWidth = 720; MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = (Brush)FindResource("Paper");
            FontFamily = (FontFamily)FindResource("Ui");
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/hush.ico"));
            UseLayoutRounding = true;
            AllowDrop = true;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- sidebar ----
            var side = new DockPanel { Background = (Brush)FindResource("Sidebar") };
            var sideBorder = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
            _search.Style = (Style)FindResource("Field");
            _search.ToolTip = "Search all notes (Ctrl+F)";
            _search.TextChanged += (s, e) => RefreshList();
            var searchHost = new Grid { Margin = new Thickness(12, 14, 12, 8) };
            var searchHint = new TextBlock
            {
                Text = "Search notes   Ctrl+F", Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 168)), FontSize = 13,
                Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };
            _search.TextChanged += (s, e) => searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            searchHost.Children.Add(_search); searchHost.Children.Add(searchHint);
            DockPanel.SetDock(searchHost, Dock.Top);
            side.Children.Add(searchHost);

            var newBtn = new Button { Content = "+  New note", Margin = new Thickness(12, 8, 12, 14), HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "New note (Ctrl+N)" };
            newBtn.Click += (s, e) => NewNote();
            DockPanel.SetDock(newBtn, Dock.Bottom);
            side.Children.Add(newBtn);

            _list.Background = Brushes.Transparent; _list.BorderThickness = new Thickness(0);
            _list.ItemContainerStyle = (Style)FindResource("NoteItem");
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.SelectionChanged += (s, e) => { if (_list.SelectedItem is ListBoxItem li && li.Tag is Note n && (_current == null || n.Id != _current.Id)) Open(n); };
            _list.ContextMenu = BuildListMenu();
            side.Children.Add(_list);
            grid.Children.Add(sideBorder);

            // ---- editor ----
            var editor = new DockPanel();
            Grid.SetColumn(editor, 1);

            var top = new DockPanel { Margin = new Thickness(24, 14, 20, 0), LastChildFill = false };
            _settingsBtn = ToolButton("", "Settings", (s, e) => OpenSettings());
            _aiBtn = ToolButton("", "AI", (s, e) => OpenAiMenu());
            _floatBtn = ToolButton("", "Floating", (s, e) => StartPrompter(true));
            _notchBtn = new Button { Style = (Style)FindResource("Primary"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Start in the notch (Ctrl+Enter)" };
            _notchBtn.Content = IconText("", "Start");
            _notchBtn.Click += (s, e) => StartPrompter(false);
            foreach (var b in new[] { _settingsBtn, _aiBtn, _floatBtn }) { DockPanel.SetDock(b, Dock.Right); }
            DockPanel.SetDock(_notchBtn, Dock.Right);
            top.Children.Add(_notchBtn); top.Children.Add(_floatBtn); top.Children.Add(_aiBtn); top.Children.Add(_settingsBtn);
            DockPanel.SetDock(top, Dock.Top);
            editor.Children.Add(top);

            var statusBar = new DockPanel { Margin = new Thickness(24, 6, 24, 10) };
            _status.Foreground = (Brush)FindResource("Muted"); _status.FontSize = 12;
            statusBar.Children.Add(_status);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            editor.Children.Add(statusBar);

            var column = new Grid { MaxWidth = 780, Margin = new Thickness(40, 8, 40, 0) };
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _title.Style = (Style)FindResource("Bare");
            _title.FontFamily = (FontFamily)FindResource("Serif"); _title.FontSize = 38;
            _title.Margin = new Thickness(0, 18, 0, 4);
            SetPlaceholder(_title, "Title");
            _title.TextChanged += (s, e) => QueueSave();
            column.Children.Add(_title);
            _meta.Foreground = (Brush)FindResource("Muted"); _meta.FontSize = 12; _meta.Margin = new Thickness(2, 0, 0, 14);
            Grid.SetRow(_meta, 1); column.Children.Add(_meta);
            _body.Style = (Style)FindResource("Bare");
            _body.FontFamily = (FontFamily)FindResource("Mono"); _body.FontSize = 15;
            TextBlock.SetLineHeight(_body, 26);
            TextBlock.SetLineStackingStrategy(_body, LineStackingStrategy.BlockLineHeight);
            _body.AcceptsReturn = true; _body.AcceptsTab = true; _body.TextWrapping = TextWrapping.Wrap;
            _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _body.SpellCheck.IsEnabled = true;
            SetPlaceholder(_body, "Write or paste your script. Use ## Heading or a --- line to split it into cues.");
            _body.TextChanged += (s, e) => { QueueSave(); UpdateMeta(); };
            _body.ContextMenu = BuildBodyMenu();
            Grid.SetRow(_body, 2); column.Children.Add(_body);
            editor.Children.Add(column);
            grid.Children.Add(editor);

            // ---- toast ----
            _toast.Background = new SolidColorBrush(Color.FromArgb(240, 28, 28, 31));
            _toast.CornerRadius = new CornerRadius(10); _toast.Padding = new Thickness(14, 9, 14, 10);
            _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Bottom;
            _toast.Margin = new Thickness(0, 0, 0, 36); _toast.Opacity = 0; _toast.IsHitTestVisible = false;
            _toastText.Foreground = Brushes.White; _toastText.FontSize = 13; _toastText.TextWrapping = TextWrapping.Wrap; _toastText.MaxWidth = 520;
            _toast.Child = _toastText;
            Grid.SetColumnSpan(_toast, 2);
            grid.Children.Add(_toast);

            Content = grid;

            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveCurrent(); };
            InputBindings.Add(new KeyBinding(new Cmd(() => StartPrompter(false)), Key.Enter, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Cmd(() => StartPrompter(true)), Key.Enter, ModifierKeys.Control | ModifierKeys.Shift));
            InputBindings.Add(new KeyBinding(new Cmd(() => { _search.Focus(); _search.SelectAll(); }), Key.F, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Cmd(NewNote), Key.N, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Cmd(OpenSettings), Key.OemComma, ModifierKeys.Control));

            Drop += OnDrop;
            DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

            SourceInitialized += (s, e) =>
            {
                Win32.ExcludeFromCapture(this, App.Settings.HideMainWindowFromCapture);
                _hotkeys = new HotkeyManager(this);
            };
            Loaded += OnLoaded;
            Closing += (s, e) => { SaveCurrent(); _prompter?.Close(); App.SaveSettings(); };
            Closed += (s, e) => { _voice.Dispose(); _hotkeys?.Dispose(); };
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            var notes = _store.All();
            if (notes.Count == 0) notes.Add(_store.Create("Welcome", WelcomeText));
            RefreshList();
            var open = App.Opts.OpenNote != null ? _store.Get(App.Opts.OpenNote) : null;
            Open(open ?? _store.All().First());
            UpdateStatus();

            if (App.Opts.StatusFile != null)
            {
                _statusTimer.Tick += (s, a) => WriteStatus();
                _statusTimer.Start();
            }

            if (App.Opts.StartMode == "notch" || App.Opts.StartMode == "float")
                Dispatcher.BeginInvoke(new Action(() => StartPrompter(App.Opts.StartMode == "float")), DispatcherPriority.ApplicationIdle);
            else if (!App.Settings.Calibrated && !App.Opts.SkipCalibration && App.Opts.AudioFile == null)
                Dispatcher.BeginInvoke(new Action(() => new CalibrationWindow(App.Settings) { Owner = this }.ShowDialog()), DispatcherPriority.ApplicationIdle);
        }

        // ---------------- notes ----------------

        void RefreshList()
        {
            string q = _search.Text;
            _list.Items.Clear();
            if (string.IsNullOrWhiteSpace(q))
            {
                foreach (var n in _store.All()) _list.Items.Add(NoteRow(n, null));
            }
            else
            {
                foreach (var h in _store.Search(q)) _list.Items.Add(NoteRow(h.Note, h.Snippet));
                if (_list.Items.Count == 0) _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No matches", Foreground = (Brush)FindResource("Muted") }, IsEnabled = false, Style = (Style)FindResource("NoteItem") });
            }
            if (_current != null)
                foreach (var it in _list.Items) if (it is ListBoxItem li && li.Tag is Note n && n.Id == _current.Id) { _loading = true; _list.SelectedItem = li; _loading = false; }
        }

        ListBoxItem NoteRow(Note n, string snippet)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(n.Title) ? "Untitled" : n.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("Ink") });
            string sub = snippet ?? n.Modified.ToLocalTime().ToString("d MMM, HH:mm");
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            return new ListBoxItem { Content = sp, Tag = n, Style = (Style)FindResource("NoteItem") };
        }

        void Open(Note n)
        {
            if (_loading) return;
            SaveCurrent();
            _loading = true;
            _current = _store.Get(n.Id) ?? n;
            _title.Text = _current.Title == "Untitled" ? "" : _current.Title;
            _body.Text = _current.Body;
            _loading = false;
            UpdateMeta();
            RefreshList();
        }

        void NewNote()
        {
            SaveCurrent();
            var n = _store.Create("Untitled", "");
            _search.Text = "";
            Open(n);
            _title.Focus();
        }

        void QueueSave() { if (_loading) return; _saveTimer.Stop(); _saveTimer.Start(); }

        void SaveCurrent()
        {
            if (_current == null || _loading) return;
            string t = _title.Text.Trim(), b = _body.Text;
            if (t == (_current.Title == "Untitled" ? "" : _current.Title) && b == _current.Body) return;
            _current.Title = t.Length == 0 ? "Untitled" : t;
            _current.Body = b;
            try { _store.Save(_current); } catch (Exception ex) { App.Log(ex); Toast("Could not save: " + ex.Message); }
            RefreshList();
        }

        void DeleteCurrent()
        {
            if (_current == null) return;
            var r = MessageBox.Show(this, "Delete \"" + (_current.Title ?? "Untitled") + "\"? This cannot be undone.", "Delete note", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
            _store.Delete(_current.Id);
            _current = null;
            var rest = _store.All();
            if (rest.Count == 0) rest.Add(_store.Create("Untitled", ""));
            Open(rest[0]);
        }

        void UpdateMeta()
        {
            string text = _body.Text;
            int words = ScriptParser.WordCount(text);
            var t = ScriptParser.ReadingTime(text);
            int cues = ScriptParser.Parse(text).Count;
            _meta.Text = $"{words} words · about {(t.TotalMinutes >= 1 ? Math.Round(t.TotalMinutes) + " min" : t.TotalSeconds + " s")} to read" + (cues > 1 ? $" · {cues} cues" : "");
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            Note last = null; var failed = new List<string>();
            foreach (var f in files.Take(20))
            {
                try
                {
                    var r = Importers.Import(f);
                    last = _store.Create(r.Title, r.Text);
                }
                catch (Exception ex) { failed.Add(Path.GetFileName(f) + ": " + ex.Message); }
            }
            if (last != null) { _search.Text = ""; Open(last); }
            Toast(failed.Count == 0 ? "Imported." : string.Join("\n", failed));
        }

        void ExportCurrent()
        {
            if (_current == null) return;
            SaveCurrent();
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = MakeSafeFileName(_current.Title) + ".md", Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt" };
            if (dlg.ShowDialog(this) != true) return;
            string content = dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? _current.Title + "\n\n" + _current.Body : "# " + _current.Title + "\n\n" + _current.Body;
            File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(false));
            Toast("Exported.");
        }

        void ImportFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Scripts|*.txt;*.md;*.markdown;*.rtf;*.docx;*.html;*.htm", Multiselect = false };
            if (dlg.ShowDialog(this) != true) return;
            try { var r = Importers.Import(dlg.FileName); var n = _store.Create(r.Title, r.Text); _search.Text = ""; Open(n); Toast("Imported."); }
            catch (Exception ex) { Toast("Could not import: " + ex.Message); }
        }

        static string MakeSafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = (s ?? "").Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "note" : (s.Length > 80 ? s.Substring(0, 80) : s);
        }

        ContextMenu BuildBodyMenu()
        {
            var m = new ContextMenu();
            m.Items.Add(new MenuItem { Command = ApplicationCommands.Cut });
            m.Items.Add(new MenuItem { Command = ApplicationCommands.Copy });
            m.Items.Add(new MenuItem { Command = ApplicationCommands.Paste });
            m.Items.Add(new Separator());
            var div = new MenuItem { Header = "Insert divider (new cue)" };
            div.Click += (s, e) => InsertAtCaret("\n---\n");
            var head = new MenuItem { Header = "Insert named cue" };
            head.Click += (s, e) => InsertAtCaret("\n## New section\n");
            m.Items.Add(div); m.Items.Add(head);
            return m;
        }

        ContextMenu BuildListMenu()
        {
            var m = new ContextMenu();
            var imp = new MenuItem { Header = "Import file…" }; imp.Click += (s, e) => ImportFile();
            var exp = new MenuItem { Header = "Export note…" }; exp.Click += (s, e) => ExportCurrent();
            var del = new MenuItem { Header = "Delete note…" }; del.Click += (s, e) => DeleteCurrent();
            m.Items.Add(imp); m.Items.Add(exp); m.Items.Add(new Separator()); m.Items.Add(del);
            return m;
        }

        void InsertAtCaret(string s)
        {
            int i = _body.CaretIndex;
            _body.Text = _body.Text.Insert(i, s);
            _body.CaretIndex = i + s.Length;
            _body.Focus();
        }

        // ---------------- prompter ----------------

        void StartPrompter(bool floating)
        {
            SaveCurrent();
            if (_prompter != null) { _prompter.SetFloating(floating); return; }
            var cues = ScriptParser.Parse(_body.Text);
            var s = App.Settings;
            var mode = s.ScrollModeValue;
            bool useVoice = false;

            // Voice input: a WAV file in tests, otherwise the chosen microphone.
            if (mode != ScrollMode.Continuous)
            {
                try
                {
                    IAudioSource src = App.Opts.AudioFile != null ? (IAudioSource)new WavFileSource(App.Opts.AudioFile) : new WaveInSource(WaveInSource.ByName(s.MicName));
                    ApplyVoiceSettings();
                    _voice.Start(src);
                    useVoice = true;
                }
                catch (Exception ex)
                {
                    // This run only: the saved mode is left as the user chose it.
                    Toast("Microphone unavailable (" + ex.Message + "). Scrolling at a constant speed for this run.");
                    mode = ScrollMode.Continuous;
                }
            }

            _prompter = new PrompterWindow(s, useVoice ? _voice : null, cues, floating, mode);
            _prompter.Stopped += OnPrompterStopped;
            _prompter.SilenceHint += name => Toast("Hush hears nothing from \"" + name + "\". Check the microphone in Settings › Voice.");
            _voice.SourceStopped += OnSourceStopped;
            _prompter.Show();
            RegisterHotkeys();
            UpdateStatus();
        }

        void OnSourceStopped(string why)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_prompter == null || App.Opts.AudioFile != null) return;
                // Hot-swap: if the device vanished, fall back to the system default microphone.
                try { _voice.Start(new WaveInSource(null)); Toast("Microphone changed: now listening to the default input."); }
                catch { Toast(why + " Scrolling stays paused until a microphone is available."); }
            }));
        }

        void OnPrompterStopped()
        {
            _lastStats = _prompter?.Stats;
            if (_prompter != null) _prompter.Stopped -= OnPrompterStopped;
            _prompter = null;
            _voice.SourceStopped -= OnSourceStopped;
            _voice.Stop();
            _hotkeys?.UnregisterAll();
            App.SaveSettings();
            Show(); Activate();
            UpdateStatus();
        }

        void RegisterHotkeys()
        {
            if (_hotkeys == null) return;
            var failed = new List<string>();
            void Reg(Key k, string name, Action a, bool repeat = false) { if (!_hotkeys.RegisterCtrlAlt(k, () => { if (_prompter != null) a(); }, repeat)) failed.Add(name); }
            Reg(Key.P, "Ctrl+Alt+P", () => _prompter.TogglePause());
            // Ctrl+Alt+Space is often taken (for example by the Claude desktop app), so it is only a bonus.
            _hotkeys.RegisterCtrlAlt(Key.Space, () => { if (_prompter != null) _prompter.TogglePause(); });
            Reg(Key.Right, "Ctrl+Alt+Right", () => _prompter.Faster(), true);
            Reg(Key.Left, "Ctrl+Alt+Left", () => _prompter.Slower(), true);
            Reg(Key.Up, "Ctrl+Alt+Up", () => _prompter.LineBack(), true);
            Reg(Key.Down, "Ctrl+Alt+Down", () => _prompter.LineForward(), true);
            Reg(Key.PageUp, "Ctrl+Alt+PgUp", () => _prompter.GoToCue(_prompter.CueIndex - 1));
            Reg(Key.PageDown, "Ctrl+Alt+PgDn", () => _prompter.GoToCue(_prompter.CueIndex + 1));
            Reg(Key.Q, "Ctrl+Alt+Q", () => _prompter.Close());
            if (failed.Count > 0) Toast("Another app already uses " + string.Join(", ", failed) + ".");
        }

        void ApplyVoiceSettings()
        {
            var s = App.Settings; var v = _voice.Vad;
            v.Sensitivity = s.Sensitivity; v.PauseMs = s.PauseMs; v.NoiseSuppression = s.NoiseSuppression; v.AutoGain = s.AutoGain;
            v.CalibratedNoiseDb = s.CalibratedNoiseDb; v.CalibratedSpeechDb = s.CalibratedSpeechDb;
            _voice.Tempo.ReferenceRate = s.ReferenceSyllableRate;
        }

        // ---------------- settings and AI ----------------

        void OpenSettings()
        {
            var w = new SettingsWindow(App.Settings, _ai) { Owner = this };
            w.ShowDialog();
            App.SaveSettings();
            ApplyVoiceSettings();
            Win32.ExcludeFromCapture(this, App.Settings.HideMainWindowFromCapture);
            _prompter?.ApplySettings();
            UpdateStatus();
        }

        void OpenAiMenu()
        {
            var m = new ContextMenu { PlacementTarget = _aiBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Item(string header, AiTask task, string hint)
            {
                var it = new MenuItem { Header = header, ToolTip = hint };
                it.Click += (s, e) => RunAi(task, null);
                m.Items.Add(it);
            }
            Item("Split into cues", AiTask.SplitCues, "Adds ## headings so you can jump between sections (fast model)");
            Item("Make it speakable", AiTask.Speakable, "Shorter sentences, natural spoken wording (balanced model)");
            Item("Bullet points", AiTask.Bullets, "Condense to speaking notes (fast model)");
            Item("Shorten by a third", AiTask.Shorten, "Keeps every key point (balanced model)");
            var custom = new MenuItem { Header = "Custom instruction…" };
            custom.Click += (s, e) =>
            {
                string instr = Prompt.Ask(this, "Custom AI edit", "What should change? For example: make it warmer, add a hook at the start.");
                if (!string.IsNullOrWhiteSpace(instr)) RunAi(AiTask.Custom, instr);
            };
            m.Items.Add(custom);
            m.Items.Add(new Separator());
            var coach = new MenuItem { Header = "Coach my last rehearsal", IsEnabled = _lastStats != null, ToolTip = _lastStats == null ? "Run the prompter once first" : "Feedback on pace and pauses (deep model)" };
            coach.Click += (s, e) => RunAi(AiTask.Coach, _lastStats?.Describe());
            m.Items.Add(coach);
            m.IsOpen = true;
        }

        void RunAi(AiTask task, string instruction)
        {
            SaveCurrent();
            if (_current == null) return;
            var w = new AiWindow(_ai, task, _body.Text, instruction) { Owner = this };
            if (w.ShowDialog() == true && w.Accepted != null)
            {
                if (w.AsNewNote)
                {
                    var n = _store.Create((_current.Title ?? "Untitled") + " (AI)", w.Accepted);
                    Open(n);
                }
                else
                {
                    _body.SelectAll();
                    _body.SelectedText = w.Accepted; // keeps Ctrl+Z undo
                    _body.CaretIndex = 0;
                }
                Toast("Applied. Ctrl+Z undoes it.");
            }
        }

        void UpdateStatus()
        {
            var s = App.Settings;
            string mic = s.ScrollModeValue == ScrollMode.Continuous ? "constant speed" : (string.IsNullOrEmpty(s.MicName) ? "default microphone" : s.MicName);
            var plan = _ai.Plan(AiTask.Speakable, "");
            string ai = plan.Providers.Count == 0 ? "AI off" : "AI: " + _ai.Provider(plan.Providers[0]).Name;
            string capture = s.HideFromCapture ? "hidden from screen sharing" : "visible in screen sharing";
            _status.Text = $"{s.Mode} · {mic} · {ScrollEngine.SpeedSteps[s.SpeedStep]:0} px/s · prompter {capture} · {ai}" + (_prompter != null ? " · running" : "");
        }

        void WriteStatus()
        {
            try
            {
                var p = _prompter;
                var d = new Dictionary<string, object>
                {
                    ["running"] = p != null,
                    ["mode"] = p?.Engine.Mode.ToString(),
                    ["offset"] = p?.Engine.Offset ?? 0,
                    ["maxOffset"] = p?.Engine.MaxOffset ?? 0,
                    ["speaking"] = _voice.Vad.IsSpeaking,
                    ["level"] = _voice.Vad.Level,
                    ["db"] = _voice.Vad.LastDb,
                    ["threshold"] = _voice.Vad.ThresholdDb,
                    ["tempo"] = _voice.Tempo.Factor,
                    ["paused"] = p?.Engine.Paused ?? false,
                    ["countdown"] = p?.Engine.CountdownRemaining ?? 0,
                    ["cue"] = p?.CueIndex ?? 0,
                    ["cues"] = p?.CueCount ?? 0,
                    ["floating"] = p?.Floating ?? false,
                    ["speedStep"] = p?.Engine.SpeedStep ?? App.Settings.SpeedStep,
                    ["affinity"] = p != null ? Win32.GetDisplayAffinity(p) : -1,
                    ["mainAffinity"] = Win32.GetDisplayAffinity(this),
                    ["windowLeft"] = p?.Left ?? 0, ["windowTop"] = p?.Top ?? 0, ["windowWidth"] = p?.ActualWidth ?? 0,
                    ["t"] = DateTime.UtcNow.ToString("o")
                };
                NoteStore.AtomicWrite(App.Opts.StatusFile, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }

        // ---------------- helpers ----------------

        public void Toast(string text)
        {
            _toastText.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(4200))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(4700))));
            _toast.BeginAnimation(OpacityProperty, a);
        }

        Button ToolButton(string glyph, string label, RoutedEventHandler click)
        {
            var b = new Button { Content = IconText(glyph, label), Margin = new Thickness(6, 0, 0, 0), ToolTip = label };
            b.Click += click;
            return b;
        }

        static StackPanel IconText(string glyph, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
            sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        /// <summary>Grey hint text shown while a box is empty.</summary>
        static void SetPlaceholder(TextBox box, string hint)
        {
            var brush = new VisualBrush
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                Visual = new TextBlock { Text = hint, FontFamily = box.FontFamily, FontSize = box.FontSize, Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 168)), Margin = new Thickness(2, 0, 0, 0) }
            };
            void Update()
            {
                if (brush.Visual is TextBlock tb) { tb.FontFamily = box.FontFamily; tb.FontSize = box.FontSize; }
                box.Background = string.IsNullOrEmpty(box.Text) ? (Brush)brush : (box.Style == (Style)Application.Current.FindResource("Field") ? Brushes.White : Brushes.Transparent);
            }
            box.TextChanged += (s, e) => Update();
            box.Loaded += (s, e) => Update();
        }

        const string WelcomeText =
@"Hush is a private teleprompter that sits at the top of your screen, right under your webcam, so you can read while you keep eye contact.

## Start
Press Start (Ctrl+Enter) to open the prompter under your camera, or Floating (Ctrl+Shift+Enter) for a window you can drag anywhere.

The prompter is hidden from screen sharing, screenshots and recordings. Only you can see it.

## Voice scrolling
The text moves while you speak and stops when you stop. Hover over the prompter to pause; click the pause button to pause for longer. Change the microphone, sensitivity and mode in Settings.

## Cues
Split a script into cues with a heading like ""## Opening"" or a line of three dashes. Jump between cues with the pager under the prompter, or Ctrl+Alt+Page Up and Page Down.

## Shortcuts while the prompter runs
Ctrl+Alt+P pauses. Ctrl+Alt+Left and Right change the speed. Ctrl+Alt+Up and Down move a line. Ctrl+Alt+Q stops.

## AI
The AI button can split a script into cues, make it easier to say aloud, shorten it, or coach your last rehearsal. It uses your Claude Code login, an API key, or a local Ollama model, and always shows you the change before applying it.

Your notes stay on this computer.";
    }

    /// <summary>Minimal ICommand for key bindings.</summary>
    sealed class Cmd : ICommand
    {
        readonly Action _a;
        public Cmd(Action a) { _a = a; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object p) => true;
        public void Execute(object p) => _a();
    }
}
