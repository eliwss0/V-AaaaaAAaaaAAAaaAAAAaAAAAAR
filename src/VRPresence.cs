using System;
using System.Collections.Generic;
using UnityEngine;

namespace AwesomeVr
{
    internal static class VRWorldQueries
    {
        internal const int Mask = ~((1 << 8) | (1 << 30) | (1 << 31));
        internal static bool Solid(Collider collider)
        {
            if (collider == null || !collider.enabled || collider.isTrigger ||
                (Mask & (1 << collider.gameObject.layer)) == 0) return false;
            Player player = Player.singleton;
            return player == null || player.rigidbody == null || (!collider.transform.IsChildOf(player.transform) &&
                collider.attachedRigidbody != player.rigidbody);
        }
        internal static bool HeadObstructed(Vector3 position, float radius)
        {
            foreach (Collider collider in Physics.OverlapSphere(position, radius, Mask))
                if (Solid(collider)) return true;
            return false;
        }

        internal static float Sweep(Vector3 from, Vector3 to, float radius, out RaycastHit contact)
        {
            contact = new RaycastHit();
            Vector3 delta = to-from;
            float distance = delta.magnitude;
            if (distance < .00001f) return 1;
            float fraction = 1;
            foreach (RaycastHit hit in Physics.SphereCastAll(new Ray(from, delta/distance), radius, distance, Mask))
            {
                if (!Solid(hit.collider) || Vector3.Dot(delta, hit.normal) >= -.00001f) continue;
                float candidate = ContactMath.Fraction(distance, hit.distance, radius*.1f);
                if (candidate < fraction) { fraction = candidate; contact = hit; }
            }
            return fraction;
        }
    }

    internal sealed class VRHeadAudio
    {
        private readonly AudioListener listener;
        private readonly List<AudioListener> suspended = new List<AudioListener>();
        internal VRHeadAudio(Transform head) { listener = head.gameObject.AddComponent<AudioListener>(); }
        internal void Update()
        {
            foreach (AudioListener original in UnityEngine.Object.FindObjectsOfType(typeof(AudioListener)))
            {
                if (original == listener || !original.enabled) continue;
                if (!suspended.Contains(original)) suspended.Add(original);
                original.enabled = false;
            }
            listener.enabled = true;
        }
        internal void Dispose()
        {
            listener.enabled = false;
            foreach (AudioListener original in suspended)
            {
                if (original != null) original.enabled = true;
            }
            UnityEngine.Object.Destroy(listener);
        }
    }

    internal sealed class VRBodyPresence
    {
        private readonly float scale;
        private Vector3 pending;
        private Quaternion heading;
        private Player player;
        private bool active;

        internal VRBodyPresence(float scale) { this.scale = scale; }

        internal void Target(Vector3 trackingDelta, Quaternion rotation, bool enabled)
        {
            player = Player.singleton;
            active = enabled && player != null && player.rigidbody != null;
            pending = active ? new Vector3(trackingDelta.x, 0, trackingDelta.z) : Vector3.zero;
            heading = rotation;
        }

        internal void Suspend()
        {
            active = false;
            pending = Vector3.zero;
        }

        internal Vector3 FixedStep()
        {
            VRControls input = VRControls.Current;
            if (!active || player == null || player != Player.singleton || !player.alive || input == null || !input.Gameplay)
            {
                Suspend();
                return Vector3.zero;
            }
            Rigidbody body = player.rigidbody;
            Vector3 requested = heading * pending * scale;
            if (requested.sqrMagnitude > .8f * .8f * scale * scale)
            {
                Vector3 reset = pending;
                pending = Vector3.zero;
                return reset;
            }

            Vector3 start = body.position;
            Vector3 remaining = Vector3.ClampMagnitude(requested, .15f * scale);
            for (int i = 0; i < 2 && remaining.sqrMagnitude > .0000001f; ++i)
            {
                float distance = remaining.magnitude;
                float fraction = 1;
                Vector3 normal = Vector3.zero;
                foreach (RaycastHit hit in body.SweepTestAll(remaining / distance, distance))
                {
                    if (!VRWorldQueries.Solid(hit.collider) || Vector3.Dot(remaining, hit.normal) >= -.00001f) continue;
                    float stop = ContactMath.Fraction(distance, hit.distance, .005f * scale);
                    if (stop >= fraction) continue;
                    fraction = stop;
                    normal = hit.normal;
                }
                body.position += remaining * fraction;
                if (fraction >= 1) break;
                remaining *= 1 - fraction;
                remaining -= normal * Vector3.Dot(remaining, normal);
                remaining.y = 0;
            }

            Vector3 consumed = Quaternion.Inverse(heading) * (body.position - start) / scale;
            pending -= consumed;
            return consumed;
        }

