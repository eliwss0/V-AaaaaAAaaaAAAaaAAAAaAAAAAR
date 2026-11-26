#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <string.h>
#include "dxgi_transfer.h"

static const GUID textureIID = {0x6f15aaf2,0xd208,0x4e89,{0x9a,0xb4,0x48,0x95,0x35,0xd3,0x4f,0x9c}};
static const GUID dxgiDeviceIID = {0x54ec77fa,0x1377,0x44e6,{0x8c,0x32,0x88,0xfd,0x5f,0x44,0xc8,0x4c}};
static const GUID factoryIID = {0x770aae78,0xf26f,0x4dba,{0xa8,0x29,0x25,0x3c,0x83,0xd1,0xb3,0x87}};
static const GUID resourceIID = {0x035f3ab4,0x482e,0x4e50,{0xb4,0x1f,0x8a,0x7f,0x8b,0xd8,0x96,0x0b}};

void AVR_DestroyTransfer(AVRTextureTransfer *t) {
    for (int eye = 0; eye < 2; ++eye) {
        if (t->submit[eye]) ID3D11Texture2D_Release(t->submit[eye]);
        if (t->shared[eye]) ID3D11Texture2D_Release(t->shared[eye]);
        if (t->source[eye]) ID3D11Texture2D_Release(t->source[eye]);
    }
    if (t->copyComplete) ID3D11Query_Release(t->copyComplete);
    if (t->submitContext) ID3D11DeviceContext_Release(t->submitContext);
    if (t->sourceContext) ID3D11DeviceContext_Release(t->sourceContext);
    if (t->submitDevice) ID3D11Device_Release(t->submitDevice);
    if (t->sourceDevice) ID3D11Device_Release(t->sourceDevice);
    memset(t, 0, sizeof(*t));
}

HRESULT AVR_CreateTransfer(AVRTextureTransfer *t, void *left, void *right) {
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    int proton = ntdll && GetProcAddress(ntdll, "wine_get_version") != NULL;
    return AVR_CreateTransferMode(t, left, right, proton);
}

HRESULT AVR_UpdateTransfer(AVRTextureTransfer *t, void *left, void *right) {
    if (!left || !right) return E_POINTER;
    if (t->source[0] == left && t->source[1] == right) return S_OK;
    AVRTextureTransfer replacement;
    HRESULT hr = t->sourceDevice
        ? AVR_CreateTransferMode(&replacement, left, right, t->directSubmit)
        : AVR_CreateTransfer(&replacement, left, right);
    if (FAILED(hr)) return hr;
    AVR_DestroyTransfer(t);
    *t = replacement;
    return S_OK;
}

