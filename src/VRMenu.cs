using System;
using System.Collections.Generic;
using UnityEngine;

namespace AwesomeVr
{
    internal sealed class MenuCameraView
    {
        internal readonly Camera Camera;
        internal float Units;
        internal readonly bool Background;
        private readonly Transform anchor;
        private readonly float forwardOffset;

        internal MenuCameraView(Camera camera, Transform reference, float scale, bool background)
        {
            Camera = camera; anchor = reference; Units = 1; Background = background;
            if (!camera.orthographic) return;
            Units = Mathf.Max(.001f, camera.orthographicSize / (.65f * scale));
            List<float> depths = new List<float>();
            foreach (Button button in UnityEngine.Object.FindObjectsOfType(typeof(Button)))
            {
                if (!VRMenu.Usable(button)) continue;
                Collider shape = VRMenu.Shape(button);
                if (shape == null || (camera.cullingMask & (1 << shape.gameObject.layer)) == 0) continue;
                Vector3 p = camera.WorldToViewportPoint(shape.bounds.center);
                if (p.z <= camera.nearClipPlane || p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) continue;
                RaycastHit hit;
                if (Physics.Raycast(camera.ViewportPointToRay(p), out hit, camera.farClipPlane,
                    camera.cullingMask & ~(1 << 30)) && VRMenu.HitButton(hit.collider) == button) depths.Add(p.z);
            }
            depths.Sort();
            float depth = depths.Count == 0 ? camera.nearClipPlane+camera.orthographicSize : depths[depths.Count/2];
            forwardOffset = depth - 1.65f*scale*Units;
        }

        private Vector3 SourceOrigin { get { return Camera.transform.position+Camera.transform.forward*forwardOffset; } }
        internal Vector3 ToSourcePoint(Vector3 point)
        { return SourceOrigin+Camera.transform.rotation*(Quaternion.Inverse(anchor.rotation)*(point-anchor.position)*Units); }
        internal Vector3 ToWorldPoint(Vector3 point)
        { return anchor.position+anchor.rotation*(Quaternion.Inverse(Camera.transform.rotation)*(point-SourceOrigin)/Units); }
        internal Quaternion ToSourceRotation(Quaternion rotation)
        { return Camera.transform.rotation*Quaternion.Inverse(anchor.rotation)*rotation; }
        internal Ray ToSourceRay(Ray ray)
        { return new Ray(ToSourcePoint(ray.origin), Camera.transform.rotation*Quaternion.Inverse(anchor.rotation)*ray.direction); }
        internal void UpdateIntroScale(bool intro, float scale)
        {
            if (Camera == null || Camera.orthographic) return;
            Units = 1;
            if (!intro || Application.loadedLevelName != "LoaderScene" || Camera.name != "Main Camera") return;
            GameObject logo = GameObject.Find("dejobaanLogo");
            if (logo == null || logo.renderer == null || !logo.renderer.enabled) return;
            Vector3 position = Camera.WorldToViewportPoint(logo.transform.position);
            if (position.z <= .001f || position.z >= .25f || position.x < 0 || position.x > 1 || position.y < 0 || position.y > 1) return;
            Units = Mathf.Clamp(position.z/(1.5f*scale), .001f, 1);
        }
    }

    internal sealed class VRMenu
    {
        private sealed class Target
        {
            internal Button Button;
            internal MenuCameraView View;
            internal Vector3 Position;
        }
        private readonly Transform root, reticle;
        private readonly Mesh reticleMesh;
        private readonly Material laserMaterial;
        private readonly Texture2D laserProfile;
        private readonly LineRenderer laser;
        private readonly float scale;
        private readonly Action<string> log;
        private readonly List<MenuCameraView> views = new List<MenuCameraView>();
        private readonly List<Camera> activeCameras = new List<Camera>();
        private readonly List<Target> targets = new List<Target>();
        private readonly MenuClickLatch clicks = new MenuClickLatch();
        private bool usedGamepadPointer, preferGamepadPointer;
        private readonly MenuSpatialLayout layout;
        private readonly VRSettingsPanel settings;
        private GUIController controller;
        private bool active, navigationMode;
        internal static bool InputActive { get; private set; }
        private GameScreen screen;
        private Button hovered, pressed, focused;
        private Camera hoverCamera, pressCamera;
        private MenuCameraView focusView;
        private int pointerRole;
        private float nextTargets;
        internal IList<MenuCameraView> Views { get { return views; } }
        private bool Ready { get { return screen != null && controller != null && controller.InputEnabled && controller.currentScreen == screen; } }
        internal static Collider Shape(Button button)
        { return button.collider != null ? button.collider : button.GetComponentInChildren<Collider>(); }
        internal static bool Usable(Button button)
        { return button != null && button.enabled && button.gameObject.activeInHierarchy; }

