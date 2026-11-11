using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace FpsBase
{
    // Newest message at the bottom. Each line holds for a few seconds, then fades out.
    public class EventFeedView : MonoBehaviour
    {
        private const int MaxEntries = 5;
        private const float HoldSeconds = 5f;
        private const float FadeSeconds = 1f;

        [SerializeField] private Text feedText;

        private readonly List<(string Message, float Time)> entries = new();
        private readonly StringBuilder builder = new();

        private void OnEnable()
        {
            EventFeed.Posted += Add;
        }

        private void OnDisable()
        {
            EventFeed.Posted -= Add;
        }

        private void Add(string message)
        {
            entries.Add((message, Time.unscaledTime));
            if (entries.Count > MaxEntries)
            {
                entries.RemoveAt(0);
            }
        }

        private void Update()
        {
            if (entries.Count == 0 && feedText.text.Length == 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            entries.RemoveAll(entry => now - entry.Time >= HoldSeconds + FadeSeconds);
            builder.Clear();
            foreach ((string message, float time) in entries)
            {
                float alpha = Mathf.Clamp01((HoldSeconds + FadeSeconds - (now - time)) / FadeSeconds);
                builder.Append("<color=#FFFFFF").Append(Mathf.RoundToInt(alpha * 255f).ToString("X2")).Append('>')
                    .Append(message).Append("</color>\n");
            }
            feedText.text = builder.ToString();
        }
    }
}
