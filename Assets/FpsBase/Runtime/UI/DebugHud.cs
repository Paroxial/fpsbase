using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FpsBase
{
    public class DebugHud : MonoBehaviour
    {
        private const float RefreshSeconds = 0.25f;
        private const float HitFlashSeconds = 0.1f;
        private const string ItemHints = "1-5 to Select Slot\nRight-Click to Toggle Sniper Scope\nE to Pick Up or Drop\nF2 to Toggle Debug Overlay";

        [SerializeField] private Text statsText;
        [SerializeField] private Graphic crosshair;
        [SerializeField] private PlayerInventory inventory;
        [SerializeField] private PlayerMotor motor;
        [SerializeField] private PauseMenu pauseMenu;
        [SerializeField] private Color hitColor = new Color(1f, 0.85f, 0.2f);
        [SerializeField] private Color killColor = new Color(1f, 0.25f, 0.2f);

        private Color crosshairColor;
        private ScopeOverlay scopeOverlay;
        private float flashEndTime;
        private int frames;
        private float elapsed;

        private void Awake()
        {
            crosshairColor = crosshair.color;
            if (inventory != null)
            {
                scopeOverlay = ScopeOverlay.Create(transform);
            }
            Scoreboard.Create(transform, statsText.font);
        }

        private void OnEnable()
        {
            if (inventory != null)
            {
                inventory.Hit += FlashCrosshair;
            }
        }

        private void OnDisable()
        {
            if (inventory != null)
            {
                inventory.Hit -= FlashCrosshair;
            }
            if (scopeOverlay != null)
            {
                scopeOverlay.Show(null, 0f);
                crosshair.enabled = true;
            }
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame && Reflex.IsAvailable)
            {
                Reflex.Mode = NextMode(Reflex.Mode);
            }

            if (Time.unscaledTime >= flashEndTime)
            {
                crosshair.color = crosshairColor;
            }

            frames++;
            elapsed += Time.unscaledDeltaTime;
            if (elapsed < RefreshSeconds)
            {
                return;
            }

            statsText.text = BuildStats(frames / elapsed, elapsed * 1000f / frames, motor != null ? motor.HorizontalSpeed : 0f, CursorHint());
            frames = 0;
            elapsed = 0f;
        }

        private void FlashCrosshair(bool killed)
        {
            crosshair.color = killed ? killColor : hitColor;
            flashEndTime = Time.unscaledTime + HitFlashSeconds;
        }

        private void LateUpdate()
        {
            if (scopeOverlay != null)
            {
                float blend = inventory.ScopeBlend;
                scopeOverlay.Show(inventory.ScopeTexture, blend);
                crosshair.enabled = blend < 0.5f;
            }
        }

        private string CursorHint()
        {
            if (pauseMenu != null && pauseMenu.IsOpen)
            {
                return "Esc to Resume";
            }
            return Cursor.lockState == CursorLockMode.Locked ? "Esc to Open Menu" : "Left-Click to Capture Cursor";
        }

        private static string BuildStats(float fps, float frameMs, float speed, string cursorHint)
        {
            string header = $"{fps:0} FPS  {frameMs:0.00} ms\nSimulation {Simulation.TickRate} Hz\nSpeed {speed:0.00} m/s";
            NetSession session = NetSession.Instance;
            if (session != null && session.IsBusy)
            {
                header += NetSession.IsClient ? $"\n{session.Status}, {session.Corrections} corrections" : $"\n{session.Status}";
                string ping = PingLine(session);
                if (ping != null)
                {
                    header += $"\n{ping}";
                }
            }
            if (!Reflex.IsAvailable)
            {
                return $"{header}\nReflex Unavailable. {Reflex.UnavailableReason}\n\n{ItemHints}\n{cursorHint}";
            }

            string reflex = "Reflex " + ModeName(Reflex.Mode);
            if (Reflex.Mode != ReflexMode.Off && !Reflex.DriverLowLatencyActive)
            {
                reflex += ", driver reports inactive";
            }

            string latency = Reflex.TryGetLatency(out ReflexLatency sample)
                ? $"Sim to GPU {sample.SimulationToGpuEndMs:0.0} ms, GPU {sample.GpuRenderMs:0.0} ms"
                : "Sim to GPU collecting samples";
            return $"{header}\n{reflex}\n{latency}\n\n{ItemHints}\nF1 to Cycle Reflex Mode\n{cursorHint}";
        }

        private static string PingLine(NetSession session)
        {
            if (NetSession.IsClient)
            {
                return session.PingMs >= 0 ? $"Ping: {session.PingMs} ms" : null;
            }
            string[] pings = session.Players.Where(player => !player.IsLocal && player.PingMs >= 0)
                .Select(player => $"{player.Health.DisplayName} {player.PingMs} ms").ToArray();
            return pings.Length > 0 ? "Ping: " + string.Join(", ", pings) : null;
        }

        private static ReflexMode NextMode(ReflexMode mode)
        {
            switch (mode)
            {
                case ReflexMode.Off: return ReflexMode.On;
                case ReflexMode.On: return ReflexMode.OnAndBoost;
                default: return ReflexMode.Off;
            }
        }

        private static string ModeName(ReflexMode mode)
        {
            switch (mode)
            {
                case ReflexMode.On: return "On";
                case ReflexMode.OnAndBoost: return "On + Boost";
                default: return "Off";
            }
        }
    }
}