        internal bool Obstructed(Vector3 head)
        {
            return active && (pending.sqrMagnitude > .12f * .12f ||
                VRWorldQueries.HeadObstructed(head, .085f * scale));
        }
    }

    internal sealed class VRPhysicsHand
    {
        private sealed class Push
        {
            internal Vector3 Point, Direction;
            internal float Depth;
        }
        private readonly Dictionary<Rigidbody, Push> pushes = new Dictionary<Rigidbody, Push>();
        private readonly Rigidbody body;
        private readonly SphereCollider[] shapes = new SphereCollider[6];
        private readonly Vector3[] previous = new Vector3[6];
        private readonly float scale;
        private bool ready;
        private Vector3 targetPosition;
        private Quaternion targetRotation;
        private float nextHaptic;
        private VRPhysicsHand otherHand;
        private Player ignoredPlayer;
        internal VRPhysicsHand(Transform owner, int role, float units)
        {
            scale = units;
            GameObject proxy = new GameObject("awesome_vr physics hand " + role);
            proxy.layer = 8; proxy.transform.parent = owner;
            body = proxy.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
            for (int i = 0; i < shapes.Length; ++i)
            {
                GameObject part = new GameObject("Hand contact " + i); part.layer = 8; part.transform.parent = proxy.transform;
                part.transform.localPosition = Vector3.zero;
                shapes[i] = part.AddComponent<SphereCollider>();
                shapes[i].radius = (i == 0 ? .032f : .009f)*scale;
            }
            proxy.SetActive(false);
        }
        internal void IgnorePlayerAndHand(VRPhysicsHand other)
        {
            if (other != null) otherHand = other;
            ignoredPlayer = Player.singleton;
            foreach (SphereCollider shape in shapes)
            {
                if (other != null) foreach (SphereCollider second in other.shapes) Physics.IgnoreCollision(shape, second);
                if (Player.singleton != null)
                    foreach (Collider collider in Player.singleton.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(shape, collider);
            }
        }
        internal void Suspend() { ready = false; pushes.Clear(); body.gameObject.SetActive(false); }
        internal float Resolve(Transform visual, Vector3 head, Vector3[] points, VRControls input, int role, bool enabled)
        {
            if (!enabled) { Suspend(); return 1; }
            Vector3 desired = visual.position; Quaternion rotation = visual.rotation;
            bool reset = !ready || (desired-targetPosition).magnitude > .6f*scale;
            float fraction = 1;
            pushes.Clear();
            for (int i = 0; i < shapes.Length; ++i)
            {
                Vector3 end = desired+rotation*points[i];
                Vector3 start = reset ? head : previous[i];
                RaycastHit contact;
                float limit = VRWorldQueries.Sweep(start, end, shapes[i].radius, out contact);
                fraction = Mathf.Min(fraction, limit);
                Rigidbody prop = contact.collider == null ? null : contact.collider.attachedRigidbody;
                if (prop != null && !prop.isKinematic)
                {
                    float depth = Mathf.Max(0, Vector3.Dot(end-start, -contact.normal)*(1-limit));
                    Push previousPush;
                    if (!pushes.TryGetValue(prop, out previousPush) || depth > previousPush.Depth)
                    {
                        Push push = new Push(); push.Point = contact.point; push.Direction = -contact.normal; push.Depth = depth;
                        pushes[prop] = push;
                    }
                }
            }
            if (reset)
            {
                Vector3 palm = desired+rotation*points[0];
                targetPosition = Vector3.Lerp(head, palm, fraction)-rotation*points[0];
                targetRotation = rotation;
            }
            else
            {
                targetPosition = Vector3.Lerp(targetPosition, desired, fraction);
                targetRotation = Quaternion.Slerp(targetRotation, rotation, fraction);
            }
            visual.position = targetPosition; visual.rotation = targetRotation;
            body.gameObject.SetActive(true);
            if (reset) { body.position = targetPosition; body.rotation = targetRotation; IgnorePlayerAndHand(otherHand); }
            else if (ignoredPlayer != Player.singleton) IgnorePlayerAndHand(otherHand);
            ready = true;
            if (fraction < .98f && Time.realtimeSinceStartup > nextHaptic)
            { input.Haptic(role, .15f); nextHaptic = Time.realtimeSinceStartup+.12f; }
            return fraction;
        }
        internal void CommitContacts(Vector3[] points)
        {
            if (!ready) return;
            for (int i = 0; i < shapes.Length; ++i)
            { shapes[i].center = points[i]; previous[i] = targetPosition+targetRotation*points[i]; }
        }
        internal void FixedStep()
        {
            if (!ready) return;
            if (VRControls.Current == null || !VRControls.Current.Gameplay) { Suspend(); return; }
            body.MovePosition(targetPosition); body.MoveRotation(targetRotation);
            foreach (KeyValuePair<Rigidbody, Push> item in pushes)
            {
                if (item.Key == null || item.Key.isKinematic) continue;
                Push push = item.Value;
                float speed = Vector3.Dot(item.Key.GetPointVelocity(push.Point), push.Direction)/scale;
                float impulse = ContactMath.PushImpulse(push.Depth/scale, speed)*scale;
                item.Key.AddForceAtPosition(push.Direction*impulse, push.Point, ForceMode.Impulse);
            }
        }
        internal void Dispose() { Suspend(); UnityEngine.Object.Destroy(body.gameObject); }
    }

