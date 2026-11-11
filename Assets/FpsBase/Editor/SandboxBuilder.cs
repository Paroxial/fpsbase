using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FpsBase
{
    // Generates the URP pipeline (when the project has none), the player prefab and the sandbox
    // scene. Runs once on first open; FPS Base > Rebuild Sandbox regenerates the prefab and scene.
    [InitializeOnLoad]
    public static class SandboxBuilder
    {
        private const string Root = "Assets/FpsBase";
        private const string ScenePath = Root + "/Scenes/Sandbox.unity";
        private const string PlayerPrefabPath = Root + "/Prefabs/Player.prefab";
        private const string ControlsPath = Root + "/Input/Controls.inputactions";
        private const string SettingsFolder = Root + "/Settings";
        private const string MaterialsFolder = Root + "/Materials";
        private const string SniperScopePath = MaterialsFolder + "/Sniper Scope.png";
        private const string AudioFolder = Root + "/Audio";
        private const string PostProcessDataPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";
        private const float ArenaSize = 80f;

        private struct Palette
        {
            public Material Ground;
            public Material Block;
            public Material Prop;
            public Material Target;
            public Material Weapon;
            public Material WeaponAccent;
            public Material SmgAccent;
            public Material PlayerSuit;
        }

        static SandboxBuilder()
        {
            if (!File.Exists(ScenePath))
            {
                EditorApplication.delayCall += Build;
            }
        }

        [MenuItem("FPS Base/Rebuild Sandbox")]
        private static void Rebuild()
        {
            if (EditorUtility.DisplayDialog("Rebuild Sandbox", "Overwrites the sandbox scene and player prefab.", "Rebuild", "Cancel"))
            {
                Build();
            }
        }

        private static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            foreach (string folder in new[] { "Scenes", "Prefabs", "Settings", "Materials" })
            {
                if (!AssetDatabase.IsValidFolder($"{Root}/{folder}"))
                {
                    AssetDatabase.CreateFolder(Root, folder);
                }
            }

            ConfigureProject();
            Palette palette = CreatePalette();
            GameObject rifle = BuildWeaponPrefab("Rifle", 600f, 25f, 7, 1.3f, 0.6f, model => BuildRifleModel(model, palette));
            GameObject smg = BuildWeaponPrefab("SMG", 960f, 18f, 21, 0.7f, 0.45f, model => BuildSmgModel(model, palette));
            GameObject sniper = BuildSniperPrefab(palette);
            GameObject playerPrefab = BuildPlayerPrefab(palette, rifle, smg, sniper);
            BuildScene(playerPrefab, palette, rifle, smg, sniper);
            AssetDatabase.SaveAssets();
            Debug.Log($"Built {ScenePath}. Press Play to run the sandbox.");
        }

        private static void ConfigureProject()
        {
            if (GraphicsSettings.defaultRenderPipeline == null)
            {
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(PostProcessDataPath);
                AssetDatabase.CreateAsset(rendererData, SettingsFolder + "/URP Renderer.asset");
                UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(pipeline, SettingsFolder + "/URP Pipeline.asset");
                GraphicsSettings.defaultRenderPipeline = pipeline;
            }

            PlayerSettings.colorSpace = ColorSpace.Linear;

            // VSync off everywhere: Reflex and its frame cap replace it for low-latency pacing.
            int activeLevel = QualitySettings.GetQualityLevel();
            for (int level = 0; level < QualitySettings.names.Length; level++)
            {
                QualitySettings.SetQualityLevel(level, false);
                QualitySettings.vSyncCount = 0;
            }
            QualitySettings.SetQualityLevel(activeLevel, false);
        }

        private static Palette CreatePalette()
        {
            Material ground = LoadOrCreateMaterial("Ground", Color.white);
            if (ground.mainTexture == null)
            {
                ground.mainTexture = LoadOrCreateGridTexture();
                ground.mainTextureScale = new Vector2(ArenaSize / 2f, ArenaSize / 2f);
                EditorUtility.SetDirty(ground);
            }

            return new Palette
            {
                Ground = ground,
                Block = LoadOrCreateMaterial("Block", new Color(0.62f, 0.64f, 0.68f)),
                Prop = LoadOrCreateMaterial("Prop", new Color(0.78f, 0.52f, 0.26f)),
                Target = LoadOrCreateMaterial("Target", new Color(0.85f, 0.2f, 0.18f)),
                Weapon = LoadOrCreateMaterial("Weapon", new Color(0.12f, 0.12f, 0.14f)),
                WeaponAccent = LoadOrCreateMaterial("Weapon Accent", new Color(0.32f, 0.3f, 0.26f)),
                SmgAccent = LoadOrCreateMaterial("SMG Accent", new Color(0.2f, 0.3f, 0.42f)),
                PlayerSuit = LoadOrCreateMaterial("Player Suit", new Color(0.22f, 0.42f, 0.8f)),
            };
        }

        private static Material LoadOrCreateMaterial(string name, Color color)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                return material;
            }

            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // A 2 x 2 m tile of 1 m checker cells with edge lines, so speed and distance read at a glance.
        private static Texture2D LoadOrCreateGridTexture()
        {
            string path = MaterialsFolder + "/Grid.png";
            if (!File.Exists(path))
            {
                const int size = 256;
                const int cell = size / 2;
                var light = new Color32(112, 116, 122, 255);
                var dark = new Color32(94, 98, 104, 255);
                var line = new Color32(70, 73, 78, 255);
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool onLine = x % cell < 3 || y % cell < 3;
                        bool lightCell = (x / cell + y / cell) % 2 == 0;
                        pixels[y * size + x] = onLine ? line : lightCell ? light : dark;
                    }
                }

                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.SetPixels32(pixels);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.anisoLevel = 8;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Slot order follows the weapon arguments.
        private static GameObject BuildPlayerPrefab(Palette palette, params GameObject[] startingWeapons)
        {
            Dictionary<string, InputActionReference> actions = LoadActionReferences();

            var player = new GameObject("Player");
            var controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.stepOffset = 0.4f;
            controller.slopeLimit = 50f;
            controller.skinWidth = 0.04f;
            controller.minMoveDistance = 0f;

            var view = new GameObject("View").transform;
            view.SetParent(player.transform, false);

            var cameraObject = new GameObject("Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            cameraObject.transform.SetParent(view, false);
            cameraObject.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var playerCamera = cameraObject.GetComponent<Camera>();
            playerCamera.fieldOfView = 75f;
            playerCamera.nearClipPlane = 0.03f;

            var audioSource = cameraObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;

            (Transform body, GameObject head) = BuildPlayerModel(view, cameraObject.transform, palette);

            var look = player.AddComponent<PlayerLook>();
            Assign(look, "lookAction", actions["Look"]);
            Assign(look, "cameraTransform", cameraObject.transform);
            Assign(look, "bodyModel", body);
            Assign(look, "headModel", head);

            var motor = player.AddComponent<PlayerMotor>();
            Assign(motor, "moveAction", actions["Move"]);
            Assign(motor, "walkAction", actions["Walk"]);
            Assign(motor, "jumpAction", actions["Jump"]);
            Assign(motor, "view", view);

            // Nested prefab instances under the camera; PlayerInventory.Start moves them into slots.
            var items = new Object[startingWeapons.Length];
            for (int i = 0; i < startingWeapons.Length; i++)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(startingWeapons[i]);
                instance.transform.SetParent(cameraObject.transform, false);
                items[i] = instance.GetComponent<InventoryItem>();
            }

            var inventory = player.AddComponent<PlayerInventory>();
            Assign(inventory, "fireAction", actions["Fire"]);
            Assign(inventory, "scopeAction", actions["Scope"]);
            Assign(inventory, "interactAction", actions["Interact"]);
            AssignArray(inventory, "slotActions", Enumerable.Range(1, PlayerInventory.SlotCount).Select(i => (Object)actions[$"Slot{i}"]).ToArray());
            Assign(inventory, "holder", cameraObject.transform);
            Assign(inventory, "audioSource", audioSource);
            AssignArray(inventory, "startingItems", items);

            var playerAudio = player.AddComponent<PlayerAudio>();
            Assign(playerAudio, "audioSource", audioSource);
            AssignArray(playerAudio, "footstepClips", LoadClips("Footstep"));
            Assign(playerAudio, "jumpClip", LoadClip("Jump"));
            Assign(playerAudio, "landClip", LoadClip("Land"));
            Assign(playerAudio, "hurtClip", LoadClip("Hurt"));
            Assign(playerAudio, "deathClip", LoadClip("Death"));

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
            Object.DestroyImmediate(player);
            return prefab;
        }

        private static Dictionary<string, InputActionReference> LoadActionReferences()
        {
            if (AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath) == null)
            {
                AssetDatabase.ImportAsset(ControlsPath, ImportAssetOptions.ForceSynchronousImport);
            }

            // The importer adds a hidden duplicate of every reference for backward compatibility.
            return AssetDatabase.LoadAllAssetsAtPath(ControlsPath)
                .OfType<InputActionReference>()
                .Where(reference => (reference.hideFlags & HideFlags.HideInHierarchy) == 0)
                .ToDictionary(reference => reference.action.name);
        }

        private static void BuildScene(GameObject playerPrefab, Palette palette, params GameObject[] itemPrefabs)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.TryGetComponent(out Camera _))
                {
                    Object.DestroyImmediate(root);
                }
                else if (root.TryGetComponent(out Light sun))
                {
                    sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                    sun.shadows = LightShadows.Soft;
                }
            }

            Transform level = new GameObject("Level").transform;
            BuildArena(level, palette);
            BuildTraversal(level, palette);
            BuildProps(level, palette);
            BuildTargets(level, palette);
            BuildSpawnPoints(level);

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab, scene);
            player.transform.position = new Vector3(0f, 0f, -20f);
            var session = new GameObject("Net Session").AddComponent<NetSession>();
            Assign(session, "localPlayer", player.GetComponent<PlayerMotor>());
            Assign(session, "playerPrefab", playerPrefab);
            AssignArray(session, "itemPrefabs", itemPrefabs.Select(prefab => (Object)prefab.GetComponent<InventoryItem>()).ToArray());
            PauseMenu pauseMenu = BuildPauseMenu(player.GetComponent<PlayerLook>(), session);
            BuildHud(player.GetComponent<PlayerInventory>(), player.GetComponent<PlayerMotor>(), pauseMenu);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static void BuildArena(Transform level, Palette palette)
        {
            float half = ArenaSize / 2f + 0.5f;
            CreateBlock("Ground", level, new Vector3(0f, -0.5f, 0f), new Vector3(ArenaSize, 1f, ArenaSize), palette.Ground);
            CreateBlock("Wall North", level, new Vector3(0f, 2f, half), new Vector3(ArenaSize + 2f, 4f, 1f), palette.Block);
            CreateBlock("Wall South", level, new Vector3(0f, 2f, -half), new Vector3(ArenaSize + 2f, 4f, 1f), palette.Block);
            CreateBlock("Wall East", level, new Vector3(half, 2f, 0f), new Vector3(1f, 4f, ArenaSize), palette.Block);
            CreateBlock("Wall West", level, new Vector3(-half, 2f, 0f), new Vector3(1f, 4f, ArenaSize), palette.Block);

            for (int i = 0; i < 4; i++)
            {
                float x = -9f + i * 6f;
                CreateBlock($"Pillar {i + 1}", level, new Vector3(x, 1.5f, -6f), new Vector3(1f, 3f, 1f), palette.Block);
                CreateBlock($"Cover {i + 1}", level, new Vector3(x + 3f, 0.5f, 12f), new Vector3(3f, 1f, 0.5f), palette.Block);
            }
        }

        // Stairs of 0.25 m steps (under the 0.4 m step offset) and a 20 degree ramp, each up to a deck.
        private static void BuildTraversal(Transform level, Palette palette)
        {
            const int steps = 8;
            const float stepHeight = 0.25f;
            const float stepDepth = 0.5f;
            for (int i = 0; i < steps; i++)
            {
                float height = (i + 1) * stepHeight;
                CreateBlock($"Step {i + 1}", level, new Vector3(14f, height / 2f, -2f + i * stepDepth + stepDepth / 2f),
                    new Vector3(3f, height, stepDepth), palette.Block);
            }
            float deckHeight = steps * stepHeight;
            CreateBlock("Stair Deck", level, new Vector3(14f, deckHeight / 2f, 5f), new Vector3(6f, deckHeight, 6f), palette.Block);

            const float angle = 20f;
            const float length = 10f;
            const float thickness = 0.4f;
            float sin = Mathf.Sin(angle * Mathf.Deg2Rad);
            float cos = Mathf.Cos(angle * Mathf.Deg2Rad);
            var start = new Vector3(-14f, 0f, -5f);
            var along = new Vector3(0f, sin, cos);
            var normal = new Vector3(0f, cos, -sin);
            CreatePrimitive(PrimitiveType.Cube, "Ramp", level, start + along * (length / 2f) - normal * (thickness / 2f),
                Quaternion.Euler(-angle, 0f, 0f), new Vector3(4f, thickness, length), palette.Block);
            float rise = sin * length;
            CreateBlock("Ramp Deck", level, new Vector3(start.x, rise / 2f, start.z + cos * length + 3f), new Vector3(4f, rise, 6f), palette.Block);
        }

        private static void BuildProps(Transform level, Palette palette)
        {
            int index = 0;
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3 - row; column++)
                {
                    var position = new Vector3(-1.5f + row * 0.5f + column * 1.05f, 0.5f + row * 1.01f, 4f);
                    GameObject crate = CreateBlock($"Crate {++index}", level, position, Vector3.one, palette.Prop);
                    var body = crate.AddComponent<Rigidbody>();
                    body.mass = 15f;
                    body.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }
        }

        private static void BuildTargets(Transform level, Palette palette)
        {
            for (int i = 0; i < 5; i++)
            {
                GameObject target = CreatePrimitive(PrimitiveType.Capsule, $"Target {i + 1}", level,
                    new Vector3(-10f + i * 5f, 1f, 25f), Quaternion.identity, Vector3.one, palette.Target);
                target.AddComponent<Damageable>();

                // Sits on the capsule's top (local y 1). Its own collider takes the hit, so the 4x
                // multiplier applies only to shots that land on the sphere.
                GameObject head = CreatePrimitive(PrimitiveType.Sphere, "Head", target.transform,
                    new Vector3(0f, 1.22f, 0f), Quaternion.identity, Vector3.one * 0.45f, palette.Target);
                SetFloat(head.AddComponent<Hitbox>(), "damageMultiplier", 4f);
            }
        }

        private static void BuildSpawnPoints(Transform level)
        {
            var positions = new[]
            {
                new Vector3(-30f, 0f, -30f), new Vector3(0f, 0f, -20f), new Vector3(30f, 0f, -30f),
                new Vector3(-30f, 0f, 20f), new Vector3(30f, 0f, 20f), new Vector3(0f, 0f, -35f),
            };
            for (int i = 0; i < positions.Length; i++)
            {
                var point = new GameObject($"Spawn Point {i + 1}", typeof(SpawnPoint));
                point.transform.SetParent(level, false);
                point.transform.position = positions[i];
            }
        }

        private static void BuildHud(PlayerInventory inventory, PlayerMotor motor, PauseMenu pauseMenu)
        {
            GameObject hud = CreateCanvas("HUD", 0);

            var crosshairObject = new GameObject("Crosshair", typeof(RectTransform), typeof(Image));
            crosshairObject.transform.SetParent(hud.transform, false);
            ((RectTransform)crosshairObject.transform).sizeDelta = new Vector2(4f, 4f);
            var crosshair = crosshairObject.GetComponent<Image>();
            crosshair.raycastTarget = false;

            Text feed = CreateText("Event Feed", hud.transform, "", 24, new Vector2(20f, -16f), new Vector2(900f, 160f), TextAnchor.UpperLeft);
            feed.gameObject.AddComponent<Shadow>();
            AnchorTopLeft(feed.rectTransform);
            Assign(hud.AddComponent<EventFeedView>(), "feedText", feed);

            // Sits below the five feed lines.
            Text stats = CreateText("Stats", hud.transform, "", 22, new Vector2(20f, -186f), new Vector2(1000f, 400f), TextAnchor.UpperLeft);
            stats.gameObject.AddComponent<Shadow>();
            AnchorTopLeft(stats.rectTransform);

            var debugHud = hud.AddComponent<DebugHud>();
            Assign(debugHud, "statsText", stats);
            Assign(debugHud, "crosshair", crosshair);
            Assign(debugHud, "inventory", inventory);
            Assign(debugHud, "motor", motor);
            Assign(debugHud, "pauseMenu", pauseMenu);

            BuildHotbar(hud.transform, inventory);
        }

        private static void AnchorTopLeft(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
        }

        private static void BuildHotbar(Transform hud, PlayerInventory inventory)
        {
            Text prompt = CreateText("Pickup Prompt", hud, "", 22, new Vector2(0f, -60f), new Vector2(600f, 32f), TextAnchor.MiddleCenter);
            prompt.gameObject.AddComponent<Shadow>();

            var inventoryHud = hud.gameObject.AddComponent<InventoryHud>();
            Assign(inventoryHud, "inventory", inventory);
            Assign(inventoryHud, "prompt", prompt);
        }

        private static PauseMenu BuildPauseMenu(PlayerLook look, NetSession session)
        {
            GameObject root = CreateCanvas("Pause Menu", 10);
            root.AddComponent<GraphicRaycaster>();
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            var resources = new DefaultControls.Resources
            {
                standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
                background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
                knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
                inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
                checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            };

            GameObject panel = DefaultControls.CreatePanel(resources);
            panel.name = "Panel";
            panel.transform.SetParent(root.transform, false);
            panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);

            CreateText("Title", panel.transform, "Paused", 48, new Vector2(0f, 440f), new Vector2(600f, 64f), TextAnchor.MiddleCenter);
            CreateText("Name Label", panel.transform, "Player Name", 24, new Vector2(0f, 372f), new Vector2(600f, 32f), TextAnchor.MiddleCenter);
            InputField nameField = CreateInputField(resources, panel.transform, "Name Field", "Enter Player Name", new Vector2(0f, 330f));
            Text sensitivityLabel = CreateText("Sensitivity Label", panel.transform, "Sensitivity", 24, new Vector2(0f, 250f), new Vector2(600f, 32f), TextAnchor.MiddleCenter);
            Slider sensitivitySlider = CreateSlider(resources, panel.transform, "Sensitivity Slider", new Vector2(0f, 212f));
            Text volumeLabel = CreateText("Volume Label", panel.transform, "Volume", 24, new Vector2(0f, 162f), new Vector2(600f, 32f), TextAnchor.MiddleCenter);
            Slider volumeSlider = CreateSlider(resources, panel.transform, "Volume Slider", new Vector2(0f, 124f));
            Text frameLimitLabel = CreateText("FPS Limit Label", panel.transform, "FPS Limit", 24, new Vector2(0f, 74f), new Vector2(600f, 32f), TextAnchor.MiddleCenter);
            Slider frameLimitSlider = CreateSlider(resources, panel.transform, "FPS Limit Slider", new Vector2(0f, 36f));

            Toggle fullscreen = CreateToggle(resources, panel.transform, "Fullscreen", new Vector2(0f, -14f));

            Button resume = CreateButton(resources, panel.transform, "Resume", new Vector2(0f, -80f));
            Button quit = CreateButton(resources, panel.transform, "Quit", new Vector2(0f, -150f));

            Text networkLabel = CreateText("Network Label", panel.transform, "Offline", 24, new Vector2(0f, -225f), new Vector2(900f, 32f), TextAnchor.MiddleCenter);
            InputField address = CreateInputField(resources, panel.transform, "Address Field", "Enter Host IP, or Leave Blank to Search the LAN", new Vector2(0f, -275f));
            Button host = CreateButton(resources, panel.transform, "Host", new Vector2(-280f, -345f));
            Button join = CreateButton(resources, panel.transform, "Join", new Vector2(0f, -345f));
            Button leave = CreateButton(resources, panel.transform, "Leave", new Vector2(280f, -345f));

            var menu = root.AddComponent<PauseMenu>();
            Assign(menu, "panel", panel);
            Assign(menu, "sensitivitySlider", sensitivitySlider);
            Assign(menu, "sensitivityLabel", sensitivityLabel);
            Assign(menu, "volumeSlider", volumeSlider);
            Assign(menu, "volumeLabel", volumeLabel);
            Assign(menu, "frameLimitSlider", frameLimitSlider);
            Assign(menu, "frameLimitLabel", frameLimitLabel);
            Assign(menu, "fullscreenToggle", fullscreen);
            Assign(menu, "nameField", nameField);
            Assign(menu, "resumeButton", resume);
            Assign(menu, "quitButton", quit);
            Assign(menu, "look", look);
            Assign(menu, "session", session);
            Assign(menu, "networkLabel", networkLabel);
            Assign(menu, "addressField", address);
            Assign(menu, "hostButton", host);
            Assign(menu, "joinButton", join);
            Assign(menu, "leaveButton", leave);
            panel.SetActive(false);
            return menu;
        }

        // A gun prefab: model children without colliders, one box collider fitted around them for the
        // dropped state, a rigidbody, and the InventoryItem and HitscanWeapon that drive both states.
        // Saved held (kinematic, collider off), which is how it starts in the player's slots.
        private static GameObject BuildSniperPrefab(Palette palette)
        {
            Texture2D scope = AssetDatabase.LoadAssetAtPath<Texture2D>(SniperScopePath);
            return BuildWeaponPrefab("Sniper Rifle", 60f, 100f, 31, 2.8f, 0.4f,
                model => BuildSniperModel(model, palette), automatic: false, scopeTexture: scope);
        }

        private static GameObject BuildWeaponPrefab(string name, float roundsPerMinute, float damage, int recoilSeed,
            float recoilKick, float recoilSway, Action<Transform> buildModel, bool automatic = true, Texture2D scopeTexture = null)
        {
            var root = new GameObject(name);
            var model = new GameObject("Model").transform;
            model.SetParent(root.transform, false);
            buildModel(model);

            Bounds bounds = root.GetComponentsInChildren<Renderer>()
                .Select(part => part.bounds)
                .Aggregate((all, part) => { all.Encapsulate(part); return all; });
            var worldCollider = root.AddComponent<BoxCollider>();
            worldCollider.center = bounds.center;
            worldCollider.size = bounds.size;
            worldCollider.enabled = false;

            var body = root.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.isKinematic = true;

            var item = root.AddComponent<InventoryItem>();
            SetString(item, "displayName", name);
            Assign(item, "worldCollider", worldCollider);

            var weapon = root.AddComponent<HitscanWeapon>();
            SetFloat(weapon, "roundsPerMinute", roundsPerMinute);
            SetFloat(weapon, "damage", damage);
            SetBool(weapon, "automatic", automatic);
            Assign(weapon, "scopeTexture", scopeTexture);
            SetInt(weapon, "recoil.seed", recoilSeed);
            SetFloat(weapon, "recoil.kick", recoilKick);
            SetFloat(weapon, "recoil.sway", recoilSway);
            AssignArray(weapon, "fireClips", LoadClips("Gunshot"));
            Assign(weapon, "hitClip", LoadClip("HitMarker"));
            Assign(weapon, "killClip", LoadClip("Kill"));

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{Root}/Prefabs/{name}.prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }

        // A primitive humanoid with its feet at the root and its eyes at the camera. The body turns
        // with the player's yaw; the head sits on the camera, so it also tilts with the aim. Both
        // start inactive: only other players' copies show them, since the local camera sits inside.
        private static (Transform Body, GameObject Head) BuildPlayerModel(Transform view, Transform camera, Palette palette)
        {
            var body = new GameObject("Body Model").transform;
            body.SetParent(view, false);
            AddBodyPart(PrimitiveType.Capsule, "Left Leg", body, new Vector3(-0.1f, 0.42f, 0f), new Vector3(0.15f, 0.42f, 0.15f), palette.PlayerSuit);
            AddBodyPart(PrimitiveType.Capsule, "Right Leg", body, new Vector3(0.1f, 0.42f, 0f), new Vector3(0.15f, 0.42f, 0.15f), palette.PlayerSuit);
            AddBodyPart(PrimitiveType.Cube, "Hips", body, new Vector3(0f, 0.9f, 0f), new Vector3(0.34f, 0.16f, 0.2f), palette.PlayerSuit);
            AddBodyPart(PrimitiveType.Cube, "Torso", body, new Vector3(0f, 1.2f, 0f), new Vector3(0.42f, 0.46f, 0.24f), palette.PlayerSuit);
            AddBodyPart(PrimitiveType.Capsule, "Left Arm", body, new Vector3(-0.29f, 1.12f, 0f), new Vector3(0.12f, 0.31f, 0.12f), palette.PlayerSuit);
            AddBodyPart(PrimitiveType.Capsule, "Right Arm", body, new Vector3(0.29f, 1.12f, 0f), new Vector3(0.12f, 0.31f, 0.12f), palette.PlayerSuit);
            AddBodyPart(PrimitiveType.Cylinder, "Neck", body, new Vector3(0f, 1.48f, 0f), new Vector3(0.1f, 0.05f, 0.1f), palette.PlayerSuit);
            body.gameObject.SetActive(false);

            var head = new GameObject("Head Model");
            head.transform.SetParent(camera, false);
            // The head uses a trigger so bullets can hit it without blocking movement.
            GameObject skull = CreatePrimitive(PrimitiveType.Sphere, "Head", head.transform, new Vector3(0f, 0.02f, -0.03f), Quaternion.identity, Vector3.one * 0.26f, palette.PlayerSuit);
            skull.GetComponent<Collider>().isTrigger = true;
            SetFloat(skull.AddComponent<Hitbox>(), "damageMultiplier", 4f);
            AddBodyPart(PrimitiveType.Cube, "Visor", head.transform, new Vector3(0f, 0.01f, 0.08f), new Vector3(0.2f, 0.07f, 0.07f), palette.Weapon);
            head.SetActive(false);
            return (body, head);
        }

        // Collider-free like the weapon parts, but casting shadows. Remote players fit a trigger
        // hitbox to each part at runtime (PlayerLook).
        private static void AddBodyPart(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject part = CreatePrimitive(type, name, parent, position, Quaternion.identity, scale, material);
            Object.DestroyImmediate(part.GetComponent<Collider>());
        }

        // Rough silhouettes from primitives, centered on the item root. Replace a prefab's Model
        // children with a real mesh; keep them collider-free so they never block the camera ray.
        private static void BuildRifleModel(Transform model, Palette palette)
        {
            AddViewModelPart(PrimitiveType.Cube, "Receiver", model, Vector3.zero, Quaternion.identity, new Vector3(0.055f, 0.08f, 0.3f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Handguard", model, new Vector3(0f, 0.004f, 0.24f), Quaternion.identity, new Vector3(0.05f, 0.06f, 0.2f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Barrel", model, new Vector3(0f, 0.012f, 0.4f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.02f, 0.1f, 0.02f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Magazine", model, new Vector3(0f, -0.09f, 0.07f), Quaternion.Euler(-12f, 0f, 0f), new Vector3(0.03f, 0.12f, 0.055f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cube, "Grip", model, new Vector3(0f, -0.075f, -0.07f), Quaternion.Euler(18f, 0f, 0f), new Vector3(0.032f, 0.1f, 0.045f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Stock", model, new Vector3(0f, -0.012f, -0.24f), Quaternion.identity, new Vector3(0.04f, 0.07f, 0.18f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cube, "Sight", model, new Vector3(0f, 0.055f, 0.02f), Quaternion.identity, new Vector3(0.018f, 0.03f, 0.06f), palette.Weapon);
        }

        // Shorter and boxier than the rifle, with a straight magazine and a blue accent.
        private static void BuildSmgModel(Transform model, Palette palette)
        {
            AddViewModelPart(PrimitiveType.Cube, "Receiver", model, Vector3.zero, Quaternion.identity, new Vector3(0.05f, 0.075f, 0.22f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Shroud", model, new Vector3(0f, 0.004f, 0.15f), Quaternion.identity, new Vector3(0.046f, 0.055f, 0.09f), palette.SmgAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Barrel", model, new Vector3(0f, 0.01f, 0.23f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.018f, 0.05f, 0.018f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Magazine", model, new Vector3(0f, -0.1f, 0.03f), Quaternion.identity, new Vector3(0.028f, 0.13f, 0.04f), palette.SmgAccent);
            AddViewModelPart(PrimitiveType.Cube, "Grip", model, new Vector3(0f, -0.07f, -0.07f), Quaternion.Euler(15f, 0f, 0f), new Vector3(0.03f, 0.09f, 0.04f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Stock", model, new Vector3(0f, -0.005f, -0.17f), Quaternion.identity, new Vector3(0.022f, 0.05f, 0.12f), palette.SmgAccent);
            AddViewModelPart(PrimitiveType.Cube, "Sight", model, new Vector3(0f, 0.05f, -0.02f), Quaternion.identity, new Vector3(0.016f, 0.025f, 0.04f), palette.Weapon);
        }

        private static void BuildSniperModel(Transform model, Palette palette)
        {
            Quaternion alongBarrel = Quaternion.Euler(90f, 0f, 0f);
            AddViewModelPart(PrimitiveType.Cube, "Receiver", model, Vector3.zero, Quaternion.identity, new Vector3(0.06f, 0.075f, 0.33f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Fore-end", model, new Vector3(0f, -0.015f, 0.24f), Quaternion.identity, new Vector3(0.058f, 0.045f, 0.28f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Barrel", model, new Vector3(0f, 0.016f, 0.39f), alongBarrel, new Vector3(0.025f, 0.245f, 0.025f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Muzzle Brake", model, new Vector3(0f, 0.016f, 0.63f), Quaternion.identity, new Vector3(0.037f, 0.033f, 0.068f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cube, "Grip", model, new Vector3(0f, -0.085f, -0.12f), Quaternion.Euler(15f, 0f, 0f), new Vector3(0.034f, 0.1f, 0.047f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Magazine", model, new Vector3(0f, -0.057f, 0.045f), Quaternion.identity, new Vector3(0.034f, 0.053f, 0.081f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Stock", model, new Vector3(0f, -0.013f, -0.28f), Quaternion.identity, new Vector3(0.048f, 0.062f, 0.27f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cube, "Butt Pad", model, new Vector3(0f, -0.031f, -0.415f), Quaternion.identity, new Vector3(0.05f, 0.094f, 0.023f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cube, "Scope Mount", model, new Vector3(0f, 0.051f, 0f), Quaternion.identity, new Vector3(0.043f, 0.035f, 0.13f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Scope Tube", model, new Vector3(0f, 0.092f, 0f), alongBarrel, new Vector3(0.044f, 0.145f, 0.044f), palette.Weapon);
            AddViewModelPart(PrimitiveType.Cylinder, "Objective Bell", model, new Vector3(0f, 0.092f, 0.15f), alongBarrel, new Vector3(0.071f, 0.039f, 0.071f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Eyepiece", model, new Vector3(0f, 0.092f, -0.126f), alongBarrel, new Vector3(0.059f, 0.034f, 0.059f), palette.WeaponAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Eyepiece Glass", model, new Vector3(0f, 0.092f, -0.16f), alongBarrel, new Vector3(0.043f, 0.003f, 0.043f), palette.SmgAccent);
            AddViewModelPart(PrimitiveType.Cylinder, "Elevation Dial", model, new Vector3(0f, 0.123f, -0.012f), Quaternion.identity, new Vector3(0.026f, 0.01f, 0.026f), palette.WeaponAccent);
        }

        private static void AddViewModelPart(PrimitiveType type, string name, Transform parent, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            GameObject part = CreatePrimitive(type, name, parent, position, rotation, scale, material);
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        }

        private static GameObject CreateCanvas(string name, int sortingOrder)
        {
            var root = new GameObject(name, typeof(Canvas), typeof(CanvasScaler));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            return root;
        }

        private static Text CreateText(string name, Transform parent, string content, int fontSize, Vector2 position, Vector2 size, TextAnchor alignment)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            var rect = (RectTransform)textObject.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var text = textObject.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = content;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Slider CreateSlider(DefaultControls.Resources resources, Transform parent, string name, Vector2 position)
        {
            GameObject slider = DefaultControls.CreateSlider(resources);
            slider.name = name;
            slider.transform.SetParent(parent, false);
            var rect = (RectTransform)slider.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(420f, 24f);
            return slider.GetComponent<Slider>();
        }

        private static Toggle CreateToggle(DefaultControls.Resources resources, Transform parent, string label, Vector2 position)
        {
            GameObject toggle = DefaultControls.CreateToggle(resources);
            toggle.name = label + " Toggle";
            toggle.transform.SetParent(parent, false);
            var rect = (RectTransform)toggle.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(200f, 32f);

            // The default box is 20 px; scale it up to read beside 24 pt labels.
            var box = (RectTransform)toggle.transform.Find("Background");
            box.sizeDelta = new Vector2(28f, 28f);
            box.anchoredPosition = new Vector2(14f, -16f);
            Text text = toggle.GetComponentInChildren<Text>();
            text.text = label;
            text.fontSize = 24;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            var textRect = text.rectTransform;
            textRect.offsetMin = new Vector2(40f, 0f);
            textRect.offsetMax = Vector2.zero;
            return toggle.GetComponent<Toggle>();
        }

        private static InputField CreateInputField(DefaultControls.Resources resources, Transform parent, string name, string placeholder, Vector2 position)
        {
            GameObject field = DefaultControls.CreateInputField(resources);
            field.name = name;
            field.transform.SetParent(parent, false);
            var rect = (RectTransform)field.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(540f, 44f);

            var input = field.GetComponent<InputField>();
            input.textComponent.fontSize = 22;
            var hint = (Text)input.placeholder;
            hint.text = placeholder;
            hint.fontSize = 22;
            return input;
        }

        private static Button CreateButton(DefaultControls.Resources resources, Transform parent, string label, Vector2 position)
        {
            GameObject button = DefaultControls.CreateButton(resources);
            button.name = label + " Button";
            button.transform.SetParent(parent, false);
            var rect = (RectTransform)button.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(260f, 52f);

            Text text = button.GetComponentInChildren<Text>();
            text.text = label;
            text.fontSize = 24;
            return button.GetComponent<Button>();
        }

        private static AudioClip LoadClip(string name)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>($"{AudioFolder}/{name}.wav");
        }

        // Every "<prefix>_N.wav" in the audio folder, so variants can be added by dropping in files.
        private static AudioClip[] LoadClips(string prefix)
        {
            if (!Directory.Exists(AudioFolder))
            {
                return Array.Empty<AudioClip>();
            }

            return Directory.GetFiles(AudioFolder, prefix + "_*.wav")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => AssetDatabase.LoadAssetAtPath<AudioClip>(path.Replace('\\', '/')))
                .Where(clip => clip != null)
                .ToArray();
        }

        private static GameObject CreateBlock(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            return CreatePrimitive(PrimitiveType.Cube, name, parent, position, Quaternion.identity, scale, material);
        }

        private static GameObject CreatePrimitive(PrimitiveType type, string name, Transform parent, Vector3 position,
            Quaternion rotation, Vector3 scale, Material material)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.SetLocalPositionAndRotation(position, rotation);
            primitive.transform.localScale = scale;
            primitive.GetComponent<Renderer>().sharedMaterial = material;
            return primitive;
        }

        private static void Assign(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            FindField(serialized, field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetString(Object target, string field, string value)
        {
            var serialized = new SerializedObject(target);
            FindField(serialized, field).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFloat(Object target, string field, float value)
        {
            var serialized = new SerializedObject(target);
            FindField(serialized, field).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetBool(Object target, string field, bool value)
        {
            var serialized = new SerializedObject(target);
            FindField(serialized, field).boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetInt(Object target, string field, int value)
        {
            var serialized = new SerializedObject(target);
            FindField(serialized, field).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignArray(Object target, string field, Object[] values)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = FindField(serialized, field);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static SerializedProperty FindField(SerializedObject serialized, string field)
        {
            return serialized.FindProperty(field)
                ?? throw new InvalidOperationException($"{serialized.targetObject.GetType().Name} has no serialized field '{field}'.");
        }
    }
}
