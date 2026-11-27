#ifndef AWESOME_VR_INPUT_H
#define AWESOME_VR_INPUT_H
static struct VR_IVRInput_FnTable *inputVR;
static VRActionSetHandle_t inputSet;
static VRActionHandle_t inputDigital[2][11], inputAnalog[2][3], inputPose[2], inputAim[2], inputSkeleton[2], inputHaptic[2];
static VRInputValueHandle_t inputSource[2];
static int inputError = -1;
enum { INPUT_POSE_VALID = 1, INPUT_STICK_ACTIVE = 2 };

API int __cdecl AVR_HasInputFocus(void) {
    return active && systemVR && systemVR->IsInputAvailable() && !systemVR->IsSteamVRDrawingControllers() && !systemVR->ShouldApplicationPause();
}

API int __cdecl AVR_InitializeInput(const char *manifest) {
    if (!active || !library) return -1;
    InterfaceFn getInterface = (InterfaceFn)GetProcAddress(library, "VR_GetGenericInterface");
    EVRInitError error = 0;
    inputVR = (struct VR_IVRInput_FnTable *)getInterface("FnTable:IVRInput_010", &error);
    if (!inputVR || error) return error ? (int)error : -2;
    inputError = (int)inputVR->SetActionManifestPath((char *)manifest);
    if (inputError) return inputError;
    inputError = (int)inputVR->GetActionSetHandle("/actions/awesome", &inputSet);
    if (inputError) return inputError;
    const char *digital[] = { "select", "grab", "thumbsup", "flipoff", "jump", "parachute", "espresso", "back", "pause", "recenter", "spray" };
    const char *analog[] = { "trigger", "grip", "stick" };
    for (int hand = 0; hand < 2; ++hand) {
        char path[160];
        const char *side = hand == 0 ? "left" : "right";
        snprintf(path, sizeof(path), "/user/hand/%s", side);
        if ((inputError = inputVR->GetInputSourceHandle(path, &inputSource[hand]))) return inputError;
        for (int i = 0; i < 11; ++i) {
            snprintf(path, sizeof(path), "/actions/awesome/in/%s_%s", side, digital[i]);
            if ((inputError = inputVR->GetActionHandle(path, &inputDigital[hand][i]))) return inputError;
        }
        for (int i = 0; i < 3; ++i) {
            snprintf(path, sizeof(path), "/actions/awesome/in/%s_%s", side, analog[i]);
            if ((inputError = inputVR->GetActionHandle(path, &inputAnalog[hand][i]))) return inputError;
        }
        snprintf(path, sizeof(path), "/actions/awesome/in/%s_pose", side);
        if ((inputError = inputVR->GetActionHandle(path, &inputPose[hand]))) return inputError;
        inputAim[hand] = 0;
        snprintf(path, sizeof(path), "/actions/awesome/in/%s_aim", side);
        inputVR->GetActionHandle(path, &inputAim[hand]);
        snprintf(path, sizeof(path), "/actions/awesome/in/%s_skeleton", side);
        if ((inputError = inputVR->GetActionHandle(path, &inputSkeleton[hand]))) return inputError;
        snprintf(path, sizeof(path), "/actions/awesome/out/%s_haptic", side);
        if ((inputError = inputVR->GetActionHandle(path, &inputHaptic[hand]))) return inputError;
    }
    return 0;
}

API int __cdecl AVR_UpdateInput(void) {
    if (!active || !inputVR || !inputSet) return -1;
    VRActiveActionSet_t set;
    memset(&set, 0, sizeof(set));
    set.ulActionSet = inputSet;
    inputError = (int)inputVR->UpdateActionState(&set, sizeof(set), 1);
    return inputError;
}

