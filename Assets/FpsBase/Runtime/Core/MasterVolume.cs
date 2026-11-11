using UnityEngine;

namespace FpsBase
{
    // Global volume through AudioListener.volume, saved in PlayerPrefs and restored before the first
    // scene loads, so it applies in any scene with or without the pause menu.
    public static class MasterVolume
    {
        private const string Key = "FpsBase.MasterVolume";

        public static float Value
        {
            get => AudioListener.volume;
            set
            {
                AudioListener.volume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(Key, AudioListener.volume);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Restore()
        {
            AudioListener.volume = PlayerPrefs.GetFloat(Key, 1f);
        }
    }
}
