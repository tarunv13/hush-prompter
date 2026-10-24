using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading;
using Hush.Core;

namespace Hush.Audio
{
    /// <summary>
    /// Owns one audio source and feeds every analyser from it: the voice detector, the tempo
    /// estimator and, in word-follow mode, the offline Windows speech recogniser.
    /// All public state is read by the UI thread; writes happen on the audio thread.
    /// </summary>
    public sealed class VoicePipeline : IDisposable
    {
        public VoiceActivityDetector Vad { get; } = new VoiceActivityDetector(WaveInSource.SampleRate);
        public TempoEstimator Tempo { get; } = new TempoEstimator();
        public IAudioSource Source { get; private set; }
        public string LastError { get; private set; }

        /// <summary>Seconds since the source started without any detected speech.</summary>
        public double SecondsWithoutSpeech => _framesNoSpeech / 100.0;
        public double SpeakingSeconds => _speakingFrames / 100.0;
        public int PauseCount { get; private set; }

        /// <summary>Raised on the audio thread when the recogniser hears words (word-follow mode).</summary>
        public event Action<string> WordsHeard;
        public event Action<string> SourceStopped;

        SpeechFollower _follower;
        long _framesNoSpeech, _speakingFrames;
        bool _wasSpeaking;

        public VoicePipeline()
        {
            Vad.Frame += (db, speaking) =>
            {
                Tempo.AddFrame(db, speaking);
                if (speaking) { _framesNoSpeech = 0; _speakingFrames++; }
                else _framesNoSpeech++;
                if (_wasSpeaking && !speaking) PauseCount++;
                _wasSpeaking = speaking;
            };
        }

        public void Start(IAudioSource source)
        {
            Stop();
            LastError = null;
            Source = source;
            Vad.Reset(); Tempo.Reset();
            _framesNoSpeech = 0; _speakingFrames = 0; PauseCount = 0; _wasSpeaking = false;
            source.Samples += OnSamples;
            source.Stopped += OnStopped;
            source.Start();
        }

        void OnSamples(short[] buf, int n)
        {
            Vad.Process(buf, n);
            _follower?.Push(buf, n);
        }

        void OnStopped(string why)
        {
            LastError = why;
            SourceStopped?.Invoke(why);
        }

        /// <summary>Starts word following for a script. Returns false if no recogniser is installed.</summary>
        public bool StartFollowing(IEnumerable<string> vocabulary, string culture)
        {
            StopFollowing();
            try
            {
                _follower = new SpeechFollower(vocabulary, culture);
                _follower.Heard += t => WordsHeard?.Invoke(t);
                _follower.Start();
                return true;
            }
            catch (Exception e)
            {
                LastError = "Word follow is unavailable: " + e.Message;
                _follower?.Dispose(); _follower = null;
                return false;
            }
        }

        public void StopFollowing() { _follower?.Dispose(); _follower = null; }

        public void Stop()
        {
            StopFollowing();
            if (Source != null)
            {
                Source.Samples -= OnSamples;
                Source.Stopped -= OnStopped;
                try { Source.Dispose(); } catch { }
                Source = null;
            }
        }

        public void Dispose() => Stop();

        public static List<CultureInfo> InstalledRecognizers()
        {
            try { return SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture).ToList(); }
            catch { return new List<CultureInfo>(); }
        }
    }

    /// <summary>
    /// Offline recognition limited to the words of the script (1 to 6 at a time), which is far
    /// more accurate than open dictation and keeps everything on the machine.
    /// </summary>
    sealed class SpeechFollower : IDisposable
    {
        public event Action<string> Heard;
        readonly SpeechRecognitionEngine _engine;
        readonly PcmPipe _pipe = new PcmPipe();

        public SpeechFollower(IEnumerable<string> vocabulary, string culture)
        {
            var info = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => !string.IsNullOrEmpty(culture) && r.Culture.Name.Equals(culture, StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();
            if (info == null) throw new InvalidOperationException("No Windows speech recogniser is installed.");
            var words = vocabulary.Where(w => w.Length > 0).Distinct().Take(3000).ToArray();
            if (words.Length == 0) throw new InvalidOperationException("The script has no words.");

            _engine = new SpeechRecognitionEngine(info);
            var gb = new GrammarBuilder { Culture = info.Culture };
            gb.Append(new Choices(words), 1, 6);
            _engine.LoadGrammar(new Grammar(gb) { Name = "script" });
            _engine.SetInputToAudioStream(_pipe, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _engine.SpeechHypothesized += (s, e) => { if (e.Result != null && e.Result.Confidence > 0.3) Heard?.Invoke(e.Result.Text); };
            _engine.SpeechRecognized += (s, e) => { if (e.Result != null && e.Result.Confidence > 0.25) Heard?.Invoke(e.Result.Text); };
        }

        public void Start() => _engine.RecognizeAsync(RecognizeMode.Multiple);

        public void Push(short[] buf, int n) => _pipe.Write(buf, n);

        public void Dispose()
        {
            try { _pipe.Close(); } catch { }
            try { _engine.RecognizeAsyncCancel(); } catch { }
            try { _engine.Dispose(); } catch { }
        }

        /// <summary>A blocking stream the recogniser reads from while the audio thread writes to it.</summary>
        sealed class PcmPipe : Stream
        {
            readonly Queue<byte[]> _chunks = new Queue<byte[]>();
            byte[] _current; int _pos;
            bool _closed; long _queued;
            readonly object _lock = new object();

            public void Write(short[] buf, int n)
            {
                var bytes = new byte[n * 2];
                Buffer.BlockCopy(buf, 0, bytes, 0, n * 2);
                lock (_lock)
                {
                    if (_closed) return;
                    // Drop old audio if the recogniser falls far behind (more than 2 seconds).
                    while (_queued > 64000 && _chunks.Count > 0) _queued -= _chunks.Dequeue().Length;
                    _chunks.Enqueue(bytes); _queued += bytes.Length;
                    Monitor.PulseAll(_lock);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_lock)
                {
                    while (_current == null || _pos >= _current.Length)
                    {
                        if (_chunks.Count > 0) { _current = _chunks.Dequeue(); _queued -= _current.Length; _pos = 0; break; }
                        if (_closed) return 0;
                        Monitor.Wait(_lock, 500);
                    }
                    int n = Math.Min(count, _current.Length - _pos);
                    Buffer.BlockCopy(_current, _pos, buffer, offset, n);
                    _pos += n;
                    return n;
                }
            }

            public override void Close() { lock (_lock) { _closed = true; Monitor.PulseAll(_lock); } base.Close(); }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}
