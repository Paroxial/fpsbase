using UnityEngine;

namespace FpsBase
{
    // Frame-rate cap saved in PlayerPrefs. With Reflex available the cap runs inside the driver's
    // sleep, which adds the least latency; otherwise it falls back to Application.targetFrameRate.
    // Only takes effect with VSync off, which the sandbox setup ensures.
    public static class FrameLimiter
    {
        private const string Key = "FpsBase.FrameRateLimit";

        // 0 means uncapped.
        public static readonly int[] Presets = { 30, 60, 75, 90, 120, 144, 165, 200, 240, 300, 360, 480, 0 };

        public static int Limit { get; private set; }

        public static void Set(int fps)
        {
            Limit = Mathf.Max(0, fps);
            PlayerPrefs.SetInt(Key, Limit);
            Apply();
        }

        // AfterSceneLoad runs after Reflex initializes in BeforeSceneLoad, so availability is known.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Restore()
        {
            Limit = PlayerPrefs.GetInt(Key, 0);
            Apply();
        }

        private static void Apply()
        {
            if (Reflex.IsAvailable)
            {
                Reflex.FrameRateLimit = Limit;
                Application.targetFrameRate = -1;
            }
            else
            {
                Application.targetFrameRate = Limit > 0 ? Limit : -1;
            }
        }
    }
}
