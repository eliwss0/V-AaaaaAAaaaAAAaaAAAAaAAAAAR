using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using InControl;
using UnityEngine;

namespace AwesomeVr
{
    internal enum HandAction
    {
        Select, Grab, ThumbsUp, FlipOff, Jump, Parachute, Espresso, Back, Pause, Recenter, Spray
    }

    internal sealed class VRHandInput
    {
        internal readonly float[] Values = new float[9], Pose = new float[12], AimPose = new float[12], Bones = new float[93], GripBones = new float[93], Curls = new float[5];
        internal readonly int[] Flags = new int[4];
        internal bool Tracked, Skeleton, GripSkeleton, Grip, GripDown, Select, SelectDown, CanHeld, ShotHeld;
        internal uint Buttons, Down;
        internal bool ReleaseRequired = true;
        internal CrowdGesture Gesture, ButtonGesture;
        internal bool AimTracked, HasFingerTracking;
        internal bool InParachuteReach, PhysicalGrip;
        internal Vector3 Position;
        internal Quaternion Rotation, AimRotation;
        internal readonly GestureLatch GestureFilter = new GestureLatch();
        internal readonly PullLatch Pull = new PullLatch();
        internal bool IsDown(HandAction action) { return (Down & Mask(action)) != 0; }
        internal bool IsHeld(HandAction action) { return (Buttons & Mask(action)) != 0; }
        internal static uint Mask(HandAction action) { return 1U << (int)action; }
        internal Vector2 Stick { get { return new Vector2(Values[2], Values[3]); } }
    }

