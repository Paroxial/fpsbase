using UnityEngine;
using UnityEngine.UI;

namespace FpsBase
{
    public class ScopeOverlay : MaskableGraphic
    {
        private const int Segments = 96;
        private static readonly Color32 MaskColor = new Color32(5, 7, 10, 255);
        private static readonly Color32 ClearMaskColor = new Color32(5, 7, 10, 0);
        private static readonly Color32 ReticleColor = new Color32(12, 16, 20, 238);
        private static readonly Color32 DotColor = new Color32(255, 120, 57, 255);

        private CanvasGroup opacity;
        private RawImage bezel;

        public static ScopeOverlay Create(Transform parent)
        {
            var root = new GameObject("Scope Overlay", typeof(RectTransform), typeof(CanvasGroup), typeof(ScopeOverlay));
            var rect = (RectTransform)root.transform;
            rect.SetParent(parent, false);
            rect.SetAsFirstSibling();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;

            var overlay = root.GetComponent<ScopeOverlay>();
            overlay.raycastTarget = false;
            overlay.opacity = root.GetComponent<CanvasGroup>();
            overlay.opacity.blocksRaycasts = false;
            overlay.bezel = new GameObject("Scope Bezel", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            overlay.bezel.transform.SetParent(rect, false);
            overlay.bezel.raycastTarget = false;
            root.SetActive(false);
            return overlay;
        }

        public void Show(Texture texture, float blend)
        {
            bool visible = texture != null && blend > 0f;
            if (gameObject.activeSelf != visible)
            {
                gameObject.SetActive(visible);
            }
            if (visible)
            {
                opacity.alpha = blend;
                bezel.texture = texture;
                bezel.rectTransform.sizeDelta = Vector2.one * BezelSize;
            }
        }

        private float BezelSize => Mathf.Min(rectTransform.rect.width, rectTransform.rect.height) * 1.035f;

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = rectTransform.rect;
            Vector2 center = rect.center;
            float size = BezelSize;
            float radius = size * 0.39f;
            float outside = rect.size.magnitude;
            float vignette = size * 0.35f;
            Vector2 previous = Vector2.right;
            for (int i = 1; i <= Segments; i++)
            {
                float angle = i * (2f * Mathf.PI / Segments);
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                AddQuad(mesh, center + previous * radius, center + direction * radius,
                    center + direction * outside, center + previous * outside, MaskColor, MaskColor);
                AddQuad(mesh, center + previous * vignette, center + direction * vignette,
                    center + direction * radius, center + previous * radius, ClearMaskColor, MaskColor);
                previous = direction;
            }

            float gap = size * 0.009f;
            float thickness = Mathf.Max(1f, size * 0.0018f);
            float length = radius - gap;
            float arm = (radius + gap) * 0.5f;
            AddRect(mesh, center + Vector2.right * arm, new Vector2(length, thickness), ReticleColor);
            AddRect(mesh, center - Vector2.right * arm, new Vector2(length, thickness), ReticleColor);
            AddRect(mesh, center + Vector2.up * arm, new Vector2(thickness, length), ReticleColor);
            AddRect(mesh, center - Vector2.up * arm, new Vector2(thickness, length), ReticleColor);
            for (int i = 1; i <= 4; i++)
            {
                float offset = radius * i * 0.13f;
                var tick = new Vector2(size * (i % 2 == 0 ? 0.025f : 0.015f), thickness);
                AddRect(mesh, center - Vector2.up * offset, tick, ReticleColor);
                var sideTick = new Vector2(thickness, tick.x);
                AddRect(mesh, center + Vector2.right * offset, sideTick, ReticleColor);
                AddRect(mesh, center - Vector2.right * offset, sideTick, ReticleColor);
            }
            AddRect(mesh, center, Vector2.one * (thickness * 1.8f), DotColor);
        }

        private static void AddRect(VertexHelper mesh, Vector2 center, Vector2 size, Color32 tint)
        {
            Vector2 half = size * 0.5f;
            AddQuad(mesh, center - half, center + new Vector2(-half.x, half.y),
                center + half, center + new Vector2(half.x, -half.y), tint, tint);
        }

        private static void AddQuad(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 inner, Color32 outer)
        {
            int start = mesh.currentVertCount;
            mesh.AddVert(a, inner, Vector2.zero);
            mesh.AddVert(b, inner, Vector2.zero);
            mesh.AddVert(c, outer, Vector2.zero);
            mesh.AddVert(d, outer, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2);
            mesh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
