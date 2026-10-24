using System;
using System.IO;
using Hush.Audio;
using Hush.Core;
using static Hush.Tests.Program;

namespace Hush.Tests
{
    /// <summary>Voice detection and tempo on synthetic audio with known properties.</summary>
    static class AudioTests
    {
        const int Fs = 16000;

        public static void Register()
        {
            Test("VAD: background noise alone is not speech", () =>
            {
                var v = new VoiceActivityDetector();
                Feed(v, Noise(4.0, -50), out int speechFrames);
                Assert.Equal(0, speechFrames, "frames flagged as speech");
            });
            Test("VAD: speech after noise is detected within 150 ms", () =>
            {
                var v = new VoiceActivityDetector();
                Feed(v, Noise(2.0, -50), out _);
                int firstSpeech = -1, frame = 0;
                v.Frame += (db, sp) => { if (sp && firstSpeech < 0) firstSpeech = frame; frame++; };
                Feed(v, Mix(Speech(2.0, 5.0, -18), Noise(2.0, -50)), out _);
                Assert.True(firstSpeech >= 0 && firstSpeech <= 15, "first speech frame " + firstSpeech);
            });
            Test("VAD: stays on through gaps between syllables", () =>
            {
                var v = new VoiceActivityDetector();
                Feed(v, Noise(2.0, -50), out _);
                Feed(v, Speech(0.5, 4.0, -18), out _);
                int off = 0; v.Frame += (db, sp) => { if (!sp) off++; };
                Feed(v, Mix(Speech(3.0, 4.0, -18), Noise(3.0, -50)), out _);
                Assert.Equal(0, off, "frames dropped during continuous speech");
            });
            Test("VAD: stops after the pause threshold", () =>
            {
                var v = new VoiceActivityDetector { PauseMs = 450 };
                Feed(v, Noise(2.0, -50), out _);
                Feed(v, Speech(2.0, 5.0, -18), out _);
                Assert.True(v.IsSpeaking, "speaking before the pause");
                int frame = 0, stoppedAt = -1;
                v.Frame += (db, sp) => { if (!sp && stoppedAt < 0) stoppedAt = frame; frame++; };
                Feed(v, Noise(2.0, -50), out _);
                Assert.True(stoppedAt >= 40 && stoppedAt <= 60, "stopped after " + stoppedAt * 10 + " ms");
            });
            Test("VAD: mains hum is filtered out", () =>
            {
                var v = new VoiceActivityDetector { NoiseSuppression = true };
                Feed(v, Mix(Noise(1.0, -55), Tone(1.0, 50, -55)), out _);
                Feed(v, Mix(Noise(4.0, -55), Tone(4.0, 50, -22)), out int sp);
                Assert.Equal(0, sp, "a sudden 50 Hz hum must not count as speech");
            });
            Test("VAD: calibrated thresholds follow sensitivity", () =>
            {
                var v = new VoiceActivityDetector { CalibratedNoiseDb = -60, CalibratedSpeechDb = -20 };
                v.Sensitivity = 0; Feed(v, Noise(0.3, -60), out _); double low = v.ThresholdDb;
                v.Sensitivity = 1; Feed(v, Noise(0.3, -60), out _); double high = v.ThresholdDb;
                Assert.True(low > high + 15, $"threshold {low:0.0} at 0 should be well above {high:0.0} at 1");
            });
            Test("VAD: quiet speech is caught at high sensitivity only", () =>
            {
                var lo = new VoiceActivityDetector { Sensitivity = 0 };
                var hi = new VoiceActivityDetector { Sensitivity = 1 };
                var bg = Noise(2.0, -50); var quiet = Mix(Speech(3.0, 5.0, -42), Noise(3.0, -50));
                Feed(lo, bg, out _); Feed(lo, quiet, out int a);
                Feed(hi, bg, out _); Feed(hi, quiet, out int b);
                Assert.True(b > 100 && a < b / 3, $"high sensitivity {b} frames vs low {a}");
            });
            Test("VAD: level rises with loudness", () =>
            {
                var v = new VoiceActivityDetector();
                Feed(v, Noise(1.0, -50), out _); double quiet = v.Level;
                Feed(v, Speech(1.0, 5, -12), out _); Assert.True(v.Level > quiet + 0.3, "level " + v.Level);
            });

            Test("Tempo: measures syllable rate of 3, 4.5 and 6 per second", () =>
            {
                foreach (double rate in new[] { 3.0, 4.5, 6.0 })
                {
                    var v = new VoiceActivityDetector(); var t = new TempoEstimator();
                    v.Frame += t.AddFrame;
                    Feed(v, Noise(1.0, -50), out _);
                    Feed(v, Mix(Speech(6.0, rate, -18), Noise(6.0, -50)), out _);
                    Assert.True(t.Rate.HasValue, "rate known");
                    Assert.Near(rate, t.Rate.Value, rate * 0.18, "measured rate");
                }
            });
            Test("Tempo: faster speech raises the factor above 1", () =>
            {
                var v = new VoiceActivityDetector(); var t = new TempoEstimator { ReferenceRate = 4.0 };
                v.Frame += t.AddFrame;
                Feed(v, Noise(1.0, -50), out _);
                Feed(v, Mix(Speech(12.0, 6.0, -18), Noise(12.0, -50)), out _);
                Assert.True(t.Factor > 1.2, "factor " + t.Factor);
            });

            Test("WAV: 44.1 kHz stereo is read as 16 kHz mono", () =>
            {
                string p = Path.Combine(CoreTests.TempDir(), "t.wav");
                WriteWav(p, 44100, 2, 1.0, 440);
                var pcm = WavFileSource.LoadAs16kMono(p);
                Assert.Near(16000, pcm.Length, 2, "samples for one second");
                double peak = 0; foreach (var s in pcm) peak = Math.Max(peak, Math.Abs(s));
                Assert.True(peak > 8000 && peak < 12000, "peak " + peak);
            });
            Test("WAV: non-PCM and junk files are rejected", () =>
            {
                string p = Path.Combine(CoreTests.TempDir(), "bad.wav");
                File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
                Assert.Throws<InvalidDataException>(() => WavFileSource.LoadAs16kMono(p));
            });
        }

