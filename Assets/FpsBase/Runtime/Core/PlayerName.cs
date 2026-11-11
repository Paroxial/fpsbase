using System.Linq;
using UnityEngine;

namespace FpsBase
{
    // The local player's name, saved in PlayerPrefs. A first run picks a numbered default so two
    // fresh installs don't share one name.
    public static class PlayerName
    {
        public const int MaxLength = 16;
        private const string Key = "FpsBase.PlayerName";

        public static string Value
        {
            get
            {
                string saved = Clean(PlayerPrefs.GetString(Key, ""), "");
                if (saved.Length > 0)
                {
                    return saved;
                }
                string generated = $"Player {Random.Range(100, 1000)}";
                PlayerPrefs.SetString(Key, generated);
                return generated;
            }
            set
            {
                string cleaned = Clean(value, "");
                if (cleaned.Length > 0)
                {
                    PlayerPrefs.SetString(Key, cleaned);
                }
            }
        }

        // Strips control characters and rich-text brackets, so a name received over the network
        // can't restyle the kill feed.
        public static string Clean(string name, string fallback)
        {
            string kept = new string((name ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
            if (kept.Length > MaxLength)
            {
                kept = kept.Substring(0, MaxLength).Trim();
            }
            return kept.Length > 0 ? kept : fallback;
        }
    }
}
