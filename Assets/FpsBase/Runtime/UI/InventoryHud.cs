using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FpsBase
{
    public class InventoryHud : MonoBehaviour
    {
        private const float SlotSize = 80f;
        private const float HealthWidth = 112f;
        private const float Gap = 8f;
        private const int ThumbnailSize = 128;

        private static readonly Color HealthyColor = new Color32(112, 240, 139, 255);
        private static readonly Color HurtColor = new Color32(255, 214, 79, 255);
        private static readonly Color CriticalColor = new Color32(255, 97, 91, 255);

        [SerializeField] private PlayerInventory inventory;
        [SerializeField] private Image[] slotBackgrounds;
        [SerializeField] private Text[] slotLabels;
        [SerializeField] private Text prompt;
        [SerializeField] private Color slotColor = new Color(0f, 0f, 0f, 0.45f);
        [SerializeField] private Color selectedColor = new Color(1f, 0.78f, 0.25f, 0.6f);

        private readonly InventoryItem[] renderedItems = new InventoryItem[PlayerInventory.SlotCount];
        private readonly RawImage[] icons = new RawImage[PlayerInventory.SlotCount];
        private readonly RenderTexture[] thumbnails = new RenderTexture[PlayerInventory.SlotCount];
        private Image[] backgrounds;
        private PlayerHealth health;
        private Text healthText;
        private ItemThumbnailRenderer thumbnailRenderer;
        private InventoryItem promptItem;
        private bool previewsDirty;

        private void Awake()
        {
            BuildHotbar();
            health = inventory.GetComponent<PlayerMotor>().Health;
        }

        private void OnEnable()
        {
            inventory.Changed += Refresh;
            health.HealthChanged += RefreshHealth;
            Refresh();
            RefreshHealth();
        }

        private void Start()
        {
            RefreshHealth();
        }

        private void OnDisable()
        {
            inventory.Changed -= Refresh;
            health.HealthChanged -= RefreshHealth;
        }

        private void OnDestroy()
        {
            thumbnailRenderer?.Dispose();
            foreach (RenderTexture thumbnail in thumbnails)
            {
                if (thumbnail != null)
                {
                    Destroy(thumbnail);
                }
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused)
            {
                previewsDirty = true;
            }
        }

        private void Update()
        {
            InventoryItem hovered = inventory.HoveredItem;
            if (hovered == promptItem)
            {
                return;
            }

            promptItem = hovered;
            prompt.text = hovered != null ? $"E to Pick Up {hovered.DisplayName}" : "";
        }

        private void Refresh()
        {
            for (int i = 0; i < backgrounds.Length; i++)
            {
                backgrounds[i].color = i == inventory.SelectedIndex ? selectedColor : slotColor;
            }
            previewsDirty = true;
        }

        private void RefreshHealth()
        {
            int hp = Mathf.CeilToInt(health.Health);
            healthText.text = $"{hp} HP";
            healthText.color = hp >= 76 ? HealthyColor : hp >= 26 ? HurtColor : CriticalColor;
        }

        private void LateUpdate()
        {
            if (!previewsDirty)
            {
                return;
            }
            previewsDirty = false;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return;
            }

            for (int i = 0; i < icons.Length; i++)
            {
                InventoryItem item = inventory.GetItem(i);
                RenderTexture thumbnail = thumbnails[i];
                if (item == renderedItems[i] && (item == null || (thumbnail != null && thumbnail.IsCreated())))
                {
                    continue;
                }
                renderedItems[i] = item;
                icons[i].enabled = false;
                if (item == null)
                {
                    continue;
                }

                thumbnailRenderer ??= new ItemThumbnailRenderer();
                thumbnails[i] ??= new RenderTexture(ThumbnailSize, ThumbnailSize, 24, RenderTextureFormat.ARGB32);
                icons[i].texture = thumbnails[i];
                icons[i].enabled = thumbnailRenderer.Render(item, thumbnails[i]);
            }
        }

        private void BuildHotbar()
        {
            // Hide the serialized labels in scenes built before the hotbar switched to thumbnails.
            if (slotLabels != null)
            {
                foreach (Text label in slotLabels)
                {
                    if (label != null)
                    {
                        label.gameObject.SetActive(false);
                    }
                }
            }

            var row = (RectTransform)new GameObject("Health and Inventory", typeof(RectTransform)).transform;
            row.SetParent(transform, false);
            row.anchorMin = row.anchorMax = row.pivot = new Vector2(0.5f, 0f);
            row.anchoredPosition = new Vector2(0f, 24f);
            row.sizeDelta = new Vector2(HealthWidth + icons.Length * (Gap + SlotSize), SlotSize);

            Image healthBackground = CreateBackground("Health");
            Place(healthBackground.rectTransform, row, 0f, HealthWidth);
            healthBackground.color = slotColor;
            healthText = new GameObject("HP", typeof(RectTransform), typeof(Text), typeof(Shadow)).GetComponent<Text>();
            Stretch(healthText.rectTransform, healthBackground.transform, 0f);
            healthText.font = prompt.font;
            healthText.fontSize = 28;
            healthText.fontStyle = FontStyle.Bold;
            healthText.alignment = TextAnchor.MiddleCenter;
            healthText.raycastTarget = false;

            backgrounds = new Image[icons.Length];
            for (int i = 0; i < backgrounds.Length; i++)
            {
                Image slot = slotBackgrounds != null && i < slotBackgrounds.Length ? slotBackgrounds[i] : null;
                slot = slot != null ? slot : CreateBackground($"Slot {i + 1}");
                Place(slot.rectTransform, row, HealthWidth + Gap + i * (SlotSize + Gap), SlotSize);
                backgrounds[i] = slot;

                RawImage icon = new GameObject("Item Model", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                Stretch(icon.rectTransform, slot.transform, 4f);
                icon.raycastTarget = false;
                icon.enabled = false;
                icons[i] = icon;
            }
        }

        private static Image CreateBackground(string name)
        {
            Image image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private static void Place(RectTransform rect, RectTransform parent, float x, float width)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(width, SlotSize);
        }

        private static void Stretch(RectTransform rect, Transform parent, float inset)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.one * inset;
            rect.offsetMax = Vector2.one * -inset;
        }
    }
}
