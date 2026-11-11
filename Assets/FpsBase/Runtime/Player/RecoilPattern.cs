using System;
using UnityEngine;
using Random = System.Random;

namespace FpsBase
{
    // A gun's spray pattern: the kick (pitch up) and sway (yaw right) each shot of a spray adds, in
    // degrees. The seed fixes the pattern, so every spray from rest follows the same path and can be
    // learned. Shots past the pattern's length start it over.
    [Serializable]
    public class RecoilPattern
    {
        private const int Length = 30;
        private const float KickVariance = 0.25f;
        private const int MinSwayRun = 3;
        private const int MaxSwayRun = 6;
        // The first kicks go nearly straight up before the sway sets in.
        private const int SwayRampShots = 4;

        [SerializeField] private int seed;
        [SerializeField] private float kick = 1f;
        [SerializeField] private float sway = 0.5f;

        [NonSerialized] private Vector2[] shots;

        // x is sway, y is kick.
        public Vector2 GetRecoil(int shot)
        {
            shots ??= Generate(new Random(seed), kick, sway);
            return shots[shot % shots.Length];
        }

        public void Invalidate()
        {
            shots = null;
        }

        // Sway runs one way for a few shots, then flips, so the spray zig-zags as it climbs.
        private static Vector2[] Generate(Random random, float kick, float sway)
        {
            var pattern = new Vector2[Length];
            float side = random.Next(2) == 0 ? -1f : 1f;
            int run = 0;
            for (int i = 0; i < Length; i++)
            {
                if (run == 0)
                {
                    side = -side;
                    run = random.Next(MinSwayRun, MaxSwayRun + 1);
                }
                run--;

                float ramp = Mathf.Min(1f, i / (float)SwayRampShots);
                float vertical = kick * (1f + KickVariance * Range(random, -1f, 1f));
                float horizontal = sway * side * ramp * Range(random, 0.3f, 1f);
                pattern[i] = new Vector2(horizontal, vertical);
            }
            return pattern;
        }

        private static float Range(Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }
    }
}
