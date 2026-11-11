using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FpsBase
{
    // F2 toggles wireframes of every collider plus the latest shots: each ray, a label where it
    // landed, and a log of what it struck. Drawn in screen space after everything else, so it shows
    // through walls and works in builds without any shader of its own.
    public class DebugOverlay : MonoBehaviour
    {
        private const int MaxShots = 12;
        private const float ShotLifetime = 6f;
        private const float DrawDistance = 80f;
        private const float RefreshSeconds = 0.5f;
        private const int CircleSegments = 24;

        private static readonly Color HitboxColor = new Color(1f, 0.2f, 0.25f);
        private static readonly Color TriggerColor = new Color(1f, 0.85f, 0.2f);
        private static readonly Color PlayerColor = new Color(0.2f, 0.9f, 1f);
        private static readonly Color BodyColor = new Color(1f, 0.55f, 0.15f);
        private static readonly Color StaticColor = new Color(0.35f, 0.85f, 0.35f, 0.5f);
        private static readonly Color MissColor = new Color(1f, 1f, 1f, 0.7f);

        private struct Shot
        {
            public float Time;
            public Vector3 Origin;
            public Vector3 Point;
            public bool Hit;
            public bool Damaging;
            public string Label;
            public string Detail;
        }

        private static readonly List<Shot> Shots = new();
        private static readonly List<string> Notes = new();
        private static int shotCount;

        private readonly List<Collider> colliders = new();
        private Material lineMaterial;
        private Camera viewCamera;
        private float nextRefresh;
        private GUIStyle textStyle;

        public static bool Enabled { get; private set; }

        public static void RecordShot(Vector3 origin, Vector3 point, Collider collider, float multiplier, float damage)
        {
            if (!Enabled)
            {
                return;
            }

            shotCount++;
            bool hit = collider != null;
            bool damaging = hit && collider.GetComponentInParent<Damageable>() != null;
            string label = hit ? $"{collider.name} x{multiplier:0.##} {damage:0.#}" : "miss";
            string detail = hit
                ? $"#{shotCount} {PathOf(collider)} x{multiplier:0.##} = {damage:0.#} dmg at {Vector3.Distance(origin, point):0.0} m" +
                  (damaging ? "" : ", no Damageable")
                : $"#{shotCount} miss";
            Shots.Add(new Shot { Time = Time.unscaledTime, Origin = origin, Point = point, Hit = hit, Damaging = damaging, Label = label, Detail = detail });
            if (Shots.Count > MaxShots)
            {
                Shots.RemoveAt(0);
            }
        }

        // A line for the log, such as the host applying a remote player's reported damage.
        public static void Note(string text)
        {
            if (!Enabled)
            {
                return;
            }
            Notes.Add(text);
            if (Notes.Count > 6)
            {
                Notes.RemoveAt(0);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<DebugOverlay>() == null)
            {
                var overlay = new GameObject("Debug Overlay").AddComponent<DebugOverlay>();
                DontDestroyOnLoad(overlay.gameObject);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Enabled = false;
            Shots.Clear();
            Notes.Clear();
            shotCount = 0;
        }

        private void Awake()
        {
            // UI/Default ships with every build that has a canvas: unlit and vertex colored.
            lineMaterial = new Material(Shader.Find("UI/Default")) { hideFlags = HideFlags.HideAndDontSave };
        }

        private void OnDestroy()
        {
            Destroy(lineMaterial);
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame)
            {
                Enabled = !Enabled;
            }
            if (Enabled && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshSeconds;
                colliders.Clear();
                colliders.AddRange(FindObjectsByType<Collider>());
            }
        }

        private void OnGUI()
        {
            if (!Enabled)
            {
                return;
            }
            viewCamera = Camera.main;
            if (viewCamera == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            Shots.RemoveAll(shot => now - shot.Time > ShotLifetime);

            if (Event.current.type == EventType.Repaint)
            {
                DrawWorld();
            }
            DrawText();
        }

        private void DrawWorld()
        {
            lineMaterial.SetPass(0);
            // Keeps IMGUI's own top-left pixel space, the one the labels use. GL.LoadPixelMatrix would
            // copy the last camera's render target, which URP on Direct3D leaves flipped vertically.
            GL.Begin(GL.LINES);

            Vector3 eye = viewCamera.transform.position;
            PlayerMotor viewer = viewCamera.GetComponentInParent<PlayerMotor>();
            foreach (Collider collider in colliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                    (collider.bounds.center - eye).sqrMagnitude > DrawDistance * DrawDistance)
                {
                    continue;
                }
                // The viewer's own capsule surrounds the camera, so its clipped edges would sweep
                // across the screen with every turn of the mouse.
                if (viewer != null && collider.transform.IsChildOf(viewer.transform))
                {
                    continue;
                }
                GL.Color(ColorFor(collider));
                DrawCollider(collider);
            }

            foreach (Shot shot in Shots)
            {
                GL.Color(shot.Damaging ? HitboxColor : shot.Hit ? TriggerColor : MissColor);
                Line(shot.Origin, shot.Point);
                const float cross = 0.06f;
                Line(shot.Point - Vector3.up * cross, shot.Point + Vector3.up * cross);
                Line(shot.Point - Vector3.right * cross, shot.Point + Vector3.right * cross);
                Line(shot.Point - Vector3.forward * cross, shot.Point + Vector3.forward * cross);
            }

            GL.End();
        }

        private void DrawText()
        {
            textStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, richText = false };

            foreach (Shot shot in Shots)
            {
                Vector3 screen = viewCamera.WorldToScreenPoint(shot.Point);
                if (screen.z > 0f)
                {
                    Shadowed(new Rect(screen.x + 8f, Screen.height - screen.y - 10f, 400f, 22f), shot.Label);
                }
            }

            var log = new StringBuilder("F2 to Hide Debug Overlay\n");
            log.Append("Red: hit zone   Yellow: trigger   Cyan: player   Orange: physics body   Green: static\n");
            for (int i = Shots.Count - 1; i >= 0; i--)
            {
                log.Append(Shots[i].Detail).Append('\n');
            }
            foreach (string note in Notes)
            {
                log.Append(note).Append('\n');
            }
            Shadowed(new Rect(Screen.width - 760f, 12f, 748f, Screen.height - 24f), log.ToString());
        }

        private void Shadowed(Rect rect, string text)
        {
            textStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, textStyle);
            textStyle.normal.textColor = Color.white;
            GUI.Label(rect, text, textStyle);
        }

        private static Color ColorFor(Collider collider)
        {
            if (collider.TryGetComponent(out Hitbox _))
            {
                return HitboxColor;
            }
            if (collider.isTrigger)
            {
                return TriggerColor;
            }
            if (collider is CharacterController)
            {
                return PlayerColor;
            }
            Rigidbody body = collider.attachedRigidbody;
            return body != null ? BodyColor : StaticColor;
        }

        private void DrawCollider(Collider collider)
        {
            Transform frame = collider.transform;
            switch (collider)
            {
                case BoxCollider box:
                    DrawBox(frame, box.center, box.size);
                    break;
                case SphereCollider sphere:
                    Vector3 scale = frame.lossyScale;
                    float radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    DrawSphere(frame.TransformPoint(sphere.center), radius);
                    break;
                case CapsuleCollider capsule:
                    DrawCapsule(frame, capsule.center, capsule.radius, capsule.height, capsule.direction);
                    break;
                case CharacterController controller:
                    DrawCapsule(frame, controller.center, controller.radius, controller.height, 1);
                    break;
                default:
                    Bounds bounds = collider.bounds;
                    DrawAxisBox(bounds.center, bounds.size);
                    break;
            }
        }

        private void DrawBox(Transform frame, Vector3 center, Vector3 size)
        {
            Vector3 half = size * 0.5f;
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var local = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                corners[i] = frame.TransformPoint(center + local);
            }
            DrawCorners(corners);
        }

        private void DrawAxisBox(Vector3 center, Vector3 size)
        {
            Vector3 half = size * 0.5f;
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                corners[i] = center + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
            }
            DrawCorners(corners);
        }

        private void DrawCorners(Vector3[] corners)
        {
            for (int i = 0; i < 8; i++)
            {
                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    if ((i & bit) == 0)
                    {
                        Line(corners[i], corners[i | bit]);
                    }
                }
            }
        }

        private void DrawSphere(Vector3 center, float radius)
        {
            Circle(center, Vector3.right, Vector3.up, radius, 1f);
            Circle(center, Vector3.forward, Vector3.up, radius, 1f);
            Circle(center, Vector3.right, Vector3.forward, radius, 1f);
        }

        // A capsule along a local axis: rings at both cap centers, side lines, and half-circle caps.
        private void DrawCapsule(Transform frame, Vector3 center, float radius, float height, int direction)
        {
            Vector3 scale = frame.lossyScale;
            Vector3 axis = direction == 0 ? frame.right : direction == 1 ? frame.up : frame.forward;
            float axisScale = Mathf.Abs(direction == 0 ? scale.x : direction == 1 ? scale.y : scale.z);
            float sideScale = direction == 0 ? Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                : direction == 1 ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
            float worldRadius = radius * sideScale;
            float offset = Mathf.Max(0f, height * axisScale * 0.5f - worldRadius);

            Vector3 middle = frame.TransformPoint(center);
            Vector3 top = middle + axis * offset;
            Vector3 bottom = middle - axis * offset;
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up).normalized;
            Vector3 other = Vector3.Cross(axis, side);

            Circle(top, side, other, worldRadius, 1f);
            Circle(bottom, side, other, worldRadius, 1f);
            foreach (Vector3 edge in new[] { side, -side, other, -other })
            {
                Line(top + edge * worldRadius, bottom + edge * worldRadius);
            }
            Circle(top, side, axis, worldRadius, 0.5f);
            Circle(top, other, axis, worldRadius, 0.5f);
            Circle(bottom, side, -axis, worldRadius, 0.5f);
            Circle(bottom, other, -axis, worldRadius, 0.5f);
        }

        // A full circle, or the half on the `v` side when `share` is 0.5.
        private void Circle(Vector3 center, Vector3 u, Vector3 v, float radius, float share)
        {
            int segments = Mathf.CeilToInt(CircleSegments * share);
            float step = Mathf.PI * 2f * share / segments;
            Vector3 previous = center + u * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = step * i;
                Vector3 next = center + (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * radius;
                Line(previous, next);
                previous = next;
            }
        }

        // Clips the segment to just past the near plane, so a ray fired from the eye still shows.
        private void Line(Vector3 from, Vector3 to)
        {
            Transform view = viewCamera.transform;
            float near = viewCamera.nearClipPlane + 0.01f;
            float fromDepth = Vector3.Dot(from - view.position, view.forward);
            float toDepth = Vector3.Dot(to - view.position, view.forward);
            if (fromDepth < near && toDepth < near)
            {
                return;
            }
            if (fromDepth < near)
            {
                from = Vector3.Lerp(from, to, (near - fromDepth) / (toDepth - fromDepth));
            }
            else if (toDepth < near)
            {
                to = Vector3.Lerp(to, from, (near - toDepth) / (fromDepth - toDepth));
            }

            Vector3 a = viewCamera.WorldToScreenPoint(from);
            Vector3 b = viewCamera.WorldToScreenPoint(to);
            GL.Vertex3(a.x, Screen.height - a.y, 0f);
            GL.Vertex3(b.x, Screen.height - b.y, 0f);
        }

        private static string PathOf(Collider collider)
        {
            if (collider == null)
            {
                return "nothing";
            }
            Transform node = collider.transform;
            string path = node.name;
            for (int depth = 0; depth < 3 && node.parent != null; depth++)
            {
                node = node.parent;
                path = node.name + "/" + path;
            }
            return $"{path} ({collider.GetType().Name}{(collider.isTrigger ? ", trigger" : "")})";
        }
    }
}
