using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using InControl;
using HarmonyLib;
using UnityEngine;

namespace AwesomeVr
{
    internal sealed class VRGameHooks
    {
        private readonly Harmony harmony = new Harmony("awesome_vr.controls");
        private Player sprayPlayer;
        private ParticleEmitter sprayParticles;
        private ParticleAnimator sprayAnimator;
        private Vector3 sprayWorldVelocity, sprayLocalVelocity, spraySpread, sprayForce, sprayRandomForce, emitterPosition;
        private Quaternion emitterRotation;
        private float sprayEmitterVelocityScale;
        private bool sprayWorldSpace, nozzleReady;
        private Ray nozzleRay;
        private readonly Dictionary<GameObject, int> emitterLayers = new Dictionary<GameObject, int>();
        private static readonly FieldInfo SprayRadius = typeof(PlayerProximity).GetField("graffitiRayRadius", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ParticleField = typeof(Player).GetField("graffitiParticleEmitter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo LastBounce = typeof(Player).GetField("lastBounceTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private delegate int GetJoystickId(UnityInputDevice device);
        private static readonly GetJoystickId JoystickId =
            (GetJoystickId)Delegate.CreateDelegate(typeof(GetJoystickId),
                AccessTools.PropertyGetter(typeof(UnityInputDevice), "JoystickId"));
        private static bool sprayContext;
        private static RaycastHit sprayHit;
        private static VRGameHooks current;

        internal VRGameHooks(Action<string> logger)
        {
            current = this;
            try
            {
                foreach (string name in new string[] { "ThumbsUp", "FlipOff", "Graffiti", "Parachute", "Jump", "Espresso", "Escape", "ClickDown", "StartToPickLevel", "Resetorientation", "Pause", "Restart" })
                    Patch(typeof(GameInput).GetMethod("get_" + name), null, "BoolInput");
                foreach (string name in new string[] { "Horizontal", "Vertical", "HorizontalSlowRepeat", "VerticalSlowRepeat" })
                    Patch(typeof(GameInput).GetMethod("get_" + name), null, "AxisInput");
                Patch(typeof(GameInput).GetMethod("get_MouseDeltaX"), null, "LookInput");
                Patch(typeof(GameInput).GetMethod("get_MouseDeltaY"), null, "LookInput");
                Patch(AccessTools.Method(typeof(UnityAnalogSource), "GetValue"), "CheckAnalogSlot", null);
                Patch(AccessTools.Method(typeof(UnityButtonSource), "GetState"), "CheckButtonSlot", null);
                harmony.Patch(AccessTools.Method(typeof(PlayerCamera), "Update"),
                    transpiler: new HarmonyMethod(typeof(VRGameHooks), "ScaleCameraShake"));
                harmony.Patch(AccessTools.Method(typeof(GameInput), "GatherPCInput"),
                    transpiler: new HarmonyMethod(typeof(VRGameHooks), "RouteMovementStick"));
                harmony.Patch(AccessTools.Method(typeof(Player), "FixedUpdate"),
                    transpiler: new HarmonyMethod(typeof(VRGameHooks), "RouteSteeringHeading"));
                Patch(AccessTools.Method(typeof(Player), "DeployGraffitiGesture"), "BeforeSpray", "AfterSpray");
                Patch(AccessTools.Method(typeof(Player), "EmitGraffitiParticles"), null, "AfterEmitGraffitiParticles");
                Patch(AccessTools.Method(typeof(Player), "TagGraffiti"), "TagAtController", null);
                Patch(AccessTools.Method(typeof(Player), "Bounce"), "BeforeBounce", "AfterBounce");
                Patch(AccessTools.Method(typeof(Player), "OnCollisionEnter"), "BeforeCollision", "AfterCollision");
                Patch(AccessTools.Method(typeof(HUD), "ShowHUDEvent"), null, "AfterHudEvent");
                Patch(AccessTools.Method(typeof(Plate), "OnTriggerEnter"), "BeforePlate", "AfterPlate");
                Patch(AccessTools.Method(typeof(GUIController), "Update"), "MenuMouseInput", null);
                Patch(AccessTools.Method(typeof(MenuAudio), "StartVO"), "StartTitleVoice", null);
                logger("VR game input hooks installed: menus, steering, crowd gestures, spray, parachute, jump and espresso");
            }
            catch { Dispose(); throw; }
        }

        private static bool MenuMouseInput() { return !VRMenu.InputActive; }

        private static bool StartTitleVoice(MenuAudio __instance)
        {
            if (GUIController.Instance.currentScreen != null || MainScreen.instance == null) return true;
            __instance.StartCoroutine(DeferredTitleVoice(__instance));
            return false;
        }

        private static IEnumerator DeferredTitleVoice(MenuAudio audio)
        {
            yield return null;
            if (audio != null && GUIController.Instance.currentScreen == MainScreen.instance && !audio.voPlaying)
                audio.StartVO();
        }

        private static void BeforeBounce(Player __instance, out float __state)
        { __state = LastBounce == null ? -1 : (float)LastBounce.GetValue(__instance); }

        private static void AfterBounce(Player __instance, float __state)
        {
            if (LastBounce == null || (float)LastBounce.GetValue(__instance) == __state) return;
            if (__instance == Player.singleton) Impact(.72f, .17f, 70);
            if (!Options.isVRSupportEnabled || PlayerCamera.singleton == null) return;
            MethodInfo spin = AccessTools.Method(typeof(PlayerCamera), "SpinCamera");
            if (spin != null) spin.Invoke(PlayerCamera.singleton, new object[] { 1f, 220f, .9f });
        }

        private static void BeforeCollision(Player __instance, out bool __state)
        { __state = __instance == Player.singleton && __instance.alive; }

        private static void AfterCollision(Player __instance, Collision __0, bool __state)
        {
            if (!__state || __instance.alive) return;
            foreach (ContactPoint contact in __0.contacts)
                if (Mathf.Abs(contact.normal.y) < .65f)
                {
                    Impact(.9f, .23f, 70);
                    break;
                }
        }

        private static void AfterHudEvent(HUDEventType __0)
        {
            if (__0 == HUDEventType.PowerLine) Impact(.88f, .34f, 170);
            else if (__0 == HUDEventType.StuntBird) Impact(.38f, .13f, 95);
        }

        private static int PlateCount()
        {
            GameController game = GameController.singleton;
            return game == null || game.levelScore == null ? -1 : game.levelScore.plateCount;
        }

        private static void BeforePlate(out int __state) { __state = PlateCount(); }

        private static void AfterPlate(int __state)
        {
            if (__state >= 0 && PlateCount() > __state) Impact(.27f, .08f, 155);
        }

        private static void Impact(float amplitude, float duration, float frequency)
        {
            VRControls input = VRControls.Current;
            if (input != null) input.ImpactHaptic(amplitude, duration, frequency);
        }

        private void Patch(MethodBase method, string prefix, string postfix)
        {
            if (method == null) throw new MissingMethodException("Required game control hook");
            harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(VRGameHooks), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(VRGameHooks), postfix));
        }

