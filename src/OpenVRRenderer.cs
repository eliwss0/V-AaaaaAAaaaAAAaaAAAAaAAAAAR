using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace AwesomeVr
{
    internal static class NativeVR
    {
        internal const int Wait = 0x415600, Left = 0x415601, Right = 0x415602, Stop = 0x415603;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal delegate int InitializeDelegate([MarshalAs(UnmanagedType.LPWStr)] string path, int singleThreadedGraphics);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void EyeInfoDelegate(int eye, [Out] float[] matrix, [Out] float[] projection, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int SetTexturesDelegate(IntPtr left, IntPtr right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int CopyPoseDelegate(int role, [Out] float[] matrix);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void StatusDelegate([Out] int[] values);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void RunEventDelegate(int eventID);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InputInitializeDelegate(IntPtr path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InputUpdateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InputReadDelegate(int role, [Out] float[] values, [Out] int[] flags, [Out] float[] matrix, [Out] float[] bones);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GripSkeletonDelegate(int role, [Out] float[] bones);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void HapticDelegate(int role, float amplitude, float duration, float frequency);
        internal static InitializeDelegate AVR_Initialize;
        internal static EyeInfoDelegate AVR_GetEyeInfo;
        internal static SetTexturesDelegate AVR_SetTextures;
        internal static CopyPoseDelegate AVR_CopyPose, AVR_ReadAimPose;
        internal static StatusDelegate AVR_GetStatus;
        internal static RunEventDelegate AVR_RunEvent;
        internal static InputInitializeDelegate AVR_InitializeInput;
        internal static InputUpdateDelegate AVR_UpdateInput, AVR_HasInputFocus, AVR_GetTextureMode;
        internal static InputReadDelegate AVR_ReadInput;
        internal static GripSkeletonDelegate AVR_ReadGripSkeleton;
        internal static HapticDelegate AVR_Haptic;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr library, string name);
        private static IntPtr module;
        internal static int InitializeInput(string path)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(path + "\0");
            IntPtr buffer = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, buffer, utf8.Length);
                return AVR_InitializeInput(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private static T Bind<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        internal static void Load(string path)
        {
            if (module != IntPtr.Zero) return;
            module = LoadLibraryW(path);
            if (module == IntPtr.Zero) throw new InvalidOperationException("Native bridge LoadLibrary error " + Marshal.GetLastWin32Error());
            AVR_Initialize = Bind<InitializeDelegate>("AVR_Initialize");
            AVR_GetEyeInfo = Bind<EyeInfoDelegate>("AVR_GetEyeInfo");
            AVR_SetTextures = Bind<SetTexturesDelegate>("AVR_SetTextures");
            AVR_CopyPose = Bind<CopyPoseDelegate>("AVR_CopyPose");
            AVR_GetStatus = Bind<StatusDelegate>("AVR_GetStatus");
            AVR_RunEvent = Bind<RunEventDelegate>("AVR_RunEvent");
            AVR_InitializeInput = Bind<InputInitializeDelegate>("AVR_InitializeInput");
            AVR_UpdateInput = Bind<InputUpdateDelegate>("AVR_UpdateInput");
            AVR_HasInputFocus = Bind<InputUpdateDelegate>("AVR_HasInputFocus");
            AVR_GetTextureMode = Bind<InputUpdateDelegate>("AVR_GetTextureMode");
            AVR_ReadInput = Bind<InputReadDelegate>("AVR_ReadInput");
            AVR_ReadAimPose = Bind<CopyPoseDelegate>("AVR_ReadAimPose");
            AVR_ReadGripSkeleton = Bind<GripSkeletonDelegate>("AVR_ReadGripSkeleton");
            AVR_Haptic = Bind<HapticDelegate>("AVR_Haptic");
        }
    }

    internal sealed class OpenVRRenderer : MonoBehaviour
    {
        private Camera source;
        private readonly Camera[] eyes = new Camera[2];
        private readonly Skybox[] eyeSkyboxes = new Skybox[2];
        private readonly RenderTexture[] textures = new RenderTexture[2];
        private readonly IntPtr[] textureHandles = new IntPtr[2];
        private readonly Vector3[] eyePositions = new Vector3[2];
        private readonly Quaternion[] eyeRotations = new Quaternion[2];
        private readonly float[][] frustums = new float[][] { new float[4], new float[4] };
        private readonly float[] pose = new float[12];
        private readonly int[] nativeStatus = new int[9];
        private Transform origin, head;
        private readonly Transform[] hands = new Transform[2];
        private Vector3 initialHead;
        private bool calibrated, running, initialized, stopping, trackingReady;
        private Action<string> log;
        private const float scale = 1f;
        private VRMenu menu;
        private bool menuMode, modeKnown;
        private Vector3 menuAnchor;
        private VRControls controls;
        private VRGameHooks hooks;
        private readonly VRGlove[] gloves = new VRGlove[2];
        private VREquipment equipment;
        private VRHeadAudio audioRig;
        private VRBodyPresence bodyPresence;
        private VRHeadOcclusion headOcclusion;
        private VRSpatialHUD spatialHUD;
        private readonly VRPhysicsHand[] physicsHands = new VRPhysicsHand[2];
        private readonly Vector3[][] handPoints = { new Vector3[6], new Vector3[6] };
        private int cameraScene = -1;
        private GameScreen cameraMenuScreen;
        private bool gameplayPoseValid, resultsPoseValid;
        private Vector3 gameplayOrigin, resultsOrigin, resultsCameraStart;
        private Quaternion gameplayRotation, resultsRotation, resultsCameraStartRotation;
        private static readonly FieldInfo CameraLook = typeof(PlayerCamera).GetField("curRot", BindingFlags.Instance | BindingFlags.NonPublic);
        private Vector3 previousGameLook;
        private float lookPitch, lookYaw;
        private Vector2 gamepadShake;
        private const float GamepadShakeReturnTime = .15f;
        private bool previousGameLookValid;
        private string waitingFor;
        private float submissionFailureSince = -1;

        internal void Begin(string gameRoot, Action<string> logger)
        {
            log = logger;
            if (!SystemInfo.graphicsDeviceVersion.StartsWith("Direct3D 11"))
                throw new InvalidOperationException("VR requires Direct3D 11. On Linux and Steam Frame, run the Windows game through Proton with DXVK enabled.");
            NativeVR.Load(Path.Combine(gameRoot, "Awesome_Data/Plugins/awesome_vr_native.dll"));
            bool directGraphics = Array.IndexOf(Environment.GetCommandLineArgs(), "-force-gfx-direct") >= 0 ||
                Environment.GetEnvironmentVariable("AWESOME_VR_GFX_DIRECT") == "1";
            VRControls.ValidateGameBindings();
            int error = NativeVR.AVR_Initialize(Path.Combine(gameRoot, "awesome_vr/runtime/openvr_api.dll"), directGraphics ? 1 : 0);
            if (error != 0) throw new InvalidOperationException("OpenVR initialization error " + error + ". Start through Steam with SteamVR available. On Linux and Steam Frame, use a current Proton release supporting 32-bit OpenVR.");
            initialized = true;
            origin = new GameObject("awesome_vr Origin").transform;
            origin.parent = transform;
            head = new GameObject("awesome_vr Head").transform;
            head.parent = origin;
            audioRig = new VRHeadAudio(head);
            audioRig.Update();
            headOcclusion = new VRHeadOcclusion(head, scale);
            spatialHUD = new VRSpatialHUD(origin, scale, log);
            uint width = 0, height = 0;
            for (int i = 0; i < 2; ++i)
            {
                float[] matrix = new float[12];
                NativeVR.AVR_GetEyeInfo(i, matrix, frustums[i], out width, out height);
                DecodePose(matrix, out eyePositions[i], out eyeRotations[i]);
                int w = (int)width;
                int h = (int)height;
                if (w <= 0 || h <= 0) throw new InvalidOperationException("SteamVR returned an invalid recommended eye resolution.");
                textures[i] = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                textures[i].antiAliasing = 1;
                textures[i].filterMode = FilterMode.Bilinear;
                textures[i].Create();
                GameObject eye = new GameObject(i == 0 ? "awesome_vr Left Eye" : "awesome_vr Right Eye");
                eye.transform.parent = head;
                eye.transform.localPosition = eyePositions[i] * scale;
                eye.transform.localRotation = eyeRotations[i];
                eyes[i] = eye.AddComponent<Camera>();
                eyeSkyboxes[i] = eye.AddComponent<Skybox>();
                eyeSkyboxes[i].enabled = false;
                eyes[i].enabled = false;
            }
            float eyeSeparation = Vector3.Distance(eyePositions[0], eyePositions[1]);
            if (eyeSeparation < .01f)
                throw new InvalidOperationException("SteamVR returned coincident eye transforms; stereo world rendering cannot start.");
            log("SteamVR eye separation=" + eyeSeparation + "m; independent tracked perspective cameras ready");
            RefreshTextures();
            for (int i = 0; i < 2; ++i)
            {
                GameObject hand = new GameObject();
                hand.name = i == 0 ? "awesome_vr Left Controller" : "awesome_vr Right Controller";
                hand.transform.parent = origin;
                hand.layer = 30;
                hands[i] = hand.transform;
                gloves[i] = new VRGlove(hand.transform, i, scale);
                physicsHands[i] = new VRPhysicsHand(transform, i, scale);
                hand.SetActive(false);
            }
            physicsHands[0].IgnorePlayerAndHand(physicsHands[1]);
            physicsHands[1].IgnorePlayerAndHand(physicsHands[0]);
            menu = new VRMenu(origin, scale, log);
            controls = new VRControls(gameRoot, log);
            bodyPresence = new VRBodyPresence(scale);
            equipment = new VREquipment(origin, scale);
            hooks = new VRGameHooks(log);
            running = true;
            log("OpenVR initialized; texture path=" + (NativeVR.AVR_GetTextureMode() != 0 ? "Proton same-device snapshot" : "DXGI 1.1 device transfer") + "; directGraphics=" + directGraphics + "; eye textures=" + textures[0].width + "x" + textures[0].height + " worldScale=" + scale);
            StartCoroutine(RenderFrames());
        }

        private Camera FindGameCamera()
        {
            GUIController gui = GUIController.Instance;
            GameScreen current = menuMode && gui != null ? gui.currentScreen : null;
            if (current != null && current.cameras != null)
            {
                Camera first = null;
                foreach (Camera candidate in current.cameras)
                {
                    if (candidate == null || !candidate.enabled || candidate.targetTexture != null) continue;
                    if (first == null || (first.orthographic && !candidate.orthographic) ||
                        (first.orthographic == candidate.orthographic && candidate.depth < first.depth)) first = candidate;
                }
                if (first != null) return first;
            }
            GameObject main = GameObject.Find("mainCam");
            if (main != null && main.camera != null) return main.camera;
            Camera selected = null;
            int bestScore = Int32.MinValue;
            foreach (Camera candidate in Camera.allCameras)
            {
                if (candidate == eyes[0] || candidate == eyes[1] || candidate.targetTexture != null) continue;
                if (!candidate.enabled) continue;
                int score = 0;
                if (candidate.name.IndexOf("replay", StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
                if (candidate.name.IndexOf("hud", StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;
                if (candidate.orthographic) score -= 20;
                if (candidate == Camera.main) score += 5;
                if (score > bestScore) { bestScore = score; selected = candidate; }
            }
            return selected;
        }

        private void AcquireCamera()
        {
            GUIController gui = GUIController.Instance;
            GameScreen current = menuMode && gui != null ? gui.currentScreen : null;
            if (current != cameraMenuScreen)
            {
                menu.Exit(); source = null; calibrated = false;
                cameraMenuScreen = current;
            }
            if (source != null && source.enabled) return;
            source = FindGameCamera();
            if (source == null) return;
            if (cameraScene != Application.loadedLevel)
            {
                lookYaw = source.transform.eulerAngles.y;
                origin.rotation = Quaternion.Euler(0, lookYaw, 0);
                cameraScene = Application.loadedLevel;
                gameplayPoseValid = resultsPoseValid = false;
            }
            calibrated = false;
            log("VR camera acquired: " + source.name + " scene=" + Application.loadedLevelName);
        }

        private IEnumerator RenderFrames()
        {
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            while (running)
            {
                yield return endOfFrame;
                if (!running) break;
                try { RenderFrame(); }
                catch (Exception error) { log("VR rendering stopped: " + error); End(); }
            }
        }

        private void FixedUpdate()
        {
            if (!running) return;
            initialHead += bodyPresence.FixedStep();
            foreach (VRPhysicsHand hand in physicsHands) if (hand != null) hand.FixedStep();
        }

        private void RenderFrame()
        {
            if (cameraScene >= 0 && cameraScene != Application.loadedLevel)
            {
                menu.Exit();
                source = null;
                calibrated = false;
            }
            controls.Sample();
            bool wantsMenu = controls.MenuMode;
            if (!modeKnown || wantsMenu != menuMode)
            {
                source = null;
                menu.Exit();
                menuMode = wantsMenu;
                modeKnown = true;
                calibrated = false;
                log("VR presentation: " + (menuMode ? "spatial menu in stereo scene" : "stereo gameplay"));
            }
            if (controls.RecenterDown)
            {
                calibrated = false;
                menu.Exit();
            }
            AcquireCamera();
            audioRig.Update();
            NativeVR.AVR_RunEvent(NativeVR.Wait);
            textures[0].GetNativeTexturePtr();
            NativeVR.AVR_GetStatus(nativeStatus);
            if (NativeVR.AVR_CopyPose(0, pose) == 0)
            {
                controls.HasHeadsetHeading = false;
                bodyPresence.Suspend();
                foreach (VRPhysicsHand hand in physicsHands) hand.Suspend();
                trackingReady = false;
                WaitForPresentation("headset tracking");
                return;
            }
            if (source == null)
            {
                controls.HasHeadsetHeading = false;
                bodyPresence.Suspend();
                WaitForPresentation("game camera");
                return;
            }
            if (!trackingReady)
            {
                trackingReady = true;
                calibrated = false;
                menu.Exit();
                log("Headset tracking acquired; menu placement reset");
            }
            Vector3 position;
            Quaternion rotation;
            DecodePose(pose, out position, out rotation);
            if (controls.RecenterDown) lookYaw = source.transform.eulerAngles.y - rotation.eulerAngles.y;
            if (!calibrated)
            {
                initialHead = position;
                menuAnchor = source.transform.position;
                calibrated = true;
                previousGameLookValid = false;
                lookPitch = 0;
                gamepadShake = Vector2.zero;
                origin.rotation = Quaternion.Euler(0, lookYaw, 0);
            }
            float yaw = controls.ReadSnapTurn();
            float pitch = 0;
            PlayerCamera gameCamera = PlayerCamera.singleton;
            bool gamepad = VRModSettings.UseGamepad;
            if (!menuMode && gameCamera != null)
            {
                Vector3 look = CameraLook != null
                    ? (Vector3)CameraLook.GetValue(gameCamera)
                    : gameCamera.transform.localEulerAngles;
                if (previousGameLookValid)
                {
                    float gameYaw = Mathf.DeltaAngle(previousGameLook.y, look.y);
                    float gamePitch = Mathf.DeltaAngle(previousGameLook.x, look.x);
                    if (gamepad)
                        gamepadShake = gamepadShake * Mathf.Exp(-Time.unscaledDeltaTime / GamepadShakeReturnTime)
                            + new Vector2(gamePitch, gameYaw);
                    else
                    {
                        yaw += gameYaw;
                        pitch = gamePitch;
                    }
                }
                previousGameLook = look;
                previousGameLookValid = true;
            }
            if (!gamepad || menuMode || gameCamera == null) gamepadShake = Vector2.zero;
            if (yaw != 0 || pitch != 0)
            {
                Quaternion heading = Quaternion.Euler(0, lookYaw, 0);
                Vector3 offset = heading * (position - initialHead);
                lookPitch += pitch;
                lookYaw = Mathf.Repeat(lookYaw + yaw, 360);
                if (yaw != 0) initialHead = position - Quaternion.Euler(0, -lookYaw, 0) * offset;
            }
            origin.rotation = Quaternion.Euler(lookPitch + gamepadShake.x, lookYaw + gamepadShake.y, 0);
            Vector3 roomOffset = Quaternion.Euler(0, lookYaw, 0) * ((position - initialHead) * scale);
            origin.position = (menuMode ? menuAnchor : source.transform.position) + roomOffset - origin.rotation * (position * scale);
            head.localPosition = position * scale;
            head.localRotation = rotation;
            Vector3 forward = head.forward;
            if (forward.x * forward.x + forward.z * forward.z > .0001f)
                controls.HeadsetYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            else if (!controls.HasHeadsetHeading)
            {
                Vector3 right = head.right;
                controls.HeadsetYaw = Mathf.Atan2(-right.z, right.x) * Mathf.Rad2Deg;
            }
            controls.HasHeadsetHeading = !menuMode;
            bodyPresence.Target(position - initialHead, Quaternion.Euler(0, lookYaw, 0), controls.Gameplay && !VRModSettings.UseGamepad);
            headOcclusion.Update(bodyPresence.Obstructed(head.position));
            for (int i = 0; i < 2; ++i)
            {
                VRHandInput input = controls.Hands[i];
                bool valid = input.Tracked;
                hands[i].gameObject.SetActive(valid);
                if (valid)
                {
                    hands[i].localPosition = input.Position * scale;
                    hands[i].localRotation = input.Rotation;
                    gloves[i].Update(input);
                    gloves[i].CollisionPoints(handPoints[i]);
                    float contact = physicsHands[i].Resolve(hands[i], head.position, handPoints[i], controls, i, controls.Gameplay);
                    gloves[i].ApplyContactFraction(contact);
                    gloves[i].CollisionPoints(handPoints[i]);
                    physicsHands[i].CommitContacts(handPoints[i]);
                    gloves[i].UpdateEquipmentPose(input);
                }
                else
                {
                    physicsHands[i].Suspend();
                    gloves[i].ResetPointer();
                }
            }
            spatialHUD.Update(head, !menuMode && Utilities.IsGameLevel);
            hooks.FollowSpray(gloves, controls.SprayHand);
            equipment.Update(head, controls);
            MenuCameraView resultsView = null;
            if (menuMode)
            {
                menu.Enter(head);
                menu.Sync();
                menu.ApplyLayout();
                menu.Point(head, gloves, controls);
                if (source == null || !source.enabled) { menu.RestoreLayout(); WaitForPresentation("menu camera transition"); return; }
                if (cameraMenuScreen is GameOverScreen)
                    foreach (MenuCameraView view in menu.Views)
                        if (view.Background && view.Camera != null) { resultsView = view; break; }
            }
            if (resultsView != null) UpdateResultsPose(resultsView);
            else resultsPoseValid = false;
            for (int eye = 0; eye < 2; ++eye)
            {
                Camera camera = eyes[eye];
                Vector3 eyePosition = head.TransformPoint(eyePositions[eye] * scale);
                Quaternion eyeRotation = head.rotation * eyeRotations[eye];
                if (menuMode && menu.Views.Count > 0)
                {
                    bool first = true;
                    foreach (MenuCameraView view in menu.Views)
                    {
                        if (view.Camera == null || (!view.Camera.enabled && !view.Background)) continue;
                        Vector3 viewPosition;
                        Quaternion viewRotation;
                        if (view == resultsView)
                        {
                            viewPosition = resultsOrigin + resultsRotation * origin.InverseTransformPoint(eyePosition);
                            viewRotation = resultsRotation * Quaternion.Inverse(origin.rotation) * eyeRotation;
                        }
                        else
                        {
                            viewPosition = view.ToSourcePoint(eyePosition);
                            viewRotation = view.ToSourceRotation(eyeRotation);
                        }
                        PrepareEye(camera, view.Camera, eye, viewPosition, viewRotation, scale * view.Units, first);
                        camera.cullingMask &= ~((1 << 30) | (1 << 31));
                        camera.Render();
                        first = false;
                    }
                }
                else
                {
                    PrepareEye(camera, source, eye, eyePosition, eyeRotation, scale, true);
                    camera.cullingMask &= ~((1 << 30) | (1 << 31));
                    camera.Render();
                }
                PrepareEye(camera, source, eye, eyePosition, eyeRotation, scale, false);
                camera.cullingMask = 1 << 30;
                if (!menuMode) camera.clearFlags = CameraClearFlags.Nothing;
                camera.Render();
            }
            if (menuMode) menu.RestoreLayout();
            RefreshTextures();
            NativeVR.AVR_RunEvent(NativeVR.Left);
            NativeVR.AVR_RunEvent(NativeVR.Right);
            waitingFor = null;
            NativeVR.AVR_GetStatus(nativeStatus);
            bool fatal = false;
            for (int eye = 0; eye < 2; ++eye)
                fatal |= nativeStatus[4+eye] != 0 && nativeStatus[4+eye] != 1 && nativeStatus[4+eye] != 101;
            bool focusLost = nativeStatus[4] == 101 || nativeStatus[5] == 101;
            bool requestFailed = !focusLost && (nativeStatus[4] == 1 || nativeStatus[5] == 1);
            if (!requestFailed) submissionFailureSince = -1;
            else if (submissionFailureSince < 0) submissionFailureSince = Time.realtimeSinceStartup;
            else if (Time.realtimeSinceStartup-submissionFailureSince > 5) fatal = true;
            if (fatal || nativeStatus[8] < 0)
                throw new InvalidOperationException("Eye transfer/submission failed: left=" + CompositorError(nativeStatus[4]) + " right=" + CompositorError(nativeStatus[5]) + " copy=0x" + nativeStatus[8].ToString("X8"));
            bool accepted = nativeStatus[4] == 0 && nativeStatus[5] == 0;
            if (accepted && !menuMode && Player.singleton != null && Player.singleton.alive)
            {
                gameplayOrigin = origin.position;
                gameplayRotation = origin.rotation;
                gameplayPoseValid = true;
            }

        }

        private void UpdateResultsPose(MenuCameraView view)
        {
            Transform camera = view.Camera.transform;
            if (!resultsPoseValid)
            {
                resultsOrigin = gameplayPoseValid ? gameplayOrigin : view.ToSourcePoint(origin.position);
                resultsRotation = gameplayPoseValid ? gameplayRotation : view.ToSourceRotation(origin.rotation);
                if (!gameplayPoseValid)
                {
                    gameplayOrigin = resultsOrigin;
                    gameplayRotation = resultsRotation;
                }
                resultsCameraStart = camera.position;
                resultsCameraStartRotation = camera.rotation;
                resultsPoseValid = true;
                return;
            }
            Quaternion cameraDelta = camera.rotation * Quaternion.Inverse(resultsCameraStartRotation);
            Vector3 startHeadOffset = gameplayRotation * initialHead * scale;
            resultsRotation = cameraDelta * gameplayRotation;
            resultsOrigin = gameplayOrigin + startHeadOffset +
                (camera.position - resultsCameraStart) / view.Units - cameraDelta * startHeadOffset;
        }

        private static string CompositorError(int code)
        {
            switch (code)
            {
                case 0: return "accepted";
                case 1: return "request failed (1)";
                case 100: return "incompatible runtime interface (100)";
                case 101: return "dashboard or another application has focus (101)";
                case 102: return "invalid texture (102)";
                case 103: return "application is not a VR scene (103)";
                case 104: return "texture and compositor use different GPUs (104)";
                case 105: return "unsupported texture format (105)";
                case 106: return "shared textures unsupported (106)";
                case 108: return "eye submitted twice (108)";
                case 109: return "invalid texture bounds (109)";
                default: return code.ToString();
            }
        }

        private void RefreshTextures()
        {
            IntPtr left = textures[0].GetNativeTexturePtr(), right = textures[1].GetNativeTexturePtr();
            if (left == IntPtr.Zero || right == IntPtr.Zero)
                throw new InvalidOperationException("Unity eye render textures are unavailable.");
            if (textureHandles[0] == left && textureHandles[1] == right) return;
            int result = NativeVR.AVR_SetTextures(left, right);
            if (result != 0) throw new InvalidOperationException("Unable to bind current eye textures: 0x" + result.ToString("X8"));
            textureHandles[0] = left; textureHandles[1] = right;
            log("Eye render targets bound: scene=" + Application.loadedLevelName + " left=0x" + left.ToString("X") + " right=0x" + right.ToString("X"));
        }

        private void WaitForPresentation(string reason)
        {
            submissionFailureSince = -1;
            if (waitingFor == reason) return;
            waitingFor = reason;
            log("VR waiting for " + reason + "; scene=" + Application.loadedLevelName + " poses=" + nativeStatus[2] + " wait=" + nativeStatus[3]);
        }

        private void PrepareEye(Camera camera, Camera original, int eye, Vector3 position,
            Quaternion rotation, float units, bool first)
        {
            camera.CopyFrom(original); camera.enabled = false;
            camera.targetTexture = textures[eye]; camera.rect = new Rect(0, 0, 1, 1);
            camera.transform.position = position; camera.transform.rotation = rotation;
            camera.ResetWorldToCameraMatrix();
            Skybox skybox = original.GetComponent<Skybox>();
            eyeSkyboxes[eye].enabled = skybox != null && skybox.enabled;
            if (skybox != null) eyeSkyboxes[eye].material = skybox.material;
            camera.orthographic = false;
            if (!first) camera.clearFlags = CameraClearFlags.Depth;
            else if (camera.clearFlags == CameraClearFlags.Depth || camera.clearFlags == CameraClearFlags.Nothing)
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.025f, .025f, .04f, 1);
            }
            camera.nearClipPlane = .03f * units;
            camera.farClipPlane = Mathf.Max(original.farClipPlane, 1000 * units);
            camera.projectionMatrix = Projection(frustums[eye], camera.nearClipPlane, camera.farClipPlane);
        }

        internal static void DecodePose(float[] matrix, out Vector3 position, out Quaternion rotation)
        {
            position = new Vector3(matrix[3], matrix[7], -matrix[11]);
            rotation = DecodeRotation(matrix);
        }

        internal static Quaternion DecodeRotation(float[] matrix)
        {
            Vector3 forward = new Vector3(-matrix[2], -matrix[6], matrix[10]);
            Vector3 up = new Vector3(matrix[1], matrix[5], -matrix[9]);
            return Quaternion.LookRotation(forward, up);
        }

        private static Matrix4x4 Projection(float[] raw, float near, float far)
        {
            float left = raw[0] * near, right = raw[1] * near;
            float top = raw[3] * near, bottom = raw[2] * near;
            Matrix4x4 result = new Matrix4x4();
            result[0, 0] = 2 * near / (right - left);
            result[1, 1] = 2 * near / (top - bottom);
            result[0, 2] = (right + left) / (right - left);
            result[1, 2] = (top + bottom) / (top - bottom);
            result[2, 2] = -(far + near) / (far - near);
            result[2, 3] = -2 * far * near / (far - near);
            result[3, 2] = -1;
            return result;
        }

        internal void End()
        {
            if (stopping) return;
            stopping = true;
            running = false;
            if (hooks != null) hooks.Dispose();
            if (controls != null) controls.Dispose();
            if (menu != null) menu.Dispose();
            if (spatialHUD != null) spatialHUD.Dispose();
            if (audioRig != null) audioRig.Dispose();
            if (headOcclusion != null) headOcclusion.Dispose();
            foreach (VRPhysicsHand hand in physicsHands) if (hand != null) hand.Dispose();
            foreach (VRGlove glove in gloves) if (glove != null) glove.Dispose();
            VREquipmentModels.Release();
            if (initialized)
            {
                NativeVR.AVR_RunEvent(NativeVR.Stop);
                if (textures[0] != null) textures[0].GetNativeTexturePtr();
                NativeVR.AVR_GetStatus(nativeStatus);
                if (nativeStatus[0] == 0)
                    foreach (RenderTexture texture in textures)
                    {
                        if (texture == null) continue;
                        texture.Release();
                        Destroy(texture);
                    }
            }
            if (origin != null) Destroy(origin.gameObject);
        }

        private void OnApplicationQuit() { End(); }
        private void OnDestroy() { End(); }
        private void OnGUI()
        {
            if (!running || textures[0] == null) return;
            GUI.depth = -1000;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), textures[0], ScaleMode.ScaleAndCrop, false);
        }
    }
}
