using System;
using UnityEngine;

namespace FpsBase
{
    // Kill and join messages for the HUD. The host and offline games post their own; a client posts
    // the ones the host sends.
    public static class EventFeed
    {
        public static event Action<string> Posted;

        public static void Post(string message)
        {
            Posted?.Invoke(message);
        }

        public static void ReportKill(string victim, string killer)
        {
            Post($"{victim} was MOGGED by {killer}");
        }

        public static void ReportJoin(string name)
        {
            Post($"{name} joined the game");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetListeners()
        {
            Posted = null;
        }
    }
}
