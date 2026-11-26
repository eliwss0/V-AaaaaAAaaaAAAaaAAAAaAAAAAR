using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AwesomeVr
{
    internal sealed class VRSpatialHUD
    {
        private sealed class Mirror
        {
            internal Renderer Source, Output;
            internal TextMesh SourceText, OutputText;
            internal void Update()
            {
                if (Source == null || Output == null) return;
                Output.enabled = Source.enabled && Source.gameObject.activeInHierarchy;
                if (Output.sharedMaterial != Source.sharedMaterial) Output.sharedMaterial = Source.sharedMaterial;
                if (SourceText != null)
                {
                    if (OutputText.text != SourceText.text) OutputText.text = SourceText.text;
                    if (OutputText.color != SourceText.color) OutputText.color = SourceText.color;
                    if (OutputText.font != SourceText.font) OutputText.font = SourceText.font;
                }
            }
        }
        private sealed class TextLayout
        {
            internal TextMesh Text;
            internal float Width, Height;
            internal void Fit()
            {
                if (Text == null || Text.renderer == null || !Text.renderer.enabled) return;
                Vector3 size = Text.renderer.bounds.size;
                if (size.x < .00001f || size.y < .00001f) return;
                float adjustment = Mathf.Min(Width / size.x, Height / size.y);
                float next = Mathf.Min(1, Text.transform.localScale.x * adjustment);
                Text.transform.localScale = Vector3.one * next;
            }
        }
        private readonly Transform owner;
        private readonly float scale;
        private readonly Action<string> log;
        private readonly List<Mirror> mirrors = new List<Mirror>();
        private readonly List<TextLayout> textLayouts = new List<TextLayout>();
        private readonly Dictionary<GameObject, int> layers = new Dictionary<GameObject, int>();
        private readonly Dictionary<GameObject, int> flatLayers = new Dictionary<GameObject, int>();
        private bool flatHidden;
        private HUD source;
        private Transform root, left, right, message;
        private TextMesh speed, proximity;
        private TextMesh fontSource;
        private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal VRSpatialHUD(Transform parent, float units, Action<string> logger) { owner = parent; scale = units; log = logger; }
        private static T Read<T>(HUD hud, string name) where T : class
        {
            FieldInfo field = typeof(HUD).GetField(name, Fields);
            return field == null ? null : field.GetValue(hud) as T;
        }
        private Transform Node(string name, Transform parent)
        {
            Transform node = new GameObject(name).transform;
            node.gameObject.layer = 30; node.parent = parent;
            node.localPosition = Vector3.zero; node.localRotation = Quaternion.identity;
            return node;
        }
        private void Hide(Renderer renderer)
        {
            if (renderer == null || layers.ContainsKey(renderer.gameObject)) return;
            layers.Add(renderer.gameObject, renderer.gameObject.layer);
            renderer.gameObject.layer = 31;
        }
        private TextMesh Text(string name, Transform anchor, TextMesh original, Vector3 offset, bool mirror)
        {
            if (original == null) return null;
            Transform node = Node(name, anchor); node.localPosition = offset*scale;
            TextMesh text = node.gameObject.AddComponent<TextMesh>();
            text.font = original.font; text.fontStyle = original.fontStyle;
            text.fontSize = 64; text.characterSize = .035f*scale;
            text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
            text.color = original.color; node.renderer.sharedMaterials = original.renderer.sharedMaterials;
            TextLayout layout = new TextLayout(); layout.Text = text;
            layout.Width = (name == "Original event text" ? .80f : .40f)*scale;
            layout.Height = (name == "Original event text" ? .10f : .075f)*scale;
            textLayouts.Add(layout);
            if (mirror)
            {
                Mirror item = new Mirror(); item.Source = original.renderer; item.Output = node.renderer;
                item.SourceText = original; item.OutputText = text; mirrors.Add(item); Hide(original.renderer);
            }
            return text;
        }
        private void Artwork(Renderer original, Transform anchor, Vector3 offset, float size)
        {
            if (original == null) return;
            MeshFilter mesh = original.GetComponent<MeshFilter>();
            if (mesh == null || mesh.sharedMesh == null) return;
            Transform node = Node("Original HUD " + original.name, anchor);
            node.localPosition = offset*scale;
            Transform cameraParent = original.transform.parent;
            Camera originalCamera = null;
            while (cameraParent != null && originalCamera == null)
            { originalCamera = cameraParent.camera; cameraParent = cameraParent.parent; }
            node.localRotation = originalCamera == null ? original.transform.localRotation :
                Quaternion.Inverse(originalCamera.transform.rotation)*original.transform.rotation;
            node.localScale = Vector3.one*(size*scale/Mathf.Max(.0001f, mesh.sharedMesh.bounds.size.magnitude));
            node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh.sharedMesh;
            MeshRenderer renderer = node.gameObject.AddComponent<MeshRenderer>();
            Mirror item = new Mirror(); item.Source = original; item.Output = renderer;
            mirrors.Add(item); Hide(original);
        }
        private void Build(HUD hud)
        {
            source = hud; root = Node("awesome_vr spatial gameplay HUD", owner);
            left = Node("Score display", root); right = Node("Descent display", root);
            left.localPosition = new Vector3(-.54f, -.34f, 0)*scale;
            right.localPosition = new Vector3(.54f, -.34f, 0)*scale;
            message = Node("Game event", root);
            fontSource = Read<TextMesh>(hud, "hugsCounter");
            Text("Hugs", left, fontSource, new Vector3(0, .12f, 0), true);
            Text("Kisses", left, Read<TextMesh>(hud, "kissesCounter"), new Vector3(0, -.12f, 0), true);
            speed = Text("Speed", right, fontSource, new Vector3(0, .22f, 0), false);
            proximity = Text("Proximity", right, fontSource, new Vector3(0, -.08f, 0), false);
            if (speed != null) speed.text = "SPEED";
            if (proximity != null) proximity.text = "PROXY";
            Transform[] speedDots = Read<Transform[]>(hud, "dotListLeft");
            Transform[] proximityDots = Read<Transform[]>(hud, "dotListRight");
            if (speedDots != null)
                for (int i = 0; i < speedDots.Length; ++i)
                    if (speedDots[i] != null) Artwork(speedDots[i].renderer, right, new Vector3((i-(speedDots.Length-1)*.5f)*.022f, .12f, 0), .017f);
            if (proximityDots != null)
                for (int i = 0; i < proximityDots.Length; ++i)
                    if (proximityDots[i] != null) Artwork(proximityDots[i].renderer, right, new Vector3((i-(proximityDots.Length-1)*.5f)*.022f, -.18f, 0), .017f);
            TextMesh eventText = Text("Original event text", message, Read<TextMesh>(hud, "eventText"), Vector3.zero, true);
            if (eventText != null) eventText.characterSize = .025f*scale;
            Artwork(Read<Renderer>(hud, "eventPlane"), message, new Vector3(0, .14f, 0), .09f);
            Hide(Read<Renderer>(hud, "lookBall")); Hide(Read<Renderer>(hud, "scanlinesHUD"));
            log("VR HUD bound: original text, event artwork and dots; mirrors=" + mirrors.Count +
                " speedDots=" + (speedDots == null ? 0 : speedDots.Length) +
                " proximityDots=" + (proximityDots == null ? 0 : proximityDots.Length));
        }
        internal void Update(Transform head, bool gameplay)
        {
            HUD hud = HUD.singleton;
            if (source != hud || (hud != null && root == null) || (hud == null && root != null))
            {
                Clear();
                Transform[] dots = hud == null ? null : Read<Transform[]>(hud, "dotListLeft");
                if (dots != null && dots.Length > 0 && dots[0] != null && dots[0].renderer != null) Build(hud);
            }
            if (root == null) return;
            root.gameObject.SetActive(gameplay);
            if (!gameplay) { RestoreFlat(); return; }
            HideFlat();
            root.position = head.position+head.forward*(1.5f*scale);
            root.rotation = head.rotation;
            message.position = head.position+head.forward*(1.35f*scale)-head.up*(.12f*scale);
            message.rotation = head.rotation;
            foreach (Mirror item in mirrors) item.Update();
            Quaternion rotation = root.rotation;
            root.rotation = Quaternion.identity;
            try { foreach (TextLayout item in textLayouts) item.Fit(); }
            finally { root.rotation = rotation; }
            if (fontSource != null)
            {
                if (speed != null)
                {
                    if (speed.font != fontSource.font) speed.font = fontSource.font;
                    if (speed.renderer.sharedMaterial != fontSource.renderer.sharedMaterial) speed.renderer.sharedMaterial = fontSource.renderer.sharedMaterial;
                }
                if (proximity != null)
                {
                    if (proximity.font != fontSource.font) proximity.font = fontSource.font;
                    if (proximity.renderer.sharedMaterial != fontSource.renderer.sharedMaterial) proximity.renderer.sharedMaterial = fontSource.renderer.sharedMaterial;
                }
            }
        }
        private static Camera FindHudCamera()
        {
            foreach (Camera camera in Camera.allCameras)
                if (camera.name == "HUD") return camera;
            return null;
        }

        private void HideFlat()
        {
            if (flatHidden) return;
            Camera camera = FindHudCamera();
            if (camera != null) HideFlat(camera.transform);
        }

        private void HideFlat(Transform artwork)
        {
            if (flatHidden || artwork == null) return;
            foreach (Renderer renderer in artwork.GetComponentsInChildren<Renderer>(true))
            {
                GameObject obj = renderer.gameObject;
                if (layers.ContainsKey(obj) || flatLayers.ContainsKey(obj)) continue;
                flatLayers.Add(obj, obj.layer); obj.layer = 31;
            }
            flatHidden = true;
        }
        private void RestoreFlat()
        {
            foreach (KeyValuePair<GameObject, int> item in flatLayers) if (item.Key != null) item.Key.layer = item.Value;
            flatLayers.Clear(); flatHidden = false;
        }
        private void Clear()
        {
            RestoreFlat();
            foreach (KeyValuePair<GameObject, int> item in layers) if (item.Key != null) item.Key.layer = item.Value;
            layers.Clear(); mirrors.Clear(); textLayouts.Clear();
            if (root != null) { root.gameObject.SetActive(false); UnityEngine.Object.Destroy(root.gameObject); }
            root = null; source = null;
        }
        internal void Dispose() { Clear(); }
    }
}
