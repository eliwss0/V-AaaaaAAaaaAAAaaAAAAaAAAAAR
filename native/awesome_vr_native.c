#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <d3d11.h>
#include "dxgi_transfer.h"
#undef EXTERN_C
#include "openvr_capi_1_26_7.h"

#define API __declspec(dllexport)
#define WAIT_EVENT 0x415600
#define LEFT_EVENT 0x415601
#define RIGHT_EVENT 0x415602
#define STOP_EVENT 0x415603

typedef uint32_t (__cdecl *InitFn)(EVRInitError *, EVRApplicationType, const char *);
typedef intptr_t (__cdecl *InterfaceFn)(const char *, EVRInitError *);
typedef void (__cdecl *ShutdownFn)(void);
static HMODULE library;
static struct VR_IVRSystem_FnTable *systemVR;
static struct VR_IVRCompositor_FnTable *compositor;
static ShutdownFn shutdownVR;
static CRITICAL_SECTION mutex;
static LONG lockReady;
static LONG active;
static LONG eventCount;
static LONG poseSequence;
static int graphicsType = -1;
static int lastWait, lastLeft, lastRight;
static int frames;
static int directGraphics;
static AVRTextureTransfer transfer;
static HRESULT lastCopyError;
static TrackedDevicePose_t poses[64];

_Static_assert(sizeof(void *) == 4, "The Unity player requires an x86 bridge");
_Static_assert(sizeof(HmdMatrix34_t) == 48, "OpenVR matrix ABI");
_Static_assert(sizeof(TrackedDevicePose_t) == 80, "OpenVR pose ABI");
_Static_assert(sizeof(Texture_t) == 12, "OpenVR texture ABI");
_Static_assert(sizeof(VRBoneTransform_t) == 32, "OpenVR bone ABI");

static int ValidPose(const TrackedDevicePose_t *pose) {
    if (!pose->bPoseIsValid || !pose->bDeviceIsConnected) return 0;
    for (int row = 0; row < 3; ++row)
        for (int column = 0; column < 4; ++column)
            if (!isfinite(pose->mDeviceToAbsoluteTracking.m[row][column])) return 0;
    return 1;
}
#include "vr_input.h"

static void EnsureLock(void) {
    if (InterlockedCompareExchange(&lockReady, 1, 0) == 0) {
        InitializeCriticalSection(&mutex);
        InterlockedExchange(&lockReady, 2);
    } else while (lockReady != 2) Sleep(0);
}

API void __stdcall UnitySetGraphicsDevice(void *device, int renderer, int eventType) {
    (void)device;
    graphicsType = eventType == 1 ? -1 : renderer;
}

API int __cdecl AVR_Initialize(const wchar_t *absoluteLibraryPath, int singleThreadedGraphics) {
    EnsureLock();
    directGraphics = singleThreadedGraphics;
    if (!directGraphics) return -5;
    if (active) return 0;
    if (!library) library = LoadLibraryExW(absoluteLibraryPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!library) return -1;
    InitFn initialize = (InitFn)GetProcAddress(library, "VR_InitInternal2");
    InterfaceFn getInterface = (InterfaceFn)GetProcAddress(library, "VR_GetGenericInterface");
    shutdownVR = (ShutdownFn)GetProcAddress(library, "VR_ShutdownInternal");
    if (!initialize || !getInterface || !shutdownVR) return -2;
    EVRInitError error = 0;
    initialize(&error, EVRApplicationType_VRApplication_Scene, NULL);
    if (error) return (int)error;
    systemVR = (struct VR_IVRSystem_FnTable *)getInterface("FnTable:IVRSystem_022", &error);
    if (!systemVR || error) { shutdownVR(); return error ? (int)error : -3; }
    compositor = (struct VR_IVRCompositor_FnTable *)getInterface("FnTable:IVRCompositor_027", &error);
    if (!compositor || error) { systemVR = NULL; shutdownVR(); return error ? (int)error : -4; }
    compositor->SetTrackingSpace(ETrackingUniverseOrigin_TrackingUniverseStanding);
    memset(poses, 0, sizeof(poses));
    lastWait = lastLeft = lastRight = -999;
    InterlockedExchange(&active, 1);
    return 0;
}