        internal VRMenu(Transform origin, float worldScale, Action<string> logger)
        {
            scale = worldScale; log = logger;
            root = new GameObject("awesome_vr menu reference").transform; root.parent = origin;
            layout = new MenuSpatialLayout();
            settings = new VRSettingsPanel(root, scale);
            laserProfile = new Texture2D(1, 64, TextureFormat.RGBA32, false);
            laserProfile.wrapMode = TextureWrapMode.Clamp;
            laserProfile.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < laserProfile.height; ++y)
            {
                float edge = 1 - Mathf.Abs(2f * y / (laserProfile.height - 1) - 1);
                float alpha = Mathf.SmoothStep(0, 1, edge);
                laserProfile.SetPixel(0, y, new Color(.39f, .95f, 1f, alpha));
            }
            laserProfile.Apply();
            Shader shader = Shader.Find("Unlit/Transparent");
            if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("GUI/Text Shader");
            laserMaterial = shader != null ? new Material(shader) : VRVisualMaterials.Texture();
            laserMaterial.mainTexture = laserProfile;
            GameObject beam = new GameObject("awesome_vr menu laser");
            beam.layer = 30;
            beam.transform.parent = root;
            laser = beam.AddComponent<LineRenderer>();
            laser.sharedMaterial = laserMaterial;
            laser.useWorldSpace = true;
            laser.SetVertexCount(2);
            GameObject marker = new GameObject("awesome_vr laser reticle");
            marker.layer = 30;
            reticle = marker.transform;
            reticle.parent = root;
            reticleMesh = CreateReticle();
            marker.AddComponent<MeshFilter>().sharedMesh = reticleMesh;
            marker.AddComponent<MeshRenderer>().sharedMaterial = laserMaterial;
            root.gameObject.SetActive(false);
        }

