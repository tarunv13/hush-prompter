using System;

namespace Hush.Core
{
    public enum ScrollMode
    {
        /// <summary>Scrolls while you speak, stops when you stop (amplitude based).</summary>
        Voice,
        /// <summary>Scrolls at a constant speed.</summary>
        Continuous,
        /// <summary>Follows the words you say, using offline Windows speech recognition.</summary>
        WordFollow
    }

    /// <summary>
    /// The prompter's motion model. Pure logic: the window calls <see cref="Tick"/> once per frame
    /// with the elapsed seconds, and reads <see cref="Offset"/>.
    /// </summary>
    public sealed class ScrollEngine
    {
        /// <summary>Stepped speeds in pixels per second, slowest first.</summary>
        public static readonly double[] SpeedSteps = { 4, 8, 12, 16, 20, 25, 30, 36, 42, 50, 60, 70, 85, 100, 120 };

        public ScrollMode Mode { get; set; } = ScrollMode.Voice;
        public int SpeedStep { get; private set; } = 6;
        public double Speed => SpeedSteps[SpeedStep];

        public double Offset { get; private set; }
        public double MinOffset { get; private set; }
        public double MaxOffset { get; private set; }
        public double Velocity { get; private set; }

        public bool UserPaused { get; set; }
        public bool HoverPaused { get; set; }
        public bool Paused => UserPaused || HoverPaused || CountdownRemaining > 0;

        /// <summary>Set by the voice detector in Voice mode.</summary>
        public bool Speaking { get; set; }
        /// <summary>1.0 = calibrated pace. Clamped to [0.5, 1.8].</summary>
        public double TempoFactor { get => _tempo; set => _tempo = Clamp(value, 0.5, 1.8); }
        double _tempo = 1.0;

        /// <summary>Word-follow target; the engine eases toward it.</summary>
        public double? FollowTarget { get; set; }

        public double CountdownRemaining { get; private set; }

        public bool AtEnd => MaxOffset > MinOffset ? Offset >= MaxOffset - 0.5 : true;

        /// <summary>How fast velocity approaches its target (per second). Higher is snappier.</summary>
        public double Responsiveness { get; set; } = 6.0;

        public void SetSpeedStep(int step) => SpeedStep = Math.Max(0, Math.Min(SpeedSteps.Length - 1, step));
        public void Faster() => SetSpeedStep(SpeedStep + 1);
        public void Slower() => SetSpeedStep(SpeedStep - 1);

        public void SetBounds(double min, double max)
        {
            MinOffset = min;
            MaxOffset = Math.Max(min, max);
            Offset = Clamp(Offset, MinOffset, MaxOffset);
        }

        public void JumpTo(double offset)
        {
            Offset = Clamp(offset, MinOffset, MaxOffset);
            Velocity = 0;
            FollowTarget = null;
        }

        public void Nudge(double delta) => JumpTo(Offset + delta);

        public void StartCountdown(double seconds) => CountdownRemaining = Math.Max(0, seconds);

        /// <summary>Advances the model by <paramref name="dt"/> seconds.</summary>
        public void Tick(double dt)
        {
            if (dt <= 0 || double.IsNaN(dt)) return;
            dt = Math.Min(dt, 0.25); // a stalled frame must not jump the text

            if (CountdownRemaining > 0)
            {
                CountdownRemaining = Math.Max(0, CountdownRemaining - dt);
                Velocity = 0;
                return;
            }

            double target;
            if (Paused || AtEnd)
            {
                target = 0;
            }
            else if (Mode == ScrollMode.Continuous)
            {
                target = Speed;
            }
            else if (Mode == ScrollMode.WordFollow && FollowTarget.HasValue)
            {
                // Ease toward the recognised position, capped so jumps stay readable.
                double gap = FollowTarget.Value - Offset;
                target = Clamp(gap * 2.5, -Speed * 4, Speed * 4);
                if (Math.Abs(gap) < 1) target = 0;
            }
            else
            {
                // Voice mode, and word-follow before the first recognised word.
                target = Speaking ? Speed * TempoFactor : 0;
            }

            double k = 1 - Math.Exp(-Responsiveness * dt);
            Velocity += (target - Velocity) * k;
            if (Math.Abs(Velocity) < 0.05 && target == 0) Velocity = 0;

            Offset = Clamp(Offset + Velocity * dt, MinOffset, MaxOffset);
            if (Offset <= MinOffset && Velocity < 0) Velocity = 0;
            if (Offset >= MaxOffset && Velocity > 0) Velocity = 0;
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