    internal sealed class VRControls
    {
        internal static VRControls Current;
        internal readonly VRHandInput[] Hands = { new VRHandInput(), new VRHandInput() };
        internal bool MenuMode
        {
            get
            {
                if (!Utilities.IsGameLevel || GameController.singleton == null || GameController.singleton.state != GameState.Play) return true;
                GUIController gui = GUIController.Instance;
                return gui != null && gui.currentScreen != null;
            }
        }
        private static readonly FieldInfo StartupInputBlock = typeof(GameInput).GetField("disabledInputAtStartOfLevel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo EspressoUsed = typeof(Espresso).GetField("espressoUsed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        internal bool Gameplay { get { return Focused && !MenuMode && Player.singleton != null && Player.singleton.alive && GameInput.singleton != null && GameInput.singleton.inputEnabled && !(bool)StartupInputBlock.GetValue(GameInput.singleton); } }
        internal static void ValidateGameBindings()
        {
            if (StartupInputBlock == null || StartupInputBlock.FieldType != typeof(bool))
                throw new InvalidOperationException("This game build does not expose the expected GameInput.disabledInputAtStartOfLevel field.");
        }
        internal bool ShotAvailable { get { return PlayerData.CaffeineEnabled && Espresso.singleton != null && EspressoUsed != null && !(bool)EspressoUsed.GetValue(Espresso.singleton); } }
        internal bool Focused;
        internal bool ParachuteDown, ConfirmDown, BackDown, PauseDown, RecenterDown, EspressoRequest;
        private float parachutePullUntil = -1;
        internal bool ParachuteRequested { get { return ParachuteDown || Time.realtimeSinceStartup < parachutePullUntil; } }
        internal MenuDirection Navigation;
        internal bool GamepadSelect, GamepadSelectDown, GamepadBackDown;
        internal bool HasHeadsetHeading;
        internal float HeadsetYaw;
        private bool horizontalLook;
        private int snapDirection;
        private float nextSnap;
        internal bool HorizontalLook { get { return horizontalLook; } }
        internal int SprayHand = -1;
        internal bool CalibratingParachute { get; private set; }
        private bool calibrationReady;
        internal void BeginParachuteCalibration()
        {
            CalibratingParachute = true;
            calibrationReady = false;
        }
        internal void CancelParachuteCalibration()
        {
            CalibratingParachute = false;
            calibrationReady = false;
        }
        internal Quaternion EquipmentHeading = Quaternion.identity;
        private bool equipmentHeadingReady, equipmentRecenter;
        private readonly UnityInputDevice steeringDevice;
        private int steeringFrame = -1;
        private ulong steeringTick;
        private float steeringTime;
        private int frame = -1;
        private readonly float[] headPose = new float[12], rawHandPose = new float[12];
        private readonly MenuRepeat repeat = new MenuRepeat();
        private readonly Action<string> log;
        private readonly bool actionsReady;
        private readonly int[] lastSkeleton = { -1, -1 };

        internal VRControls(string gameRoot, Action<string> logger)
        {
            log = logger;
            steeringDevice = new UnityInputDevice(new VRStickProfile(Hands[0]));
            steeringTime = Time.realtimeSinceStartup;
            int error = NativeVR.InitializeInput(Path.Combine(gameRoot, "awesome_vr/input/actions.json"));
            actionsReady = error == 0;
            log("SteamVR actions initialization=" + error + "; Frame/Touch/Index/Vive/WMR bindings available");
            Current = this;
        }

        internal void Sample()
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            bool focused = NativeVR.AVR_HasInputFocus() != 0;
            Focused = focused;
            bool motionGameplay = !VRModSettings.UseGamepad;
            bool menu = MenuMode;
            bool motionInput = focused && (motionGameplay || menu);
            int updateError = motionInput && actionsReady ? NativeVR.AVR_UpdateInput() : -1;
            InputDevice gamepad = VRGameHooks.GamepadDevice();
            GamepadSelect = focused && gamepad != null && gamepad.Action1.IsPressed;
            GamepadSelectDown = focused && gamepad != null && gamepad.Action1.WasPressed;
            GamepadBackDown = focused && gamepad != null && gamepad.Action2.WasPressed;
            ParachuteDown = ConfirmDown = BackDown = PauseDown = RecenterDown = EspressoRequest = false;
            SprayHand = -1;
            Vector3 headPosition = Vector3.zero;
            Quaternion headRotation = Quaternion.identity;
            bool headValid = NativeVR.AVR_CopyPose(0, headPose) != 0;
            if (headValid) OpenVRRenderer.DecodePose(headPose, out headPosition, out headRotation);
            if (motionInput && headValid && Gameplay)
            {
                Vector3 forward = headRotation * Vector3.forward;
                forward.y = 0;
                if (forward.sqrMagnitude > .01f)
                {
                    float headYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                    if (!equipmentHeadingReady || equipmentRecenter)
                    {
                        EquipmentHeading = Quaternion.Euler(0, headYaw, 0);
                        equipmentHeadingReady = true;
                        equipmentRecenter = false;
                    }
                    else
                    {
                        float difference = Mathf.DeltaAngle(EquipmentHeading.eulerAngles.y, headYaw);
                        const float headTurnAllowance = 35f;
                        if (Mathf.Abs(difference) > headTurnAllowance)
                        {
                            float targetYaw = headYaw - Mathf.Sign(difference) * headTurnAllowance;
                            float yaw = Mathf.MoveTowardsAngle(EquipmentHeading.eulerAngles.y, targetYaw, 90f * Time.unscaledDeltaTime);
                            EquipmentHeading = Quaternion.Euler(0, yaw, 0);
                        }
                    }
                }
            }
            Quaternion trackingToHead = Quaternion.Inverse(headRotation);
            bool playing = motionGameplay && Gameplay && headValid;
            bool shortcuts = VRModSettings.ButtonShortcuts;
            bool canPull = playing && !Player.singleton.grounded && Parachute.singleton != null && !Parachute.singleton.deployed;
            if (!canPull || shortcuts) parachutePullUntil = -1;
            float now = Time.realtimeSinceStartup;
            Vector3 mouth = headPosition + headRotation * new Vector3(0, -.10f, .08f);
            const float drinkRadius = .085f;
            for (int i = 0; i < 2; ++i)
            {
                VRHandInput hand = Hands[i];
                uint old = hand.Buttons;
                bool oldGrip = hand.Grip, oldSelect = hand.Select;
                Array.Clear(hand.Flags, 0, hand.Flags.Length);
                Array.Clear(hand.Values, 0, hand.Values.Length);
                bool modern = motionInput && updateError == 0 && NativeVR.AVR_ReadInput(i+1, hand.Values, hand.Flags, hand.Pose, hand.Bones) != 0;
                hand.Tracked = motionInput && (hand.Flags[1] != 0 || NativeVR.AVR_CopyPose(i+1, hand.Pose) != 0);
                hand.Buttons = modern ? (uint)hand.Flags[0] : 0;
                hand.PhysicalGrip = hand.IsHeld(HandAction.Grab) || hand.Values[1] > (hand.PhysicalGrip ? .25f : .42f);
                if (!hand.Tracked) { hand.Buttons = 0; Array.Clear(hand.Values, 0, hand.Values.Length); }
                if (!hand.Tracked) hand.ReleaseRequired = true;
                if (hand.ReleaseRequired)
                {
                    bool released = hand.Buttons == 0 && hand.Values[0] < .35f && hand.Values[1] < .35f;
                    hand.Buttons = 0; hand.Values[0] = hand.Values[1] = 0;
                    if (hand.Tracked && released) hand.ReleaseRequired = false;
                }
                hand.Down = hand.Buttons & ~old;
                hand.ButtonGesture = CrowdGesture.None;
                hand.Grip = hand.Tracked && (hand.IsHeld(HandAction.Grab) || hand.Values[1] > (oldGrip ? .35f : .6f));
                hand.GripDown = hand.Grip && !oldGrip;
                hand.Select = hand.Tracked && (hand.IsHeld(HandAction.Select) || hand.Values[0] > (oldSelect ? .35f : .55f));
                hand.SelectDown = hand.Select && !oldSelect;
                hand.Skeleton = hand.Tracked && hand.Flags[2] != 0;
                if (hand.Skeleton) hand.HasFingerTracking = hand.Flags[3] >= 1;
                for (int finger = 0; finger < 5; ++finger)
                    hand.Curls[finger] = hand.Skeleton ? hand.Values[4+finger] : finger == 1 ? hand.Values[0] : hand.Grip ? 1 : 0;
                hand.Gesture = hand.GestureFilter.Update(hand.Curls, playing && hand.Skeleton && hand.HasFingerTracking, now);
                if (hand.Tracked) OpenVRRenderer.DecodePose(hand.Pose, out hand.Position, out hand.Rotation);
                hand.AimTracked = hand.Tracked && modern && NativeVR.AVR_ReadAimPose(i+1, hand.AimPose) != 0;
                if (hand.AimTracked) hand.AimRotation = OpenVRRenderer.DecodeRotation(hand.AimPose);
                int confidence = hand.Skeleton ? hand.Flags[3] : -1;
                if (confidence != lastSkeleton[i]) { log("Hand " + (i+1) + " skeleton tracking level=" + confidence + " (-1 unavailable, 0 estimated, 1 partial, 2 full)"); lastSkeleton[i] = confidence; }
                ConfirmDown |= hand.IsDown(HandAction.Jump);
                BackDown |= menu && (hand.IsDown(HandAction.Back) || hand.IsDown(HandAction.Spray));
                PauseDown |= hand.IsDown(HandAction.Pause);
                RecenterDown |= hand.IsDown(HandAction.Recenter);
                bool wasInReach = hand.InParachuteReach;
                bool wasArmed = hand.Pull.Armed;
                Vector3 controller = hand.Position;
                bool physicalPull = canPull && !shortcuts;
                bool rawTracked = physicalPull && NativeVR.AVR_CopyPose(i+1, rawHandPose) != 0;
                bool reachTracked = physicalPull && (rawTracked || hand.Tracked);
                if (rawTracked)
                    controller = new Vector3(rawHandPose[3], rawHandPose[7], -rawHandPose[11]);
                Vector3 offset = controller-headPosition;
                Vector3 reach = trackingToHead * offset;
                Vector3 parachutePosition = VRModSettings.ParachutePosition;
                bool inReach = reachTracked && ParachuteReach.Contains(reach.x, reach.y, reach.z,
                    parachutePosition.x, parachutePosition.y, parachutePosition.z);
                hand.InParachuteReach = inReach && !wasArmed;
                if (hand.Pull.Update(physicalPull, reachTracked, hand.PhysicalGrip, inReach, offset.y, now))
                {
                    parachutePullUntil = now+.25f;
                    Haptic(i, .7f, .085f);
                }
                else if (!wasArmed && hand.Pull.Armed)
                {
                    Haptic(i, .8f, .16f);
                }
                else if (hand.InParachuteReach && !wasInReach)
                {
                    Haptic(i, .5f, .12f, 145);
                }
                if (playing)
                {
                    Vector3 relativeHand = Quaternion.Inverse(EquipmentHeading) * (hand.Position-headPosition);
                    ParachuteDown |= shortcuts && canPull && hand.IsDown(HandAction.Parachute);
                    Vector3 canHolster = VREquipment.HolsterOffset(1);
                    Vector3 shotHolster = VREquipment.HolsterOffset(0);
                    if (shortcuts || !hand.Tracked || !hand.Grip) { hand.CanHeld = false; hand.ShotHeld = false; }
                    if (!ShotAvailable) hand.ShotHeld = false;
                    if (!shortcuts && hand.GripDown && !hand.Pull.Armed)
                    {
                        if (PlayerData.SprayCanEnabled && (relativeHand-canHolster).sqrMagnitude < .17f*.17f) hand.CanHeld = true;
                        else if (ShotAvailable && !Hands[0].ShotHeld && !Hands[1].ShotHeld &&
                            (relativeHand-shotHolster).sqrMagnitude < .20f*.20f) hand.ShotHeld = true;
                    }
                    if (hand.Tracked && PlayerData.SprayCanEnabled && ((shortcuts && hand.IsDown(HandAction.Spray)) || (hand.CanHeld && hand.Select))) SprayHand = i;
                    if (hand.ShotHeld && (hand.Position - mouth).sqrMagnitude < drinkRadius * drinkRadius)
                        EspressoRequest = true;
                    if (shortcuts && ShotAvailable && hand.IsDown(HandAction.Espresso)) EspressoRequest = true;
                }
                else { hand.CanHeld = false; hand.ShotHeld = false; }
                hand.GripSkeleton = hand.Skeleton && (hand.CanHeld || hand.ShotHeld) &&
                    NativeVR.AVR_ReadGripSkeleton(i+1, hand.GripBones) != 0;
            }
            if (CalibratingParachute && motionInput && headValid)
            {
                if (!calibrationReady)
                    calibrationReady = !Hands[0].Select && !Hands[1].Select;
                else
                {
                    for (int i = 0; i < Hands.Length; ++i)
                    {
                        if (!Hands[i].Tracked || !Hands[i].SelectDown) continue;
                        Vector3 position = Hands[i].Position;
                        if (NativeVR.AVR_CopyPose(i + 1, rawHandPose) != 0)
                            position = new Vector3(rawHandPose[3], rawHandPose[7], -rawHandPose[11]);
                        Vector3 offset = trackingToHead * (position - headPosition);
                        if (offset.z >= -.01f) continue;
                        VRModSettings.SetParachutePosition(offset);
                        CancelParachuteCalibration();
                        Haptic(i, .8f, .16f);
                        break;
                    }
                }
            }
            if (playing && (shortcuts || (!Hands[0].HasFingerTracking && !Hands[1].HasFingerTracking)))
            {
                VRHandInput glove = Hands[VRModSettings.FlipItGloveHand == GloveHand.Left ? 0 : 1];
                if (Hands[0].IsHeld(HandAction.FlipOff) || Hands[1].IsHeld(HandAction.FlipOff)) glove.ButtonGesture = CrowdGesture.FlipOff;
                else if (Hands[0].IsHeld(HandAction.ThumbsUp) || Hands[1].IsHeld(HandAction.ThumbsUp)) glove.ButtonGesture = CrowdGesture.ThumbsUp;
            }
            equipmentRecenter |= RecenterDown;
            Vector2 leftStick = Hands[0].Stick;
            Vector2 rightStick = Hands[1].Stick;
            if (!Gameplay || rightStick.sqrMagnitude < .35f*.35f) horizontalLook = false;
            else if (Mathf.Abs(rightStick.x) >= .35f && Mathf.Abs(rightStick.x) >= Mathf.Abs(rightStick.y))
                horizontalLook = true;
            Vector2 menuStick = leftStick.sqrMagnitude >= rightStick.sqrMagnitude ? leftStick : rightStick;
            if (!menu) menuStick = Vector2.zero;
            Navigation = repeat.Update(menuStick.x, menuStick.y, Time.realtimeSinceStartup);
        }

