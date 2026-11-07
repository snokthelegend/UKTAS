# ULTRAKILL TAS Tool (UKTAS 1.1.0)

Developed by snok.

A deterministic, tick-based TAS tool for ULTRAKILL (BepInEx 5 plugin). TAS means *tool-assisted speedrun*:
you write down which keys are pressed on every tick, and the game replays it exactly the same way every time.
It has a script editor, a recorder, savestates, a video renderer and an on-screen HUD.

**The full guide is `UKTAS_DOCS.html`** (same folder as this file; open it in a browser). Its "Start here" part
explains every word and walks you through your first 10 minutes. This README only covers install and the basics.

Version numbers: the tool is **UKTAS 1.1.0** (the plugin version). Scripts start with the line `version 1`
(the script format version). They are separate numbers.

## What is in the zip

| Path | What |
| --- | --- |
| `install.bat` | the installer (double-click this) |
| `install.ps1` | the installer script that `install.bat` runs |
| `README.md` | this file |
| `UKTAS_DOCS.html` | the full guide and script format reference |
| `BepInEx\plugins\UltrakillTAS\UltrakillTAS.dll` | the plugin |
| `BepInEx\plugins\UltrakillTAS\examples\*.uktas` | example scripts |

## Install (nothing to compile)

You need ULTRAKILL on Windows (Steam). The installer takes care of BepInEx.

