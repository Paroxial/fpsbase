using System;
using UnityEngine;

namespace FpsBase
{
    // Recent poses of one entity by server tick, sampled between ticks, for a client to render
    // remote entities from.
    public class PoseBuffer
    {
        private readonly int[] ticks;
        private readonly Vector3[] positions;
        private readonly Quaternion[] rotations;
        private int count;

        public PoseBuffer(int capacity)
        {
            ticks = new int[capacity];
            positions = new Vector3[capacity];
            rotations = new Quaternion[capacity];
        }

        public void Add(int tick, Vector3 position, Quaternion rotation)
        {
            if (count > 0 && tick <= ticks[count - 1])
            {
                return;
            }
            if (count == ticks.Length)
            {
                Array.Copy(ticks, 1, ticks, 0, count - 1);
                Array.Copy(positions, 1, positions, 0, count - 1);
                Array.Copy(rotations, 1, rotations, 0, count - 1);
                count--;
            }
            ticks[count] = tick;
            positions[count] = position;
            rotations[count] = rotation;
            count++;
        }

        public void Clear()
        {
            count = 0;
        }

        // Holds the oldest pose before the buffer and the newest past it, rather than extrapolating.
        public bool Sample(float tick, out Vector3 position, out Quaternion rotation)
        {
            if (count == 0)
            {
                position = default;
                rotation = Quaternion.identity;
                return false;
            }

            int next = 1;
            while (next < count && ticks[next] < tick)
            {
                next++;
            }
            if (next >= count)
            {
                position = positions[count - 1];
                rotation = rotations[count - 1];
                return true;
            }

            float t = Mathf.Clamp01((tick - ticks[next - 1]) / (ticks[next] - ticks[next - 1]));
            position = Vector3.Lerp(positions[next - 1], positions[next], t);
            rotation = Quaternion.Slerp(rotations[next - 1], rotations[next], t);
            return true;
        }
    }
}
