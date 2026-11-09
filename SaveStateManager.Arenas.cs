using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Re-arms the game's own arena triggers when a savestate taken before a fight is loaded.
    ///
    /// ULTRAKILL's ActivateArena trigger destroys its component once it has spawned its first
    /// wave (Object.Destroy(this)), and every ActivateNextWave destroys itself when its wave
    /// ends. A load can write the saved fields back, but a component that no longer exists has
    /// nothing to write into: the trigger volume is still sitting in the level, yet walking into
    /// it does nothing and the arena can never start again.
    ///
    /// The level-element capture already keeps the component snapshot and the trigger root. A
    /// load therefore adds the component back when the snapshot's object is gone and restores
    /// the saved fields into it. Arena.AutoSetupWaves then rewires the doors, first-wave enemies,
    /// next-wave links and counts from the live hierarchy, exactly as the game does in Awake,
    /// and the wave bookkeeping is reset to its pre-fight values. The wave enemies are switched
    /// off last, which is the state Arena.Awake leaves them in before the trigger activates them.
    ///
    /// A state taken during or after a fight has no ActivateArena snapshot (the component was
    /// already destroyed when the state was captured), so it is never reset by this path.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        static readonly string[] ArenaScriptNames = { "ActivateArena", "ActivateNextWave", "ActivateNextWaveHP" };

        /// <summary>The recreated trigger components; each one stands for one arena to re-arm.</summary>
        readonly List<Component> _rebuiltArenaTriggers = new List<Component>();
        int _rebuiltArenaScripts;
        bool _arenaResetEnabled;

        void BeginArenaRestore(bool enabled)
        {
            _arenaResetEnabled = enabled;
            _rebuiltArenaTriggers.Clear();
            _rebuiltArenaScripts = 0;
        }

        /// <summary>
        /// Called for every captured level element before its fields are restored. If the game
        /// destroyed this arena script after the state was captured, add it back on the saved
        /// root; <see cref="RestoreComponent"/> then writes the snapshot into it.
        /// </summary>
        void RebuildArenaScript(LevelElementState element, GameObject root)
        {
            if (!_arenaResetEnabled || element == null || root == null)
                return;
            ComponentSnapshot snapshot = element.Component;
            if (snapshot == null || string.IsNullOrEmpty(snapshot.TypeName))
                return;
            if (Array.IndexOf(ArenaScriptNames, snapshot.TypeName) < 0)
                return;
            if (Resolve(snapshot.Component) != null)
                return;
            Type type = _bridge.FindType(snapshot.TypeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return;
            Component created;
            try
            {
                created = root.GetComponent(type);
                if (created == null)
                    created = root.AddComponent(type);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Arena script '" + snapshot.TypeName + "' could not be rebuilt on '" +
                                   root.name + "': " + ex.Message);
                return;
            }
            if (created == null)
                return;
            snapshot.Component = created;
            _rebuiltArenaScripts++;
            if (snapshot.TypeName == "ActivateArena")
                _rebuiltArenaTriggers.Add(created);
            _plugin.LogInfo("Arena script rebuilt: '" + snapshot.TypeName + "' on '" + root.name +
                            "' (the game destroyed it when the fight started).");
        }

        /// <summary>
        /// Run after all level elements have their saved values. For every recreated trigger,
        /// let the game's Arena.AutoSetupWaves rebuild its arrays, then put the trigger and wave
        /// counters back to the state a fresh level has and switch the wave enemies off.
        /// </summary>
        void FinishArenaRestore()
        {
            if (!_arenaResetEnabled || _rebuiltArenaTriggers.Count == 0)
                return;
            Type arenaType = _bridge.FindType("Arena");
            Type triggerType = _bridge.FindType("ActivateArena");
            if (arenaType == null || triggerType == null)
                return;
            MethodInfo getWaves = arenaType.GetMethod("GetWaves",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            MethodInfo autoSetup = arenaType.GetMethod("AutoSetupWaves",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getWaves == null || autoSetup == null)
                return;

            HashSet<int> seen = new HashSet<int>();
            int arenas = 0;
            int disabled = 0;
            for (int i = 0; i < _rebuiltArenaTriggers.Count; i++)
            {
                Component trigger = _rebuiltArenaTriggers[i];
                Component arena = FindArena(trigger, arenaType);
                if (arena == null || !seen.Add(arena.GetInstanceID()))
                    continue;
                ArenaResetResult result = ResetArena(arena, triggerType, getWaves, autoSetup);
                if (!result.Done)
                    continue;
                arenas++;
                disabled += result.DisabledEnemies;
            }
            if (arenas == 0)
                return;
            _plugin.LogInfo("Arena reset: re-armed " + arenas + " arena(s), " + _rebuiltArenaScripts +
                            " rebuilt script(s); " + disabled + " wave enemy object(s) set inactive. " +
                            "Walk back into the trigger to restart the fight.");
            _plugin.Toast("Arena reset: walk back into the trigger to restart the fight.", false);
        }

        struct ArenaResetResult
        {
            public bool Done;
            public int DisabledEnemies;
        }

        ArenaResetResult ResetArena(Component arena, Type triggerType, MethodInfo getWaves, MethodInfo autoSetup)
        {
            ArenaResetResult result = new ArenaResetResult();

            // Arena.GetActivateArena() only sees active components, and AutoSetupWaves would
            // throw on a null one. A pre-fight trigger is active, so require one here.
            Component[] triggers = arena.gameObject.GetComponentsInChildren(triggerType, true);
            bool hasActiveTrigger = false;
            for (int i = 0; i < triggers.Length; i++)
            {
                if (triggers[i] != null && triggers[i].gameObject.activeInHierarchy)
                {
                    hasActiveTrigger = true;
                    break;
                }
            }
            if (!hasActiveTrigger)
                return result;

            Array waves;
            try
            {
                EnsureArenaArray(arena, "doors");
                waves = getWaves.Invoke(arena, null) as Array;
                if (waves == null)
                    return result;
                // The game's own Awake setup: doors, first-wave enemies, next-wave links/counts.
                autoSetup.Invoke(arena, new object[] { waves });
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Arena reset skipped for '" + arena.name + "': " + ex.Message);
                return result;
            }

            for (int i = 0; i < triggers.Length; i++)
            {
                Component trigger = triggers[i];
                if (trigger == null)
                    continue;
                // A non-primary trigger is not configured by AutoSetupWaves; an empty array
                // keeps the recreated component from null-referencing in Activate().
                EnsureArenaArray(trigger, "enemies");
                EnsureArenaArray(trigger, "doors");
                SetArenaMember(trigger, "activated", false);
                SetArenaMember(trigger, "currentEnemy", 0);
                SetArenaMember(trigger, "playerIn", false);
            }

            for (int i = 0; i < waves.Length; i++)
            {
                Component wave = waves.GetValue(i) as Component;
                if (wave == null)
                    continue;
                SetArenaMember(wave, "activated", false);
                SetArenaMember(wave, "deadEnemies", 0);
                SetArenaMember(wave, "enemyCount", wave.transform.childCount);
                SetArenaMember(wave, "currentEnemy", 0);
                SetArenaMember(wave, "currentDoor", 0);
                SetArenaMember(wave, "objectsActivated", false);
                SetArenaMember(wave, "slowDown", 1f);
                EnsureArenaArray(wave, "toActivate");
                ResetArenaLinks(wave);

                // Arena.Awake disables every direct child of every wave before the fight, so
                // the trigger's SpawnEnemy is what switches them on. Mirror that state.
                Transform parent = wave.transform;
                for (int c = 0; c < parent.childCount; c++)
                {
                    GameObject child = parent.GetChild(c).gameObject;
                    if (child.activeSelf)
                    {
                        child.SetActive(false);
                        result.DisabledEnemies++;
                    }
                }
            }
            result.Done = true;
            return result;
        }

        static Component FindArena(Component trigger, Type arenaType)
        {
            if (trigger == null)
                return null;
            for (Transform current = trigger.transform; current != null; current = current.parent)
            {
                Component arena = current.GetComponent(arenaType);
                if (arena != null)
                    return arena;
            }
            return null;
        }

        static void SetArenaMember(object target, string name, object value)
        {
            if (target == null)
                return;
            FieldInfo field = NamedField(target.GetType(), name);
            if (field == null || field.IsInitOnly)
                return;
            try { field.SetValue(target, value); }
            catch { }
        }

        /// <summary>A recreated wave component starts with null arrays; the game indexes them.</summary>
        static void EnsureArenaArray(object target, string name)
        {
            if (target == null)
                return;
            FieldInfo field = NamedField(target.GetType(), name);
            if (field == null || !field.FieldType.IsArray || field.IsInitOnly)
                return;
            try
            {
                if (field.GetValue(target) == null)
                    field.SetValue(target, Array.CreateInstance(field.FieldType.GetElementType(), 0));
            }
            catch { }
        }

        /// <summary>
        /// ActivateNextWave.Awake caches the components on its own object for linked waves; a
        /// rebuilt component only saw the copies that existed when it was added.
        /// </summary>
        static void ResetArenaLinks(Component wave)
        {
            FieldInfo field = NamedField(wave.GetType(), "linkedAnws");
            if (field == null || !field.FieldType.IsArray)
                return;
            Type element = field.FieldType.GetElementType();
            if (element == null)
                return;
            try
            {
                Component[] linked = wave.gameObject.GetComponents(element);
                Array array = Array.CreateInstance(element, linked.Length);
                for (int i = 0; i < linked.Length; i++)
                    array.SetValue(linked[i], i);
                field.SetValue(wave, array);
            }
            catch { }
        }
    }
}
