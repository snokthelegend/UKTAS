Changelog 1.2.1

- Added S spam autoslide and m/m(number) SSJ macros to the script language
- Added fist arm-switch frame that does not invalidate bake hashes, using `fist` command mid-script lets you also switch fists from the knuckleblaster to the feedbacker however you want.
- Added bake_start_nme to bake states while keeping doors and streamed rooms live
- Overhauled savestate capture: CORRECTLY restore player movement exactly, *WARNING* there are still  some bugs with savestates, but i can say confidently savestates are consistant for movement when TASing
- Added option to reload map checkpoints before restoring savestates to reset rooms cleanly
- Added new optimizer search modes: Sweep + Random, Sweep Only, and Adaptive ML
- Added geometry awareness to the optimizer to pathfind around walls and corners after bonks
- Fixed optimizer stopping when the game window loses focus
- Fixed NoRefresh checkpoint drift and timing offsets with optional post-checkpoint auto-baking
- Added baked-input video recording support