        private static void BoolInput(MethodBase __originalMethod, ref bool __result)
        {
            VRControls input = VRControls.Current;
            if (input == null) return;
            input.Sample();
            if (VRSettingsPanel.BlocksGameInput) { __result = false; return; }
            string name = __originalMethod.Name;
            bool gamepad = VRModSettings.UseGamepad;
            if (GUIController.Instance != null && GUIController.Instance.currentScreen is GamePauseScreen &&
                (name == "get_Escape" || name == "get_Pause" || name == "get_Restart"))
            {
                __result = gamepad && name == "get_Escape" && input.GamepadBackDown;
                return;
            }
            if (gamepad && !input.MenuMode) return;
            if (gamepad && name == "get_Escape") __result |= input.GamepadBackDown;
            if (name == "get_Escape") { __result |= input.BackDown || input.PauseDown; return; }
            if (name == "get_Resetorientation") { __result |= input.RecenterDown; return; }
            if (input.MenuMode)
            {
                GUIController gui = GUIController.Instance;
                GameScreen screen = gui == null ? null : gui.currentScreen;
                if (name == "get_ClickDown" && screen == null)
                    __result |= input.ConfirmDown || input.Hands[0].SelectDown || input.Hands[1].SelectDown;
                if (name == "get_StartToPickLevel" && screen is LevelSelectScreen) __result |= input.ConfirmDown;
                return;
            }
            if (!input.Gameplay) return;
            bool crowdInRange = PlayerProximity.singleton != null && PlayerProximity.singleton.spectatorsInRange.Count != 0;
            if (name == "get_ThumbsUp") __result = input.ThumbsUp && !input.FlipOff && crowdInRange;
            else if (name == "get_FlipOff") __result = input.FlipOff && crowdInRange;
            else if (name == "get_Graffiti") __result = input.SprayHand >= 0;
            else if (name == "get_Parachute") __result |= input.ParachuteRequested;
            else if (name == "get_Jump") __result |= input.ConfirmDown && Player.singleton.grounded;
            else if (name == "get_Espresso") __result |= input.EspressoRequest;
        }

