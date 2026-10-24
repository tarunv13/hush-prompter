using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Hush.Core
{
    /// <summary>
    /// User settings, stored as JSON in the user's profile. Every value is clamped on load, so a
    /// hand-edited or tampered file cannot push the app into a bad state. The API key is never
    /// stored in clear text: it is encrypted with Windows DPAPI for the current user.
    /// </summary>
    public sealed class Settings
    {
        // Prompter appearance
        public double FontSize { get; set; } = 22;
        public string TextColor { get; set; } = "#F4F5F8";
        public string BeamColor { get; set; } = "#5865FF";
        public double NotchWidth { get; set; } = 560;
        public double NotchHeight { get; set; } = 150;
        public double FloatLeft { get; set; } = double.NaN;
        public double FloatTop { get; set; } = double.NaN;
        public double FloatWidth { get; set; } = 620;
        public double FloatHeight { get; set; } = 300;
        public bool StartFloating { get; set; }
        public double CountdownSeconds { get; set; } = 3;
        public bool CueMode { get; set; }
        public bool HoverToPause { get; set; } = true;

        // Privacy
        public bool HideFromCapture { get; set; } = true;
        public bool HideMainWindowFromCapture { get; set; } = true;

        // Voice
        public string Mode { get; set; } = "Voice";
        public int SpeedStep { get; set; } = 6;
        public string MicName { get; set; } = "";
        public double Sensitivity { get; set; } = 0.5;
        public double PauseMs { get; set; } = 450;
        public bool AdaptiveTempo { get; set; } = true;
        public bool NoiseSuppression { get; set; } = true;
        public bool AutoGain { get; set; } = true;
        public double? CalibratedNoiseDb { get; set; }
        public double? CalibratedSpeechDb { get; set; }
        public double ReferenceSyllableRate { get; set; } = 4.5;
        public string SpeechCulture { get; set; } = "";
        public bool Calibrated { get; set; }

        // AI
        public string AiProvider { get; set; } = "Auto";
        public string AiModel { get; set; } = "Auto";
        public string OllamaModel { get; set; } = "";
        public string ApiKeyProtected { get; set; } = "";

        public ScrollMode ScrollModeValue
        {
            get => Enum.TryParse(Mode, out ScrollMode m) ? m : ScrollMode.Voice;
            set => Mode = value.ToString();
        }

        public void Clamp()
        {
            FontSize = Range(FontSize, 12, 72, 22);
            NotchWidth = Range(NotchWidth, 280, 1600, 560);
            NotchHeight = Range(NotchHeight, 70, 700, 150);
            FloatWidth = Range(FloatWidth, 260, 3000, 620);
            FloatHeight = Range(FloatHeight, 120, 2000, 300);
            CountdownSeconds = Range(CountdownSeconds, 0, 10, 3);
            SpeedStep = (int)Range(SpeedStep, 0, ScrollEngine.SpeedSteps.Length - 1, 6);
            Sensitivity = Range(Sensitivity, 0, 1, 0.5);
            PauseMs = Range(PauseMs, 150, 2000, 450);
            ReferenceSyllableRate = Range(ReferenceSyllableRate, 2, 8, 4.5);
            if (CalibratedNoiseDb.HasValue) CalibratedNoiseDb = Range(CalibratedNoiseDb.Value, -100, 0, -60);
            if (CalibratedSpeechDb.HasValue) CalibratedSpeechDb = Range(CalibratedSpeechDb.Value, -100, 0, -30);
            if (!IsColor(TextColor)) TextColor = "#F4F5F8";
            if (!IsColor(BeamColor)) BeamColor = "#5865FF";
            if (!Enum.TryParse(Mode, out ScrollMode _)) Mode = "Voice";
            if (!Enum.TryParse(AiProvider, out ProviderPreference _)) AiProvider = "Auto";
            if (!Enum.TryParse(AiModel, out ModelPreference _)) AiModel = "Auto";
            MicName = Trunc(MicName, 128); OllamaModel = Trunc(OllamaModel, 128); SpeechCulture = Trunc(SpeechCulture, 16);
            if (ApiKeyProtected != null && ApiKeyProtected.Length > 8192) ApiKeyProtected = "";
            if (!double.IsNaN(FloatLeft) && (FloatLeft < -10000 || FloatLeft > 20000)) FloatLeft = double.NaN;
            if (!double.IsNaN(FloatTop) && (FloatTop < -10000 || FloatTop > 20000)) FloatTop = double.NaN;
        }

        static double Range(double v, double lo, double hi, double dflt) => double.IsNaN(v) || double.IsInfinity(v) ? dflt : Math.Max(lo, Math.Min(hi, v));
        static string Trunc(string s, int n) => s == null ? "" : (s.Length > n ? s.Substring(0, n) : s);

        public static bool IsColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#' || (s.Length != 7 && s.Length != 9)) return false;
            for (int i = 1; i < s.Length; i++) if (Uri.IsHexDigit(s[i]) == false) return false;
            return true;
        }

        // ---------- persistence ----------

        public static Settings Load(string path)
        {
            Settings s = null;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 256 * 1024)
                {
                    var ser = new JavaScriptSerializer { MaxJsonLength = 256 * 1024, RecursionLimit = 8 };
                    var dict = ser.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                    if (dict != null) s = FromDictionary(dict);
                }
            }
            catch (Exception) { s = null; } // a corrupt file falls back to defaults
            s = s ?? new Settings();
            s.Clamp();
            return s;
        }

        public void Save(string path)
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var ser = new JavaScriptSerializer();
            var dict = ToDictionary();
            NoteStore.AtomicWrite(path, ser.Serialize(dict));
        }

        Dictionary<string, object> ToDictionary()
        {
            var d = new Dictionary<string, object>();
            foreach (var p in typeof(Settings).GetProperties())
            {
                if (!p.CanWrite || p.Name == nameof(ScrollModeValue)) continue;
                object v = p.GetValue(this);
                if (v is double dv && double.IsNaN(dv)) v = null;
                d[p.Name] = v;
            }
            return d;
        }

        static Settings FromDictionary(Dictionary<string, object> d)
        {
            var s = new Settings();
            foreach (var p in typeof(Settings).GetProperties())
            {
                if (!p.CanWrite || p.Name == nameof(ScrollModeValue) || !d.TryGetValue(p.Name, out object v)) continue;
                try
                {
                    Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    if (v == null) { if (Nullable.GetUnderlyingType(p.PropertyType) != null) p.SetValue(s, null); else if (t == typeof(double)) p.SetValue(s, double.NaN); continue; }
                    if (t == typeof(string)) p.SetValue(s, v as string ?? "");
                    else if (t == typeof(bool) && v is bool b) p.SetValue(s, b);
                    else if (t == typeof(double)) p.SetValue(s, Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
                    else if (t == typeof(int)) p.SetValue(s, Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception) { /* ignore one bad field, keep the default */ }
            }
            return s;
        }

        // ---------- secrets ----------

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hush.Prompter.v1.api-key");

        public void SetApiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { ApiKeyProtected = ""; return; }
            byte[] plain = Encoding.UTF8.GetBytes(key.Trim());
            try
            {
                ApiKeyProtected = Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public string GetApiKey()
        {
            if (string.IsNullOrEmpty(ApiKeyProtected)) return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), Entropy, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(plain); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (Exception) { return null; }
        }

        public bool HasApiKey => !string.IsNullOrEmpty(ApiKeyProtected);
    }
}
