using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FpsBase
{
    // Frees the cursor while open and recaptures on close. Opening pauses the simulation through
    // Time.timeScale; rendering, input and Reflex keep running. The full-screen backdrop is a
    // raycast target, which stops PlayerLook from recapturing on clicks inside the menu.
    public class PauseMenu : MonoBehaviour
    {
        [SerializeField] private GameObject panel;
        [SerializeField] private Slider sensitivitySlider;
        [SerializeField] private Text sensitivityLabel;
        [SerializeField] private Slider volumeSlider;
        [SerializeField] private Text volumeLabel;
        [SerializeField] private Slider frameLimitSlider;
        [SerializeField] private Text frameLimitLabel;
        [SerializeField] private Toggle fullscreenToggle;
        [SerializeField] private InputField nameField;
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private PlayerLook look;
        [SerializeField] private NetSession session;
        [SerializeField] private Text networkLabel;
        [SerializeField] private InputField addressField;
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button leaveButton;
        [SerializeField] private float minSensitivity = 0.1f;
        [SerializeField] private float maxSensitivity = 8f;

        public bool IsOpen { get; private set; }

        private void Start()
        {
            sensitivitySlider.minValue = minSensitivity;
            sensitivitySlider.maxValue = maxSensitivity;
            sensitivitySlider.SetValueWithoutNotify(look.Sensitivity);
            sensitivitySlider.onValueChanged.AddListener(SetSensitivity);
            ShowSensitivity(look.Sensitivity);

            volumeSlider.minValue = 0f;
            volumeSlider.maxValue = 1f;
            volumeSlider.SetValueWithoutNotify(MasterVolume.Value);
            volumeSlider.onValueChanged.AddListener(SetVolume);
            ShowVolume(MasterVolume.Value);

            // Steps through FrameLimiter.Presets rather than raw FPS, so common caps like 144 are
            // one notch each instead of a pixel-precise drag.
            frameLimitSlider.wholeNumbers = true;
            frameLimitSlider.minValue = 0;
            frameLimitSlider.maxValue = FrameLimiter.Presets.Length - 1;
            int presetIndex = Array.IndexOf(FrameLimiter.Presets, FrameLimiter.Limit);
            frameLimitSlider.SetValueWithoutNotify(presetIndex >= 0 ? presetIndex : FrameLimiter.Presets.Length - 1);
            frameLimitSlider.onValueChanged.AddListener(SetFrameLimit);
            ShowFrameLimit(FrameLimiter.Limit);

            // Scenes built before LAN play have no session; FPS Base > Rebuild Sandbox adds one.
            if (session != null)
            {
                addressField.text = session.SavedAddress;
                hostButton.onClick.AddListener(session.Host);
                joinButton.onClick.AddListener(() => session.Join(addressField.text));
                leaveButton.onClick.AddListener(session.Leave);
            }

            if (nameField != null)
            {
                nameField.characterLimit = PlayerName.MaxLength;
                nameField.text = PlayerName.Value;
                nameField.onValueChanged.AddListener(value => PlayerName.Value = value);
            }

            // Unity saves the screen mode itself and restores it at startup.
            if (fullscreenToggle != null)
            {
                fullscreenToggle.SetIsOnWithoutNotify(Screen.fullScreen);
                fullscreenToggle.onValueChanged.AddListener(SetFullscreen);
            }

            resumeButton.onClick.AddListener(Close);
            quitButton.onClick.AddListener(Quit);
            Close();
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f;
        }

        private void Update()
        {
            if (IsOpen)
            {
                ShowNetwork();
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame)
            {
                return;
            }

            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public void Open()
        {
            IsOpen = true;
            panel.SetActive(true);
            // A session keeps running for everyone else while one player has the menu open.
            if (session == null || !session.IsBusy)
            {
                Time.timeScale = 0f;
            }
            CursorCapture.Set(false);
            ShowNetwork();
        }

        public void Close()
        {
            IsOpen = false;
            panel.SetActive(false);
            Time.timeScale = 1f;
            CursorCapture.Set(true);
        }

        private void ShowNetwork()
        {
            if (session == null)
            {
                return;
            }
            networkLabel.text = session.Status;
            bool busy = session.IsBusy;
            hostButton.interactable = !busy;
            joinButton.interactable = !busy;
            addressField.interactable = !busy;
            leaveButton.interactable = busy;
            // The host learns a name once, when the player joins.
            if (nameField != null)
            {
                nameField.interactable = !busy;
            }
        }

        private static void SetFullscreen(bool fullscreen)
        {
            Screen.fullScreenMode = fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
        }

        private void SetSensitivity(float value)
        {
            look.Sensitivity = value;
            ShowSensitivity(value);
        }

        private void ShowSensitivity(float value)
        {
            sensitivityLabel.text = $"Sensitivity {value:0.00}";
        }

        private void SetVolume(float value)
        {
            MasterVolume.Value = value;
            ShowVolume(value);
        }

        private void ShowVolume(float value)
        {
            volumeLabel.text = $"Volume {value * 100f:0}%";
        }

        private void SetFrameLimit(float presetIndex)
        {
            int fps = FrameLimiter.Presets[Mathf.RoundToInt(presetIndex)];
            FrameLimiter.Set(fps);
            ShowFrameLimit(fps);
        }

        private void ShowFrameLimit(int fps)
        {
            frameLimitLabel.text = fps > 0 ? $"FPS Limit {fps}" : "FPS Limit Off";
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
