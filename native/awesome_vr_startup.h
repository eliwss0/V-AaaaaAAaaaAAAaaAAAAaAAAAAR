#ifndef AWESOME_STARTUP_H
#define AWESOME_STARTUP_H
#include <windows.h>
#include <shellapi.h>

static BOOL awesome_startup_prepared;
static WCHAR awesome_startup_log[2048];
static BOOL awesome_choice_pending, awesome_flat_selected, awesome_steam_frame;
static volatile LONG awesome_choice_state;

static WCHAR *awesome_path(const WCHAR *root, const WCHAR *relative) {
    SIZE_T length = lstrlenW(root) + lstrlenW(relative) + 2;
    WCHAR *path = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, length * sizeof(WCHAR));
    if (!path) return NULL;
    lstrcpyW(path, root);
    if (path[lstrlenW(path)-1] != L'\\') lstrcatW(path, L"\\");
    lstrcatW(path, relative);
    return path;
}

static BOOL awesome_ini_true(const WCHAR *path, const WCHAR *section, const WCHAR *key, const WCHAR *fallback) {
    WCHAR value[32];
    GetPrivateProfileStringW(section, key, fallback, value, 32, path);
    return lstrcmpiW(value, L"true") == 0 || lstrcmpiW(value, L"1") == 0;
}

static void awesome_prune_logs(const WCHAR *directory) {
    WCHAR *pattern = awesome_path(directory, L"awesome_vr_log_*.log");
    if (!pattern) return;
    WIN32_FIND_DATAW entry;
    HANDLE search = FindFirstFileW(pattern, &entry);
    HeapFree(GetProcessHeap(), 0, pattern);
    if (search == INVALID_HANDLE_VALUE) return;

    WCHAR oldest[64] = {0}, second[64] = {0};
    do {
        if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || lstrlenW(entry.cFileName) >= 64) continue;
        WCHAR *name = entry.cFileName;
        if (!oldest[0] || lstrcmpW(name, oldest) < 0) {
            WCHAR previous[64];
            lstrcpyW(previous, oldest);
            lstrcpyW(oldest, name);
            lstrcpyW(name, previous);
        }
        if (name[0] && (!second[0] || lstrcmpW(name, second) < 0)) {
            WCHAR previous[64];
            lstrcpyW(previous, second);
            lstrcpyW(second, name);
            lstrcpyW(name, previous);
        }
        if (!name[0]) continue;
        WCHAR *old_log = awesome_path(directory, name);
        if (old_log) {
            DeleteFileW(old_log);
            HeapFree(GetProcessHeap(), 0, old_log);
        }
    } while (FindNextFileW(search, &entry));
    FindClose(search);
}

static void awesome_initialize_log(const WCHAR *root) {
    WCHAR *directory = awesome_path(root, L"awesome_vr\\logs");
    if (!directory) return;
    CreateDirectoryW(directory, NULL);
    awesome_prune_logs(directory);

    SYSTEMTIME utc;
    GetSystemTime(&utc);
    WCHAR name[64];
    wsprintfW(name, L"awesome_vr_log_%04u%02u%02u_%02u%02u%02u_%03u_%lu.log",
        utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond,
        utc.wMilliseconds, GetCurrentProcessId());
    WCHAR *path = awesome_path(directory, name);
    if (path) {
        lstrcpynW(awesome_startup_log, path, 2048);
        SetEnvironmentVariableW(L"AWESOME_VR_LOG_FILE", name);
        HeapFree(GetProcessHeap(), 0, path);
    }
    HeapFree(GetProcessHeap(), 0, directory);
}

