using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace AwesomeVr
{
    internal static class VRVisualMaterials
    {
        private static Shader GetShader()
        {
            Shader shader = Shader.Find("Unlit/Texture");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null)
                throw new InvalidOperationException("No usable shader for VR visuals (Unlit/Texture or Diffuse).");
            return shader;
        }

        internal static Material Texture()
        {
            return new Material(GetShader());
        }

        internal static Material Palette(Color32[] colors, int stripeWidth = 1)
        {
            Texture2D texture = new Texture2D(colors.Length * stripeWidth, 1, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[texture.width];
            for (int x = 0; x < pixels.Length; ++x)
            {
                float shade = Mathf.Clamp((x + .5f) / stripeWidth - .5f, 0, colors.Length - 1);
                int lower = Mathf.FloorToInt(shade);
                int upper = Mathf.Min(lower + 1, colors.Length - 1);
                pixels[x] = Color.Lerp(colors[lower], colors[upper], shade - lower);
            }
            texture.SetPixels32(pixels);
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = stripeWidth == 1 ? FilterMode.Point : FilterMode.Bilinear;
            texture.Apply();
            Material material = Texture();
            material.mainTexture = texture;
            return material;
        }

        internal static Material Image(string name, Shader shader = null)
        {
            Material material = shader == null ? Texture() : new Material(shader);
            string resource = "awesome_vr." + name + ".png";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null) throw new FileNotFoundException("Embedded VR texture missing", resource);
                byte[] bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException(resource);
                    read += count;
                }
                Texture2D image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!image.LoadImage(bytes)) throw new InvalidOperationException("Could not decode " + resource);
                image.wrapMode = TextureWrapMode.Repeat;
                image.filterMode = FilterMode.Bilinear;
                material.mainTexture = image;
            }
            return material;
        }
    }

    internal static class VREquipmentModels
    {
        private static Mesh canMesh, shotMesh;
        private static Material canMaterial, shotMaterial;

        private static readonly Color32[] CanColors = {
            new Color32(90, 150, 206, 255), new Color32(149, 163, 177, 255), new Color32(4, 4, 4, 255)
        };
        private static readonly Color32[] ShotColors = {
            new Color32(231, 231, 231, 255), new Color32(28, 13, 0, 255)
        };

        private static Transform Node(Transform parent, string name)
        {
            Transform node = new GameObject(name).transform;
            node.gameObject.layer = 30;
            node.parent = parent;
            node.localPosition = Vector3.zero;
            node.localRotation = Quaternion.identity;
            return node;
        }

        internal static Transform Can(Transform parent, float scale, out Transform nozzle)
        {
            if (canMesh == null) LoadModel("spray_can", CanColors, out canMesh, out canMaterial);
            float canScale = scale * 1.15f;
            Transform can = Node(parent, "Spray can");
            AddMesh(can, "Can mesh", canScale, canMesh, canMaterial);
            nozzle = Node(can, "Spray nozzle");
            nozzle.localPosition = new Vector3(0, .071f, .0064f) * canScale;
            return can;
        }

        internal static Transform Shot(Transform parent, float scale)
        {
            if (shotMesh == null) LoadModel("espresso", ShotColors, out shotMesh, out shotMaterial);
            Transform cup = Node(parent, "Espresso shot");
            AddMesh(cup, "Cup mesh", scale * .9f, shotMesh, shotMaterial);
            return cup;
        }

        private static void AddMesh(Transform parent, string name, float scale, Mesh mesh, Material material)
        {
            Transform model = Node(parent, name);
            model.localScale = Vector3.one * scale;
            model.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            model.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void LoadModel(string name, Color32[] colors, out Mesh mesh, out Material material)
        {
            material = VRVisualMaterials.Palette(colors);
            int paletteSize = colors.Length;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("awesome_vr." + name + ".mesh"))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                int count = reader.ReadInt32();
                Vector3[] vertices = new Vector3[count];
                Vector2[] uv = new Vector2[count];
                int[] triangles = new int[count];
                for (int i = 0; i < count; ++i)
                {
                    vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    uv[i] = new Vector2((reader.ReadByte() + .5f) / paletteSize, .5f);
                    triangles[i] = i;
                }
                mesh = new Mesh();
                mesh.name = name;
                mesh.vertices = vertices;
                mesh.uv = uv;
                mesh.triangles = triangles;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
            }
        }

        internal static void Release()
        {
            ReleaseModel(ref canMesh, ref canMaterial);
            ReleaseModel(ref shotMesh, ref shotMaterial);
        }

        private static void ReleaseModel(ref Mesh mesh, ref Material material)
        {
            if (material != null)
            {
                UnityEngine.Object.Destroy(material.mainTexture);
                UnityEngine.Object.Destroy(material);
                material = null;
            }
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            mesh = null;
        }
    }

    internal sealed class VRGlove
    {
        private readonly Transform root, can, shot, nozzle;
        private readonly Transform[] bones = new Transform[21];
        private readonly Vector3[] rest = new Vector3[31], joints = new Vector3[31];
        private readonly float[] restLengths = new float[21];
        private readonly Vector3[] previousPositions = new Vector3[21], previousScales = new Vector3[21];
        private readonly Quaternion[] previousRotations = new Quaternion[21];
        private bool cachedPose;
        private readonly Mesh skinMesh;
        private readonly SkinnedMeshRenderer skinRenderer;
        private readonly Vector3 indexDirection;
        private Vector3 pointerAnchor, pointerFallback;
        private bool pointerAttached, pointerSkeleton, pointerResting;
        private static readonly Color32[] SkinColors = {
            new Color32(159, 161, 163, 255), new Color32(177, 179, 180, 255), new Color32(193, 193, 190, 255),
            new Color32(205, 205, 202, 255), new Color32(210, 210, 207, 255), new Color32(149, 151, 154, 255)
        };
        private readonly int side;
        private readonly float scale;
        private static readonly int[][] Chains = {
            new int[] {1,2,3,4,5}, new int[] {6,7,8,9,10}, new int[] {11,12,13,14,15},
            new int[] {16,17,18,19,20}, new int[] {21,22,23,24,25} };

        internal VRGlove(Transform trackingRoot, int hand, float worldScale)
        {
            root = trackingRoot; side = hand == 0 ? -1 : 1; scale = worldScale;
            GameObject skin = new GameObject(hand == 0 ? "Human left hand" : "Human right hand");
            skin.layer = 30;
            skin.transform.parent = root;
            skin.transform.localPosition = Vector3.zero;
            skin.transform.localRotation = Quaternion.identity;
            skin.transform.localScale = Vector3.one * scale;
            skinMesh = LoadSkin();
            indexDirection = (rest[7] - rest[6]).normalized;
            Matrix4x4[] bindPoses = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; ++i)
            {
                bones[i] = new GameObject("Human hand bone " + i).transform;
                bones[i].parent = skin.transform;
                if (i == 0)
                {
                    bones[i].localPosition = Vector3.zero;
                    bones[i].localRotation = Quaternion.identity;
                }
                else
                {
                    int finger = (i-1)/4, segment = (i-1)%4;
                    Vector3 a = rest[Chains[finger][segment]], b = rest[Chains[finger][segment+1]];
                    bones[i].localPosition = a;
                    restLengths[i] = (b-a).magnitude;
                    bones[i].localRotation = Quaternion.LookRotation(b-a, Vector3.up);
                }
                bindPoses[i] = bones[i].worldToLocalMatrix * skin.transform.localToWorldMatrix;
            }
            skinMesh.bindposes = bindPoses;
            SkinnedMeshRenderer renderer = skin.AddComponent<SkinnedMeshRenderer>();
            skinRenderer = renderer;
            renderer.sharedMesh = skinMesh;
            renderer.sharedMaterial = VRVisualMaterials.Palette(SkinColors, 8);
            renderer.bones = bones;
            renderer.quality = SkinQuality.Bone4;
            renderer.updateWhenOffscreen = true;
            renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * .6f);
            Transform sprayNozzle;
            can = VREquipmentModels.Can(root, scale, out sprayNozzle);
            nozzle = sprayNozzle;
            can.gameObject.SetActive(false);
            shot = VREquipmentModels.Shot(root, scale);
            shot.gameObject.SetActive(false);
        }

        private Mesh LoadSkin()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("awesome_vr.human_hand.mesh"))
            {
                if (stream == null) throw new FileNotFoundException("Embedded human hand mesh missing");
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != 0x444E4841 || reader.ReadInt32() != 1)
                        throw new IOException("Human hand mesh header");
                    int count = reader.ReadInt32(), indices = reader.ReadInt32();
                    for (int i = 0; i < rest.Length; ++i)
                        rest[i] = new Vector3(reader.ReadSingle()*side, reader.ReadSingle(), reader.ReadSingle());
                    Vector3[] vertices = new Vector3[count];
                    Vector2[] uv = new Vector2[count];
                    BoneWeight[] weights = new BoneWeight[count];
                    for (int i = 0; i < count; ++i)
                    {
                        vertices[i] = new Vector3(reader.ReadSingle()*side, reader.ReadSingle(), reader.ReadSingle());
                        uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                        weights[i].boneIndex0 = reader.ReadInt32(); weights[i].boneIndex1 = reader.ReadInt32();
                        weights[i].boneIndex2 = reader.ReadInt32(); weights[i].boneIndex3 = reader.ReadInt32();
                        weights[i].weight0 = reader.ReadSingle(); weights[i].weight1 = reader.ReadSingle();
                        weights[i].weight2 = reader.ReadSingle(); weights[i].weight3 = reader.ReadSingle();
                    }
                    int[] triangles = new int[indices];
                    for (int i = 0; i < indices; i += 3)
                    {
                        int a = reader.ReadInt32(), b = reader.ReadInt32(), c = reader.ReadInt32();
                        triangles[i] = a;
                        triangles[i+1] = side < 0 ? c : b;
                        triangles[i+2] = side < 0 ? b : c;
                    }
                    Mesh mesh = new Mesh();
                    mesh.name = "Human hand skin";
                    mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles; mesh.boneWeights = weights;
                    mesh.RecalculateNormals(); mesh.RecalculateBounds();
                    return mesh;
                }
            }
        }

        private static Vector3 Bone(float[] bones, int index)
        { return new Vector3(bones[index*3], bones[index*3+1], -bones[index*3+2]); }

        internal void Update(VRHandInput input)
        {
            bool equipped = !input.ShotHeld && input.CanHeld;
            skinRenderer.enabled = !equipped && !input.ShotHeld && !input.Pull.Armed;
            can.gameObject.SetActive(equipped && PlayerData.SprayCanEnabled);
            shot.gameObject.SetActive(input.ShotHeld && PlayerData.CaffeineEnabled);
            Quaternion palmFrame = Quaternion.identity;
            Vector3 wrist = rest[1];
            Vector3 palmScale = Vector3.one;
            bool itemPose = equipped || input.ShotHeld;
            bool canPose = itemPose && (!input.GripSkeleton || !input.Grip);
            float[] trackingBones = itemPose && !canPose ? input.GripBones : input.Bones;
            if (input.Skeleton)
            {
                wrist = Bone(trackingBones, 1);
                Vector3 knuckle = (Bone(trackingBones,7)+Bone(trackingBones,12)+Bone(trackingBones,17)+Bone(trackingBones,22)) * .25f;
                Vector3 across = Bone(trackingBones,7)-Bone(trackingBones,22);
                Vector3 along = knuckle-wrist;
                if (along.sqrMagnitude > .00001f && across.sqrMagnitude > .00001f)
                    palmFrame = Quaternion.LookRotation(along, Vector3.Cross(across,along) * side);
                Vector3 restKnuckle = (rest[7]+rest[12]+rest[17]+rest[22])*.25f;
                palmScale = new Vector3(Mathf.Clamp(across.magnitude/(rest[7]-rest[22]).magnitude, .7f, 1.4f),
                    1, Mathf.Clamp(along.magnitude/(restKnuckle-rest[1]).magnitude, .7f, 1.4f));
            }
            bones[0].localRotation = palmFrame;
            bones[0].localScale = palmScale;
            bones[0].localPosition = wrist-palmFrame*Vector3.Scale(rest[1], palmScale);
            Vector3 palmUp = palmFrame * Vector3.up;
            for (int i = 0; i < joints.Length; ++i)
                joints[i] = input.Skeleton && !canPose ? Bone(trackingBones, i) : wrist+palmFrame*(rest[i]-rest[1]);
            for (int finger = 0; finger < 5; ++finger)
            {
                for (int segment = 0; segment < 4; ++segment)
                {
                    int start = Chains[finger][segment], end = Chains[finger][segment+1];
                    if ((!input.Skeleton || canPose) && segment > 0)
                    {
                        float curl = input.Curls[finger];
                        if (canPose) curl = finger == 0 ? .65f : finger == 1 ? Mathf.Max(.45f, input.Values[0]) : .8f;
                        Vector3 direction = rest[end]-rest[start];
                        Vector3 bendAxis = Vector3.Cross(Vector3.up, direction).normalized;
                        float angle = curl * (finger == 0 ? segment*30 : segment == 1 ? 60 : segment == 2 ? 130 : 170);
                        joints[end] = joints[start] + palmFrame * (Quaternion.AngleAxis(angle, bendAxis) * direction);
                    }
                    int bone = 1+finger*4+segment;
                    Vector3 delta = joints[end]-joints[start];
                    bones[bone].localPosition = joints[start];
                    if (delta.sqrMagnitude > .000001f)
                    {
                        Vector3 up = Vector3.Cross(delta, palmFrame * Vector3.right);
                        if (up.sqrMagnitude < .000001f) up = palmUp;
                        bones[bone].localRotation = Quaternion.LookRotation(delta, up);
                    }
                    bones[bone].localScale = new Vector3(1, 1, Mathf.Clamp(delta.magnitude/restLengths[bone], .4f, 2));
                }
            }
        }

        internal void ApplyContactFraction(float fraction)
        {
            for (int i = 0; i < bones.Length; ++i)
            {
                if (cachedPose && fraction < 1)
                {
                    bones[i].localPosition = Vector3.Lerp(previousPositions[i], bones[i].localPosition, fraction);
                    bones[i].localRotation = Quaternion.Slerp(previousRotations[i], bones[i].localRotation, fraction);
                    bones[i].localScale = Vector3.Lerp(previousScales[i], bones[i].localScale, fraction);
                }
                previousPositions[i] = bones[i].localPosition;
                previousRotations[i] = bones[i].localRotation;
                previousScales[i] = bones[i].localScale;
            }
            cachedPose = true;
        }

        internal void CollisionPoints(Vector3[] points)
        {
            Vector3 knuckle = (rest[7]+rest[12]+rest[17]+rest[22])*.25f;
            points[0] = root.InverseTransformPoint(bones[0].TransformPoint((rest[1]+knuckle)*.5f));
            for (int finger = 0; finger < 5; ++finger)
            {
                int bone = 1+finger*4+3;
                points[finger+1] = root.InverseTransformPoint(bones[bone].TransformPoint(new Vector3(0, 0, restLengths[bone])));
            }
        }

        internal Ray PointingRay(VRHandInput input)
        {
            bool resting = input.Curls[1] < .2f && !input.Select;
            if (!pointerAttached || pointerSkeleton != input.Skeleton || (resting && !pointerResting))
            {
                Vector3 metacarpal = bones[5].position;
                Vector3 knuckle = bones[6].position;
                pointerAnchor = root.InverseTransformPoint(Vector3.Lerp(metacarpal, knuckle, .8f));
                Vector3 direction = knuckle - metacarpal;
                pointerFallback = direction.sqrMagnitude > .000001f
                    ? root.InverseTransformDirection(direction.normalized)
                    : indexDirection;
                pointerAttached = true;
                pointerSkeleton = input.Skeleton;
                pointerResting = resting;
            }

            Vector3 aim = input.AimTracked
                ? root.parent.TransformDirection(input.AimRotation * Vector3.forward)
                : root.TransformDirection(pointerFallback);
            return new Ray(root.TransformPoint(pointerAnchor), aim);
        }

        internal void ResetPointer() { pointerAttached = cachedPose = false; }

        private Ray PalmRay()
        {
            Vector3 wrist = bones[0].TransformPoint(rest[1]);
            Vector3 knuckles = (bones[6].position+bones[10].position+bones[14].position+bones[18].position)*.25f;
            Vector3 direction = knuckles-wrist;
            if (direction.sqrMagnitude < .000001f) direction = root.forward;
            return new Ray(knuckles, direction.normalized);
        }

        internal void UpdateEquipmentPose(VRHandInput input)
        {
            Ray pointing = PalmRay();
            Vector3 palmUp = bones[0].up;
            if (Vector3.Cross(pointing.direction, palmUp).sqrMagnitude < .0001f) palmUp = root.up;
            Vector3 index = bones[6].position, pinky = bones[18].position;
            Vector3 axis = index-pinky;
            if (axis.sqrMagnitude < .000001f || Vector3.Cross(axis, pointing.direction).sqrMagnitude < .000001f)
                axis = root.right*side;
            Vector3 middle = (bones[7].position+bones[11].position+bones[15].position+bones[19].position)*.25f;
            Vector3 fingerBend = middle-pointing.origin;
            Vector3 palmFace = Vector3.Dot(fingerBend, palmUp) > 0 ? palmUp : -palmUp;
            can.position = pointing.origin + pointing.direction*(.008f*scale) + palmFace*(.020f*scale);
            Vector3 aim = input.AimTracked
                ? root.parent.TransformDirection(input.AimRotation * Vector3.forward)
                : root.TransformDirection(indexDirection);
            Vector3 canUp = axis - Vector3.Dot(axis, aim) * aim;
            if (canUp.sqrMagnitude < .000001f) canUp = root.up;
            if (Vector3.Cross(canUp, aim).sqrMagnitude < .000001f) canUp = root.right;
            can.rotation = Quaternion.LookRotation(aim, canUp);
            shot.position = pointing.origin + pointing.direction*(.008f*scale) + palmFace*(.015f*scale);
            shot.rotation = Quaternion.LookRotation(pointing.direction, axis);
        }
        internal Ray SprayRay() { return new Ray(nozzle.position, nozzle.forward); }

        internal void Dispose()
        {
            Material material = skinRenderer.sharedMaterial;
            UnityEngine.Object.Destroy(material.mainTexture);
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(skinMesh);
        }
    }

    internal sealed class VREquipment
    {
        internal static Vector3 HolsterOffset(int hip)
        { return new Vector3((hip == 0) != VRModSettings.SwapHipItems ? -.25f : .25f, -.55f + VRModSettings.HolsterOffset, 0); }
        private readonly Transform rig;
        private readonly GameObject[] holsters = new GameObject[2];
        private readonly float scale;
        internal VREquipment(Transform origin, float scale)
        {
            this.scale = scale;
            rig = new GameObject("awesome_vr equipment").transform;
            rig.parent = origin;
            for (int hand = 0; hand < 2; ++hand)
            {
                Transform unusedNozzle;
                GameObject holster = (hand == 0 ? VREquipmentModels.Shot(rig, scale) : VREquipmentModels.Can(rig, scale, out unusedNozzle)).gameObject;
                holster.name = hand == 0 ? "Espresso at left hip" : "Graffiti can at right hip";
                holster.layer = 30;
                holster.transform.localPosition = HolsterOffset(hand) * scale;
                holsters[hand] = holster;
            }
        }
        internal void Update(Transform head, VRControls input)
        {
            rig.position = head.position;
            rig.localRotation = input.EquipmentHeading;
            for (int hand = 0; hand < 2; ++hand)
            {
                bool playing = input.Gameplay;
                bool holdingShot = input.Hands[0].ShotHeld || input.Hands[1].ShotHeld;
                bool holdingCan = input.Hands[0].CanHeld || input.Hands[1].CanHeld;
                holsters[hand].transform.localPosition = HolsterOffset(hand) * scale;
                holsters[hand].SetActive(playing && !VRModSettings.UseGamepad && !VRModSettings.ButtonShortcuts && (hand == 0 ? input.ShotAvailable && !holdingShot : PlayerData.SprayCanEnabled && !holdingCan));
            }
        }
    }
}
