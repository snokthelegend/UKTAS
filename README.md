# UKTAS: ULTRAKILL TAS Tool

A deterministic, tick-based tool-assisted speedrun (TAS) tool for [ULTRAKILL](https://store.steampowered.com/app/1229490/ULTRAKILL/), built as a BepInEx 5 plugin.

You write down which inputs happen on every tick, and the game replays them exactly the same way every time. UKTAS includes a script editor with syntax highlighting, a recorder, savestates, a video renderer, and an on-screen HUD.

> **Status: work in progress.** UKTAS is usable, but it still has known bugs. Issues and pull requests are welcome.

## Features

- **Script editor** for the `.uktas` script language, with syntax highlighting and an in-game window (`Insert`).
- **Deterministic replay** of recorded inputs, tick by tick.
- **Savestates** that capture and restore game state, including enemies, weapons, physics, and timing.
- **Recorder and video renderer** (uses `ffmpeg`).
- **On-screen HUD** with entity inspection and state readouts.
- **Optimizer** tools for searching input sequences.
- **Trajectory and coin path** visualization.

## Install (players)

Prebuilt downloads are on the [Releases](../../releases) page.

1. Install BepInEx 5.4.x (x64) next to `ULTRAKILL.exe`. Or run `install.bat` from the release, which can download it for you.
2. Copy `UltrakillTAS.dll` into `BepInEx/plugins/UltrakillTAS/`.
3. Start ULTRAKILL. Press `Insert` to open the TAS window.

The full guide is in [`docs/index.html`](docs/index.html) (also published on GitHub Pages, see the link in the repo sidebar). It has a "Start here" section for new users and a reference for the script format.

## Build from source

Requirements:

- Windows, with ULTRAKILL installed through Steam
- .NET SDK 8 (x64), or Visual Studio with the .NET Framework 4.7.2 targeting pack
- BepInEx 5.4.23.x

Steps:

1. Copy `build_config.example.ini` to `build_config.ini` and set `GAME_DIR` to your ULTRAKILL folder.
2. Run `build_and_deploy.bat`. It builds the plugin and copies it into the game folder.

Or open `UltrakillTAS.csproj` in an IDE and build it. Game assemblies are referenced from `GAME_DIR\ULTRAKILL_Data\Managed`.

## Script language

Scripts are plain text files with the `.uktas` extension. The first line is always `version 1`.

```text
version 1
host_frametime 0.008
seed 3
start_type load_level 8-3
difficulty brutal
```

See [`UKTAS_FORMAT_SPEC.txt`](UKTAS_FORMAT_SPEC.txt) for the format and [`UKTAS_LANG_FORMAT.md`](UKTAS_LANG_FORMAT.md) for the language. Example scripts are in [`examples/`](examples/).

## Repository layout

| Path | What |
| --- | --- |
| `Plugin.cs` | BepInEx entry point |
| `GameBridge.cs` | Reads and writes game state |
| `TasEngine.cs`, `TasScript.cs`, `TasRuntime.cs` | Script parsing and replay |
| `SaveStateManager*.cs` | Savestate capture and restore |
| `GuiManager*.cs` | In-game UI, HUD, editor tab, theme |
| `InputPatcher*.cs` | Input injection and key bindings |
| `VideoRecorder.cs` | Recording and rendering through ffmpeg |
| `TasOptimizer*.cs` | Input search |
| `docs/` | User guide (GitHub Pages source) |
| `examples/` | Example `.uktas` scripts |

## Known issues

- TODO: replace this list with your real known-bug list.
- Video rendering needs `ffmpeg` on your PATH.

## Contributing

Bug reports and pull requests are welcome. Open an issue first for large changes. Please describe your ULTRAKILL version and steps to reproduce.

## Disclaimer

UKTAS is an unofficial fan tool. It is not made by or affiliated with the ULTRAKILL developers, and it does not include any game assets or code.

## License

<!-- Pick one and add a LICENSE file. See the notes below. -->
TBD. See the LICENSE file once added.

## Credits

Developed by snok.
