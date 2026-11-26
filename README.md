# V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!!

## Why
AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome has a VR mode, but it uses the old proprietary Oculus SDK and is not compatible with modern VR headsets. This mod creates a new VR implementation for modern headsets with extras like immersive controls and Steam Frame standalone support.

## Requirements

- The Windows version of AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome (the native Linux build is **not** supported, but the Windows build runs great through Proton).
- SteamVR with a modern VR headset
- A Windows/Linux PC or a Steam Frame

## Install

Follow the [installation guide](docs/INSTALL.md).

## Play

Start the game from Steam and select "Play AaaaaAAaaaAAAaaAAAAaAAAAA!!! for the Awesome", or run `Awesome.exe`. Then choose VR in the pop up menu.

See [Controls](docs/CONTROLS.md) for movement, gestures, and button shortcuts. Settings are stored in `BepInEx/config/awesome_vr.cfg`.

## Troubleshooting

To enable logging, set `EnableLogging = true` in `awesome_vr.cfg`. Each launch writes one file in `awesome_vr/logs/`. BepInEx writes its own log to `BepInEx/LogOutput.log`.

To disable the mod, set `General/SafeMode` to true in the config (/Awesome/BepInEx/config/awesome_vr.cfg).

## Building from source

Requires Python 3.9+, a C# compiler, a 32 bit MinGW compiler, and the Windows game files.

Set `GAME_DIRECTORY` in [tools/build.py](tools/build.py) to your game folder. The default is `C:\Program Files (x86)\Steam\steamapps\common\Awesome` on Windows.

Run from the repository root:

```sh
python3 tools/build.py
```

The build downloads BepInEx, UnityDoorstop, and OpenVR as needed and writes the installers and source archive to `dist/`.

## Disclaimers
See [third-party notices](THIRD-PARTY-NOTICES.txt) for dependency licenses and model credits.

This mod was programmed with AI assistance.

You should also check out stephen304's VR mod [here](https://github.com/stephen304/AwesomeForTheXR)!