        private static IEnumerable<CodeInstruction> RouteSteeringHeading(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            FieldInfo lateral = AccessTools.Field(typeof(Player), "lateralMovement");
            MethodInfo rotate = AccessTools.Method(typeof(Quaternion), "op_Multiply", new [] { typeof(Quaternion), typeof(Vector3) });
            MethodInfo steer = AccessTools.Method(typeof(VRGameHooks), "RotateSteering");
            int replaced = 0;
            for (int i = 1; i < code.Count; ++i)
            {
                if (code[i - 1].opcode != OpCodes.Ldfld || !Equals(code[i - 1].operand, lateral)) continue;
                if (code[i].opcode != OpCodes.Call || !Equals(code[i].operand, rotate)) continue;
                code[i].operand = steer;
                ++replaced;
            }
            if (replaced != 1)
                throw new InvalidOperationException("Unexpected steering force in Player.FixedUpdate.");
            return code;
        }

        private static Vector3 RotateSteering(Quaternion playerRotation, Vector3 movement)
        {
            Vector3 worldMovement = playerRotation * movement;
            VRControls input = VRControls.Current;
            if (input == null || !input.HasHeadsetHeading || !input.Gameplay) return worldMovement;
            float yaw = Mathf.DeltaAngle(playerRotation.eulerAngles.y, input.HeadsetYaw);
            return Quaternion.AngleAxis(yaw, Vector3.up) * worldMovement;
        }

        private static IEnumerable<CodeInstruction> ScaleCameraShake(IEnumerable<CodeInstruction> instructions)
        {
            bool shakeComponent = false;
            MethodInfo intensity = AccessTools.PropertyGetter(typeof(VRModSettings), "ShakeScale");
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (shakeComponent && instruction.opcode == OpCodes.Ldfld)
                {
                    yield return new CodeInstruction(OpCodes.Call, intensity);
                    yield return new CodeInstruction(OpCodes.Mul);
                }
                FieldInfo field = instruction.operand as FieldInfo;
                shakeComponent = instruction.opcode == OpCodes.Ldflda && field != null &&
                    field.DeclaringType == typeof(PlayerCamera) && (field.Name == "simpleCamShake" || field.Name == "smoothedCamShake");
            }
        }

        private static void LookInput(MethodBase __originalMethod, ref float __result)
        {
            VRControls input = VRControls.Current;
            if (input == null) return;
            input.Sample();
            if (!input.Gameplay) return;
            if (VRModSettings.UseGamepad || __originalMethod.Name == "get_MouseDeltaY") { __result = 0; return; }
            if (!input.Hands[1].Tracked) return;
            if (input.HorizontalLook) { __result = 0; return; }
            __result -= InputManager.ActiveDevice.RightStickX.Value;
        }

        private static void AxisInput(MethodBase __originalMethod, ref float __result)
        {
            if (VRSettingsPanel.BlocksGameInput) { __result = 0; return; }
            VRControls input = VRControls.Current;
            if (input == null) return;
            input.Sample();
            bool horizontal = __originalMethod.Name.IndexOf("Horizontal", StringComparison.Ordinal) >= 0;
            bool repeat = __originalMethod.Name.IndexOf("SlowRepeat", StringComparison.Ordinal) >= 0;
            if (!input.MenuMode) return;
            float value;
            if (repeat)
            {
                MenuDirection positive = horizontal ? MenuDirection.Right : MenuDirection.Up;
                MenuDirection negative = horizontal ? MenuDirection.Left : MenuDirection.Down;
                value = input.Navigation == positive ? 1 : input.Navigation == negative ? -1 : 0;
            }
            else
            {
                Vector2 stick = input.Hands[0].Stick;
                value = horizontal ? stick.x : stick.y;
            }
            if (Mathf.Abs(value) > Mathf.Abs(__result)) __result = value;
        }