1. **Extract the whole zip** to a folder (do not run the installer from inside the zip).
2. Close ULTRAKILL.
3. Double-click **`install.bat`**. It ends with "Done. Next steps:".
   * It finds your game folder by itself; if it cannot, it asks for the folder that contains `ULTRAKILL.exe`
     (Steam: right-click ULTRAKILL, Manage, Browse local files).
   * If BepInEx 5 is missing it downloads it from GitHub (checksum verified). **This needs access to
     github.com.** If that fails, use the manual install below.
   * It copies the plugin to `BepInEx\plugins\UltrakillTAS\` and the examples to
     `BepInEx\config\ULTRAKILL-TAS\examples\` (example files that already exist there are not overwritten).
4. Start ULTRAKILL. **The first start after installing is slower than usual.**
   A message shows in the top right: `ULTRAKILL TAS Tool 1.1.0 loaded. Insert opens the TAS window.`
5. Press **Insert** to open the TAS window.

Already know your folder? `install.bat "D:\Games\ULTRAKILL"`. From PowerShell use
`powershell -ExecutionPolicy Bypass -File .\install.ps1` (add `-GameDir "D:\Games\ULTRAKILL"` if needed).

### Manual install (fallback)

1. Install [BepInEx 5.4.x (x64)](https://github.com/BepInEx/BepInEx/releases) by extracting it next to `ULTRAKILL.exe`.
2. Start the game once and close it (this creates `BepInEx\plugins` and `BepInEx\config`).
3. Copy `BepInEx\plugins\UltrakillTAS\UltrakillTAS.dll` from the zip to
   `<game folder>\BepInEx\plugins\UltrakillTAS\UltrakillTAS.dll`.
4. Optional: copy the `examples` folder to `<game folder>\BepInEx\config\ULTRAKILL-TAS\examples\`.

On Linux (Proton) use the Windows BepInEx build and set the launch option
`WINEDLLOVERRIDES="winhttp=n,b" %command%`.

### If it does not appear

* No message in the top right: BepInEx is not loading. Check that `winhttp.dll` sits next to `ULTRAKILL.exe`
  and read `BepInEx\LogOutput.log`.
* Only one `UltrakillTAS.dll` may exist under `BepInEx\plugins` (the installer offers to disable extra copies).

## First steps

1. Press **Insert**. Hover any button or field for a one-line explanation.
2. **While the TAS window is open the game gets no keyboard or mouse input from you, and the viewangles stay
   where they were when it opened.** Close the window (Insert) to play. Esc still reaches the game unless a
   text box, the Script Editor, a popup or a rebind is using it. The switch is "Block gameplay input while
   this window is open" (Keybinds tab, card "Input injection").
3. **General** tab, *Script file* card: click `level_load_from_command.uktas` in the file list (group
   **examples**), or type `examples\level_load_from_command.uktas` and press **Load**. Then press **F5**.
   Relative paths are looked up in `BepInEx\config\ULTRAKILL-TAS\` first.
4. Press F5 to pause, F6 to step one tick, F11 to stop.
5. In new scripts put `start_type restart_mission` (the default `none` plays from wherever the game is).

### Recording (read before pressing F10)

* F10 starts recording **paused**. F5 runs, F6 steps, F10 (or F11, or **Stop recording**) stops and saves.
* **If a script is loaded, recording writes into it** (paused at a tick above 0 it cuts the script off there;
  at tick 0 it adds after the last frame) **and on stop saves over its file, dropping comments.** Because the file
  exists, the window asks first: **Overwrite** saves, **Cancel** keeps recording (paused). Press **New** first,
  or copy the file, before recording over an example.
* With no script file (empty path box), it saves to `BepInEx\config\ULTRAKILL-TAS\recording.uktas`
  (setting `Recording file`), asking first if that file exists.
* Save paths: a relative name goes into `BepInEx\config\ULTRAKILL-TAS\`, `.uktas` is added when there is no
  extension, and a full path is used as typed.

## Main hotkeys

Rebind any of them in the **Keybinds** tab (Esc cancels a rebind; right-click a row or its Rebind button to unbind it).

| Key | Action |
| --- | --- |
| Insert | open / close the TAS window |
| F5 | Play (from the start) / pause / resume; with no script loaded it only shows a message |
| F6 | step one tick (writes input only when Step-record is on; with no script: frame-steps the live game) |
| F7 | fast-forward while held |
| F8 / F9 | save / load savestate in the active slot (General tab, Savestates card) |
| F10 | start / stop recording (starts paused) |
| F11 | stop the run (while recording: stop and save the recording) |
| F4 | rewind to tick 0 |
| F3 | reload script from disk and play |
| F12 | show / hide the HUD |
| F1 | cycle camera mode |
| F2 | freecam on / off |
| Home | reset camera |
| Left Alt | freecam cursor |
| = / - | freecam faster / slower |
| Ctrl+Z | Undo Step in game (in the Script Editor it is normal text undo) |

## Script Editor

A text editor for `.uktas` files with colours and live syntax checks. The **Shortcuts** button lists every
key (the full list is also in `UKTAS_DOCS.html`). The main ones: Ctrl+S save, Ctrl+Z / Ctrl+Y undo / redo,
Ctrl+K / Ctrl+Shift+K comment / uncomment, Ctrl+D duplicate, Ctrl+Shift+L delete line,
Alt+Up/Down or Ctrl+Shift+Up/Down move line, Ctrl+F find, Ctrl+G go to line.

Comments start with `//` or `#`. The Script Editor keeps comments when it saves. Saves made from the in-memory
script (stopping a recording, the Frame Editor, Auto-save, the General tab's Save) drop them.

## Video recording

Video needs `ffmpeg.exe`, which is **not** shipped. Download it yourself and set its path in the
**Recording** tab (config: `ffmpeg path`).

## Files in the game folder

| Path | What |
| --- | --- |
| `BepInEx\plugins\UltrakillTAS\UltrakillTAS.dll` | the plugin |
| `BepInEx\config\ULTRAKILL-TAS\` | your scripts (default folder) |
| `BepInEx\config\ULTRAKILL-TAS\examples\` | example scripts |
| `BepInEx\config\com.tas.ultrakill.cfg` | plugin settings (also editable from the window) |
| `BepInEx\LogOutput.log` | log, useful for bug reports (Diagnostics tab can save a report) |

## Building from source

This applies to the source project folder, not the release zip. With the game installed, `package.bat`
builds the plugin in Release and creates the distribution zip in `dist\` (optional: `package.bat "D:\Games\ULTRAKILL"`).

## Credits

Developed by snok.
Thanks to YALTER (bunnymodXT) and yuu (UNItas) for the tools and ideas this project builds on.
