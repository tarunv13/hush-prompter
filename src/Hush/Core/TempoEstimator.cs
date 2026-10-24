using System;
using System.Collections.Generic;

namespace Hush.Core
{
    /// <summary>
    /// Estimates speaking pace as syllable nuclei per second of voiced time, from the 10 ms frame
    /// levels the voice detector produces. Each syllable shows up as a loudness peak; counting peaks
    /// over the last few voiced seconds gives a pace that the scroll speed follows.
    /// </summary>
    public sealed class TempoEstimator
    {
        /// <summary>Calibrated pace in syllables per second. Typical speech is 4 to 5.5.</summary>
        public double ReferenceRate { get => _ref; set => _ref = Math.Max(2.0, Math.Min(8.0, value)); }
        double _ref = 4.5;

        public double WindowSeconds { get; set; } = 4.0;
        /// <summary>Peak prominence in dB.</summary>
        public double ProminenceDb { get; set; } = 3.0;

        /// <summary>Most recent syllables per voiced second, or null before enough speech.</summary>
        public double? Rate { get; private set; }
        /// <summary>Smoothed pace relative to the reference, 1.0 when unknown.</summary>
        public double Factor { get; private set; } = 1.0;

        readonly Queue<bool> _voicedFrames = new Queue<bool>();
        readonly Queue<bool> _peakFrames = new Queue<bool>();
        int _voicedCount, _peakCount;
        double _smooth = -100, _max = -100, _min = 100;
        bool _seekingPeak = true, _maxVoiced;
        int _sinceLastPeak = 1000;

        public void Reset()
        {
            _voicedFrames.Clear(); _peakFrames.Clear(); _voicedCount = _peakCount = 0;
            _smooth = -100; _max = -100; _min = 100; _seekingPeak = true; _maxVoiced = false;
            _sinceLastPeak = 1000; Rate = null; Factor = 1.0;
        }

        /// <summary>Feed one 10 ms frame.</summary>
        public void AddFrame(double db, bool voiced)
        {
            // Smooth the frame levels (about 25 ms), then pick peaks with hysteresis: a peak counts
            // when the level falls a set prominence below the running maximum, and the next peak can
            // only start once the level has risen that much above the following minimum. Ripple
            // smaller than the prominence can never produce a second peak inside one syllable.
            _smooth = _smooth < -99 ? db : _smooth + (db - _smooth) * 0.4;
            bool peak = false;
            _sinceLastPeak++;
            if (_seekingPeak)
            {
                if (_smooth > _max) { _max = _smooth; _maxVoiced = voiced; }
                else if (_smooth < _max - ProminenceDb)
                {
                    if (_maxVoiced && _sinceLastPeak >= 10) { peak = true; _sinceLastPeak = 0; }
                    _seekingPeak = false; _min = _smooth;
                }
            }
            else
            {
                if (_smooth < _min) _min = _smooth;
                else if (_smooth > _min + ProminenceDb) { _seekingPeak = true; _max = _smooth; _maxVoiced = voiced; }
            }

            Push(voiced, peak);

            int maxFrames = (int)(WindowSeconds * 100);
            while (_voicedFrames.Count > maxFrames) Pop();

            double voicedSec = _voicedCount / 100.0;
            if (voicedSec >= 1.5)
            {
                Rate = _peakCount / voicedSec;
                double f = Rate.Value / ReferenceRate;
                f = Math.Max(0.6, Math.Min(1.6, f));
                // Smooth with a time constant of about two seconds of voiced audio.
                if (voiced) Factor += (f - Factor) * 0.005;
            }
        }

        void Push(bool voiced, bool peak)
        {
            _voicedFrames.Enqueue(voiced); _peakFrames.Enqueue(peak);
            if (voiced) _voicedCount++;
            if (peak) _peakCount++;
        }

        void Pop()
        {
            if (_voicedFrames.Dequeue()) _voicedCount--;
            if (_peakFrames.Dequeue()) _peakCount--;
        }
    }
}