        internal float ReadSnapTurn()
        {
            float stick = Hands[1].Stick.x;
            if (!Gameplay || !Hands[1].Tracked || !horizontalLook || Mathf.Abs(stick) <= .75f)
            {
                snapDirection = 0;
                return 0;
            }
            int direction = stick > 0 ? 1 : -1;
            float now = Time.realtimeSinceStartup;
            if (direction == snapDirection && now < nextSnap) return 0;
            snapDirection = direction;
            nextSnap = now + .35f;
            return direction * VRModSettings.SnapTurnAngle;
        }

        internal InputDevice ReadSteering()
        {
            if (steeringFrame != Time.frameCount)
            {
                steeringFrame = Time.frameCount;
                ++steeringTick;
                float now = Time.realtimeSinceStartup;
                float elapsed = now - steeringTime;
                steeringTime = now;
                steeringDevice.PreUpdate(steeringTick, elapsed);
                steeringDevice.Update(steeringTick, elapsed);
                steeringDevice.PostUpdate(steeringTick, elapsed);
            }
            return steeringDevice;
        }

        internal bool ThumbsUp { get { return HasCrowdGesture(CrowdGesture.ThumbsUp); } }
        internal bool FlipOff { get { return HasCrowdGesture(CrowdGesture.FlipOff); } }