API void __cdecl AVR_GetEyeInfo(int eye, float *eyeMatrix, float *projection, uint32_t *width, uint32_t *height) {
    if (!active) return;
    HmdMatrix34_t transform = systemVR->GetEyeToHeadTransform((EVREye)eye);
    memcpy(eyeMatrix, &transform, sizeof(transform));
    systemVR->GetProjectionRaw((EVREye)eye, &projection[0], &projection[1], &projection[2], &projection[3]);
    systemVR->GetRecommendedRenderTargetSize(width, height);
}

API int __cdecl AVR_SetTextures(void *left, void *right) {
    if (!active || !left || !right) return -1;
    lastCopyError = AVR_UpdateTransfer(&transfer, left, right);
    return (int)lastCopyError;
}

API int __cdecl AVR_CopyPose(int role, float *matrix) {
    if (!active) return 0;
    uint32_t index = role == 0 ? 0 : systemVR->GetTrackedDeviceIndexForControllerRole(
        role == 1 ? ETrackedControllerRole_TrackedControllerRole_LeftHand : ETrackedControllerRole_TrackedControllerRole_RightHand);
    if (index >= 64) return 0;
    EnterCriticalSection(&mutex);
    int valid = ValidPose(&poses[index]);
    if (valid) memcpy(matrix, &poses[index].mDeviceToAbsoluteTracking, sizeof(HmdMatrix34_t));
    LeaveCriticalSection(&mutex);
    return valid;
}

API void __cdecl AVR_GetStatus(int *values) {
    values[0] = active; values[1] = eventCount; values[2] = poseSequence;
    values[3] = lastWait; values[4] = lastLeft; values[5] = lastRight;
    values[6] = frames; values[7] = graphicsType;
    values[8] = (int)lastCopyError;
}

API int __cdecl AVR_GetTextureMode(void) { return transfer.directSubmit; }

API void __stdcall UnityRenderEvent(int eventID) {
    if (eventID < WAIT_EVENT || eventID > STOP_EVENT) return;
    InterlockedIncrement(&eventCount);
    if (!active) return;
    if (eventID == WAIT_EVENT) {
        TrackedDevicePose_t current[64] = {0};
        lastWait = (int)compositor->WaitGetPoses(current, 64, NULL, 0);
        EnterCriticalSection(&mutex);
        if (lastWait == EVRCompositorError_VRCompositorError_None) {
            memcpy(poses, current, sizeof(poses));
            InterlockedIncrement(&poseSequence);
        } else memset(poses, 0, sizeof(poses));
        LeaveCriticalSection(&mutex);
    } else if (eventID == LEFT_EVENT || eventID == RIGHT_EVENT) {
        if (lastWait != EVRCompositorError_VRCompositorError_None) return;
        int eye = eventID == LEFT_EVENT ? 0 : 1;
        if (!transfer.submit[eye]) return;
        lastCopyError = AVR_CopyForSubmit(&transfer, eye);
        if (FAILED(lastCopyError)) {
            if (eye == 0) lastLeft = -1000; else lastRight = -1000;
            return;
        }
        Texture_t texture = { transfer.submit[eye], ETextureType_TextureType_DirectX, EColorSpace_ColorSpace_Gamma };
        VRTextureBounds_t bounds = { 0, 1, 1, 0 };
        int result = (int)compositor->Submit((EVREye)eye, &texture, &bounds, EVRSubmitFlags_Submit_Default);
        if (eye == 0) lastLeft = result;
        else { lastRight = result; frames++; compositor->PostPresentHandoff(); }
    } else if (eventID == STOP_EVENT) {
        InterlockedExchange(&active, 0);
        compositor->ClearLastSubmittedFrame();
        AVR_DestroyTransfer(&transfer);
        shutdownVR();
        compositor = NULL; systemVR = NULL;
    }
}

API void __cdecl AVR_RunEvent(int eventID) { UnityRenderEvent(eventID); }
