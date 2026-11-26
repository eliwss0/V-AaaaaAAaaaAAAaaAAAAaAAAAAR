import argparse
from contextlib import contextmanager
from html import escape
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile


SETUP_TITLE = "V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!! setup"
APP_ID = "15560"
REG_SECTION = r"Software\\Wine\\AppDefaults\\Awesome.exe\\DllOverrides"


def relative_path(name):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name:
        raise ValueError("Invalid package path")
    parts = name.split("/")
    if any(p in ("", ".", "..") or p.rstrip(" .") != p for p in parts):
        raise ValueError("Invalid package path: " + name)
    return PurePosixPath(name)


def allowed(name):
    return name in {
        "version.dll", "doorstop_config.ini", ".doorstop_version",
        "Awesome_Data/Plugins/awesome_vr_native.dll",
        "BepInEx/plugins/awesome_vr/awesome_vr.dll",
        "BepInEx/config/BepInEx.cfg", "BepInEx/config/awesome_vr.cfg",
    } or name.startswith(("BepInEx/core/", "awesome_vr/input/", "awesome_vr/runtime/"))


class Package:
    def __init__(self, data):
        self.files = {}
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            if len(archive.namelist()) != len(set(archive.namelist())):
                raise ValueError("Duplicate package entries")
            if len(archive.infolist()) > 1024 or sum(x.file_size for x in archive.infolist()) > 128 * 1024 * 1024:
                raise ValueError("Package is too large")
            self.index = json.loads(archive.read("manifest.json"))
            if self.index.get("Version") != 1 or self.index.get("Product") != "awesome_vr":
                raise ValueError("Unsupported package")
            seen = set()
            for spec in self.index["Files"]:
                name = spec["Relative"]
                relative_path(name)
                if not allowed(name) or name.lower() in seen:
                    raise ValueError("Invalid package entry: " + name)
                seen.add(name.lower())
                content = archive.read("payload/" + name)
                self.files[name] = content
            if not {"version.dll", "BepInEx/plugins/awesome_vr/awesome_vr.dll"} <= self.files.keys():
                raise ValueError("Incomplete package")
            self.shared_loader = archive.read("support/BepInEx-version.dll")


def safe_path(root, name):
    relative_path(name)
    current = root
    for part in name.split("/"):
        current = current / part
        if current.is_symlink():
            raise ValueError("Installation paths cannot contain links: " + name)
        if current.exists() and not (current.is_dir() or current.is_file()):
            raise ValueError("Unsupported installation file: " + name)
    return current



def steam_frame():
    for path in (Path("/etc/os-release"), Path("/usr/lib/os-release")):
        try:
            release = path.read_text(encoding="utf-8")
        except OSError:
            continue
        fields = dict(line.split("=", 1) for line in release.splitlines()
                      if line.startswith(("ID=", "VARIANT_ID=")))
        return fields.get("ID", "").strip("\"'") == "steamos" and fields.get("VARIANT_ID", "").strip("\"'") == "vr"
    return False


def configure_frame_chooser(config):
    newline = b"\r\n" if b"\r\n" in config else b"\n"
    section = re.search(rb"(?mi)^\[AwesomeVR\][^\r\n]*", config)
    setting = b"SteamFrame = true"
    if section is None:
        return config.rstrip(b"\r\n") + newline * 2 + b"[AwesomeVR]" + newline + setting + newline
    end = re.search(rb"(?m)^\[", config[section.end():])
    end = len(config) if end is None else section.end() + end.start()
    body = config[section.end():end]
    body = re.sub(rb"(?mi)^SkipModeChooser[ \t]*=[^\r\n]*(?:\r?\n|$)", b"", body)
    body, count = re.subn(rb"(?mi)^SteamFrame[ \t]*=[^\r\n]*", setting, body)
    if count == 0:
        body = newline + setting + body
    return config[:section.end()] + body + config[end:]


