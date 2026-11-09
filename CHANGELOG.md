Changelog

Added

Script language
- S spam autoslide. The autofunc now has two forms, lowercase s holds Slide on every eligible grounded tick, and uppercase S presses Slide on alternate eligible ticks (one frame on, one frame off). Both use the same grounded/jump/SSJ gate; the first eligible tick is an on tick and losing eligibility resets the phase. S is the only case-sensitive autofunc.
- m / mN SSJ macro autofunc. On the landing tick it holds Slide for one tick, releases it for a blank tick, then jumps. m/m0 is the perfect 3-tick Super Slide Jump; m1 and m2 add blank wait ticks for the weaker 4- and 5-tick boosts. The macro can make field 1 eleven characters long.
- fist <arm> special frame. Changes the equipped arm on the first tick of the next framebulk by injecting the game's normal x change-fist action. It injects nothing when the requested arm is already out, and it is deliberately excluded from the bake hash.
- bake_start_nme [name]. A bake block that records the same player, RNG, enemy, projectile, charge and timing state as bake_start, but leaves doors and streamed map rooms live instead of baking them. The block mode is part of the bake hash.

Savestate completeness
- Effect and coroutine state. TrailRenderer and LineRenderer positions/settings, audio filter state, live particle buffers/module settings and tracked coroutine instances are captured and restored.
- Input and physics state. Full InputSource fields, held-button state with press edges suppressed, global gravity/solver settings, Physics.simulationMode, the 32x32 layer-collision matrix and the saved fixed-step phase.
- Map progression. Full RoomManager.visitedRooms order, RoomArea and door-streamed room state are preserved.
- Enemy hierarchy and navigation. Enemy root scale/parent, child active flags and poses, NavMeshAgent state including dormant agents, and NavMeshObstacles are restored.
- Ground and capsule state. NewMovement.gc contacts, onGround, canJump, slide/dash fields and the exact player capsule are restored, then the game's own ground check runs after the pose is in PhysX.
- Cleanup of state created after the save. Gore, fire, fluid, stain and other spawned components that did not exist at capture are removed; objects that existed at capture are kept.

Optimizer
- New search strategies. Sweep + Random (the new default), deterministic Sweep Only, the previous Random Only, and Adaptive ML.
- Deterministic sweep. Walks one framebulk at a time, retries the same bulk after an improvement, and halves the step size after a pass with no gain. It is repeatable and suited to polishing a known route.
- Adaptive ML. A UCB1 multi-armed bandit learns which mutation operators pay off, an elite pool keeps up to 12 diverse routes, and generations breed by mutation or temporal crossover. Elite consensus, differential trend projection, mined input rhythms and stagnation pivots help it leave local optima.
- Geometry awareness. After a wall bonk, read-only whisker probes around the impact find the nearest clearance or corner/doorway. The optimizer can steer subsequent candidates through it instead of rolling blind yaw. An optional whisker fan visualizes wall hits, the chosen clearance and the corner threshold.

Video and diagnostics
- Baked-input video recording. The Recording tab can enable baked playback for a render, with a permanent NOT LEGITIMATE warning; starting a baked video starts playback when needed.
- Full determinism diagnostics. Per-tick rows now include the fixed-step phase, steps, grounded state, camera angles, RNG hash and a checkpoint/restored comparison.

Changed

- The bake hash no longer includes the top-level fist header or mid-script fist rows, so changing arms does not force a re-bake. Existing sidecars from before this change are accepted through a legacy fist hash when the script has the matching header.
- bake_start_nme block mode is part of the hash, so switching a block to or from NME forces a re-record.
- Savestate loads can reset the level to the last map checkpoint first (General tab, Savestates card, **Load the last map checkpoint before a savestate**; off by default). The load then respawns at the checkpoint the player touched last, so rooms, arenas, doors and checkpoint-scoped objects go back to their pre-fight state before the saved values are written. It now runs the same `StatsManager.Restart` path as the pause menu's CHECKPOINT button (arena music ends, `StatsManager.checkpointRestart` fires and the restart is counted before `CheckPoint.OnRespawn` rebuilds the rooms), instead of calling `OnRespawn` on its own, and it is skipped when no banner has been touched or the level is already finished. The reload is triggered when the load is requested and the savestate is applied two frames later, so the game's deferred destruction of the old room clones (`Object.Destroy` flushes at the end of the respawn frame) has completed before the restore writes into the world. The rooms the reload builds are protected from the restore's orphan sweeps, which otherwise delete the fresh enemies and hazards because the savestate has never seen them. With the same setting on, the pause menu's CHECKPOINT button now loads the game's checkpoint and then restores the active savestate on top of it (the game's own `OptionsManager.RestartCheckpoint` is hooked; the restored state keeps the freshened rooms). It applies to slot loads and norefresh resumes, not to rewinds or optimizer candidate restores.
- Savestate loads restore the saved Time.timeScale and hitstop state, and align Unity's fixed-step phase before restoring.
- The HUD status line now says that Time.timeScale is game-owned and only loads restore the saved value.
- The optimizer's path overlay no longer depends on OnGUI being called; it updates from the frame loop.
- Every slider in the window draws the value it is set to on its own track, on the side the thumb is not, so a setting can be read without a separate number column. The HUD sliders lose their trailing number column and keep that width on the track.