        private static IEnumerable<CodeInstruction> RouteMovementStick(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo activeDevice = AccessTools.PropertyGetter(typeof(InputManager), "ActiveDevice");
            MethodInfo x = AccessTools.PropertyGetter(typeof(InputDevice), "LeftStickX");
            MethodInfo y = AccessTools.PropertyGetter(typeof(InputDevice), "LeftStickY");
            MethodInfo device = AccessTools.Method(typeof(VRGameHooks), "MovementDevice");
            MethodInfo gamepad = AccessTools.Method(typeof(VRGameHooks), "GamepadDevice");
            for (int i = 0; i + 1 < code.Count; ++i)
            {
                if (!Equals(code[i].operand, activeDevice)) continue;
                code[i].operand = Equals(code[i+1].operand, x) || Equals(code[i+1].operand, y) ? device : gamepad;
            }
            return code;
        }

        private static InputDevice MovementDevice()
        {
            VRControls input = VRControls.Current;
            if (!VRModSettings.UseGamepad && input != null && !input.MenuMode)
            {
                input.Sample();
                if (input.Focused && input.Hands[0].Tracked) return input.ReadSteering();
            }
            return GamepadDevice();
        }

        internal static InputDevice GamepadDevice()
        {
            InputDevice active = InputManager.ActiveDevice;
            if (VRControls.Current == null || !VRModSettings.UseGamepad || ValidJoystickSlot(active))
                return active;

            InputDevice gamepad = null;
            foreach (InputDevice device in InputManager.Devices)
            {
                if (ValidJoystickSlot(device) && (gamepad == null || device.LastChangeTick > gamepad.LastChangeTick))
                    gamepad = device;
            }
            return gamepad ?? active;
        }

        private static bool ValidJoystickSlot(InputDevice device)
        {
            UnityInputDevice joystick = device as UnityInputDevice;
            if (joystick == null) return false;
            int slot = JoystickId(joystick);
            return slot >= 1 && slot <= UnityInputDevice.MaxDevices;
        }

        private static bool CheckAnalogSlot(InputDevice __0, int ___analogId, ref float __result)
        {
            if (ValidJoystickSlot(__0) && ___analogId >= 0 && ___analogId < UnityInputDevice.MaxAnalogs) return true;
            __result = 0;
            return false;
        }

        private static bool CheckButtonSlot(InputDevice __0, int ___buttonId, ref bool __result)
        {
            if (ValidJoystickSlot(__0) && ___buttonId >= 0 && ___buttonId < UnityInputDevice.MaxButtons) return true;
            __result = false;
            return false;
        }

        private static void BeforeSpray(ref GameObject __state)
        {
            sprayContext = false;
            VRControls input = VRControls.Current;
            if (input == null || !input.Gameplay || input.SprayHand < 0 || PlayerProximity.singleton == null) return;
            PlayerProximity proximity = PlayerProximity.singleton;
            __state = proximity.targetedGraffitiBuilding;
            float radius = (float)SprayRadius.GetValue(proximity);
            if (current.gloves == null || current.gloves[input.SprayHand] == null) return;
            current.FollowSpray(current.gloves,input.SprayHand);
            Ray ray = current.gloves[input.SprayHand].SprayRay();
            bool hit = Physics.Raycast(ray, out sprayHit, radius, ~(1 << 8 | 1 << 30 | 1 << 31));
            GraffitiObstacle target = hit ? sprayHit.collider.GetComponent<GraffitiObstacle>() : null;
            proximity.targetedGraffitiBuilding = target != null && !target.beenTagged ? target.gameObject : null;
            sprayContext = true;
        }

        private static void AfterSpray(GameObject __state)
        {
            if (!sprayContext) return;
            if (PlayerProximity.singleton != null) PlayerProximity.singleton.targetedGraffitiBuilding = __state;
            sprayContext = false;
        }

        private static void AfterEmitGraffitiParticles(Player __instance, ref IEnumerator __result)
        {
            if (sprayContext && current.sprayParticles != null)
                __result = current.EmitAtNozzle(__instance, current.sprayParticles, __result);
        }

