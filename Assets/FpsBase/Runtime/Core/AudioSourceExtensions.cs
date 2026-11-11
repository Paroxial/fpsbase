using UnityEngine;
using Random = UnityEngine.Random;

namespace FpsBase
{
    // Unassigned clips stay silent instead of logging, so sounds can be added one at a time.
    public static class AudioSourceExtensions
    {
        public static void PlayIfSet(this AudioSource source, AudioClip clip, float volume = 1f)
        {
            if (source != null && clip != null)
            {
                source.PlayOneShot(clip, volume);
            }
        }

        // Picks a random variant so repeated sounds like shots and footsteps don't machine-gun.
        public static void PlayRandom(this AudioSource source, AudioClip[] clips, float volume = 1f)
        {
            if (clips != null && clips.Length > 0)
            {
                source.PlayIfSet(clips[Random.Range(0, clips.Length)], volume);
            }
        }
    }
}