Fixed

- Arena triggers are re-armed when a savestate taken before a fight is loaded. ULTRAKILL destroys the `ActivateArena` trigger after it spawns the first wave and destroys each `ActivateNextWave` when its wave ends, so a load had no component left to write the saved state into and walking back into the trigger did nothing. A user load now re-adds the destroyed arena scripts from the captured level elements, restores their saved fields, runs the game's own `Arena.AutoSetupWaves` to rewire doors, first-wave enemies and next-wave links, resets the trigger/wave counters to their pre-fight values, and switches the wave enemies off exactly as `Arena.Awake` does. Walking back into the trigger then starts the fight again. Mid-fight and post-fight states contain no trigger snapshot and are left alone, and rewinds/optimizer restores never rebuild scripts. The map checkpoint option and its code are unchanged and independent.
- V1 no longer drifts, gains velocity or gets re-posed by game scripts during a savestate load.
- The default `norefresh` capture offset is -1: the checkpoint's state is cached one tick later than the marker, the boundary that lines the resumed world up with continuous playback. The NoRefresh card's **Capture offset** slider still moves that boundary between -3 and +3 ticks and clears the cache when it changes, so an existing config that kept the old 0 can be dialled in the menu.
- A `norefresh` checkpoint taken mid-slide resumes at the same height: the load hold now re-applies the saved capsule shape, slide/crouch fields and ground flags together with the pose, so the first resumed tick cannot stand V1 up (about 0.8 units, with the velocity untouched) and leave the rest of the run at a different position.
- A `norefresh` resume no longer starts one frame of clock ahead: a checkpoint cached on the frame after its boundary (the default capture offset, -1) has that frame's Update accounting - the level timer/framecount, weapon recharge and style freshness - rewound out of the state, so the resumed run counts exactly the frames continuous playback does instead of running one ahead.
- **Bake the first frames after a checkpoint** (NoRefresh card, off by default): caching a norefresh checkpoint also records the first ticks into a temporary `.ukbake` in the temp folder, stamped with the script's own hash over that tick range. A resume writes those frames over the same ticks first, so the run re-syncs to the continuous trajectory instead of drifting, then continues live. The window is short on purpose: **Sync bake ticks** defaults to 30 ticks (about a quarter second) instead of baking everything to the next block or the end of the script; 0 restores the old full-range behaviour. The script is never touched, and editing the covered frames changes the hash so the resume simply ignores the stale bake. Applied baked state marks the run as not submittable.
- A **norefresh** resume now runs an automatic `bake_end` for the bake block it lands in: with **Bake the first ticks after a checkpoint** on, the block closes when the **Sync bake ticks** window ends, as if a `bake_end` stood there, and the rest of the block plays live. Only norefresh resumes do this; normal playback, savestate loads and rewinds still use the script's own markers. The script, its hashes and its `.ukbake` files are untouched, and a partial recording cut short this way is discarded rather than written with a mismatched range.
- **`norefresh` resume bulk frames** debug slider (NoRefresh card, next to Capture offset): adds or removes ticks in the framebulk a resume starts in, for the resumed run only. It is a transient timeline adjustment, so the script, its checksum and its saved frame counts stay as written.
- norefresh resumes at the correct tick and script time instead of replaying from the wrong boundary.
- Saving a state while V1 is held no longer records a kinematic player with zero velocity.
- Loading a state at the final tick no longer leaves V1 kinematic after playback ends.
- Loading in Idle no longer leaves V1 frozen forever.
- Ground contacts, can-jump state and the crouched capsule shape survive a restore.
- Destroyed enemies and projectiles can be rebuilt with their saved hierarchy, scale, navigation and child state.
- Gore, fire, fluid and stain objects created after the save are cleaned up on load.
- An optimizer run no longer stops when the ULTRAKILL window loses focus.

Known issues

- Savestates remain experimental with maps, the movement is still not exactly 100% done on savestates so expect them to NOT BE FULLY CONSISTANT.
- The optimizer's Full FFF Replay rollback option is present but still uses the master savestate at Start.
- Adaptive ML is a heuristic. It improves the search, but it does not guarantee a global optimum.