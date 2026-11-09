using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// What is loaded when the state is taken. ULTRAKILL streams a level's rooms: doors switch
    /// the rooms they open on (Door.activatedRooms) and off (Door.deactivatedRooms), and some
    /// content lives in additively loaded scenes. A state must come back with the same rooms
    /// visible and the same scenes loaded, or enemies and geometry from another moment appear.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        /// <summary>Rooms switched by every door: the objects a streamed room toggles.</summary>
        void CollectDoorRooms(HashSet<GameObject> targets)
        {
            Type doorType = _bridge.FindType("Door");
            if (doorType == null)
                return;
            UnityEngine.Object[] doors;
            try { doors = UnityEngine.Object.FindObjectsOfType(doorType, true); }
            catch { return; }
            for (int i = 0; i < doors.Length; i++)
            {
                // One broken door must not drop the rooms of every other door.
                try
                {
                    Component door = doors[i] as Component;
                    if (door != null)
                        CollectTargetObjects(door, targets, 0);
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Door room capture skipped for one door: " + ex.Message);
                }
            }
        }

        void CaptureLoadedParts(Savestate state)
        {
            state.LoadedScenes.Clear();
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded)
                    state.LoadedScenes.Add(scene.name);
            }
        }

        /// <summary>
        /// Scenes that were loaded at capture but are gone now cannot be reopened safely from here
        /// (ULTRAKILL loads levels through its own Addressables path). They are reported as missing
        /// so a bad replay is explained rather than silently drifting. Scenes that were not loaded
        /// at capture but are loaded now are reported too: their objects are not part of the state.
        /// </summary>
        void RestoreLoadedParts(Savestate state, ref int missing)
        {
            HashSet<string> now = new HashSet<string>(StringComparer.Ordinal);
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded)
                    now.Add(scene.name);
            }

            List<string> unloaded = new List<string>();
            for (int i = 0; i < state.LoadedScenes.Count; i++)
            {
                if (!now.Contains(state.LoadedScenes[i]))
                    unloaded.Add(state.LoadedScenes[i]);
            }
            HashSet<string> saved = new HashSet<string>(state.LoadedScenes, StringComparer.Ordinal);
            List<string> extra = new List<string>();
            foreach (string name in now)
            {
                if (!saved.Contains(name))
                    extra.Add(name);
            }

            if (unloaded.Count > 0)
            {
                missing += unloaded.Count;
                _plugin.LogWarning("Savestate expects scene(s) that are no longer loaded: " +
                                   string.Join(", ", unloaded.ToArray()) +
                                   ". Their contents cannot be restored; reload the level first.");
            }
            if (extra.Count > 0)
            {
                // A scene loaded after the save is extra timeline content. Unload it when the
                // cleanup option is on; Unity only offers an asynchronous unload, so log the
                // operation and let the next frame complete it rather than leaving the scene
                // silently active forever.
                if (_plugin.SavestateCleanupSpawned != null && _plugin.SavestateCleanupSpawned.Value)
                {
                    for (int i = 0; i < extra.Count; i++)
                    {
                        Scene scene = SceneManager.GetSceneByName(extra[i]);
                        if (!scene.IsValid() || !scene.isLoaded || scene == SceneManager.GetActiveScene())
                            continue;
                        try
                        {
                            SceneManager.UnloadSceneAsync(scene);
                            _plugin.LogInfo("Savestate unloading additive scene '" + extra[i] + "' loaded after the save.");
                        }
                        catch (Exception ex)
                        {
                            _plugin.LogWarning("Savestate could not unload additive scene '" + extra[i] + "': " + ex.Message);
                        }
                    }
                }
                _plugin.LogInfo("Scene(s) loaded since the save, not part of the state: " + string.Join(", ", extra.ToArray()) + ".");
            }
        }
    }
}