static void awesome_write_startup_log(const WCHAR *message) {
    if (!awesome_startup_log[0]) return;
    HANDLE file = CreateFileW(awesome_startup_log, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME utc;
    GetSystemTime(&utc);
    WCHAR line[512];
    wsprintfW(line, L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ [launcher] %s",
        utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond,
        utc.wMilliseconds, message);
    char text[512];
    int length = WideCharToMultiByte(CP_UTF8, 0, line, -1, text, sizeof(text), NULL, NULL);
    DWORD written;
    if (length > 1) WriteFile(file, text, length-1, &written, NULL);
    CloseHandle(file);
}

static int awesome_startup_option(const WCHAR *arg) {
    WCHAR key[100];
    int i = 0;
    while (arg[i] && arg[i] != L'=' && i < 99) { key[i] = arg[i]; ++i; }
    key[i] = 0;
    const WCHAR *pairs[] = { L"-screen-fullscreen", L"-screen-width", L"-screen-height", L"-adapter", L"-window-mode" };
    for (int n = 0; n < 5; ++n) if (!lstrcmpiW(key, pairs[n])) return arg[i] == L'=' ? 1 : 2;
    const WCHAR *switches[] = { L"-vr", L"-force-d3d9", L"-force-d3d11", L"-force-opengl", L"-force-d3d11-no-singlethreaded", L"-force-gfx-direct", L"-force-gfx-multi", L"-fullscreen", L"-windowed", L"-popupwindow" };
    for (int n = 0; n < 10; ++n) if (!lstrcmpiW(key, switches[n])) return 1;
    return 0;
}

static WCHAR *awesome_quote_arg(WCHAR *out, const WCHAR *arg) {
    *out++ = L'"';
    for (;;) {
        int slashes = 0;
        while (*arg == L'\\') { ++slashes; ++arg; }
        if (!*arg || *arg == L'"') {
            for (int n = 0; n < slashes*2; ++n) *out++ = L'\\';
            if (!*arg) break;
            *out++ = L'\\'; *out++ = *arg++;
        } else {
            for (int n = 0; n < slashes; ++n) *out++ = L'\\';
            *out++ = *arg++;
        }
    }
    *out++ = L'"';
    *out = 0;
    return out;
}

static char *awesome_ansi_command_line(const WCHAR *command) {
    int bytes = WideCharToMultiByte(CP_ACP, 0, command, -1, NULL, 0, NULL, NULL);
    if (!bytes) return NULL;
    char *result = HeapAlloc(GetProcessHeap(), 0, bytes);
    if (result) WideCharToMultiByte(CP_ACP, 0, command, -1, result, bytes, NULL, NULL);
    return result;
}

static BOOL awesome_prepare_startup(const WCHAR *root, BOOL loader_enabled, WCHAR **wide, char **ansi) {
    SetEnvironmentVariableW(L"AWESOME_VR_LOG_FILE", NULL);
    if (!loader_enabled) return FALSE;
    WCHAR requested[16] = {0};
    GetEnvironmentVariableW(L"AWESOME_VR_ENABLE", requested, 16);
    if (!lstrcmpiW(requested, L"0")) return FALSE;
    WCHAR *prefs = awesome_path(root, L"BepInEx\\config\\awesome_vr.cfg");
    BOOL safe_mode = !prefs || awesome_ini_true(prefs, L"General", L"SafeMode", L"true");
    BOOL logging_enabled = prefs && awesome_ini_true(prefs, L"General", L"EnableLogging", L"false");
    if (prefs) HeapFree(GetProcessHeap(), 0, prefs);
    if (safe_mode) return FALSE;
    WCHAR *ini = awesome_path(root, L"doorstop_config.ini");
    BOOL enabled = ini && awesome_ini_true(ini, L"AwesomeVR", L"EnableOnNormalLaunch", L"true");
    awesome_steam_frame = ini && awesome_ini_true(ini, L"AwesomeVR", L"SteamFrame", L"false");
    if (ini) HeapFree(GetProcessHeap(), 0, ini);
    if (!enabled && lstrcmpiW(requested, L"1")) return FALSE;
    int argc;
    const WCHAR *original = *wide ? *wide : GetCommandLineW();
    WCHAR **argv = CommandLineToArgvW(original, &argc);
    if (!argv || !argc) { if (argv) LocalFree(argv); return FALSE; }
    for (int i = 1; i < argc; ++i) {
        if (!lstrcmpiW(argv[i], L"-batchmode") || !lstrcmpiW(argv[i], L"-awesome_vr_flat")) { LocalFree(argv); return FALSE; }
        if (!lstrcmpiW(argv[i], L"-show-screen-selector") ||
            (!lstrcmpiW(argv[i], L"-vr") && lstrcmpiW(requested, L"1"))) {
            awesome_flat_selected = TRUE;
            LocalFree(argv);
            return FALSE;
        }
    }
    SIZE_T capacity = (SIZE_T)lstrlenW(original)*2 + 1024;
    WCHAR *result = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, capacity * sizeof(WCHAR));
    if (!result) { LocalFree(argv); return FALSE; }
    WCHAR *out = awesome_quote_arg(result, argv[0]);
    for (int i = 1; i < argc; ++i) {
        int option = awesome_startup_option(argv[i]);
        if (option) {
            if (option == 2 && i+1 < argc && argv[i+1][0] != L'-') ++i;
            continue;
        }
        *out++ = L' ';
        out = awesome_quote_arg(out, argv[i]);
    }
    lstrcpyW(out, L" -force-d3d11 -force-gfx-direct -screen-fullscreen 0 -screen-width 1280 -screen-height 720");
    BOOL ask = lstrcmpiW(requested, L"1") != 0;
    LocalFree(argv);
    char *narrow_result = awesome_ansi_command_line(result);
    if (!narrow_result) {
        HeapFree(GetProcessHeap(), 0, result);
        return FALSE;
    }
    *wide = result;
    *ansi = narrow_result;
    awesome_choice_pending = ask;
    if (logging_enabled) {
        awesome_initialize_log(root);
        awesome_write_startup_log(L"Launcher initialized.\r\n");
    }
    awesome_startup_prepared = TRUE;
    return TRUE;
}

