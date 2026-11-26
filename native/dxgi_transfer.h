#ifndef AWESOME_DXGI_TRANSFER_H
#define AWESOME_DXGI_TRANSFER_H
#include <windows.h>
#include <d3d11.h>

typedef struct AVRTextureTransfer {
    int directSubmit;
    ID3D11Device *sourceDevice;
    ID3D11DeviceContext *sourceContext;
    ID3D11Device *submitDevice;
    ID3D11DeviceContext *submitContext;
    ID3D11Texture2D *source[2];
    ID3D11Texture2D *shared[2];
    ID3D11Texture2D *submit[2];
    ID3D11Query *copyComplete;
} AVRTextureTransfer;

HRESULT AVR_CreateTransfer(AVRTextureTransfer *transfer, void *left, void *right);
HRESULT AVR_CreateTransferMode(AVRTextureTransfer *transfer, void *left, void *right, int directSubmit);
HRESULT AVR_UpdateTransfer(AVRTextureTransfer *transfer, void *left, void *right);
HRESULT AVR_CopyForSubmit(AVRTextureTransfer *transfer, int eye);
void AVR_DestroyTransfer(AVRTextureTransfer *transfer);
#endif
