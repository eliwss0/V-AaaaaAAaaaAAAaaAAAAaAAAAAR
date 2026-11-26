import hashlib
from pathlib import Path
import sys
import urllib.request
import zipfile

project = Path(__file__).absolute().parents[1]


def download(archive, url, expected, timeout):
    if archive.exists():
        data = archive.read_bytes()
    else:
        with urllib.request.urlopen(url, timeout=timeout) as response:
            data = response.read()
    if hashlib.sha256(data).hexdigest() != expected:
        raise RuntimeError('Dependency archive SHA-256 mismatch: ' + archive.name)
    if not archive.exists():
        archive.parent.mkdir(parents=True, exist_ok=True)
        archive.write_bytes(data)


def fetch_bepinex():
    version = '5.4.23.6'
    name = f'BepInEx_win_x86_{version}.zip'
    url = f'https://github.com/BepInEx/BepInEx/releases/download/v{version}/{name}'
    expected = 'a4024dafaca5d3784097e34b3c2921415030c0b461e52f30053ed8914be9622d'
    vendor = project / 'vendor'
    vendor.mkdir(exist_ok=True)
    archive = vendor / name
    download(archive, url, expected, 60)
    stage = vendor / 'bepinex'
    stage.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as package:
        for entry in package.infolist():
            target = (stage / entry.filename).resolve()
            if not target.is_relative_to(stage.resolve()):
                raise RuntimeError('Unsafe archive member')
        package.extractall(stage)
    print(f'Verified and staged {name}; no game files changed.')


def fetch_doorstop():
    archive = project / "vendor/doorstop-source.zip"
    expected = "95a3d62f5cb2fc792f1dfc61e6b09f78e685e0966401e6f9e6fa58540249e0ea"
    download(archive, "https://codeload.github.com/NeighTools/UnityDoorstop/zip/refs/tags/v4.6.0", expected, 30)
    destination = project / "vendor/doorstop-source"
    if not (destination / "UnityDoorstop-4.6.0/src/windows/entrypoint.c").exists():
        destination.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(archive) as source:
            for member in source.infolist():
                (destination / member.filename).resolve().relative_to(destination.resolve())
            source.extractall(destination)
    print("Pinned UnityDoorstop 4.6.0 source verified.")


def fetch_openvr():
    base = 'https://raw.githubusercontent.com/ValveSoftware/openvr/v1.26.7/'
    destination = project / 'vendor/openvr-1.26.7'
    files = (
        ('headers/openvr_capi.h', 'openvr_capi_1_26_7.h', 'c07ea9facb3e792c6e216e3ac294e08e52b0d4f84eaab31ca95629581d0f303c'),
        ('bin/win32/openvr_api.dll', 'openvr_api.dll', 'd27d9aa2459e7533c3a6cf18a8cc2ff1d5cb2249115f42ba9a3ce1fe2bc4662b'),
        ('LICENSE', 'LICENSE', 'f56ff606104d4ef18e617921a75c73ad73b5a1a1d70c69590c29de16919e04ad'),
    )
    for remote, name, expected in files:
        download(destination / name, base + remote, expected, 60)


if __name__ == '__main__':
    if sys.argv[1:] == ['bepinex']:
        fetch_bepinex()
    elif sys.argv[1:] == ['openvr']:
        fetch_openvr()
    elif sys.argv[1:] == ['doorstop']:
        fetch_doorstop()
    else:
        sys.exit('Choose bepinex, doorstop or openvr.')