def steam_roots():
    home = Path.home()
    candidates = [home / ".steam/steam", home / ".steam/root", home / ".local/share/Steam",
                  home / ".var/app/com.valvesoftware.Steam/.local/share/Steam"]
    if os.environ.get("STEAM_COMPAT_CLIENT_INSTALL_PATH"):
        candidates.insert(0, Path(os.environ["STEAM_COMPAT_CLIENT_INSTALL_PATH"]))
    return list(dict.fromkeys(p.resolve() for p in candidates if p.is_dir()))


def steam_libraries(roots):
    libraries = list(roots)
    for root in roots:
        config = root / "steamapps/libraryfolders.vdf"
        try:
            if config.is_file():
                for value in re.findall(r'"path"\s+"((?:\\.|[^"\\])*)"', config.read_text(encoding="utf-8")):
                    libraries.append(Path(value.replace(r"\\", "\\").replace(r'\"', '"')).resolve())
        except (OSError, UnicodeError):
            continue
    try:
        mount_lines = Path("/proc/self/mountinfo").read_text(encoding="utf-8").splitlines()
    except OSError:
        mount_lines = []
    for line in mount_lines:
        fields = line.split()
        if len(fields) < 6:
            continue
        mount = Path(re.sub(r"\\([0-7]{3})", lambda match: chr(int(match[1], 8)), fields[4]))
        for candidate in (mount, mount / "SteamLibrary", mount / "Steam", mount / "steamapps"):
            library = candidate.parent if candidate.name == "steamapps" else candidate
            try:
                if (library / "steamapps").is_dir():
                    libraries.append(library.resolve())
            except OSError:
                continue
    return list(dict.fromkeys(libraries))


def find_games(roots):
    games = []
    for library in steam_libraries(roots):
        candidates = [library / "steamapps/common/Awesome"]
        manifest = library / ("steamapps/appmanifest_" + APP_ID + ".acf")
        if manifest.is_file():
            match = re.search(r'"installdir"\s+"([^"/\\]+)"', manifest.read_text(encoding="utf-8"))
            if match:
                candidates.insert(0, library / "steamapps/common" / match[1])
        for game in candidates:
            if (game / "Awesome.exe").is_file() and (game / "Awesome_Data/Managed/Assembly-CSharp.dll").is_file():
                games.append(game.resolve())
    return list(dict.fromkeys(games))


def find_prefix(game, roots):
    libraries = steam_libraries(roots)
    if game.parent.name == "common" and game.parent.parent.name == "steamapps":
        libraries.insert(0, game.parent.parent.parent)
    for library in dict.fromkeys(libraries):
        prefix = library / ("steamapps/compatdata/" + APP_ID + "/pfx")
        if (prefix / "user.reg").is_file():
            return prefix.resolve()
    raise ValueError("The game's Proton prefix was not found. Select Proton in Steam's Compatibility settings, run the regular game once, close it, then retry. Launch options need no changes.")


def ensure_idle(prefix):
    proc = Path("/proc")
    if not proc.is_dir():
        return
    for entry in proc.iterdir():
        if not entry.name.isdigit():
            continue
        try:
            command = (entry / "cmdline").read_bytes().split(b"\0")
            if any(Path(os.fsdecode(x).replace("\\", "/")).name.lower() == "awesome.exe" for x in command if x):
                raise ValueError("Close Awesome before using setup.")
            if prefix:
                environment = (entry / "environ").read_bytes().split(b"\0")
                for item in environment:
                    if item.startswith(b"WINEPREFIX=") and Path(os.fsdecode(item[11:])).resolve() == prefix:
                        raise ValueError("Close applications using this game's Proton prefix before using setup.")
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue


