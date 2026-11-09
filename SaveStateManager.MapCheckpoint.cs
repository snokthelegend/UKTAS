using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// "Load the map checkpoint first" for savestate loads.
    ///
    /// A savestate only writes the objects and values it captured, so anything the player did
    /// after the saved tick stays done: arenas already fought, doors already opened, corpses and
    /// checkpoint-scoped pickups. The game's own respawn rewinds the level instead, and the pause
    /// menu's CHECKPOINT button is the reference behaviour: OptionsManager.RestartCheckpoint
    /// calls StatsManager.Restart, which ends the arena music, raises
    /// StatsManager.checkpointRestart (arenas re-arm), counts the restart and then calls
    /// currentCheckPoint.OnRespawn. That respawn brings back the checkpoint's kills and style,
    /// unlocks its doors, destroys its checkpoint-only objects and, through ResetRoom, replaces
    /// every live room clone with a fresh room prefab, which puts its arenas back to their
    /// pre-fight state.
    ///
    /// Calling OnRespawn on its own, as this used to, skipped everything around it: no arena
    /// music end, no checkpointRestart event and no restart count. The reload also builds brand
    /// new room objects, and the restore that follows sweeps every enemy, projectile and hazard
    /// whose instance id is not in the savestate (CleanupOrphans/CleanupEnemyOrphans). Those new
    /// objects are exactly that, so the sweep used to delete the checkpoint's fresh enemies and
    /// hazards the same frame, and the level looked like it had never reloaded. The reload's
    /// own objects are recorded here and skipped by the sweeps.
    ///
    /// The checkpoint is the one the game itself would respawn at:
    /// StatsManager.currentCheckPoint, which CheckPoint.ActivateCheckPoint sets when the player
    /// touches a banner. A level that has not reached a banner yet has none, and the step is
    /// skipped. A finished level (StatsManager.infoSent) skips it too, exactly as the pause menu
    /// button does.
    ///
    /// Only user-initiated loads run this (slot loads and norefresh resumes). Rewinds and
    /// optimizer candidate restores restore many times per second and must not rebuild rooms.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        /// <summary>
        /// Objects the checkpoint reload created: the fresh room clones and everything inside
        /// them. They have no saved counterpart, so the restore's orphan sweeps must leave them
        /// alone.
        /// </summary>
        readonly HashSet<GameObject> _checkpointFresh = new HashSet<GameObject>();
        /// <summary>The same set, taken just before the game's own CHECKPOINT runs.</summary>
        readonly HashSet<GameObject> _checkpointReloadBefore = new HashSet<GameObject>();

        /// <summary>
        /// Respawn at the last activated map checkpoint, through the game's own restart path.
        /// False when the level has none, is finished, or the game call was unavailable; the
        /// caller carries on with the restore either way.
        /// </summary>
        public bool ResetToMapCheckpoint()
        {
            try
            {
                if (_bridge.FindType("CheckPoint") == null)
                    return false;

                object stats = _bridge.StatsManager;
                if (stats == null)
                    return false;

                object checkpoint = _bridge.GetMember(stats, new[] { "currentCheckPoint" });
                if (!GameBridge.IsAlive(checkpoint))
                {
                    _plugin.LogInfo("No map checkpoint has been activated yet; restoring without a map reload.");
                    return false;
                }

                // The pause menu button only unpauses once the level has finished, so a savestate
                // load does not rebuild the level at that point either.
                object infoSent = _bridge.GetMember(stats, new[] { "infoSent" });
                if (infoSent is bool && (bool)infoSent)
                {
                    _plugin.LogInfo("The level is already finished; skipping the map checkpoint reload.");
                    return false;
                }

                // Which objects exist before the reload, so the ones it builds can be told apart
                // from everything the restore's sweeps are meant to delete.
                BeginCheckpointReloadSnapshot();

                // StatsManager.Restart is what the pause menu's CHECKPOINT button runs after
                // unpausing (OptionsManager.RestartCheckpoint). It ends the arena music, raises
                // checkpointRestart, counts the restart and calls currentCheckPoint.OnRespawn.
                // Fall back to the respawn alone if a game build does not expose Restart.
                bool reloaded = TryInvoke(stats, "Restart");
                if (!reloaded)
                    reloaded = TryInvoke(checkpoint, "OnRespawn");

                // Protect whatever the reload built even if a game call failed halfway: those
                // objects still have no saved counterpart and would only be swept.
                EndCheckpointReloadSnapshot();

                if (!reloaded)
                {
                    _plugin.LogWarning("Map checkpoint respawn could not be invoked; restoring without it.");
                    return false;
                }

                // The reload moves the player, rewrites kills/style and replaces every room
                // clone, so this must be the last world write before the restore starts.
                Component component = checkpoint as Component;
                string name = component != null ? component.name : "(unnamed)";
                _plugin.LogInfo("Reloaded the level at map checkpoint '" + name +
                                "' before restoring the savestate; " + _checkpointFresh.Count +
                                " rebuilt object(s) kept out of the orphan sweeps.");
                _plugin.Toast("Reloaded map checkpoint '" + name + "' before the savestate.", false);
                return true;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Map checkpoint reset skipped: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The game's own CHECKPOINT is about to reload the level (the pause-menu button, which
        /// calls OptionsManager.RestartCheckpoint -> StatsManager.Restart): remember which
        /// sweepable objects exist now, so the ones the respawn builds can be told apart.
        /// </summary>
        public void BeginCheckpointReloadSnapshot()
        {
            _checkpointReloadBefore.Clear();
            CollectSweepable(_checkpointReloadBefore);
        }

        /// <summary>
        /// The game's CHECKPOINT has run. Everything new it built is protected from the orphan
        /// sweeps of the savestate restore that follows it.
        /// </summary>
        public void EndCheckpointReloadSnapshot()
        {
            HashSet<GameObject> after = new HashSet<GameObject>();
            CollectSweepable(after);
            _checkpointFresh.Clear();
            _checkpointFresh.UnionWith(after);
            _checkpointFresh.ExceptWith(_checkpointReloadBefore);
            _checkpointReloadBefore.Clear();
        }

        /// <summary>
        /// Every object the restore's sweeps can delete: enemies, projectiles and transient
        /// hazards. Snapshotting this before and after the reload isolates the fresh checkpoint
        /// copies, whose instance ids the savestate has never seen.
        /// </summary>
        void CollectSweepable(HashSet<GameObject> into)
        {
            CollectSweepable(into, _identifierType);
            for (int i = 0; i < _projectileTypes.Count; i++)
                CollectSweepable(into, _projectileTypes[i]);
            for (int i = 0; i < _transientTypes.Count; i++)
                CollectSweepable(into, _transientTypes[i]);
        }

        static void CollectSweepable(HashSet<GameObject> into, Type type)
        {
            if (type == null)
                return;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
            catch { return; }
            for (int i = 0; i < found.Length; i++)
            {
                Component component = found[i] as Component;
                if (component != null)
                    into.Add(component.gameObject);
            }
        }
    }
}
