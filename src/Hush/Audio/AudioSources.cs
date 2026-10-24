using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hush.Audio
{
    /// <summary>16 kHz, 16-bit, mono PCM frames.</summary>
    public interface IAudioSource : IDisposable
    {
        string DeviceName { get; }
        /// <summary>Raised on a background thread with a buffer and the valid sample count.</summary>
        event Action<short[], int> Samples;
        /// <summary>Raised when the device stops (unplugged, permission revoked, end of file).</summary>
        event Action<string> Stopped;
        void Start();
        void Stop();
    }

    public sealed class MicDevice
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Microphone capture through the Windows waveIn API. Uses an event handle and a dedicated
    /// thread instead of a native callback, so no delegate can be collected under the driver.
    /// </summary>
    public sealed class WaveInSource : IAudioSource
    {
        public const int SampleRate = 16000;
        const int BufferMs = 40, BufferCount = 6;
        const int WAVE_MAPPER = -1, CALLBACK_EVENT = 0x00050000, WHDR_DONE = 0x1, WAVE_FORMAT_PCM = 1;

        public string DeviceName { get; private set; }
        public event Action<short[], int> Samples;
        public event Action<string> Stopped;

        readonly int _deviceId;
        IntPtr _handle;
        Thread _thread;
        volatile bool _running;
        AutoResetEvent _event;
        readonly List<GCHandle> _pins = new List<GCHandle>();
        readonly List<IntPtr> _headers = new List<IntPtr>();
        readonly List<short[]> _buffers = new List<short[]>();

        public WaveInSource(MicDevice device)
        {
            _deviceId = device?.Id ?? WAVE_MAPPER;
            DeviceName = device?.Name ?? "Default microphone";
        }

        public static List<MicDevice> Devices()
        {
            var list = new List<MicDevice>();
            int n = waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEINCAPS();
                if (waveInGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                    list.Add(new MicDevice { Id = i, Name = caps.szPname });
            }
            return list;
        }

        /// <summary>Finds a device by saved name, or null for the system default.</summary>
        public static MicDevice ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var d in Devices()) if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        public void Start()
        {
            if (_running) return;
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16,
                nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2, cbSize = 0
            };
            _event = new AutoResetEvent(false);
            int rc = waveInOpen(out _handle, (IntPtr)_deviceId, ref fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (rc != 0) { _event.Dispose(); _event = null; throw new IOException("The microphone could not be opened (code " + rc + ")."); }

            int samples = SampleRate * BufferMs / 1000;
            for (int i = 0; i < BufferCount; i++)
            {
                var buf = new short[samples];
                var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                var hdr = new WAVEHDR { lpData = pin.AddrOfPinnedObject(), dwBufferLength = samples * 2 };
                IntPtr hp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                Marshal.StructureToPtr(hdr, hp, false);
                waveInPrepareHeader(_handle, hp, Marshal.SizeOf(typeof(WAVEHDR)));
                waveInAddBuffer(_handle, hp, Marshal.SizeOf(typeof(WAVEHDR)));
                _buffers.Add(buf); _pins.Add(pin); _headers.Add(hp);
            }
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Hush audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            rc = waveInStart(_handle);
            if (rc != 0) { Stop(); throw new IOException("The microphone could not start (code " + rc + ")."); }
        }

        void Loop()
        {
            int idleMs = 0;
            while (_running)
            {
                bool signalled = _event.WaitOne(250);
                bool any = false;
                for (int i = 0; i < _headers.Count && _running; i++)
                {
                    var hdr = (WAVEHDR)Marshal.PtrToStructure(_headers[i], typeof(WAVEHDR));
                    if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
                    any = true;
                    int count = (int)hdr.dwBytesRecorded / 2;
                    if (count > 0) { try { Samples?.Invoke(_buffers[i], count); } catch { } }
                    hdr.dwFlags &= ~WHDR_DONE; hdr.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(hdr, _headers[i], false);
                    if (_running) waveInAddBuffer(_handle, _headers[i], Marshal.SizeOf(typeof(WAVEHDR)));
                }
                idleMs = any ? 0 : idleMs + (signalled ? 0 : 250);
                if (idleMs >= 3000 && _running)
                {
                    // No buffers returned for three seconds: the device has gone away.
                    _running = false;
                    try { Stopped?.Invoke("The microphone stopped responding."); } catch { }
                }
            }
        }

        public void Stop()
        {
            bool wasRunning = _running;
            _running = false;
            if (_handle != IntPtr.Zero)
            {
                waveInReset(_handle);
                if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(1000);
                foreach (var hp in _headers) { waveInUnprepareHeader(_handle, hp, Marshal.SizeOf(typeof(WAVEHDR))); Marshal.FreeHGlobal(hp); }
                waveInClose(_handle);
                _handle = IntPtr.Zero;
            }
            foreach (var p in _pins) if (p.IsAllocated) p.Free();
            _pins.Clear(); _headers.Clear(); _buffers.Clear();
            _event?.Dispose(); _event = null;
        }

        public void Dispose() => Stop();

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS { public short wMid, wPid; public int vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public int dwFormats; public short wChannels, wReserved1; }

        [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] static extern int waveInGetDevCaps(IntPtr id, ref WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, IntPtr id, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    }

    /// <summary>
    /// Plays a 16-bit PCM WAV file into the pipeline in real time. Used by the end-to-end tests
    /// (--audio-file) so voice scrolling can be checked without a live microphone.
    /// </summary>
    public sealed class WavFileSource : IAudioSource
    {
        public string DeviceName { get; }
        public event Action<short[], int> Samples;
        public event Action<string> Stopped;
        public bool Loop { get; set; }

        readonly short[] _pcm;
        Thread _thread;
        volatile bool _running;

        public WavFileSource(string path)
        {
            DeviceName = "File: " + Path.GetFileName(path);
            _pcm = LoadAs16kMono(path);
        }

        public static short[] LoadAs16kMono(string path)
        {
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
                br.ReadInt32();
                if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
                int channels = 1, rate = 16000, bits = 16; byte[] data = null;
                while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
                {
                    string id = new string(br.ReadChars(4)); int len = br.ReadInt32();
                    if (len < 0 || len > 512 * 1024 * 1024) throw new InvalidDataException("Bad WAV chunk.");
                    if (id == "fmt ")
                    {
                        short tag = br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                        if (tag != 1 || bits != 16) throw new InvalidDataException("Only 16-bit PCM WAV is supported.");
                        br.BaseStream.Seek(len - 16, SeekOrigin.Current);
                    }
                    else if (id == "data") { data = br.ReadBytes(len); break; }
                    else br.BaseStream.Seek(len + (len & 1), SeekOrigin.Current);
                }
                if (data == null) throw new InvalidDataException("WAV has no data.");
                int frames = data.Length / 2 / channels;
                var mono = new double[frames];
                for (int f = 0; f < frames; f++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(data, (f * channels + c) * 2);
                    mono[f] = sum / (double)channels;
                }
                if (rate == 16000) { var o = new short[frames]; for (int i = 0; i < frames; i++) o[i] = (short)mono[i]; return o; }
                int outLen = (int)((long)frames * 16000 / rate);
                var res = new short[outLen];
                for (int i = 0; i < outLen; i++)
                {
                    double pos = i * (double)rate / 16000; int a = (int)pos; double t = pos - a;
                    double v = a + 1 < frames ? mono[a] * (1 - t) + mono[a + 1] * t : mono[Math.Min(a, frames - 1)];
                    res[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
                }
                return res;
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(() =>
            {
                const int chunk = 640; // 40 ms
                var buf = new short[chunk];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long sent = 0;
                do
                {
                    for (int pos = 0; pos < _pcm.Length && _running; pos += chunk)
                    {
                        int n = Math.Min(chunk, _pcm.Length - pos);
                        Array.Copy(_pcm, pos, buf, 0, n);
                        Samples?.Invoke(buf, n);
                        sent += n;
                        long dueMs = sent * 1000 / 16000;
                        int wait = (int)(dueMs - sw.ElapsedMilliseconds);
                        if (wait > 0) Thread.Sleep(wait);
                    }
                } while (Loop && _running);
                if (_running) { _running = false; Stopped?.Invoke("End of audio file."); }
            }) { IsBackground = true, Name = "Hush wav" };
            _thread.Start();
        }

        public void Stop() { _running = false; }
        public void Dispose() => Stop();
    }
}