static WORD *dialog_text(WORD *out, const WCHAR *text) {
    lstrcpyW((WCHAR *)out, text);
    return out + lstrlenW(text) + 1;
}

static WORD *dialog_item(WORD *out, DWORD style, short x, short y, short w, short h,
                         WORD id, WORD kind, const WCHAR *label) {
    out = (WORD *)(((UINT_PTR)out + 3) & ~(UINT_PTR)3);
    DLGITEMTEMPLATE item = { WS_CHILD | WS_VISIBLE | style, 0, x, y, w, h, id };
    memcpy(out, &item, sizeof(item));
    out += sizeof(item) / sizeof(WORD);
    *out++ = 0xffff;
    *out++ = kind;
    out = dialog_text(out, label);
    *out++ = 0;
    return out;
}

static void layout_frame_chooser(HWND window) {
    RECT client;
    if (!GetClientRect(window, &client)) return;
    int width = client.right;
    int height = client.bottom;
    if (width < 160 || height < 96) return;

    int font_size = min(width / 34, height / 13);
    NONCLIENTMETRICSW metrics = {0};
    metrics.cbSize = sizeof(metrics);
    HFONT font = NULL;
    if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, metrics.cbSize, &metrics, 0)) {
        metrics.lfMessageFont.lfHeight = -font_size;
        metrics.lfMessageFont.lfWidth = 0;
        font = CreateFontIndirectW(&metrics.lfMessageFont);
    }
    HWND prompt = GetDlgItem(window, 100);
    HWND flat = GetDlgItem(window, IDNO);
    HWND vr = GetDlgItem(window, IDYES);
    if (font) {
        SendMessageW(prompt, WM_SETFONT, (WPARAM)font, FALSE);
        SendMessageW(flat, WM_SETFONT, (WPARAM)font, FALSE);
        SendMessageW(vr, WM_SETFONT, (WPARAM)font, FALSE);
        HFONT previous = (HFONT)SetWindowLongPtrW(window, DWLP_USER, (LONG_PTR)font);
        if (previous) DeleteObject(previous);
    }

    int top = (height - 7 * font_size) / 2;
    int button_width = 7 * font_size;
    int button_y = top + 4 * font_size;
    MoveWindow(prompt, width / 10, top, width * 8 / 10, 3 * font_size, TRUE);
    MoveWindow(flat, width / 2 - button_width - font_size / 2, button_y, button_width, 3 * font_size, TRUE);
    MoveWindow(vr, width / 2 + font_size / 2, button_y, button_width, 3 * font_size, TRUE);
}