        private bool HasCrowdGesture(CrowdGesture gesture)
        {
            VRHandInput hand = Hands[VRModSettings.FlipItGloveHand == GloveHand.Left ? 0 : 1];
            return hand.Tracked && !hand.CanHeld && !hand.ShotHeld && !hand.Pull.Armed &&
                (hand.ButtonGesture == CrowdGesture.None ? hand.Gesture : hand.ButtonGesture) == gesture;
        }
        internal void Haptic(int index, float amplitude, float duration = .06f, float frequency = 120)
        {
            if (actionsReady && Focused && Hands[index].Tracked)
                NativeVR.AVR_Haptic(index+1, amplitude, duration, frequency);
        }

        internal void ImpactHaptic(float amplitude, float duration, float frequency)
        {
            for (int hand = 0; hand < Hands.Length; ++hand) Haptic(hand, amplitude, duration, frequency);
        }
        internal void Dispose() { if (Current == this) Current = null; }
    }
    internal sealed class VRStickProfile : Xbox360WinProfile
    {
        private sealed class StickAxis : InputControlSource
        {
            private readonly VRHandInput hand;
            private readonly bool vertical;

            internal StickAxis(VRHandInput hand, bool vertical)
            {
                this.hand = hand;
                this.vertical = vertical;
            }

            public float GetValue(InputDevice device)
            {
                return vertical ? -hand.Stick.y : hand.Stick.x;
            }

            public bool GetState(InputDevice device) { return false; }
        }

        internal VRStickProfile(VRHandInput leftHand)
        {
            List<InputControlMapping> axes = new List<InputControlMapping>(2);
            foreach (InputControlMapping mapping in AnalogMappings)
            {
                InputControlType target = mapping.Target;
                if (target != InputControlType.LeftStickX && target != InputControlType.LeftStickY) continue;
                mapping.Source = new StickAxis(leftHand, target == InputControlType.LeftStickY);
                axes.Add(mapping);
            }
            AnalogMappings = axes.ToArray();
            ButtonMappings = new InputControlMapping[0];
        }
    }

}