        private static Mesh CreateReticle()
        {
            const int segments = 48;
            Vector3[] vertices = new Vector3[2*segments];
            Vector2[] uv = new Vector2[vertices.Length];
            int[] triangles = new int[6*segments];
            for (int i = 0; i < segments; ++i)
            {
                float angle = 2*Mathf.PI*i/segments;
                int current = 2*i, next = 2*((i+1)%segments), triangle = 6*i;
                vertices[current] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                vertices[current+1] = vertices[current]*.45f;
                uv[current+1] = Vector2.up;
                triangles[triangle] = current;
                triangles[triangle+1] = current+1;
                triangles[triangle+2] = next;
                triangles[triangle+3] = next;
                triangles[triangle+4] = current+1;
                triangles[triangle+5] = next+1;
            }
            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void DrawPointer(Ray ray, Vector3 end, bool hit, Transform head)
        {
            float endDistance = Vector3.Distance(head.position, end);
            laser.SetWidth(.012f * scale, Mathf.Max(.014f * scale, endDistance * .005f));
            laser.SetPosition(0, ray.origin);
            laser.SetPosition(1, end);
            reticle.gameObject.SetActive(hit);
            if (!hit) return;
            reticle.position = end-ray.direction*(.002f*scale);
            Vector3 fromEye = reticle.position-head.position;
            if (fromEye.sqrMagnitude > .000001f) reticle.rotation = Quaternion.LookRotation(fromEye, head.up);
            reticle.localScale = Vector3.one*Mathf.Max(fromEye.magnitude * .009f, .005f * scale);
        }

        internal void Enter(Transform head)
        {
            if (active) return;
            preferGamepadPointer = false;
            active = InputActive = true; root.position = head.position; root.rotation = Quaternion.Euler(0, head.eulerAngles.y, 0);
            root.gameObject.SetActive(true); clicks.Reset(); nextTargets = 0;
        }
        internal void Exit()
        {
            if (!active) return;
            layout.Restore();
            layout.RestoreSampling();
            settings.Close();
            CancelPress(); ClearHover();
            controller = null; screen = null; focused = null; focusView = null;
            active = InputActive = navigationMode = false; pointerRole = 0;
            activeCameras.Clear(); views.Clear(); targets.Clear(); clicks.Reset(); root.gameObject.SetActive(false);
        }

        internal void Sync()
        {
            if (!active) return;
            if (controller == null) controller = GUIController.Instance;
            GameScreen current = controller == null ? null : controller.currentScreen;
            if (current != screen)
            {
                CancelPress(); ClearHover(); clicks.Reset(); screen = current;
                focused = null; focusView = null; navigationMode = false;
                views.Clear(); targets.Clear(); nextTargets = 0;
                log("VR menu: "+(screen == null ? "intro" : screen.GetType().Name));
            }
            Camera[] cameras = screen == null || screen.cameras == null || screen.cameras.Length == 0 ? Camera.allCameras : screen.cameras;
            activeCameras.Clear();
            if (cameras != null)
                foreach (Camera camera in cameras)
                    if (camera != null && camera.enabled && camera.targetTexture == null && !activeCameras.Contains(camera)) activeCameras.Add(camera);
            MainScreen main = screen as MainScreen;
            Camera replay = main == null ? null : main.replayCamera;
            if (replay != null && replay.enabled && !activeCameras.Contains(replay)) activeCameras.Add(replay);
            if (screen is GameOverScreen)
            {
                GameObject world = GameObject.Find("mainCam");
                if (world != null && world.camera != null && world.camera.targetTexture == null)
                {
                    replay = world.camera;
                    if (!activeCameras.Contains(replay)) activeCameras.Add(replay);
                }
            }
            activeCameras.Sort(delegate(Camera a, Camera b) {
                if (a == replay && b != replay) return -1;
                if (b == replay && a != replay) return 1;
                return a.depth.CompareTo(b.depth);
            });
            bool changed = activeCameras.Count != views.Count;
            if (!changed)
                for (int i = 0; i < activeCameras.Count; ++i) if (activeCameras[i] != views[i].Camera) { changed = true; break; }
            if (changed)
            {
                views.Clear(); nextTargets = 0;
                foreach (Camera camera in activeCameras)
                {
                    MenuCameraView view = new MenuCameraView(camera, root, scale, camera == replay); views.Add(view);
                    log("VR menu camera: "+camera.name+" depth="+camera.depth+" ortho="+camera.orthographic+" units="+view.Units);
                }
            }
            foreach (MenuCameraView view in views) view.UpdateIntroScale(screen == null, scale);
            settings.Sync(screen);
            settings.PlaceTitleEntry(views);
            settings.PlacePauseEntry(views);
            if (Time.realtimeSinceStartup < nextTargets) return;
            nextTargets = Time.realtimeSinceStartup+.25f; targets.Clear();
            if (screen == null || screen is LevelSelectScreen) return;
            foreach (Button button in UnityEngine.Object.FindObjectsOfType(typeof(Button)))
            {
                if (!Usable(button)) continue;
                Collider shape = Shape(button);
                if (shape == null || !shape.enabled) continue;
                for (int i = views.Count-1; i >= 0; --i)
                {
                    MenuCameraView view = views[i]; Camera camera = view.Camera;
                    if (view.Background) continue;
                    Vector3 p = camera.WorldToViewportPoint(shape.bounds.center);
                    if (p.z <= 0 || p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) continue;
                    RaycastHit hit;
                    if (!Physics.Raycast(camera.ViewportPointToRay(p), out hit, controller.RayDistance,
                        camera.cullingMask & ~(1 << 30)) || HitButton(hit.collider) != button) continue;
                    Target target = new Target(); target.Button = button; target.View = view;
                    target.Position = root.InverseTransformPoint(view.ToWorldPoint(shape.bounds.center));
                    targets.Add(target); break;
                }
            }
            if (focused != null && !targets.Exists(delegate(Target item) { return item.Button == focused; }))
            { focused = null; focusView = null; }
        }

        internal static Button HitButton(Collider collider)
        {
            Transform transform = collider.transform;
            while (transform != null)
            {
                Button button = transform.GetComponent<Button>();
                if (button != null) return Usable(button) ? button : null;
                transform = transform.parent;
            }
            return null;
        }
        private bool PointAt(Ray ray, out Button button, out MenuCameraView selected, out Vector3 point)
        {
            button = null; selected = null; point = ray.GetPoint(5*scale);
            if (!Ready) return false;
            for (int i = views.Count-1; i >= 0; --i)
            {
                MenuCameraView view = views[i]; RaycastHit hit;
                if (view.Background) continue;
                if (!Physics.Raycast(view.ToSourceRay(ray), out hit, controller.RayDistance,
                    view.Camera.cullingMask & ~((1 << 30) | (1 << 31)))) continue;
                Button candidate = HitButton(hit.collider);
                if (candidate == null) continue;
                LevelSelectScreen levels = screen as LevelSelectScreen;
                if (levels != null && candidate is LevelButton && levels.currentLevelGallery != null &&
                    !candidate.transform.IsChildOf(levels.currentLevelGallery.transform)) continue;
                button = candidate; selected = view; point = view.ToWorldPoint(hit.point); return true;
            }
            return false;
        }

        internal void ApplyLayout() { layout.Apply(screen, views); }
        internal void RestoreLayout() { layout.Restore(); }

        internal void Point(Transform head, VRGlove[] gloves, VRControls input)
        {
            int role = pointerRole == 0 ? 2 : pointerRole;
            if (input.Hands[0].SelectDown) role = 1;
            if (input.Hands[1].SelectDown) role = 2;
            if (!input.Hands[role-1].Tracked) role = input.Hands[2-role].Tracked ? 3-role : 0;
            bool triggerDown = role != 0 && input.Hands[role-1].SelectDown;
            if (triggerDown || input.Navigation != MenuDirection.None) preferGamepadPointer = false;
            else if (VRModSettings.UseGamepad && input.GamepadSelectDown) preferGamepadPointer = true;
            bool gamepadPointer = input.Focused && (role == 0 || VRModSettings.UseGamepad && preferGamepadPointer);
            if (role != pointerRole || gamepadPointer != usedGamepadPointer)
            {
                CancelPress();
                settings.CancelPress();
                clicks.Reset();
                pointerRole = role;
                usedGamepadPointer = gamepadPointer;
                if (gamepadPointer ? input.GamepadSelectDown : triggerDown) clicks.Update(Ready, false);
            }
            bool held = gamepadPointer ? input.GamepadSelect : role != 0 && input.Hands[role-1].Select;
            MenuClick click = clicks.Update((role != 0 || gamepadPointer) && Ready, held);
            if (!Ready) { CancelPress(); ClearHover(); }
            laser.enabled = role != 0 && !gamepadPointer;
            if (role == 0 || gamepadPointer) reticle.gameObject.SetActive(false);
            Ray ray = gamepadPointer ? new Ray(head.position, head.forward) :
                role == 0 ? new Ray(root.position, root.forward) : gloves[role-1].PointingRay(input.Hands[role-1]);
            Vector3 point;
            bool settingsHit;
            if (settings.Point(ray, (role != 0 || gamepadPointer) && Ready, click, input, out point, out settingsHit))
            {
                CancelPress(); ClearHover(); navigationMode = false;
                if (gamepadPointer) DrawGazePoint(point, settingsHit, head);
                else if (role != 0) DrawPointer(ray, point, settingsHit, head);
                return;
            }
            Button hitButton = null; MenuCameraView hitView = null;
            if (gamepadPointer ? !(screen is LevelSelectScreen) : role != 0)
            {
                bool hit = PointAt(ray, out hitButton, out hitView, out point);
                if (gamepadPointer) DrawGazePoint(point, hit, head);
                else DrawPointer(ray, point, hit, head);
            }
            if (click == MenuClick.Pressed) navigationMode = false;
            if (Ready && !(screen is LevelSelectScreen) && !(screen is GamePauseScreen) && input.Navigation != MenuDirection.None)
            {
                if (!navigationMode) { focused = hitButton; focusView = hitView; }
                MoveFocus(input.Navigation); navigationMode = true;
            }
            if (navigationMode && Usable(focused) && !layout.IsHidden(focused)) { hitButton = focused; hitView = focusView; }
            Camera hitCamera = hitView == null ? null : hitView.Camera;
            if (hovered != hitButton)
            {
                ClearHover(); hovered = hitButton; hoverCamera = hitCamera;
                if (hovered != null && Ready) { hovered.OnEnter(); screen.DoButton(hovered, ButtonEvents.onEnter, false, hoverCamera); }
            }
            else if (hovered != null && Ready && !held)
            { hovered.OnHover(); screen.DoButton(hovered, ButtonEvents.onHover, true, hoverCamera); }
            if (!Ready) { CancelPress(); return; }
            if (click == MenuClick.Pressed && hitButton != null)
            {
                pressed = hitButton; pressCamera = hitCamera; hitButton.OnDown();
                screen.DoButton(hitButton, ButtonEvents.onDown, false, hitCamera);
            }
            else if (click == MenuClick.Released)
            {
                Button released = pressed; Camera releasedCamera = pressCamera; pressed = null; pressCamera = null;
                if (released != null)
                {
                    released.OnUp();
                    if (Ready && released == hitButton) screen.DoButton(released, ButtonEvents.onUp, true, releasedCamera);
                }
            }
            else if (held && pressed != null && pressed == hitButton && Ready)
            { pressed.OnPress(); screen.DoButton(pressed, ButtonEvents.onPress, true, pressCamera); }
            if (input.ConfirmDown && click == MenuClick.None && pressed == null && Ready && !(screen is LevelSelectScreen) && !(screen is GamePauseScreen))
            {
                if (hitButton == null)
                {
                    if (!Usable(focused)) MoveFocus(MenuDirection.None);
                    hitButton = focused; hitCamera = focusView == null ? null : focusView.Camera;
                }
                if (Usable(hitButton))
                {
                    GameScreen owner = screen; hitButton.OnDown(); owner.DoButton(hitButton, ButtonEvents.onDown, false, hitCamera);
                    if (hitButton != null) hitButton.OnUp();
                    if (Ready && owner == screen && hitButton != null) owner.DoButton(hitButton, ButtonEvents.onUp, true, hitCamera);
                    input.Haptic(role == 0 ? 1 : role-1, .3f);
                }
            }
        }

        private void DrawGazePoint(Vector3 point, bool hit, Transform head)
        {
            DrawPointer(new Ray(head.position, head.forward), point, hit, head);
            laser.enabled = false;
        }

        private void MoveFocus(MenuDirection direction)
        {
            Target best = null;
            float score = float.MaxValue;
            Vector3 from = Vector3.zero;
            foreach (Target item in targets)
            {
                if (item.Button != focused) continue;
                from = item.Position;
                break;
            }
            bool horizontal = direction == MenuDirection.Right || direction == MenuDirection.Left;
            bool forward = direction == MenuDirection.Right || direction == MenuDirection.Up;
            bool nearest = focused == null || direction == MenuDirection.None;
            foreach (Target item in targets)
            {
                if (!Usable(item.Button) || layout.IsHidden(item.Button) || item.Button == focused) continue;
                Vector3 offset = item.Position - from;
                float along = horizontal ? offset.x : offset.y;
                if (!forward) along = -along;
                if (!nearest && along <= .001f) continue;
                float across = horizontal ? Mathf.Abs(offset.y) : Mathf.Abs(offset.x);
                float candidate = nearest ? offset.sqrMagnitude : along + across*3;
                if (candidate < score) { best = item; score = candidate; }
            }
            if (best != null) { focused = best.Button; focusView = best.View; }
        }
        private void CancelPress()
        { if (pressed != null) { pressed.OnUp(); pressed.OnExit(); } pressed = null; pressCamera = null; }
        private void ClearHover()
        {
            if (hovered != null)
            { hovered.OnExit(); if (Ready) screen.DoButton(hovered, ButtonEvents.onExit, true, hoverCamera); }
            hovered = null; hoverCamera = null;
        }
        internal void Dispose()
        {
            Exit();
            settings.Dispose();
            UnityEngine.Object.Destroy(root.gameObject);
            UnityEngine.Object.Destroy(reticleMesh);
            UnityEngine.Object.Destroy(laserMaterial);
            UnityEngine.Object.Destroy(laserProfile);
        }
    }
}