    internal sealed class VRHeadOcclusion
    {
        private readonly GameObject shell;
        private readonly Material material;
        private readonly Texture2D texture;
        private readonly Mesh mesh;
        private readonly bool transparent;
        private float opacity;
        private FadePlane sceneFade;
        internal VRHeadOcclusion(Transform head, float scale)
        {
            shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            shell.name = "awesome_vr collision fade"; shell.layer = 30;
            UnityEngine.Object.Destroy(shell.collider);
            shell.transform.parent = head; shell.transform.localPosition = Vector3.zero;
            shell.transform.localScale = Vector3.one*(.4f*scale);
            mesh = shell.GetComponent<MeshFilter>().mesh;
            int[] triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3) { int a = triangles[i]; triangles[i] = triangles[i+1]; triangles[i+1] = a; }
            mesh.triangles = triangles;
            Shader shader = Shader.Find("Unlit/Transparent");
            if (shader == null) shader = Shader.Find("GUI/Text Shader");
            if (shader == null) shader = Shader.Find("Transparent/Diffuse");
            transparent = shader != null;
            material = transparent ? new Material(shader) : VRVisualMaterials.Texture();
            texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            material.mainTexture = texture; material.renderQueue = 4000;
            shell.renderer.sharedMaterial = material; shell.renderer.enabled = false;
        }
        internal void Update(bool obstructed)
        {
            opacity = Mathf.MoveTowards(opacity, obstructed ? 1 : 0, Time.unscaledDeltaTime*(obstructed ? 12 : 5));
            if (sceneFade == null) sceneFade = UnityEngine.Object.FindObjectOfType(typeof(FadePlane)) as FadePlane;
            float alpha = Mathf.Max(opacity, sceneFade == null ? 0 : Mathf.Clamp01(sceneFade.Alpha));
            texture.SetPixel(0, 0, new Color(0, 0, 0, alpha)); texture.Apply();
            shell.renderer.enabled = transparent ? alpha > .001f : obstructed || alpha > .5f;
        }
        internal void Dispose()
        { UnityEngine.Object.Destroy(shell); UnityEngine.Object.Destroy(mesh); UnityEngine.Object.Destroy(texture); UnityEngine.Object.Destroy(material); }
    }
}
