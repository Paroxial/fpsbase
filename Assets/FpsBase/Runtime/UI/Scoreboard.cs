using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FpsBase
{
    // Lists every player's kills, deaths and ping while Tab is held during a session.
    public class Scoreboard : MonoBehaviour
    {
        private const int FontSize = 24;
        private const float Padding = 16f;
        private const float PanelWidth = 632f;
        private const string LocalColor = "#FFD54A";
        private static readonly Color PanelColor = new Color(0f, 0f, 0f, 0.75f);

        // One Text per column, so every row lines up without a monospaced font.
        private static readonly (string Header, float X, float Width, TextAnchor Alignment)[] Columns =
        {
            ("Player", Padding, 300f, TextAnchor.UpperLeft),
            ("Kills", 316f, 90f, TextAnchor.UpperRight),
            ("Deaths", 406f, 110f, TextAnchor.UpperRight),
            ("Ping", 516f, 100f, TextAnchor.UpperRight),
        };

        private readonly StringBuilder[] lines = Columns.Select(_ => new StringBuilder()).ToArray();
        private readonly string[] cells = new string[Columns.Length];
        private RectTransform panel;
        private Text[] columnTexts;

        public static Scoreboard Create(Transform parent, Font font)
        {
            var scoreboard = new GameObject("Scoreboard", typeof(RectTransform)).AddComponent<Scoreboard>();
            scoreboard.transform.SetParent(parent, false);

            scoreboard.panel = new GameObject("Panel", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            scoreboard.panel.SetParent(scoreboard.transform, false);
            var background = scoreboard.panel.GetComponent<Image>();
            background.color = PanelColor;
            background.raycastTarget = false;

            scoreboard.columnTexts = new Text[Columns.Length];
            for (int i = 0; i < Columns.Length; i++)
            {
                var text = new GameObject(Columns[i].Header, typeof(RectTransform), typeof(Text)).GetComponent<Text>();
                RectTransform rect = text.rectTransform;
                rect.SetParent(scoreboard.panel, false);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(Columns[i].X, -Padding);
                rect.sizeDelta = new Vector2(Columns[i].Width, 0f);
                text.font = font;
                text.fontSize = FontSize;
                text.color = Color.white;
                text.alignment = Columns[i].Alignment;
                text.supportRichText = true;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                scoreboard.columnTexts[i] = text;
            }

            scoreboard.panel.gameObject.SetActive(false);
            return scoreboard;
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            NetSession session = NetSession.Instance;
            bool visible = keyboard != null && keyboard.tabKey.isPressed && session != null && NetSession.IsOnline;
            if (panel.gameObject.activeSelf != visible)
            {
                panel.gameObject.SetActive(visible);
            }
            if (visible)
            {
                Refresh(session);
            }
        }

        private void Refresh(NetSession session)
        {
            for (int i = 0; i < Columns.Length; i++)
            {
                lines[i].Clear().Append("<b>").Append(Columns[i].Header).Append("</b>");
            }

            foreach (NetPlayer player in session.Players.OrderByDescending(player => player.Kills)
                         .ThenBy(player => player.Deaths)
                         .ThenBy(player => player.Id))
            {
                cells[0] = player.Health.DisplayName;
                cells[1] = player.Kills.ToString();
                cells[2] = player.Deaths.ToString();
                cells[3] = PingCell(player);
                AddRow(player.IsLocal);
            }

            for (int i = 0; i < Columns.Length; i++)
            {
                columnTexts[i].text = lines[i].ToString();
            }
            panel.sizeDelta = new Vector2(PanelWidth, columnTexts[0].preferredHeight + Padding * 2f);
        }

        private void AddRow(bool highlight)
        {
            for (int i = 0; i < Columns.Length; i++)
            {
                lines[i].Append('\n');
                if (highlight)
                {
                    lines[i].Append("<color=").Append(LocalColor).Append('>').Append(cells[i]).Append("</color>");
                }
                else
                {
                    lines[i].Append(cells[i]);
                }
            }
        }

        private static string PingCell(NetPlayer player)
        {
            if (player.Id == NetSession.HostPlayerId)
            {
                return "Host";
            }
            return player.PingMs >= 0 ? $"{player.PingMs} ms" : "";
        }
    }
}
