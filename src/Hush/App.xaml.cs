using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Hush.Core;
using Hush.UI;

namespace Hush
{
    /// <summary>Command-line options. The test hooks never touch the network or other users' data.</summary>
    public sealed class Options
    {
        public string DataDir;
        public string AudioFile;
        public string StatusFile;
        public string StartMode;  // "notch" or "float": start the prompter on launch
        public string OpenNote;
        public bool SkipCalibration;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : null;
                switch (a)
                {
                    case "--data-dir": o.DataDir = AbsoluteOrNull(Next()); break;
                    case "--audio-file": o.AudioFile = AbsoluteOrNull(Next()); break;
                    case "--status-file": o.StatusFile = AbsoluteOrNull(Next()); break;
                    case "--start": o.StartMode = Next(); break;
                    case "--open-note": o.OpenNote = Next(); break;
                    case "--skip-calibration": o.SkipCalibration = true; break;
                }
            }
            return o;
        }

        static string AbsoluteOrNull(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            try { return Path.IsPathRooted(p) ? Path.GetFullPath(p) : null; } catch { return null; }
        }
    }

    public partial class App : Application
    {
        public static Options Opts { get; private set; } = new Options();
        public static string DataDir { get; private set; }
        public static string LocalDir { get; private set; }
        public static Settings Settings { get; private set; }
        public static string SettingsPath => Path.Combine(DataDir, "settings.json");

        Mutex _single;
        EventWaitHandle _activate;

        protected override void OnStartup(StartupEventArgs e)
        {
            Opts = Options.Parse(e.Args);
            DataDir = Opts.DataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hush");
            LocalDir = Opts.DataDir != null ? Path.Combine(Opts.DataDir, "local") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hush");
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LocalDir);

            // One instance per data folder; a second launch brings the first to the front.
            string key = "Hush-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(DataDir.ToLowerInvariant()))).Substring(0, 16).Replace('/', '_').Replace('+', '-');
            _single = new Mutex(true, "Local\\" + key, out bool first);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + key + "-show");
            if (!first) { _activate.Set(); Shutdown(0); return; }

            DispatcherUnhandledException += OnCrash;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log(a.ExceptionObject as Exception);

            Settings = Hush.Core.Settings.Load(SettingsPath);
            base.OnStartup(e);

            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            new Thread(() =>
            {
                while (_activate.WaitOne())
                    Dispatcher.BeginInvoke(new Action(() => { main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal; main.Activate(); }));
            }) { IsBackground = true, Name = "Hush activate" }.Start();
        }

        public static void SaveSettings()
        {
            try { Settings.Save(SettingsPath); } catch (Exception ex) { Log(ex); }
        }

        void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception);
            MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nYour notes are saved. Details are in the log folder.", "Hush", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        /// <summary>Writes the error type and stack only; no note text ever goes to the log.</summary>
        public static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(LocalDir ?? Path.GetTempPath(), "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.UtcNow.ToString("o") + " " + ex.GetType().FullName + ": " + ex.Message + "\n" + ex.StackTrace + "\n\n");
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _single?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
