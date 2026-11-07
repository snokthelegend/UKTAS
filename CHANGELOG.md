# Changelog

## 1.2.0

### Added
- **Auto-show leaderboard** setting (default off): shows the Tab stats panel automatically. Startup tab > Game helpers card.
- **Auto-enable cheats** setting (default off): turns cheats on directly, without clicking the confirmation popup. Startup tab > Game helpers card.
- **Pause on death (kill plane)** toggle (default off).
  - Off: a death is recorded and shown in the HUD. The level is never reloaded automatically.
  - On: the old behavior.

### Fixed
- F4 (return to frame 0) now leaves the spawn door open. Door state is saved and restored on its own.
- Level elements and the style module now save and restore correctly. Other levels may behave differently than before.

### Changed
- Releases are now a zip with a drop-in `ultrakill` folder. Drag it onto your ULTRAKILL folder. `install.bat` and `install.ps1` are gone. See `INSTALL.txt`.

### Known issues
- The F4 door fix and Auto-enable cheats are not verified in game.
- Baked-state files (`*.ukbake`) are now gitignored.