def registry_update(text):
    if not text.startswith("WINE REGISTRY Version 2"):
        raise ValueError("The Proton user registry has an unsupported format")
    section = None
    for match in re.finditer(r"(?m)^\[([^\r\n]+)\][^\r\n]*", text):
        if match[1].lower() == REG_SECTION.lower():
            section = match
            break
    if section:
        end = text.find("\n[", section.end())
        end = len(text) if end < 0 else end
        body = text[section.end():end]
        value = re.search(r'(?mi)^"version"\s*=([^\r\n]*)', body)
        if value:
            if value[1].strip().lower() not in ('"native,builtin"', '"native;builtin"'):
                raise ValueError("Awesome.exe already has a conflicting version.dll override; its settings were preserved.")
            return text
        return text[:section.end()] + '\n"version"="native,builtin"' + text[section.end():]
    return text.rstrip() + "\n\n[" + REG_SECTION + ']\n"version"="native,builtin"\n'


def write_registry(path, text):
    output = tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False)
    pending = Path(output.name)
    try:
        with output:
            output.write(text)
        pending.chmod(path.stat().st_mode & 0o777)
        pending.replace(path)
    finally:
        pending.unlink(missing_ok=True)


def remove_registry_override(text):
    for section in re.finditer(r"(?m)^\[([^\r\n]+)\][^\r\n]*", text):
        if section[1].lower() != REG_SECTION.lower():
            continue
        end = text.find("\n[", section.end())
        end = len(text) if end < 0 else end
        body = text[section.end():end]
        match = re.search(r'(?m)^"version"="native,builtin"\n?', body)
        if match:
            return text[:section.end() + match.start()] + text[section.end() + match.end():]
        break
    return text


