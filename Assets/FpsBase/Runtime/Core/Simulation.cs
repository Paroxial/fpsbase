using System;
using UnityEngine;

namespace FpsBase
{
    // Gameplay ticks at 60 Hz. Rendering interpolates movement and previews pending mouse input.
    public static class Simulation
    {
        public const int TickRate = 60;
        public const float TickDelta = 1f / TickRate;

        // Index of the tick FixedUpdate is running, or the latest one outside FixedUpdate.
        public static int CurrentTick => (int)Math.Round(Time.fixedTimeAsDouble * TickRate);

        // 0 at the previous tick, 1 at the latest. Valid in Update and LateUpdate.
        public static float InterpolationFactor =>
            Mathf.Clamp01((float)((Time.timeAsDouble - Time.fixedTimeAsDouble) / Time.fixedDeltaTime));

        // Where `time` falls between a frame rendered at `shownTime` and the current frame: 0 at
        // that frame, 1 at this one.
        public static float FrameBlend(double time, double shownTime)
        {
            double span = Time.timeAsDouble - shownTime;
            return span > 0.0 ? Mathf.Clamp01((float)((time - shownTime) / span)) : 1f;
        }

        // ProjectSettings/TimeManager.asset stores the same step. Setting it here keeps code that
        // reads TickRate in agreement even if someone edits the project setting.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyTickRate()
        {
            Time.fixedDeltaTime = TickDelta;
        }
    }
}