API int __cdecl AVR_ReadInput(int role, float *values, int *flags, float *matrix, float *bones) {
    memset(values, 0, 9 * sizeof(float));
    memset(flags, 0, 4 * sizeof(int));
    if (!active || !inputVR || inputError || role < 1 || role > 2) return 0;
    int hand = role - 1;
    for (int i = 0; i < 11; ++i) {
        InputDigitalActionData_t data;
        memset(&data, 0, sizeof(data));
        if (!inputVR->GetDigitalActionData(inputDigital[hand][i], &data, sizeof(data), inputSource[hand]) && data.bActive && data.bState)
            flags[0] |= 1 << i;
    }
    for (int i = 0; i < 3; ++i) {
        InputAnalogActionData_t data;
        memset(&data, 0, sizeof(data));
        if (!inputVR->GetAnalogActionData(inputAnalog[hand][i], &data, sizeof(data), inputSource[hand]) && data.bActive) {
            values[i] = data.x;
            if (i == 2) {
                values[3] = data.y;
                flags[1] |= INPUT_STICK_ACTIVE;
            }
        }
    }
    InputPoseActionData_t pose;
    memset(&pose, 0, sizeof(pose));
    if (!inputVR->GetPoseActionDataForNextFrame(inputPose[hand], ETrackingUniverseOrigin_TrackingUniverseStanding, &pose, sizeof(pose), inputSource[hand]) && pose.bActive && ValidPose(&pose.pose)) {
        flags[1] |= INPUT_POSE_VALID;
        memcpy(matrix, &pose.pose.mDeviceToAbsoluteTracking, sizeof(HmdMatrix34_t));
    }
    InputSkeletalActionData_t skeleton;
    memset(&skeleton, 0, sizeof(skeleton));
    if (!inputVR->GetSkeletalActionData(inputSkeleton[hand], &skeleton, sizeof(skeleton)) && skeleton.bActive) {
        VRSkeletalSummaryData_t summary;
        VRBoneTransform_t transforms[31];
        EVRSkeletalTrackingLevel level;
        if (!inputVR->GetSkeletalSummaryData(inputSkeleton[hand], EVRSummaryType_VRSummaryType_FromAnimation, &summary) &&
            !inputVR->GetSkeletalBoneData(inputSkeleton[hand], EVRSkeletalTransformSpace_VRSkeletalTransformSpace_Model, EVRSkeletalMotionRange_VRSkeletalMotionRange_WithoutController, transforms, 31)) {
            flags[2] = 1;
            InputPoseActionData_t skeletonPose;
            memset(&skeletonPose, 0, sizeof(skeletonPose));
            if (!inputVR->GetPoseActionDataForNextFrame(inputSkeleton[hand], ETrackingUniverseOrigin_TrackingUniverseStanding, &skeletonPose, sizeof(skeletonPose), 0) && skeletonPose.bActive && ValidPose(&skeletonPose.pose)) {
                flags[1] |= INPUT_POSE_VALID;
                memcpy(matrix, &skeletonPose.pose.mDeviceToAbsoluteTracking, sizeof(HmdMatrix34_t));
            }
            if (!inputVR->GetSkeletalTrackingLevel(inputSkeleton[hand], &level)) flags[3] = (int)level;
            for (int i = 0; i < 5; ++i) values[4+i] = summary.flFingerCurl[i];
            for (int i = 0; i < 31; ++i) {
                for (int j = 0; j < 3; ++j) bones[i*3+j] = transforms[i].position.v[j];
            }
        }
    }
    return 1;
}

API int __cdecl AVR_ReadAimPose(int role, float *matrix) {
    if (!active || !inputVR || inputError || role < 1 || role > 2 || !matrix || !inputAim[role-1]) return 0;
    InputPoseActionData_t pose = {0};
    if (inputVR->GetPoseActionDataForNextFrame(inputAim[role-1], ETrackingUniverseOrigin_TrackingUniverseStanding,
            &pose, sizeof(pose), inputSource[role-1]) || !pose.bActive || !ValidPose(&pose.pose)) return 0;
    memcpy(matrix, &pose.pose.mDeviceToAbsoluteTracking, sizeof(HmdMatrix34_t));
    return 1;
}

API int __cdecl AVR_ReadGripSkeleton(int role, float *bones) {
    if (!active || !inputVR || inputError || role < 1 || role > 2 || !bones) return 0;
    VRBoneTransform_t transforms[31];
    if (inputVR->GetSkeletalBoneData(inputSkeleton[role-1],
            EVRSkeletalTransformSpace_VRSkeletalTransformSpace_Model,
            EVRSkeletalMotionRange_VRSkeletalMotionRange_WithController, transforms, 31)) return 0;
    for (int i = 0; i < 31; ++i)
        for (int j = 0; j < 3; ++j) bones[i*3+j] = transforms[i].position.v[j];
    return 1;
}

API void __cdecl AVR_Haptic(int role, float amplitude, float duration, float frequency) {
    if (!active || !inputVR || inputError || role < 1 || role > 2 || !inputHaptic[role-1] ||
        !isfinite(amplitude) || !isfinite(duration) || !isfinite(frequency) ||
        amplitude <= 0 || duration <= 0 || frequency <= 0) return;
    if (duration > .5f) duration = .5f;
    if (amplitude > 1) amplitude = 1;
    inputVR->TriggerHapticVibrationAction(inputHaptic[role-1], 0, duration, frequency, amplitude, inputSource[role-1]);
}
#endif