@contextmanager
def prefix_lock(prefix):
    if prefix is None:
        yield
        return
    import fcntl
    lock = safe_path(prefix.parent, "pfx.lock")
    descriptor = os.open(lock, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ValueError("Proton is preparing this game's prefix. Wait for it to finish before using setup.") from error
        yield
    finally:
        os.close(descriptor)


@contextmanager
def setup_lock(game):
    import fcntl
    descriptor = os.open(game, os.O_RDONLY | os.O_DIRECTORY)
    try:
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ValueError("Another setup is running for this game") from error
        yield
    finally:
        os.close(descriptor)


class Installer:
    def __init__(self, game, package):
        self.game = Path(game).resolve(strict=True)
        self.package = package

    def target(self, name):
        return safe_path(self.game, name)

    def check_game(self):
        if not self.target("Awesome.exe").is_file() or not self.target("Awesome_Data/Managed/Assembly-CSharp.dll").is_file():
            raise ValueError("Select the Windows game folder containing Awesome.exe and Awesome_Data. For a Linux installation, select Proton in Steam's Compatibility settings to obtain the Windows build.")
    def other_mods(self):
        found = self.target("winhttp.dll").exists()
        for name in ("BepInEx/plugins", "BepInEx/patchers"):
            base = self.target(name)
            if not base.exists():
                continue
            for directory, dirs, files in os.walk(base, followlinks=False):
                for part in dirs + files:
                    path = self.target((Path(directory) / part).relative_to(self.game).as_posix())
                    if path.suffix.lower() == ".dll" and path != self.target("BepInEx/plugins/awesome_vr/awesome_vr.dll"):
                        found = True
        return found

    def install(self, prefix):
        self.check_game()
        registry = safe_path(Path(prefix).resolve(strict=True), "user.reg")
        original = registry.read_text(encoding="utf-8")
        updated = registry_update(original)
        frame = steam_frame()
        for spec in sorted(self.package.index["Files"], key=lambda x: x["Relative"] == "version.dll"):
            name = spec["Relative"]
            path = self.target(name)
            content = self.package.files[name]
            if spec.get("Mutable") and path.exists():
                if frame and name == "doorstop_config.ini":
                    original_config = path.read_bytes()
                    updated_config = configure_frame_chooser(original_config)
                    if updated_config != original_config:
                        path.write_bytes(updated_config)
                continue
            if frame and name == "doorstop_config.ini":
                content = configure_frame_chooser(content)
            backup = self.target("backup/" + spec["Relative"])
            absent = self.target("backup/.absent/" + spec["Relative"])
            if not backup.exists() and not absent.exists():
                if path.is_file():
                    backup.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(path, backup)
                else:
                    absent.parent.mkdir(parents=True, exist_ok=True)
                    absent.touch()
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        if updated != original:
            marker = self.target("backup/.proton-override")
            marker.write_text(str(prefix), encoding="utf-8")
            write_registry(registry, updated)
        return "Installed V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!!"

    def uninstall(self, prefix):
        self.check_game()
        marker = self.target("backup/.proton-override")
        if marker.is_file() and prefix is not None and str(prefix) == marker.read_text(encoding="utf-8"):
            registry = safe_path(prefix, "user.reg")
            original = registry.read_text(encoding="utf-8")
            updated = remove_registry_override(original)
            if updated != original:
                write_registry(registry, updated)
            marker.unlink()
        shared = self.other_mods()
        for spec in self.package.index["Files"]:
            self.restore_file(spec["Relative"], spec.get("Mutable"), shared)
        return "Restored to base game"

    def restore_file(self, name, mutable, shared):
        path = self.target(name)
        backup = self.target("backup/" + name)
        absent = self.target("backup/.absent/" + name)
        if backup.is_file():
            path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(backup, path)
            backup.unlink()
        elif absent.is_file() and not mutable:
            if name == "version.dll" and shared and path.is_file():
                path.write_bytes(self.package.shared_loader)
            elif path.is_file():
                path.unlink()
        if absent.is_file():
            absent.unlink()


def dialog(args):
    result = subprocess.run(args, text=True, stdout=subprocess.PIPE, check=False)
    if result.returncode:
        raise KeyboardInterrupt
    return result.stdout.strip()


def select_game(games, kdialog=None, zenity=None, start=None):
    if games:
        if kdialog:
            choices = [value for i, game in enumerate(games) for value in (str(i), str(game))]
            choices += ["browse", "Browse..."]
            index = dialog([kdialog, "--menu", "Choose the game folder", *choices])
            if index != "browse":
                return games[int(index)]
        elif zenity:
            selection = dialog([zenity, "--list", "--title=Choose the game folder",
                                "--column=Game folder", *map(str, games), "Browse..."])
            if selection != "Browse...":
                return Path(selection)
        if not kdialog and not zenity:
            for i, game in enumerate(games, 1):
                print("%d: %s" % (i, game))
            index = int(input("Choose an installation, or 0 to enter a folder: "))
            if index:
                if not 1 <= index <= len(games):
                    raise ValueError("Invalid installation selection")
                return games[index - 1]
    start = str(start or (games[0] if games else Path.home()))
    if kdialog:
        return Path(dialog([kdialog, "--getexistingdirectory", start, "--title", "Choose the folder containing Awesome.exe"]))
    if zenity:
        return Path(dialog([zenity, "--file-selection", "--directory", "--title=Choose the folder containing Awesome.exe", "--filename=" + start + "/"]))
    return Path(input("Enter the folder containing Awesome.exe: ").strip()).expanduser()


def choose(games):
    kdialog = shutil.which("kdialog") if os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY") else None
    zenity = shutil.which("zenity") if os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY") else None
    if not kdialog and not zenity:
        if not sys.stdin.isatty():
            raise ValueError("No desktop dialog tool found. Run from a terminal with --install or --uninstall and --game-dir PATH.")
        game = select_game(games)
        action = "uninstall" if input("Install [1] or uninstall [2]? ").strip() == "2" else "install"
        return action, game, None, None

    game = games[0] if games else None
    while True:
        if game is None:
            message = "Select your game folder containing Awesome.exe."
        elif len(games) != 1 or game != games[0]:
            message = "Close the game before continuing."
        else:
            message = "Game found automatically. Close the game before continuing."

        choices = [("install", "Install"), ("uninstall", "Uninstall")] if game is not None else []
        choices.append(("browse", "Browse..."))
        if kdialog:
            prompt = "<qt>" + escape(message)
            if game is not None:
                prompt += "<p>" + escape(str(game)) + "</p>"
            action = dialog([kdialog, "--title", SETUP_TITLE, "--cancel-label", "Close",
                             "--menu", prompt + "</qt>", *(value for choice in choices for value in choice)])
        else:
            prompt = escape(message)
            if game is not None:
                prompt += "\n\n" + escape(str(game))
            action = dialog([zenity, "--list", "--title=" + SETUP_TITLE, "--width=650", "--height=300",
                             "--text=" + prompt, "--cancel-label=Close", "--hide-header",
                             "--column=Choice", "--column=Action", "--hide-column=1",
                             "--print-column=1", *(value for choice in choices for value in choice)])
        if action != "browse":
            if game is None or action not in ("install", "uninstall"):
                raise KeyboardInterrupt
            return action, game, kdialog, zenity

        candidates = list(dict.fromkeys(path for path in [game, *games] if path is not None))
        try:
            game = select_game(candidates if len(candidates) > 1 else [], kdialog, zenity,
                               start=game)
        except KeyboardInterrupt:
            continue


def main():
    parser = argparse.ArgumentParser(description="V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!! 0.1 setup for Steam Frame and Linux desktops (Windows game through Proton)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--install", action="store_true")
    mode.add_argument("--uninstall", action="store_true")
    mode.add_argument("--check", action="store_true")
    parser.add_argument("--game-dir", type=Path)
    parser.add_argument("--prefix", type=Path)
    parser.add_argument("--steam-root", type=Path)
    args = parser.parse_args()
    gui = not (args.install or args.uninstall or args.check)
    kdialog = zenity = None
    try:
        roots = [args.steam_root.resolve()] if args.steam_root else steam_roots()
        if gui:
            action, game, kdialog, zenity = choose([args.game_dir.resolve()] if args.game_dir else find_games(roots))
        else:
            games = find_games(roots)
            game = args.game_dir or (games[0] if len(games) == 1 else None)
            if game is None:
                raise ValueError("Choose the game folder with --game-dir PATH")
            action = "uninstall" if args.uninstall else "check" if args.check else "install"
        with zipfile.ZipFile(Path(sys.argv[0])) as archive:
            package = Package(archive.read("payload.zip"))
        installer = Installer(game, package)
        installer.check_game()
        prefix = (args.prefix.resolve() if args.prefix else find_prefix(installer.game, roots)) if action != "uninstall" else None
        if action == "uninstall":
            try:
                prefix = args.prefix.resolve() if args.prefix else find_prefix(installer.game, roots)
            except ValueError:
                pass
        ensure_idle(prefix)
        with setup_lock(installer.game), prefix_lock(prefix):
            ensure_idle(prefix)
            if action == "check":
                registry_update(safe_path(prefix, "user.reg").read_text(encoding="utf-8"))
                message = "Game folder and Proton prefix found. Runtime/headset compatibility has not been verified."
            else:
                message = installer.uninstall(prefix) if action == "uninstall" else installer.install(prefix)
        print(message)
        if kdialog:
            dialog([kdialog, "--msgbox", message, "--title", SETUP_TITLE])
        elif zenity:
            dialog([zenity, "--info", "--title=" + SETUP_TITLE, "--text=" + message])
        return 0
    except KeyboardInterrupt:
        return 0
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        message = str(error)
        print(message, file=sys.stderr)
        if kdialog:
            subprocess.run([kdialog, "--error", message], check=False)
        elif zenity:
            subprocess.run([zenity, "--error", "--text=" + message], check=False)
        return 1


if __name__ == "__main__":
    sys.exit(main())
