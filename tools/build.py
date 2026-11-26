import argparse
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

project = Path(__file__).absolute().parents[1]
tools = project / 'tools'
build = project / 'build'
GAME_DIRECTORY = Path(r'C:\Program Files (x86)\Steam\steamapps\common\Awesome')
managed = GAME_DIRECTORY / 'Awesome_Data/Managed'
core = project / 'vendor/bepinex/BepInEx/core'


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True)


def script(name, *args):
    run(sys.executable, tools / name, *args)


def csharp_compiler():
    configured = os.environ.get('CSC')
    if configured:
        return configured
    framework = os.environ.get('WINDIR')
    if framework:
        compiler = Path(framework) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
        if compiler.is_file():
            return str(compiler)
    for name in ('mcs', 'csc'):
        compiler = shutil.which(name)
        if compiler:
            return compiler
    raise RuntimeError('Install Mono or set CSC to a compatible C# compiler.')


def native_compiler():
    configured = os.environ.get('MINGW_CC')
    if configured:
        return configured
    for name in ('i686-w64-mingw32-clang', 'i686-w64-mingw32-gcc'):
        compiler = shutil.which(name)
        if compiler:
            return compiler
    raise RuntimeError('Install an i686 MinGW cross-compiler or set MINGW_CC.')


def plugin(compiler):
    if not (managed / 'Assembly-CSharp.dll').is_file():
        raise RuntimeError(f'Game assemblies not found in {managed}. Set GAME_DIRECTORY in tools/build.py to the game folder.')
    sources = ('AwesomeVRPlugin', 'OpenVRRenderer', 'VRMenu', 'VRMenuLayout', 'VRSettingsPanel',
               'VRConfig', 'InputLogic', 'VRControls', 'VRGameHooks', 'VRPresence', 'VRSpatialHUD', 'VRGlove')
    resources = ('human_hand.mesh', 'spray_can.mesh', 'espresso.mesh', 'vr_settings_label.png')
    refs = [managed / name for name in ('mscorlib.dll', 'System.dll', 'UnityEngine.dll', 'Assembly-CSharp.dll')]
    refs += [core / 'BepInEx.dll', core / '0Harmony.dll']
    args = ['/nologo', '/noconfig', '/target:library', '/platform:x86', '/nostdlib+', '/optimize+', '/warnaserror+',
            '/out:' + str(build / 'awesome_vr.dll')]
    args += ['/reference:' + str(path) for path in refs]
    args += ['/resource:' + str(project / 'assets' / name) + ',awesome_vr.' + name for name in resources]
    run(compiler, *args, *(project / 'src' / (name + '.cs') for name in sources))


def native(compiler):
    script('fetch_dependencies.py', 'openvr')
    run(compiler, '-shared', '-O2', '-I', project / 'vendor/openvr-1.26.7', project / 'native/awesome_vr_native.c',
        project / 'native/dxgi_transfer.c', project / 'native/awesome_vr_native.def', '-ld3d11', '-ldxgi',
        '-o', build / 'awesome_vr_native.dll')
    script('fetch_dependencies.py', 'doorstop')
    script('prepare_startup_loader.py')
    source = project / 'vendor/doorstop-source/UnityDoorstop-4.6.0/src'
    startup = build / 'startup-loader'
    names = ('bootstrap.c', 'config/common.c', 'runtimes/globals.c', 'util/paths.c', 'windows/config.c',
             'windows/util.c', 'windows/wincrt.c')
    run(compiler, '-shared', '-O2', '-fno-builtin', '-fno-stack-protector', '-DUNICODE', '-D_UNICODE',
        '-Wno-ignored-pragmas', '-Wno-macro-redefined', '-Wno-return-type', '-Wno-incompatible-pointer-types',
        '-I', source, '-I', project / 'native', *(source / name for name in names), startup / 'entrypoint.c',
        startup / 'proxy.c', startup / 'version.def', '-nostdlib', '-Wl,--entry,DllEntry@12',
        '-lkernel32', '-lshell32', '-luser32', '-lgdi32', '-o', build / 'version.dll')


def linux_installer():
    dist = project / "dist"
    dist.mkdir(exist_ok=True)
    name = "awesome_vr-0.1-Linux-Setup.pyz"
    output = io.BytesIO()
    output.write(b"#!/usr/bin/env python3\n")
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for entry, content in {
            "__main__.py": (project / "installer/linux_setup.py").read_bytes(),
            "payload.zip": (project / "build/installer-payload.zip").read_bytes(),
        }.items():
            info = zipfile.ZipInfo(entry, (1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content)
    (dist / name).write_bytes(output.getvalue())
    (dist / name).chmod(0o755)
    print("Built Linux installer.")


def installers(compiler):
    script('build_installer_package.py')
    args = ['/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warnaserror+',
            '/out:' + str(project / 'dist/awesome_vr-0.1-Windows-Setup.exe'),
            '/resource:' + str(build / 'installer-payload.zip') + ',awesome_vr.payload.zip']
    args += ['/reference:' + name for name in ('System.Windows.Forms.dll', 'System.Drawing.dll',
                                               'System.Runtime.Serialization.dll', 'System.IO.Compression.dll')]
    run(compiler, *args, *(project / 'installer' / name for name in ('InstallerEngine.cs', 'GameDiscovery.cs', 'WindowsInstaller.cs')))
    linux_installer()


def main():
    parser = argparse.ArgumentParser(description='Build V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!!.')
    parser.add_argument('target', nargs='?', choices=('plugin', 'native', 'all'), default='all')
    args = parser.parse_args()
    build.mkdir(exist_ok=True)
    if args.target in ('plugin', 'all'):
        script('fetch_dependencies.py', 'bepinex')
        compiler = csharp_compiler()
        plugin(compiler)
    if args.target in ('native', 'all'):
        native(native_compiler())
    if args.target == 'all':
        installers(compiler)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
