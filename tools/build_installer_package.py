import json
from pathlib import Path
import zipfile

project = Path(__file__).absolute().parents[1]
build = project / 'build'
dist = project / 'dist'
dist.mkdir(exist_ok=True)
payload = {}
specs = []
def add(relative, source, mutable=False):
    data = Path(source).read_bytes()
    relative = relative.replace('\\', '/')
    if relative.lower() in {key.lower() for key in payload}:
        raise ValueError('Duplicate package path: ' + relative)
    payload[relative] = data
    specs.append({'Relative': relative, 'Mutable': mutable})

add('version.dll', build/'version.dll')
add('doorstop_config.ini', project/'vendor/bepinex/doorstop_config.ini', mutable=True)
add('.doorstop_version', project/'vendor/bepinex/.doorstop_version')
for path in sorted((project/'vendor/bepinex/BepInEx/core').glob('*')):
    if path.is_file():
        add('BepInEx/core/'+path.name, path)
add('BepInEx/plugins/awesome_vr/awesome_vr.dll', build/'awesome_vr.dll')
add('BepInEx/config/BepInEx.cfg', project/'config/BepInEx.cfg', mutable=True)
add('BepInEx/config/awesome_vr.cfg', project/'installer/default-config.cfg', mutable=True)
add('Awesome_Data/Plugins/awesome_vr_native.dll', build/'awesome_vr_native.dll')
for path in sorted((project/'input').glob('*.json')):
    add('awesome_vr/input/'+path.name, path)
add('awesome_vr/runtime/openvr_api.dll', project/'vendor/openvr-1.26.7/openvr_api.dll')
add('awesome_vr/runtime/LICENSE.txt', project/'LICENSE')
add('awesome_vr/runtime/THIRD-PARTY-NOTICES.txt', project/'THIRD-PARTY-NOTICES.txt')
shared_loader = (project/'vendor/bepinex/winhttp.dll').read_bytes()
manifest = {'Version': 1, 'Product': 'awesome_vr', 'Files': specs}

def write_zip(path, members):
    with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(members.items()):
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)

members = {'manifest.json': json.dumps(manifest, indent=2).encode('utf-8')}
members['support/BepInEx-version.dll'] = shared_loader
members.update({'payload/'+name: data for name, data in payload.items()})
write_zip(build/'installer-payload.zip', members)
source = {}
for name in ('src', 'native', 'tools', 'installer', 'input', 'config', 'assets', 'docs'):
    for path in (project/name).rglob('*'):
        if path.is_file() and '__pycache__' not in path.parts and path.suffix != '.glb':
            source['awesome_vr/'+path.relative_to(project).as_posix()] = path.read_bytes()
for name in ('README.md', '.gitignore', 'THIRD-PARTY-NOTICES.txt'):
    source['awesome_vr/'+name] = (project/name).read_bytes()
source['awesome_vr/vendor/doorstop-source.zip'] = (project/'vendor/doorstop-source.zip').read_bytes()
for name in ('entrypoint.c', 'proxy.c', 'version.def'):
    source['awesome_vr/doorstop-build/'+name] = (build/'startup-loader'/name).read_bytes()
license_file = project/'LICENSE'
if license_file.exists():
    source['awesome_vr/LICENSE'] = license_file.read_bytes()
write_zip(dist/'awesome_vr-0.1-source.zip', source)
print('Prepared installer payload and source archive.')
