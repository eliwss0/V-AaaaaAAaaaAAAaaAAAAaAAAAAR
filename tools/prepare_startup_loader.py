from pathlib import Path

project = Path(__file__).absolute().parents[1]
vendor = project / "vendor/doorstop-source/UnityDoorstop-4.6.0"
output = project / "build/startup-loader"
output.mkdir(parents=True, exist_ok=True)
source = (vendor / "src/windows/entrypoint.c").read_text(encoding="utf-8")
def replace_once(source, old, new):
    if source.count(old) != 1:
        raise RuntimeError(f"Expected one UnityDoorstop patch target: {old}")
    return source.replace(old, new)


declaration = "char *new_cmdline_args_narrow = NULL;"
source = replace_once(source, declaration, declaration + '\n#include "awesome_vr_startup.h"')
for signature in ("LPWSTR WINAPI get_command_line_hook() {", "LPSTR WINAPI get_command_line_hook_narrow() {"):
    source = replace_once(source, signature, signature + "\n    awesome_choose_startup();")
signature = "void *WINAPI get_proc_address_detour(void *module, char *name) {"
source = replace_once(source, signature, signature + "\n    awesome_choose_startup();\n    if (awesome_flat_selected) return (void *)GetProcAddress(module, name);")
source = replace_once(source, "    inject(paths);", "    awesome_prepare_startup(paths->app_dir, config.enabled, &new_cmdline_args, &new_cmdline_args_narrow);\n    inject(paths);")
source = replace_once(source, "    if (!ok) {", "    awesome_complete_startup(ok);\n    if (!ok) {")

patched = output / "entrypoint.c"
source = source.replace('#include "entrypoint.h"', '#include "windows/entrypoint.h"')
for relative in ("../bootstrap.h", "../config/config.h", "../crt.h", "../util/logging.h", "../util/paths.h", "hook.h", "proxy/proxy.h"):
    replacement = relative[3:] if relative.startswith("../") else "windows/" + relative
    source = source.replace(f'#include "{relative}"', f'#include "{replacement}"')
patched.write_text("/* Modified by Eli Weiss for V-AaaaaR!, 2026-11-25. */\n" + source, encoding="utf-8")
names = [line.strip() for line in (vendor / "src/windows/proxy/proxylist.txt").read_text().splitlines() if line.strip()]
proxy = ['#include <windows.h>']
proxy += [f'FARPROC awesome_proxy_{name};' for name in names]
proxy += ['void load_functions(void *dll) {']
proxy += [f'awesome_proxy_{name} = GetProcAddress((HMODULE)dll, "{name}");' for name in names]
proxy += ['}']

proxy += [f'__attribute__((naked)) void exp_{name}(void) {{ __asm__("jmp *_awesome_proxy_{name}"); }}' for name in names]
(output / "proxy.c").write_text("\n".join(proxy) + "\n")
(output / "version.def").write_text("LIBRARY version\nEXPORTS\n" + "".join(f"    {name}=exp_{name}\n" for name in names))
print("Prepared UnityDoorstop 4.6.0 startup integration; upstream vendor sources unchanged.")