        private IEnumerator EmitAtNozzle(Player player, ParticleEmitter particles, IEnumerator original)
        {
            try
            {
                while (original.MoveNext())
                {
                    if (current == this && particles != null && player == Player.singleton)
                    {
                        particles.emit = nozzleReady;
                        particles.worldVelocity = Vector3.zero;
                    }
                    yield return original.Current;
                }
            }
            finally
            {
                IDisposable disposable = original as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        private static bool TagAtController(Player __instance)
        {
            if (!sprayContext) return true;
            if (sprayHit.collider == null || __instance.graffitiObj == null) return false;
            GameObject decal = UnityEngine.Object.Instantiate(__instance.graffitiObj, sprayHit.point,
                Quaternion.FromToRotation(Vector3.forward, -sprayHit.normal)) as GameObject;
            if (decal != null)
            {
                GraffitiDecal projector = decal.GetComponent<GraffitiDecal>();
                if (projector != null) projector.GenerateDecal(sprayHit.collider.gameObject);
            }
            VRControls input = VRControls.Current;
            if (input != null && input.SprayHand >= 0) input.Haptic(input.SprayHand, .5f);
            return false;
        }

        private VRGlove[] gloves;
        internal void FollowSpray(VRGlove[] hands, int sprayHand)
        {
            gloves = hands;
            nozzleReady = false;
            Player player = Player.singleton;
            if (sprayPlayer != player || VRModSettings.UseGamepad && sprayParticles != null) RestoreEmitter();

            VRControls input = VRControls.Current;
            if (input == null || !input.Gameplay || player == null) return;
            if (sprayHand < 0)
            {
                for (int hand = 0; hand < input.Hands.Length; ++hand)
                {
                    VRHandInput held = input.Hands[hand];
                    if (!held.Tracked || held.ShotHeld || !held.CanHeld) continue;
                    sprayHand = hand;
                    break;
                }
            }
            if (sprayHand < 0) return;
            nozzleRay = hands[sprayHand].SprayRay();
            nozzleReady = true;
            if (sprayParticles == null)
            {
                ParticleEmitter particles = ParticleField.GetValue(player) as ParticleEmitter;
                if (particles == null) return;

                sprayPlayer = player;
                sprayParticles = particles;
                sprayWorldVelocity = particles.worldVelocity;
                sprayLocalVelocity = particles.localVelocity;
                spraySpread = particles.rndVelocity;
                sprayEmitterVelocityScale = particles.emitterVelocityScale;
                sprayWorldSpace = particles.useWorldSpace;
                emitterPosition = particles.transform.localPosition;
                emitterRotation = particles.transform.localRotation;

                float speed = Mathf.Max(4f, sprayLocalVelocity.magnitude);
                particles.localVelocity = Vector3.forward * speed;
                particles.rndVelocity = new Vector3(.1f * speed, .1f * speed, 0);
                particles.emitterVelocityScale = 0;
                particles.useWorldSpace = true;
                sprayAnimator = particles.GetComponent<ParticleAnimator>();
                if (sprayAnimator != null)
                {
                    sprayForce = sprayAnimator.force;
                    sprayRandomForce = sprayAnimator.rndForce;
                    sprayAnimator.force = sprayAnimator.rndForce = Vector3.zero;
                }
                foreach (Renderer renderer in particles.GetComponentsInChildren<Renderer>(true))
                {
                    GameObject obj = renderer.gameObject;
                    if (emitterLayers.ContainsKey(obj)) continue;
                    emitterLayers.Add(obj, obj.layer);
                    obj.layer = 30;
                }
            }

            sprayParticles.transform.position = nozzleRay.origin;
            sprayParticles.transform.rotation = Quaternion.LookRotation(nozzleRay.direction);
            sprayParticles.worldVelocity = Vector3.zero;
        }

        private void RestoreEmitter()
        {
            if (sprayAnimator != null)
            {
                sprayAnimator.force = sprayForce;
                sprayAnimator.rndForce = sprayRandomForce;
            }
            if (sprayParticles != null)
            {
                sprayParticles.emit = false;
                sprayParticles.transform.localPosition = emitterPosition;
                sprayParticles.transform.localRotation = emitterRotation;
                sprayParticles.worldVelocity = sprayWorldVelocity;
                sprayParticles.localVelocity = sprayLocalVelocity;
                sprayParticles.rndVelocity = spraySpread;
                sprayParticles.emitterVelocityScale = sprayEmitterVelocityScale;
                sprayParticles.useWorldSpace = sprayWorldSpace;
            }
            foreach (KeyValuePair<GameObject, int> item in emitterLayers)
                if (item.Key != null) item.Key.layer = item.Value;
            emitterLayers.Clear();
            sprayAnimator = null;
            sprayParticles = null;
            sprayPlayer = null;
        }

        internal void Dispose()
        {
            harmony.UnpatchSelf();
            RestoreEmitter();
            sprayContext = false;
            if (current == this) current = null;
        }
    }
}
