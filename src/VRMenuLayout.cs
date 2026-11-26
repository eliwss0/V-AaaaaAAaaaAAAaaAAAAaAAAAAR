using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AwesomeVr
{
    internal sealed class MenuSpatialLayout
    {
        private static readonly FieldInfo PauseTooltip = typeof(GamePauseScreen).GetField("tooltipTextMesh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private struct Sampling
        {
            internal FilterMode Filter;
            internal int Anisotropy;
        }
        private readonly Dictionary<Texture,Sampling> sampling = new Dictionary<Texture,Sampling>();
        private float nextSampling;
        private readonly List<Button> hidden = new List<Button>();
        private readonly List<GameObject> hiddenArtwork = new List<GameObject>();
        private readonly Dictionary<GameObject, int> layers = new Dictionary<GameObject, int>();
        private GameScreen current;
        private Transform currentGallery;
        private static bool IntersectsViewport(Camera camera, Bounds bounds)
        {
            Vector3 min = bounds.min, max = bounds.max;
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            for (int i = 0; i < 8; ++i)
            {
                Vector3 p = camera.WorldToViewportPoint(new Vector3((i&1) == 0 ? min.x : max.x,
                    (i&2) == 0 ? min.y : max.y, (i&4) == 0 ? min.z : max.z));
                left = Mathf.Min(left, p.x); right = Mathf.Max(right, p.x);
                bottom = Mathf.Min(bottom, p.y); top = Mathf.Max(top, p.y);
            }
            return right >= 0 && left <= 1 && top >= 0 && bottom <= 1;
        }

        private static bool OutsideViews(Bounds bounds, int layer, GameScreen screen, IList<MenuCameraView> views)
        {
            bool inspected = false;
            foreach (MenuCameraView view in views)
            {
                Camera camera = view.Camera;
                if (view.Background || camera == null || (camera.cullingMask & (1 << layer)) == 0 ||
                    (!camera.orthographic && !(screen is MainScreen) && !(screen is SetupScreen)) ||
                    camera.WorldToViewportPoint(bounds.center).z <= 0) continue;
                inspected = true;
                if (IntersectsViewport(camera, bounds)) return false;
            }
            return inspected;
        }

        private void Inventory(GameScreen screen, IList<MenuCameraView> views)
        {
            hidden.Clear(); hiddenArtwork.Clear();
            GamePauseScreen pause = screen as GamePauseScreen;
            if (pause != null)
            {
                Transform parent = pause.transform.parent;
                Transform caffeine = parent == null ? null : parent.Find("GameHUD/HUDRedOverlayCaffeine");
                if (caffeine != null) hiddenArtwork.Add(caffeine.gameObject);
                TextMesh tooltip = PauseTooltip == null ? null : PauseTooltip.GetValue(pause) as TextMesh;
                if (tooltip != null) hiddenArtwork.Add(tooltip.gameObject);
                GameObject title = GameObject.Find("GamePausedTopLabel");
                if (title != null)
                    foreach (Renderer renderer in title.GetComponentsInChildren<Renderer>(true))
                        if (!hiddenArtwork.Contains(renderer.gameObject)) hiddenArtwork.Add(renderer.gameObject);
            }
            MainScreen main = screen as MainScreen;
            if (main != null)
            {
                SetupScreen setup = main.setupScreen as SetupScreen;
                if (setup != null)
                {
                    HideButton(setup.volumeButton); HideButton(setup.musicButton); HideButton(setup.mouseLookButton);
                    HideButton(setup.fallSpeedButton); HideButton(setup.mouseSensitivityButton); HideButton(setup.controlSchemeButton);
                }
            }
            foreach (GameScreen other in UnityEngine.Object.FindObjectsOfType(typeof(GameScreen)))
            {
                if (other == null || other == screen || screen.transform.IsChildOf(other.transform) ||
                    other.transform.IsChildOf(screen.transform)) continue;
                foreach (Renderer renderer in other.GetComponentsInChildren<Renderer>())
                    if (renderer != null && renderer.gameObject.layer != 30 && !hiddenArtwork.Contains(renderer.gameObject))
                        hiddenArtwork.Add(renderer.gameObject);
                foreach (Button button in other.GetComponentsInChildren<Button>()) HideButton(button);
            }
            foreach (Button button in UnityEngine.Object.FindObjectsOfType(typeof(Button)))
            {
                if (!VRMenu.Usable(button)) continue;
                Collider shape = VRMenu.Shape(button);
                if (shape != null && OutsideViews(shape.bounds, shape.gameObject.layer, screen, views)) HideButton(button);
            }
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType(typeof(Renderer)))
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    renderer.gameObject.layer == 30 || renderer.gameObject.layer == 31) continue;
                if (OutsideViews(renderer.bounds, renderer.gameObject.layer, screen, views) && !hiddenArtwork.Contains(renderer.gameObject))
                    hiddenArtwork.Add(renderer.gameObject);
            }
        }
        private void HideButton(Button button) { if (button != null && !hidden.Contains(button)) hidden.Add(button); }
        internal bool IsHidden(Button button) { return button != null && hidden.Contains(button); }
        private void HideObject(GameObject obj)
        {
            if (obj == null || layers.ContainsKey(obj)) return;
            layers.Add(obj, obj.layer); obj.layer = 31;
        }
        internal void Apply(GameScreen screen, IList<MenuCameraView> views)
        {
            Restore();
            UpdateSampling(screen);
            if (screen == null)
            {
                current = null;
                currentGallery = null;
                hidden.Clear();
                hiddenArtwork.Clear();
                return;
            }
            LevelSelectScreen levels = screen as LevelSelectScreen;
            Transform gallery = levels != null && levels.currentLevelGallery != null ? levels.currentLevelGallery.transform : null;
            if (screen != current || gallery != currentGallery)
            {
                current = screen;
                currentGallery = gallery;
                Inventory(screen, views);
            }
            foreach (Button button in hidden)
            {
                if (button == null) continue;
                HideObject(button.gameObject);
                foreach (Renderer renderer in button.GetComponentsInChildren<Renderer>()) HideObject(renderer.gameObject);
                foreach (Collider collider in button.GetComponentsInChildren<Collider>()) HideObject(collider.gameObject);
            }
            foreach (GameObject obj in hiddenArtwork) HideObject(obj);
        }
        private void UpdateSampling(GameScreen screen)
        {
            LevelSelectScreen levels = screen as LevelSelectScreen;
            if (levels == null) { RestoreSampling(); return; }
            if (Time.realtimeSinceStartup < nextSampling) return;
            nextSampling = Time.realtimeSinceStartup+.5f;
            if (levels.currentLevelGallery == null) return;
            foreach (Renderer renderer in levels.currentLevelGallery.GetComponentsInChildren<Renderer>())
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty("_MainTex")) continue;
                    Texture texture = material.mainTexture;
                    if (texture == null || sampling.ContainsKey(texture)) continue;
                    Sampling original = new Sampling(); original.Filter = texture.filterMode; original.Anisotropy = texture.anisoLevel;
                    sampling.Add(texture,original); texture.filterMode = FilterMode.Trilinear; texture.anisoLevel = Mathf.Max(8,texture.anisoLevel);
                }
        }
        internal void RestoreSampling()
        {
            foreach (KeyValuePair<Texture,Sampling> item in sampling)
                if (item.Key != null) { item.Key.filterMode = item.Value.Filter; item.Key.anisoLevel = item.Value.Anisotropy; }
            sampling.Clear(); nextSampling = 0;
        }
        internal void Restore()
        {
            foreach (KeyValuePair<GameObject, int> item in layers) if (item.Key != null) item.Key.layer = item.Value;
            layers.Clear();
        }
    }
}