HRESULT AVR_CreateTransferMode(AVRTextureTransfer *t, void *left, void *right, int directSubmit) {
    HRESULT hr = E_FAIL;
    IDXGIDevice *dxgiDevice = NULL;
    IDXGIAdapter *oldAdapter = NULL;
    IDXGIFactory1 *factory = NULL;
    IDXGIAdapter1 *adapter = NULL;
    memset(t, 0, sizeof(*t));
    if (!left || !right) return E_POINTER;
    hr = IUnknown_QueryInterface((IUnknown *)left, &textureIID, (void **)&t->source[0]);
    if (FAILED(hr)) goto done;
    hr = IUnknown_QueryInterface((IUnknown *)right, &textureIID, (void **)&t->source[1]);
    if (FAILED(hr)) goto done;
    ID3D11Texture2D_GetDevice(t->source[0], &t->sourceDevice);
    ID3D11Device_GetImmediateContext(t->sourceDevice, &t->sourceContext);
    ID3D11Device *rightDevice = NULL;
    ID3D11Texture2D_GetDevice(t->source[1], &rightDevice);
    int sameDevice = rightDevice == t->sourceDevice;
    ID3D11Device_Release(rightDevice);
    if (!sameDevice) { hr = E_INVALIDARG; goto done; }
    if (directSubmit) {
        t->directSubmit = 1;
        t->submitDevice = t->sourceDevice;
        ID3D11Device_AddRef(t->submitDevice);
        t->submitContext = t->sourceContext;
        ID3D11DeviceContext_AddRef(t->submitContext);
        for (int eye = 0; eye < 2; ++eye) {
            D3D11_TEXTURE2D_DESC description;
            ID3D11Texture2D_GetDesc(t->source[eye], &description);
            if (description.SampleDesc.Count != 1 || description.ArraySize != 1) { hr = E_INVALIDARG; goto done; }
            description.MipLevels = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            description.CPUAccessFlags = 0;
            description.MiscFlags = 0;
            hr = ID3D11Device_CreateTexture2D(t->sourceDevice, &description, NULL, &t->submit[eye]);
            if (FAILED(hr)) goto done;
        }
        D3D11_QUERY_DESC query = {D3D11_QUERY_EVENT, 0};
        hr = ID3D11Device_CreateQuery(t->sourceDevice, &query, &t->copyComplete);
        goto done;
    }
    hr = ID3D11Device_QueryInterface(t->sourceDevice, &dxgiDeviceIID, (void **)&dxgiDevice);
    if (FAILED(hr)) goto done;
    hr = IDXGIDevice_GetAdapter(dxgiDevice, &oldAdapter);
    if (FAILED(hr)) goto done;
    DXGI_ADAPTER_DESC oldDescription;
    hr = IDXGIAdapter_GetDesc(oldAdapter, &oldDescription);
    if (FAILED(hr)) goto done;
    hr = CreateDXGIFactory1(&factoryIID, (void **)&factory);
    if (FAILED(hr)) goto done;
    for (UINT index = 0; ; ++index) {
        hr = IDXGIFactory1_EnumAdapters1(factory, index, &adapter);
        if (FAILED(hr)) goto done;
        DXGI_ADAPTER_DESC1 candidate;
        hr = IDXGIAdapter1_GetDesc1(adapter, &candidate);
        if (FAILED(hr)) goto done;
        if (candidate.AdapterLuid.LowPart == oldDescription.AdapterLuid.LowPart &&
            candidate.AdapterLuid.HighPart == oldDescription.AdapterLuid.HighPart) break;
        IDXGIAdapter1_Release(adapter); adapter = NULL;
    }
    D3D_FEATURE_LEVEL featureLevel;
    hr = D3D11CreateDevice((IDXGIAdapter *)adapter, D3D_DRIVER_TYPE_UNKNOWN, NULL,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, NULL, 0, D3D11_SDK_VERSION,
        &t->submitDevice, &featureLevel, &t->submitContext);
    if (FAILED(hr)) goto done;
    for (int eye = 0; eye < 2; ++eye) {
        D3D11_TEXTURE2D_DESC description;
        ID3D11Texture2D_GetDesc(t->source[eye], &description);
        if (description.SampleDesc.Count != 1 || description.ArraySize != 1) { hr = E_INVALIDARG; goto done; }
        description.MipLevels = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        description.CPUAccessFlags = 0;
        description.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
        hr = ID3D11Device_CreateTexture2D(t->sourceDevice, &description, NULL, &t->shared[eye]);
        if (FAILED(hr)) goto done;
        IDXGIResource *resource = NULL;
        HANDLE handle = NULL;
        hr = ID3D11Texture2D_QueryInterface(t->shared[eye], &resourceIID, (void **)&resource);
        if (FAILED(hr)) goto done;
        hr = IDXGIResource_GetSharedHandle(resource, &handle);
        IDXGIResource_Release(resource);
        if (FAILED(hr)) goto done;
        hr = ID3D11Device_OpenSharedResource(t->submitDevice, handle, &textureIID, (void **)&t->submit[eye]);
        if (FAILED(hr)) goto done;
    }
    D3D11_QUERY_DESC query = {D3D11_QUERY_EVENT, 0};
    hr = ID3D11Device_CreateQuery(t->sourceDevice, &query, &t->copyComplete);
done:
    if (adapter) IDXGIAdapter1_Release(adapter);
    if (factory) IDXGIFactory1_Release(factory);
    if (oldAdapter) IDXGIAdapter_Release(oldAdapter);
    if (dxgiDevice) IDXGIDevice_Release(dxgiDevice);
    if (FAILED(hr)) AVR_DestroyTransfer(t);
    return hr;
}

HRESULT AVR_CopyForSubmit(AVRTextureTransfer *t, int eye) {
    if (eye < 0 || eye > 1 || !t->sourceContext || !t->submit[eye] || !t->copyComplete) return E_INVALIDARG;
    ID3D11Texture2D *destination = t->directSubmit ? t->submit[eye] : t->shared[eye];
    ID3D11DeviceContext_CopyResource(t->sourceContext, (ID3D11Resource *)destination, (ID3D11Resource *)t->source[eye]);
    ID3D11DeviceContext_End(t->sourceContext, (ID3D11Asynchronous *)t->copyComplete);
    ID3D11DeviceContext_Flush(t->sourceContext);
    DWORD started = GetTickCount();
    for (;;) {
        HRESULT hr = ID3D11DeviceContext_GetData(t->sourceContext, (ID3D11Asynchronous *)t->copyComplete, NULL, 0, 0);
        if (hr != S_FALSE) return hr;
        if (GetTickCount() - started > 2000) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        Sleep(0);
    }
}