        // ---------------- signal helpers ----------------

        static readonly Random Rng = new Random(7);

        static double Amp(double dbfs) => Math.Pow(10, dbfs / 20);

        static double[] Noise(double sec, double dbfs)
        {
            var x = new double[(int)(sec * Fs)]; double a = Amp(dbfs) * Math.Sqrt(3); // uniform noise RMS = a/sqrt(3)
            for (int i = 0; i < x.Length; i++) x[i] = (Rng.NextDouble() * 2 - 1) * a;
            return x;
        }

        static double[] Tone(double sec, double hz, double dbfs)
        {
            var x = new double[(int)(sec * Fs)]; double a = Amp(dbfs) * Math.Sqrt(2);
            for (int i = 0; i < x.Length; i++) x[i] = a * Math.Sin(2 * Math.PI * hz * i / Fs);
            return x;
        }

        /// <summary>Voiced sound (140 Hz with harmonics) shaped into syllables at a given rate.</summary>
        static double[] Speech(double sec, double syllablesPerSec, double peakDbfs)
        {
            var x = new double[(int)(sec * Fs)]; double a = Amp(peakDbfs);
            for (int i = 0; i < x.Length; i++)
            {
                double t = (double)i / Fs;
                double phase = (t * syllablesPerSec) % 1.0;
                // Each syllable: a 70% loud nucleus with a dip of 18 dB between syllables.
                double env = phase < 0.7 ? Math.Sin(Math.PI * phase / 0.7) : 0;
                env = 0.12 + 0.88 * env;
                double s = 0;
                for (int h = 1; h <= 8; h++) s += Math.Sin(2 * Math.PI * 140 * h * t + h) / h;
                x[i] = a * env * s / 2.0;
            }
            return x;
        }

        static double[] Mix(double[] a, double[] b)
        {
            var x = new double[Math.Min(a.Length, b.Length)];
            for (int i = 0; i < x.Length; i++) x[i] = a[i] + b[i];
            return x;
        }

        static void Feed(VoiceActivityDetector v, double[] x, out int speechFrames)
        {
            int count = 0;
            Action<double, bool> h = (db, sp) => { if (sp) count++; };
            v.Frame += h;
            var buf = new short[640];
            for (int pos = 0; pos < x.Length; pos += buf.Length)
            {
                int n = Math.Min(buf.Length, x.Length - pos);
                for (int i = 0; i < n; i++) buf[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, x[pos + i] * 32767));
                v.Process(buf, n);
            }
            v.Frame -= h;
            speechFrames = count;
        }

        public static void WriteWav(string path, int rate, int channels, double sec, double hz)
        {
            int frames = (int)(rate * sec);
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write("RIFF".ToCharArray()); bw.Write(36 + frames * channels * 2); bw.Write("WAVE".ToCharArray());
                bw.Write("fmt ".ToCharArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)channels); bw.Write(rate);
                bw.Write(rate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
                bw.Write("data".ToCharArray()); bw.Write(frames * channels * 2);
                for (int i = 0; i < frames; i++)
                {
                    short s = (short)(10000 * Math.Sin(2 * Math.PI * hz * i / rate));
                    for (int c = 0; c < channels; c++) bw.Write(s);
                }
            }
        }
    }
}