static INT_PTR CALLBACK chooser_dialog(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    (void)lparam;
    if (message == WM_INITDIALOG) {
        if (awesome_steam_frame) {
            ShowWindow(window, SW_MAXIMIZE);
            layout_frame_chooser(window);
        }
        SetForegroundWindow(window);
        return TRUE;
    }
    if (awesome_steam_frame && message == WM_SIZE && wparam != SIZE_MINIMIZED)
        layout_frame_chooser(window);
    if (message == WM_NCDESTROY) {
        HFONT font = (HFONT)GetWindowLongPtrW(window, DWLP_USER);
        if (font) DeleteObject(font);
    }
    if (message == WM_COMMAND) {
        WORD id = LOWORD(wparam);
        if (id == IDNO || id == IDYES || id == IDCANCEL) {
            EndDialog(window, id);
            return TRUE;
        }
    }
    if (message == WM_CLOSE) {
        EndDialog(window, IDCANCEL);
        return TRUE;
    }
    return FALSE;
}

static INT_PTR awesome_choose_mode(void) {
    union { DWORD alignment; WORD words[512]; } buffer = {0};
    DWORD style = WS_CAPTION | WS_SYSMENU | DS_CENTER | DS_SETFONT;
    style |= awesome_steam_frame ? WS_THICKFRAME | WS_MAXIMIZEBOX : WS_POPUP | DS_MODALFRAME;
    DLGTEMPLATE dialog = { style, awesome_steam_frame ? WS_EX_APPWINDOW : 0, 3, 0, 0, 388, 92 };
    memcpy(buffer.words, &dialog, sizeof(dialog));
    WORD *out = buffer.words + sizeof(dialog) / sizeof(WORD);
    *out++ = 0;
    *out++ = 0;
    out = dialog_text(out, L"AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome");
    *out++ = 9;
    out = dialog_text(out, L"MS Shell Dlg 2");
    DWORD prompt_style = SS_CENTER | (awesome_steam_frame ? 0 : SS_CENTERIMAGE);
    out = dialog_item(out, prompt_style, 16, 15, 356, 22, 100, 0x82,
                      L"How do you want to play AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome?");
    out = dialog_item(out, WS_GROUP | WS_TABSTOP | BS_DEFPUSHBUTTON, 60, 46, 128, 30, IDNO, 0x80, L"Flat");
    dialog_item(out, WS_TABSTOP | BS_PUSHBUTTON, 200, 46, 128, 30, IDYES, 0x80, L"VR");
    return DialogBoxIndirectParamW(GetModuleHandleW(NULL), (DLGTEMPLATE *)buffer.words, NULL, chooser_dialog, 0);
}

static void awesome_choose_startup(void) {
    if (!awesome_choice_pending) return;
    if (InterlockedCompareExchange(&awesome_choice_state, 1, 0) != 0) {
        while (InterlockedCompareExchange(&awesome_choice_state, 2, 2) != 2) Sleep(1);
        return;
    }
    INT_PTR selected = awesome_choose_mode();
    if (selected == -1) {
        awesome_write_startup_log(L"Could not create the game mode dialog.\r\n");
        ExitProcess(1);
    }
    if (selected != IDNO && selected != IDYES) ExitProcess(0);
    if (selected == IDNO) {
        awesome_flat_selected = TRUE;
        new_cmdline_args = NULL;
        new_cmdline_args_narrow = NULL;
        SetEnvironmentVariableW(L"AWESOME_VR_ENABLE", L"0");
        SetEnvironmentVariableW(L"AWESOME_VR_GFX_DIRECT", NULL);
        awesome_write_startup_log(L"Flat selected; using the original game launch arguments.\r\n");
    } else {
        SetEnvironmentVariableW(L"AWESOME_VR_ENABLE", L"1");
        SetEnvironmentVariableW(L"AWESOME_VR_GFX_DIRECT", L"1");
        awesome_write_startup_log(L"SteamVR mod selected; VR graphics arguments enabled.\r\n");
    }
    InterlockedExchange(&awesome_choice_state, 2);
}

static void awesome_complete_startup(BOOL hooks_installed) {
    if (!awesome_startup_prepared) return;
    if (hooks_installed) {
        if (!awesome_choice_pending) {
            SetEnvironmentVariableW(L"AWESOME_VR_ENABLE", L"1");
            SetEnvironmentVariableW(L"AWESOME_VR_GFX_DIRECT", L"1");
            awesome_write_startup_log(L"Native startup applied before Unity: D3D11, direct graphics, windowed 1280x720; both command-line hooks installed.\r\n");
        }
    } else awesome_write_startup_log(L"ERROR: native startup command-line hooks failed; VR startup not enabled.\r\n");
}
#endif
