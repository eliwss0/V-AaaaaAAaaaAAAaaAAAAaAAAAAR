using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace AwesomeVr
{
    internal enum InputMethod { ImmersiveMotionControls, MotionControlsWithButtonShortcuts, Gamepad, MotionControls }

    internal enum GloveHand { Left, Right }

    internal static class VRModSettings
    {
        private static ConfigFile config;
        private static ConfigEntry<GloveHand> gloveHand;
        private static ConfigEntry<float> parachuteX, parachuteY, parachuteZ;
        internal static Vector3 ParachutePosition
        {
            get { return new Vector3(parachuteX.Value, parachuteY.Value, parachuteZ.Value); }
        }
        internal static void SetParachutePosition(Vector3 position)
        {
            parachuteX.Value = Mathf.Abs(position.x);
            parachuteY.Value = position.y;
            parachuteZ.Value = position.z;
            VRConfig.Save(config);
        }
        private static ConfigEntry<bool> swapHipItems;
        internal static bool SwapHipItems { get { return swapHipItems.Value; } }
        internal static void ToggleHipItems()
        {
            swapHipItems.Value = !swapHipItems.Value;
            VRConfig.Save(config);
        }
        private static ConfigEntry<int> snapTurnAngle;
        internal static int SnapTurnAngle { get { return Mathf.Clamp(snapTurnAngle.Value, 15, 90); } }
        internal static void ChangeSnapTurn(int amount)
        {
            snapTurnAngle.Value = Mathf.Clamp(SnapTurnAngle + amount, 15, 90);
            VRConfig.Save(config);
        }
        private static ConfigEntry<int> shakePercent;
        internal static float ShakeScale { get { return shakePercent.Value * .01f; } }
        internal static void SetShake(float fraction)
        {
            shakePercent.Value = Mathf.RoundToInt(100 * Mathf.Clamp01(fraction));
        }
        internal static void SaveShake() { VRConfig.Save(config); }
        private static ConfigEntry<float> holsterOffset;
        internal static float HolsterOffset { get { return holsterOffset.Value; } }

        internal static void MoveHolsters(float amount)
        {
            holsterOffset.Value = Mathf.Clamp(HolsterOffset + amount, -.35f, .30f);
            VRConfig.Save(config);
        }
        internal static GloveHand FlipItGloveHand { get { return gloveHand.Value; } }
        internal static void ToggleGloveHand()
        {
            gloveHand.Value = gloveHand.Value == GloveHand.Left ? GloveHand.Right : GloveHand.Left;
            VRConfig.Save(config);
        }

        private static ConfigEntry<InputMethod> inputMethod;
        internal static bool ButtonShortcuts { get { return inputMethod.Value == InputMethod.MotionControlsWithButtonShortcuts; } }
        internal static bool UseImmersiveControls { get { return !UseGamepad && !ButtonShortcuts; } }
        internal static bool UseGamepad { get { return inputMethod.Value == InputMethod.Gamepad; } }

        internal static void CycleInputMethod()
        {
            inputMethod.Value = UseImmersiveControls ? InputMethod.MotionControlsWithButtonShortcuts :
                ButtonShortcuts ? InputMethod.Gamepad : InputMethod.ImmersiveMotionControls;
            if (VRControls.Current != null) VRControls.Current.CancelParachuteCalibration();
            VRConfig.Save(config);
        }

        internal static void Initialize(ConfigFile file)
        {
            config = file;
            var legacyShortcuts = file.Bind<bool>("Controls", "ButtonShortcuts", false);
            inputMethod = file.Bind<InputMethod>("Controls", "InputMethod", InputMethod.ImmersiveMotionControls);
            bool migrateInput = inputMethod.Value == InputMethod.MotionControls || legacyShortcuts.Value;
            if (UseImmersiveControls && migrateInput)
                inputMethod.Value = legacyShortcuts.Value
                    ? InputMethod.MotionControlsWithButtonShortcuts
                    : InputMethod.ImmersiveMotionControls;
            file.Remove(legacyShortcuts.Definition);
            snapTurnAngle = file.Bind<int>("VR", "SnapTurnAngle", 30,
                new ConfigDescription("Degrees per snap turn.", new AcceptableValueRange<int>(15, 90)));
            shakePercent = file.Bind<int>("VR", "CameraShakePercent", 100,
                new ConfigDescription("Camera shake intensity.", new AcceptableValueRange<int>(0, 100)));
            gloveHand = file.Bind<GloveHand>("Gestures", "FlipItGloveHand", GloveHand.Right, "Hand used for crowd gestures.");
            parachuteX = file.Bind<float>("Equipment", "ParachuteOffsetX", .18f, "Parachute grip position sideways from the headset, in metres.");
            parachuteY = file.Bind<float>("Equipment", "ParachuteOffsetY", -.20f, "Parachute grip position above or below the headset, in metres.");
            parachuteZ = file.Bind<float>("Equipment", "ParachuteOffsetZ", -.27f, "Parachute grip position behind the headset, in metres.");
            swapHipItems = file.Bind<bool>("Equipment", "SwapHipItems", false, "Swap the graffiti and espresso hips.");
            holsterOffset = file.Bind<float>("Equipment", "HolsterHeightOffset", 0f,
                new ConfigDescription("Height adjustment for both hip items in metres.", new AcceptableValueRange<float>(-.35f, .30f)));
            if (float.IsNaN(HolsterOffset)) holsterOffset.Value = 0f;
            if (migrateInput) VRConfig.Save(config);
        }
    }

    internal sealed class VRSettingsPanel
    {
        private delegate void SettingAction();
        private TextMesh inputMethodValue;
        private Transform backButton, panelBorder, panelBackground;
        private readonly Transform[] settingRows = new Transform[7];
        private const float RowSpacing = .16f;
        private const float PanelHeight = 1.66f;
        private const float PanelTop = .59f;

        private sealed class Control
        {
            internal TextMesh Text;
            internal BoxCollider Shape;
            internal Renderer Face;
            internal SettingAction Activate;
        }
        internal static bool IsOpen { get; private set; }
        private static int consumedFrame = -1;
        internal static bool BlocksGameInput { get { return IsOpen || consumedFrame == Time.frameCount; } }
        private static readonly FieldInfo FleeInitialScale = typeof(ScaleOnHoverButton)
            .GetField("initialScale", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private readonly Transform root;
        private readonly float scale;
        private readonly List<Control> controls = new List<Control>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private Transform panel, entry, titleEntry;
        private Mesh quad;
        private BoxCollider panelShape;
        private AudioClip hoverSound, pressSound, settingsSelectSound;
        private Material pressedFill, buttonFill, hoverFill, badgeMaterial, badgeLabel;
        private Renderer badgePattern;
        private Renderer jump, flee;
        private ScaleOnHoverButton fleeButton;
        private float titleSize = 1;
        private float nextArtworkSearch;
        private TextMesh holsterHeight, gloveHandValue, snapTurnValue, hipItemsValue;
        private Control pressed, hovered, titleControl, shakeSlider;
        private Transform shakeThumb;
        private TextMesh shakeLabel, parachuteLabel, parachuteValue;
        private Control parachuteControl;
        private const float SliderWidth = .52f;
        private GameScreen screen;
        private bool available;
        private readonly Color cream = new Color(.94f,.91f,.78f), gold = new Color(1,.77f,.18f);

        internal VRSettingsPanel(Transform parent, float units) { root = parent; scale = units; }

        private Transform Node(string name, Transform parent, Vector3 position)
        {
            Transform node = new GameObject(name).transform;
            node.parent = parent; node.gameObject.layer = 30;
            node.localPosition = position*scale; node.localRotation = Quaternion.identity;
            return node;
        }

        private TextMesh Label(Transform parent, TextMesh font, string text, Vector3 position, float size, bool left = false)
        {
            Transform node = Node(text, parent, position);
            TextMesh label = node.gameObject.AddComponent<TextMesh>();
            label.font = font.font;
            label.fontStyle = font.fontStyle;
            label.fontSize = 64;
            label.characterSize = size * scale;
            label.anchor = left ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            label.alignment = left ? TextAlignment.Left : TextAlignment.Center;
            label.color = cream;
            label.text = text;
            label.renderer.sharedMaterials = font.renderer.sharedMaterials;
            return label;
        }

        private Material Fill(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            Material material = VRVisualMaterials.Texture();
            material.mainTexture = texture;
            owned.Add(texture);
            owned.Add(material);
            return material;
        }

        private Renderer Plate(Transform parent, Material material, float width, float height, Vector3 position)
        {
            Transform face = Node("Panel face", parent, position);
            face.localScale = new Vector3(width * scale, height * scale, 1);
            face.gameObject.AddComponent<MeshFilter>().sharedMesh = quad;
            MeshRenderer renderer = face.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        private BoxCollider HitArea(Transform parent, float width, float height)
        {
            BoxCollider shape = parent.gameObject.AddComponent<BoxCollider>();
            shape.isTrigger = true;
            shape.size = new Vector3(width, height, .018f) * scale;
            return shape;
        }

        private TextMesh Button(Transform parent, TextMesh font, string text, float x, float y, float width,
            float size, SettingAction action)
        {
            Transform node = Node(text, parent, new Vector3(x, y, 0));
            TextMesh label = Label(node, font, text, new Vector3(0, 0, -.008f), size);
            Renderer face = Plate(node, buttonFill, width, .125f, Vector3.zero);
            controls.Add(new Control { Text = label, Face = face, Shape = HitArea(node, width, .125f), Activate = action });
            return label;
        }

        private TextMesh Stepper(Transform parent, TextMesh font, string caption, float y, SettingAction down, SettingAction up)
        {
            Label(parent, font, caption, new Vector3(-.70f, y, 0), .038f, true);
            Button(parent, font, "-", .19f, y, .19f, .050f, down);
            Button(parent, font, "+", .64f, y, .19f, .050f, up);
            return Label(parent, font, "", new Vector3(.415f, y, 0), .038f);
        }

        private bool Build()
        {
            TextMesh font = null;
            foreach (TextMesh candidate in UnityEngine.Object.FindObjectsOfType(typeof(TextMesh)))
            {
                if (candidate.font == null || candidate.renderer == null || candidate.gameObject.layer == 30) continue;
                if (font == null) font = candidate;
                if (candidate.font.name.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) < 0) continue;
                font = candidate;
                break;
            }
            if (font == null) return false;

            quad = new Mesh();
            quad.vertices = new [] { new Vector3(-.5f,-.5f,0), new Vector3(-.5f,.5f,0),
                new Vector3(.5f,.5f,0), new Vector3(.5f,-.5f,0) };
            quad.uv = new [] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            quad.triangles = new [] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateNormals();
            quad.RecalculateBounds();
            owned.Add(quad);

            Material panelFill = Fill(new Color(.079f, .086f, .104f));
            Material rowFill = Fill(new Color(.125f, .136f, .16f));
            Material amber = Fill(new Color(.66f, .42f, .17f));
            pressedFill = amber;
            buttonFill = Fill(new Color(.245f, .251f, .27f));
            hoverFill = Fill(new Color(.36f, .265f, .14f));
            Shader transparent = Shader.Find("Unlit/Transparent");
            if (transparent == null) transparent = Shader.Find("Particles/Alpha Blended");
            if (transparent == null) transparent = Shader.Find("GUI/Text Shader");
            badgeLabel = VRVisualMaterials.Image("vr_settings_label", transparent);
            badgeLabel.mainTexture.wrapMode = TextureWrapMode.Clamp;
            badgeMaterial = new Material(badgeLabel);
            owned.Add(badgeLabel.mainTexture);
            owned.Add(badgeLabel);
            owned.Add(badgeMaterial);

            titleEntry = Node("VR settings title corner", root, Vector3.zero);
            titleEntry.gameObject.SetActive(false);
            badgePattern = Plate(titleEntry, badgeMaterial, .49f, .49f, Vector3.zero);
            badgePattern.enabled = false;
            Renderer labelBacking = Plate(titleEntry, Fill(Color.black), .376f, .116f, new Vector3(-.018f, .006f, -.002f));
            labelBacking.transform.localRotation = Quaternion.Euler(0, 0, -45);
            Renderer labelExtension = Plate(titleEntry, Fill(Color.black), .094f, .034f, new Vector3(-.165f, .053f, -.002f));
            labelExtension.transform.localRotation = Quaternion.Euler(0, 0, -45);
            Plate(titleEntry, badgeLabel, .49f, .49f, new Vector3(0, 0, -.003f));
            BoxCollider titleShape = HitArea(titleEntry, .42f, .40f);
            titleShape.center = new Vector3(-.035f, -.045f, 0) * scale;
            titleControl = new Control { Shape = titleShape, Activate = () => IsOpen = true };
            controls.Add(titleControl);

            entry = Node("VR settings entry", root, new Vector3(.64f, .54f, 1.45f));
            Button(entry, font, "VR Settings", 0, 0, .42f, .043f, () => IsOpen = true);

            panel = Node("VR settings panel", root, new Vector3(0, .02f, 1.34f));
            panelBorder = Plate(panel, amber, 1.60f, PanelHeight, new Vector3(0, -.24f, .038f)).transform;
            panelBackground = Plate(panel, panelFill, 1.578f, 1.635f, new Vector3(0, -.24f, .032f)).transform;
            panelShape = HitArea(panel, 1.60f, PanelHeight);
            panelShape.center = new Vector3(0, -.24f, .038f) * scale;
            Label(panel, font, "VR SETTINGS", new Vector3(-.70f, .43f, 0), .068f, true);
            Plate(panel, amber, 1.40f, .009f, new Vector3(0, .322f, .014f));
            for (int i = 0; i < settingRows.Length; ++i)
            {
                settingRows[i] = Node("Setting row", panel, Vector3.zero);
                Plate(settingRows[i], rowFill, 1.43f, .145f, new Vector3(0, 0, .018f));
            }
            holsterHeight = Stepper(settingRows[0], font, "Holster height", 0,
                () => VRModSettings.MoveHolsters(-.025f), () => VRModSettings.MoveHolsters(.025f));
            Transform inputRow = settingRows[1];
            Label(inputRow, font, "Input method", new Vector3(-.70f, 0, 0), .038f, true);
            inputMethodValue = Button(inputRow, font, "", .35f, 0, .70f, .032f, VRModSettings.CycleInputMethod);
            Transform shakeRow = settingRows[2];
            Label(shakeRow, font, "Camera shake", new Vector3(-.70f, 0, 0), .038f, true);
            Transform slider = Node("Camera shake slider", shakeRow, new Vector3(.28f, 0, 0));
            Renderer track = Plate(slider, buttonFill, SliderWidth, .024f, Vector3.zero);
            shakeThumb = Plate(slider, amber, .045f, .11f, new Vector3(0, 0, -.006f)).transform;
            shakeSlider = new Control { Face = track, Shape = HitArea(slider, SliderWidth + .04f, .145f) };
            controls.Add(shakeSlider);
            shakeLabel = Label(shakeRow, font, "", new Vector3(.65f, 0, 0), .037f);
            Transform gloveRow = settingRows[3];
            Label(gloveRow, font, "Flip It Glove hand", new Vector3(-.70f, 0, 0), .038f, true);
            gloveHandValue = Button(gloveRow, font, "", .415f, 0, .57f, .044f, VRModSettings.ToggleGloveHand);
            snapTurnValue = Stepper(settingRows[4], font, "Snap turn", 0,
                () => VRModSettings.ChangeSnapTurn(-5), () => VRModSettings.ChangeSnapTurn(5));
            Transform hipRow = settingRows[5];
            Label(hipRow, font, "Swap hip items", new Vector3(-.70f, 0, 0), .038f, true);
            hipItemsValue = Button(hipRow, font, "OFF", .415f, 0, .57f, .044f, VRModSettings.ToggleHipItems);
            Transform parachuteRow = settingRows[6];
            parachuteLabel = Label(parachuteRow, font, "Set parachute grip location", new Vector3(-.70f, 0, 0), .038f, true);
            parachuteValue = Button(parachuteRow, font, "SET", .415f, 0, .57f, .044f, ToggleParachuteCalibration);
            parachuteControl = controls[controls.Count - 1];
            backButton = Button(panel, font, "Back", .55f, -.948f, .38f, .043f, Close).transform.parent;
            panel.gameObject.SetActive(false);
            return true;
        }

        internal void PlacePauseEntry(IList<MenuCameraView> views)
        {
            GamePauseScreen pause = screen as GamePauseScreen;
            if (!available || entry == null || pause == null || IsOpen) return;
            Transform background = pause.transform.Find("panelBackground");
            if (background == null) return;

            foreach (MenuCameraView view in views)
            {
                Camera camera = view.Camera;
                if (view.Background || camera == null || (camera.cullingMask & (1 << background.gameObject.layer)) == 0)
                    continue;

                Vector3 up = camera.transform.up;
                Vector3 axis = new Vector3(Mathf.Abs(up.x), Mathf.Abs(up.y), Mathf.Abs(up.z));
                float bottom = float.PositiveInfinity;
                foreach (Button button in pause.GetComponentsInChildren<Button>())
                {
                    if (!VRMenu.Usable(button)) continue;
                    Collider shape = VRMenu.Shape(button);
                    if (shape == null) continue;
                    Bounds bounds = shape.bounds;
                    bottom = Mathf.Min(bottom, Vector3.Dot(bounds.center, up) - Vector3.Dot(bounds.extents, axis));
                }
                if (float.IsPositiveInfinity(bottom)) continue;

                Vector3 center = background.position;
                Vector3 position = center + up * (bottom - Vector3.Dot(center, up) - .10f * scale * view.Units);
                entry.position = view.ToWorldPoint(position);
                entry.gameObject.SetActive(true);
                return;
            }
        }

        internal void PlaceTitleEntry(IList<MenuCameraView> views)
        {
            if (titleEntry == null || !(screen is MainScreen) || IsOpen) return;
            if ((jump == null || flee == null) && Time.realtimeSinceStartup >= nextArtworkSearch)
            {
                nextArtworkSearch = Time.realtimeSinceStartup + .5f;
                if (jump == null)
                {
                    GameObject jumpButton = GameObject.Find("JumpButton");
                    if (jumpButton != null) jump = jumpButton.GetComponentInChildren<Renderer>();
                }
                if (flee == null) FindFleeArtwork();
            }

            if (jump != null && jump.enabled && jump.gameObject.activeInHierarchy &&
                flee != null && flee.enabled && flee.gameObject.activeInHierarchy)
            {
                int buttonLayers = (1 << jump.gameObject.layer) | (1 << flee.gameObject.layer);
                for (int i = views.Count - 1; i >= 0; --i)
                {
                    MenuCameraView view = views[i];
                    Camera camera = view.Camera;
                    if (view.Background || (camera.cullingMask & buttonLayers) != buttonLayers) continue;
                    Vector3 upperLeft = camera.WorldToViewportPoint(jump.bounds.center);
                    Bounds bounds = flee.bounds;
                    Vector3 lowerRight = camera.WorldToViewportPoint(bounds.center);
                    if (upperLeft.z <= camera.nearClipPlane || lowerRight.z <= camera.nearClipPlane ||
                        upperLeft.x < 0 || lowerRight.x > 1 || upperLeft.x >= lowerRight.x ||
                        lowerRight.y < 0 || upperLeft.y > 1 || lowerRight.y >= upperLeft.y) continue;

                    Vector3 corner = new Vector3(upperLeft.x, lowerRight.y, upperLeft.z);
                    titleEntry.position = view.ToWorldPoint(camera.ViewportToWorldPoint(corner));
                    Vector3 extents = bounds.extents / view.Units;
                    Vector3 right = camera.transform.right, up = camera.transform.up;
                    float width = 2 * (Mathf.Abs(right.x) * extents.x + Mathf.Abs(right.y) * extents.y + Mathf.Abs(right.z) * extents.z);
                    float height = 2 * (Mathf.Abs(up.x) * extents.x + Mathf.Abs(up.y) * extents.y + Mathf.Abs(up.z) * extents.z);
                    float rest = StockRestScale();
                    float displayedZoom = rest > .001f ? Mathf.Abs(fleeButton.t.localScale.x) / rest : 1;
                    titleSize = Mathf.Clamp(Mathf.Min(width, height) / (.49f * scale * displayedZoom), .65f, 1.5f);
                    UpdateTitleScale();
                    titleEntry.gameObject.SetActive(true);
                    return;
                }
            }
            titleEntry.gameObject.SetActive(false);
        }

        private void FindFleeArtwork()
        {
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType(typeof(Renderer)))
            {
                Material material = renderer.sharedMaterial;
                if (material == null || !material.HasProperty("_MainTex")) continue;
                Texture texture = material.mainTexture;
                if (texture == null || texture.name != "title_button_flee") continue;

                flee = renderer;
                for (Transform node = renderer.transform; node != null && fleeButton == null; node = node.parent)
                    fleeButton = node.GetComponent<ScaleOnHoverButton>();
                if (fleeButton != null) ReadSounds(fleeButton);
                badgeMaterial.shader = material.shader;
                badgeMaterial.CopyPropertiesFromMaterial(material);
                Vector2 uvScale = material.mainTextureScale;
                badgeMaterial.mainTextureScale = new Vector2(-uvScale.x, uvScale.y);
                badgeMaterial.mainTextureOffset = material.mainTextureOffset + new Vector2(uvScale.x, 0);
                if (badgeMaterial.HasProperty("_Color")) badgeMaterial.color = new Color(.88f, 1, .72f);
                badgePattern.enabled = true;

                break;
            }
        }

        private float StockRestScale()
        {
            return fleeButton != null && fleeButton.t != null && FleeInitialScale != null
                ? Mathf.Abs(((Vector3)FleeInitialScale.GetValue(fleeButton)).x) : 0;
        }

        private void UpdateTitleScale()
        {
            if (titleEntry == null) return;
            float zoom = 1;
            if (hovered == titleControl)
            {
                float rest = StockRestScale();
                zoom = rest > .001f ? Mathf.Max(1, Mathf.Abs(fleeButton.hoverScale.x) / rest) : 1.14f;
            }
            titleEntry.localScale = Vector3.one * (titleSize * zoom);
        }

        internal void Sync(GameScreen current)
        {
            if (screen != current)
            {
                Close();
                screen = current;
                jump = null;
                flee = null;
                fleeButton = null;
                titleSize = 1;
                nextArtworkSearch = 0;
                if (badgePattern != null) badgePattern.enabled = false;
                if (titleEntry != null)
                {
                    titleEntry.gameObject.SetActive(false);
                    titleEntry.localScale = Vector3.one;
                }
            }
            available = current != null && !(current is GameOverScreen) &&
                (current is SetupScreen || current is MainScreen || Utilities.IsGameLevel);
            if (available && entry == null) available = Build();
            if (entry != null)
            {
                entry.gameObject.SetActive(available && !IsOpen && !(current is MainScreen) && !(current is GamePauseScreen));
                entry.localPosition = new Vector3(.64f, .54f, 1.45f) * scale;
            }
            if (titleEntry != null && (!available || IsOpen || !(current is MainScreen)))
                titleEntry.gameObject.SetActive(false);
            if (panel != null) panel.gameObject.SetActive(available && IsOpen);
            if (available && IsOpen)
            {
                bool immersive = VRModSettings.UseImmersiveControls;
                inputMethodValue.text = immersive ? "Immersive motion controls" :
                    VRModSettings.ButtonShortcuts ? "Motion controls\nw/ button shortcuts" : "Gamepad";
                if (!immersive && IsParachuteCalibrationActive()) VRControls.Current.CancelParachuteCalibration();
                LayoutRows(immersive);
                shakeThumb.localPosition = new Vector3((VRModSettings.ShakeScale - .5f) * SliderWidth, 0, -.006f) * scale;
                shakeLabel.text = Mathf.RoundToInt(100 * VRModSettings.ShakeScale) + "%";
                snapTurnValue.text = VRModSettings.SnapTurnAngle + "\u00b0";
                gloveHandValue.text = VRModSettings.FlipItGloveHand == GloveHand.Left ? "LEFT" : "RIGHT";
                bool calibrating = IsParachuteCalibrationActive();
                parachuteLabel.text = calibrating ? "Reach behind and press trigger" : "Set parachute grip location";
                parachuteValue.text = calibrating ? "CANCEL" : "SET";
                hipItemsValue.text = VRModSettings.SwapHipItems ? "ON" : "OFF";
                holsterHeight.text = (100 * VRModSettings.HolsterOffset).ToString("+0.#;-0.#;0")+" cm";
            }
        }

        private void LayoutRows(bool immersive)
        {
            int visibleRows = 0;
            for (int i = 0; i < settingRows.Length; ++i)
            {
                Transform row = settingRows[i];
                bool visible = immersive || i == 1 || i == 2 || i == 4;
                row.gameObject.SetActive(visible);
                if (visible)
                    row.localPosition = new Vector3(0, .211f - visibleRows++ * RowSpacing, 0) * scale;
            }

            float removedHeight = (settingRows.Length - visibleRows) * RowSpacing;
            float height = PanelHeight - removedHeight;
            float center = PanelTop - height * .5f;
            panelBorder.localPosition = new Vector3(0, center, .038f) * scale;
            panelBorder.localScale = new Vector3(1.60f * scale, height * scale, 1);
            panelBackground.localPosition = new Vector3(0, center, .032f) * scale;
            panelBackground.localScale = new Vector3(1.578f * scale, (height - .025f) * scale, 1);
            panelShape.center = new Vector3(0, center, .038f) * scale;
            panelShape.size = new Vector3(panelShape.size.x, height * scale, panelShape.size.z);
            backButton.localPosition = new Vector3(.55f, -.948f + removedHeight, 0) * scale;
        }

        private static bool IsParachuteCalibrationActive()
        {
            return VRControls.Current != null && VRControls.Current.CalibratingParachute;
        }

        private void ToggleParachuteCalibration()
        {
            VRControls input = VRControls.Current;
            if (input == null || !VRModSettings.UseImmersiveControls) return;
            if (input.CalibratingParachute) input.CancelParachuteCalibration();
            else input.BeginParachuteCalibration();
        }

        private void Activate(Control control)
        {
            pressed = null;
            control.Activate();
            Sync(screen);
        }

        internal bool Point(Ray ray, bool tracked, MenuClick click, VRControls input, out Vector3 point, out bool hitTarget)
        {
            point = ray.GetPoint(5*scale);
            hitTarget = false;
            if (!available) { pressed = null; return false; }
            bool modal = IsOpen;
            Control hit = null; float distance = float.MaxValue;
            foreach (Control control in controls)
            {
                RaycastHit contact;
                if ((!input.CalibratingParachute || control == parachuteControl) && tracked && control.Shape.gameObject.activeInHierarchy && control.Shape.Raycast(ray,out contact,5*scale) && contact.distance < distance)
                { hit = control; distance = contact.distance; point = contact.point; }
            }
            hitTarget = hit != null;
            SetHover(hit);
            RaycastHit panelContact;
            if (!hitTarget && tracked && modal && panelShape.Raycast(ray, out panelContact, 5 * scale))
                point = panelContact.point;
            if (!tracked || !input.Focused) CancelPress();
            else if (click == MenuClick.Pressed && hit != null)
            {
                pressed = hit;
                Paint(hit);
                PlayFeedback(true);
            }
            if (pressed != null && pressed == shakeSlider)
            {
                Transform slider = shakeSlider.Shape.transform;
                float distanceToPlane;
                if (new Plane(slider.forward, slider.position).Raycast(ray, out distanceToPlane))
                    VRModSettings.SetShake(.5f + slider.InverseTransformPoint(ray.GetPoint(distanceToPlane)).x / (SliderWidth * scale));
                if (click == MenuClick.Released) CancelPress();
            }
            else if (click == MenuClick.Released)
            {
                Control released = pressed; pressed = null;
                Paint(released);
                if (released != null && released == hit) Activate(released);
            }
            if (!(screen is GamePauseScreen) && hit != shakeSlider && tracked && input.Focused && input.ConfirmDown && click == MenuClick.None && pressed == null && hit != null)
            {
                PlayFeedback(true);
                Activate(hit);
            }
            if (modal && (input.GamepadBackDown || (!(screen is GamePauseScreen) && (input.BackDown || input.PauseDown)))) Close();
            bool consumed = modal || IsOpen || hit != null;
            if (consumed) consumedFrame = Time.frameCount;
            return consumed;
        }

        private void ReadSounds(Button button)
        {
            AudioClip[] clips = button.audioClips;
            if (clips == null || clips.Length == 0) return;
            if (hoverSound == null) hoverSound = clips[0];
            int down = button is ScaleOnHoverButton ? 1 : (int)ButtonEvents.onDown;
            if (pressSound == null && clips.Length > down) pressSound = clips[down];
            if (pressSound == null && !(button is ScaleOnHoverButton) && clips.Length > (int)ButtonEvents.onUp)
                pressSound = clips[(int)ButtonEvents.onUp];
        }

        private void PlayFeedback(bool press)
        {
            if ((press ? pressSound : hoverSound) == null)
            {
                foreach (Button button in Resources.FindObjectsOfTypeAll(typeof(Button)))
                {
                    ReadSounds(button);
                    if (pressSound != null && hoverSound != null) break;
                }
            }
            AudioClip sound = press && pressSound != null ? pressSound : hoverSound;
            if (press && IsOpen)
            {
                if (settingsSelectSound == null)
                    foreach (LevelSelectScreen levelSelect in Resources.FindObjectsOfTypeAll(typeof(LevelSelectScreen)))
                    {
                        settingsSelectSound = levelSelect.levelButtonSelectedClip;
                        if (settingsSelectSound != null) break;
                    }
                if (settingsSelectSound != null) sound = settingsSelectSound;
            }
            if (sound != null && AudioManager.Singleton != null) AudioManager.Singleton.Play(sound);
        }

        private void Paint(Control control)
        {
            if (control == null) return;
            bool held = control == pressed;
            if (control.Text != null) control.Text.color = held ? Color.white : control == hovered ? gold : cream;
            if (control.Face != null) control.Face.sharedMaterial = held ? pressedFill : control == hovered ? hoverFill : buttonFill;
        }

        private void SetHover(Control control)
        {
            if (hovered == control) return;
            Control previous = hovered;
            hovered = control;
            Paint(previous);
            Paint(control);
            UpdateTitleScale();
            if (control != null && pressed == null) PlayFeedback(false);
        }

        internal void Close()
        {
            if (VRControls.Current != null) VRControls.Current.CancelParachuteCalibration();
            IsOpen = false; CancelPress();
            SetHover(null);
            if (panel != null) panel.gameObject.SetActive(false);
        }

        internal void CancelPress()
        {
            Control previous = pressed;
            if (previous != null && previous == shakeSlider) VRModSettings.SaveShake();
            pressed = null;
            Paint(previous);
        }

        internal void Dispose()
        {
            Close();
            foreach (UnityEngine.Object obj in owned) UnityEngine.Object.Destroy(obj);
        }
    }
}
