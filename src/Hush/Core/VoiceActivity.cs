using System;

namespace Hush.Core
{
    /// <summary>
    /// Decides whether the speaker is talking, from raw 16-bit PCM. No transcription.
    /// The level is measured after a band filter (about 150 Hz to 4 kHz) that removes mains hum and
    /// hiss; the threshold sits a set margin above a slowly tracked noise floor, or between the
    /// calibrated noise and speech levels.
    /// </summary>
    public sealed class VoiceActivityDetector
    {
        public int SampleRate { get; }

        /// <summary>0 (needs loud speech) to 1 (reacts to whispers). Default 0.5.</summary>
        public double Sensitivity { get => _sens; set => _sens = Math.Max(0, Math.Min(1, value)); }
        double _sens = 0.5;

        /// <summary>Silence needed before scrolling stops, in milliseconds.</summary>
        public double PauseMs { get; set; } = 450;
        /// <summary>Speech needed before scrolling starts, in milliseconds.</summary>
        public double AttackMs { get; set; } = 60;

        public bool NoiseSuppression { get; set; } = true;
        /// <summary>Lowers the absolute floor for quiet microphones.</summary>
        public bool AutoGain { get; set; } = true;

        /// <summary>Calibrated levels in dBFS, or null for adaptive mode.</summary>
        public double? CalibratedNoiseDb { get; set; }
        public double? CalibratedSpeechDb { get; set; }

        public bool IsSpeaking { get; private set; }
        /// <summary>Smoothed 0..1 loudness for the voice beam.</summary>
        public double Level { get; private set; }
        public double LastDb { get; private set; } = -100;
        public double NoiseFloorDb { get; private set; } = -60;
        public double ThresholdDb { get; private set; } = -50;

        /// <summary>Raised once per analysis frame (10 ms) with the frame level in dBFS.</summary>
        public event Action<double, bool> Frame;

        readonly Biquad _hp, _hp2, _lp;
        readonly int _frameLen;
        int _frameFill;
        double _frameSumSq;
        double _aboveMs, _belowMs;

        public VoiceActivityDetector(int sampleRate = 16000)
        {
            SampleRate = sampleRate;
            _frameLen = sampleRate / 100; // 10 ms
            // Two cascaded high-pass sections (4th order) at 150 Hz remove mains hum at 50/60 Hz
            // by about 38 dB while keeping the harmonics that carry speech energy.
            _hp = Biquad.HighPass(sampleRate, 150, 0.707);
            _hp2 = Biquad.HighPass(sampleRate, 150, 0.707);
            _lp = Biquad.LowPass(sampleRate, 4000, 0.707);
        }

        public void Reset()
        {
            IsSpeaking = false; Level = 0; _aboveMs = _belowMs = 0; _frameFill = 0; _frameSumSq = 0;
            NoiseFloorDb = CalibratedNoiseDb ?? -60;
        }

        public void Process(short[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double x = samples[i] / 32768.0;
                if (NoiseSuppression) x = _lp.Process(_hp2.Process(_hp.Process(x)));
                _frameSumSq += x * x;
                if (++_frameFill >= _frameLen)
                {
                    double rms = Math.Sqrt(_frameSumSq / _frameFill);
                    _frameFill = 0; _frameSumSq = 0;
                    AnalyseFrame(rms);
                }
            }
        }

        void AnalyseFrame(double rms)
        {
            const double frameMs = 10;
            double db = rms > 1e-9 ? 20 * Math.Log10(rms) : -100;
            LastDb = db;

            // Track the noise floor: falls fast to quieter frames, rises slowly (2 dB per second)
            // and never while speech is detected.
            if (db < NoiseFloorDb) NoiseFloorDb = NoiseFloorDb + (db - NoiseFloorDb) * 0.2;
            else if (!IsSpeaking) NoiseFloorDb += 2.0 * frameMs / 1000.0;
            double absoluteMin = AutoGain ? -72 : -62;
            if (NoiseFloorDb < absoluteMin) NoiseFloorDb = absoluteMin;

            if (CalibratedNoiseDb.HasValue && CalibratedSpeechDb.HasValue && CalibratedSpeechDb > CalibratedNoiseDb + 3)
            {
                double noise = Math.Max(CalibratedNoiseDb.Value, NoiseFloorDb - 3);
                double span = CalibratedSpeechDb.Value - CalibratedNoiseDb.Value;
                // Sensitivity 1 puts the threshold 20% of the way up, 0 puts it 80% up.
                ThresholdDb = noise + span * (0.8 - 0.6 * Sensitivity);
            }
            else
            {
                double margin = 18 - 12 * Sensitivity; // 18 dB (low) .. 6 dB (high)
                ThresholdDb = NoiseFloorDb + margin;
            }

            if (db > ThresholdDb) { _aboveMs += frameMs; _belowMs = 0; }
            else if (db < ThresholdDb - 3) { _belowMs += frameMs; _aboveMs = 0; }

            if (!IsSpeaking && _aboveMs >= AttackMs) IsSpeaking = true;
            else if (IsSpeaking && _belowMs >= PauseMs) IsSpeaking = false;

            double target = Math.Max(0, Math.Min(1, (db - NoiseFloorDb) / 30.0));
            Level += (target - Level) * (target > Level ? 0.5 : 0.12);

            Frame?.Invoke(db, IsSpeaking);
        }

        /// <summary>Second-order IIR filter (RBJ cookbook).</summary>
        sealed class Biquad
        {
            double b0, b1, b2, a1, a2, z1, z2;
            public double Process(double x)
            {
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
            public static Biquad HighPass(int fs, double f, double q) => Make(fs, f, q, true);
            public static Biquad LowPass(int fs, double f, double q) => Make(fs, f, q, false);
            static Biquad Make(int fs, double f, double q, bool high)
            {
                double w = 2 * Math.PI * f / fs, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
                double a0 = 1 + alpha;
                var bq = new Biquad();
                if (high)
                {
                    bq.b0 = (1 + cos) / 2 / a0; bq.b1 = -(1 + cos) / a0; bq.b2 = (1 + cos) / 2 / a0;
                }
                else
                {
                    bq.b0 = (1 - cos) / 2 / a0; bq.b1 = (1 - cos) / a0; bq.b2 = (1 - cos) / 2 / a0;
                }
                bq.a1 = -2 * cos / a0; bq.a2 = (1 - alpha) / a0;
                return bq;
            }
        }
    }
}
