using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Coroutine module.
    ///
    /// Unity keeps no public list of running coroutines, but a script that drives a
    /// long-lived sequence almost always keeps the handle in a <c>Coroutine</c> field
    /// (Geryon beams, Sisyphus attack loops, Deathcatcher.RespawnPuppets, the punch
    /// chainsaw chain, TimeController.TimeIsStopped, StyleHUD.UpdateItems). Those handles
    /// are what this module stores:
    ///
    ///  - On load every stored handle is stopped before any value is written. A coroutine
    ///    started after the save belongs to the timeline being discarded, and one that
    ///    outlives it keeps moving limbs, beams and style items over the restored values.
    ///  - Afterwards, handles that were running at the save are restarted by name. The
    ///    coroutine restarts from its first yield rather than its saved position; Unity
    ///    does not expose that position, and a restarted attack loop is far closer to the
    ///    saved state than a loop that never runs again.
    ///
    /// A coroutine without a handle field cannot be observed or stopped; those are left to
    /// their own state fields, which the core capture restores.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        sealed class CoroutineFieldState
        {
            public Component Owner;
            public FieldInfo Field;
            public string Method;
            public bool Running;
        }

        static readonly Dictionary<Type, FieldInfo[]> CoroutineFieldCache = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, Dictionary<string, MethodInfo>> CoroutineMethodCache =
            new Dictionary<Type, Dictionary<string, MethodInfo>>();

        partial void CaptureCoroutines(Savestate state)
        {
            List<Component> components = CollectCoroutineComponents(state);
            List<CoroutineFieldState> fields = new List<CoroutineFieldState>(components.Count);
            for (int i = 0; i < components.Count; i++)
            {
                Component component = components[i];
                if (component == null)
                    continue;
                try
                {
                    FieldInfo[] candidates = GetCoroutineFields(component.GetType());
                    for (int f = 0; f < candidates.Length; f++)
                    {
                        FieldInfo field = candidates[f];
                        Coroutine current = null;
                        try { current = field.GetValue(component) as Coroutine; }
                        catch { continue; }
                        fields.Add(new CoroutineFieldState
                        {
                            Owner = component,
                            Field = field,
                            Method = GuessCoroutineMethod(component.GetType(), field.Name),
                            Running = current != null
                        });
                    }
                }
                catch
                {
                }
            }
            SetExt(state, "Coroutines", fields);
        }

        partial void RestoreCoroutines(Savestate state, ref int restored, ref int missing)
        {
            List<CoroutineFieldState> fields = GetExt<List<CoroutineFieldState>>(state, "Coroutines");
            if (fields == null)
                return;
            for (int i = 0; i < fields.Count; i++)
            {
                CoroutineFieldState field = fields[i];
                if (field == null || field.Owner == null)
                    continue;
                if (Resolve(field.Owner) == null)
                {
                    if (field.Running)
                        missing++;
                    continue;
                }
                if (field.Running)
                    restored++;
            }
        }

        /// <summary>First restore step: every stored handle is dropped, running or not.</summary>
        partial void PreRestore(Savestate state)
        {
            List<CoroutineFieldState> fields = GetExt<List<CoroutineFieldState>>(state, "Coroutines");
            if (fields == null)
                return;

            int stopped = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                CoroutineFieldState entry = fields[i];
                if (entry == null)
                    continue;
                Component owner = Resolve(entry.Owner);
                if (owner == null || entry.Field == null)
                    continue;
                try
                {
                    Coroutine current = entry.Field.GetValue(owner) as Coroutine;
                    if (current == null)
                        continue;
                    MonoBehaviour behaviour = owner as MonoBehaviour;
                    if (behaviour != null)
                        behaviour.StopCoroutine(current);
                    entry.Field.SetValue(owner, null);
                    stopped++;
                }
                catch
                {
                }
            }
            if (stopped > 0)
                _plugin.LogInfo("Savestate stopped " + stopped + " coroutine(s) from the discarded timeline.");
        }

        /// <summary>Last restore step: restart the handles that were running at the save.</summary>
        partial void PostRestore(Savestate state)
        {
            // The hitstop reset (clock carry resync) runs between the module restores and
            // this step, so the timing module's saved phase is applied here, not there.
            ApplySavedPhysicsPhase(state);

            List<CoroutineFieldState> fields = GetExt<List<CoroutineFieldState>>(state, "Coroutines");
            if (fields == null)
                return;

            int restarted = 0;
            int skipped = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                CoroutineFieldState entry = fields[i];
                if (entry == null || !entry.Running)
                    continue;
                Component owner = Resolve(entry.Owner);
                if (owner == null || !owner.gameObject.activeInHierarchy)
                {
                    skipped++;
                    continue;
                }
                MonoBehaviour behaviour = owner as MonoBehaviour;
                if (behaviour == null || !behaviour.enabled)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    Coroutine current = entry.Field.GetValue(owner) as Coroutine;
                    if (current != null)
                        continue;
                    Coroutine started = StartCoroutineByName(behaviour, entry.Method);
                    if (started != null)
                    {
                        entry.Field.SetValue(owner, started);
                        restarted++;
                    }
                }
                catch
                {
                    skipped++;
                }
            }
            if (restarted > 0)
                _plugin.LogInfo("Savestate re-armed " + restarted + " coroutine(s)" +
                                (skipped > 0 ? ", " + skipped + " could not be restarted." : "."));
        }

        static Coroutine StartCoroutineByName(MonoBehaviour behaviour, string methodName)
        {
            if (behaviour == null || string.IsNullOrEmpty(methodName))
                return null;
            MethodInfo method = FindCoroutineMethod(behaviour.GetType(), methodName);
            if (method == null)
                return null;
            IEnumerator routine = method.Invoke(behaviour, null) as IEnumerator;
            if (routine == null)
                return null;
            return behaviour.StartCoroutine(routine);
        }

        static MethodInfo FindCoroutineMethod(Type type, string methodName)
        {
            if (type == null || string.IsNullOrEmpty(methodName))
                return null;
            Dictionary<string, MethodInfo> methods = GetCoroutineMethods(type);
            MethodInfo found;
            if (methods.TryGetValue(methodName, out found))
                return found;
            string[] candidates = CoroutineMethodCandidates(methodName);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (methods.TryGetValue(candidates[i], out found))
                    return found;
            }
            return null;
        }

        static Dictionary<string, MethodInfo> GetCoroutineMethods(Type type)
        {
            Dictionary<string, MethodInfo> cached;
            if (CoroutineMethodCache.TryGetValue(type, out cached))
                return cached;
            cached = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.GetParameters().Length != 0 || !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
                        continue;
                    if (!cached.ContainsKey(method.Name))
                        cached[method.Name] = method;
                }
            }
            catch
            {
            }
            CoroutineMethodCache[type] = cached;
            return cached;
        }

        /// <summary>"beamRoutine" -> beam, Beam, beamCo, BeamCoroutine, ... in that order.</summary>
        static string[] CoroutineMethodCandidates(string fieldName)
        {
            string name = fieldName;
            if (name.StartsWith("m_", StringComparison.Ordinal))
                name = name.Substring(2);
            name = name.TrimStart('_');
            if (string.IsNullOrEmpty(name))
                return new string[0];

            string trimmed = name;
            string[] suffixes = { "RoutineHandle", "CoroutineHandle", "Routine", "Corutine", "Coroutine", "Handle", "Runner", "Loop", "Co", "CR" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (!trimmed.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Length <= suffixes[i].Length)
                    continue;
                trimmed = trimmed.Substring(0, trimmed.Length - suffixes[i].Length);
                break;
            }

            string upper = char.ToUpperInvariant(trimmed[0]) + (trimmed.Length > 1 ? trimmed.Substring(1) : string.Empty);
            return new[]
            {
                trimmed,
                upper,
                upper + "Routine",
                upper + "Coroutine",
                upper + "Co",
                name,
                char.ToUpperInvariant(name[0]) + (name.Length > 1 ? name.Substring(1) : string.Empty)
            };
        }

        static string GuessCoroutineMethod(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrEmpty(fieldName))
                return null;
            MethodInfo method = FindCoroutineMethod(type, fieldName);
            return method != null ? method.Name : null;
        }

        static FieldInfo[] GetCoroutineFields(Type type)
        {
            FieldInfo[] cached;
            if (CoroutineFieldCache.TryGetValue(type, out cached))
                return cached;

            List<FieldInfo> fields = new List<FieldInfo>(2);
            for (Type current = type;
                 current != null && current != typeof(MonoBehaviour) && current != typeof(object);
                 current = current.BaseType)
            {
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    if (declared[i].FieldType == typeof(Coroutine))
                        fields.Add(declared[i]);
                }
            }
            cached = fields.ToArray();
            CoroutineFieldCache[type] = cached;
            return cached;
        }

        /// <summary>Every script whose coroutine handles are stored (same reach as the snapshots).</summary>
        static List<Component> CollectCoroutineComponents(Savestate state)
        {
            List<Component> result = new List<Component>(64);
            if (state == null)
                return result;

            if (state.Player != null && state.Player.Extra != null)
                AddCoroutineComponent(result, state.Player.Extra.Component);
            if (state.PlayerParts != null)
            {
                for (int i = 0; i < state.PlayerParts.Count; i++)
                {
                    if (state.PlayerParts[i] != null)
                        AddCoroutineComponent(result, state.PlayerParts[i].Component);
                }
            }
            if (state.ExtraComponents != null)
            {
                for (int i = 0; i < state.ExtraComponents.Count; i++)
                    AddCoroutineComponent(result, state.ExtraComponents[i].Component);
            }
            if (state.HudParts != null)
            {
                for (int i = 0; i < state.HudParts.Count; i++)
                    AddCoroutineComponent(result, state.HudParts[i].Component);
            }
            if (state.Enemies != null)
            {
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    EnemyState enemy = state.Enemies[i];
                    if (enemy == null)
                        continue;
                    AddCoroutineComponent(result, enemy.Identifier);
                    if (enemy.Lightweight || enemy.Behaviours == null)
                        continue;
                    for (int b = 0; b < enemy.Behaviours.Count; b++)
                    {
                        if (enemy.Behaviours[b] != null)
                            AddCoroutineComponent(result, enemy.Behaviours[b].Component);
                    }
                }
            }
            if (state.LevelElements != null)
            {
                for (int i = 0; i < state.LevelElements.Count; i++)
                {
                    LevelElementState element = state.LevelElements[i];
                    if (element != null && element.Component != null)
                        AddCoroutineComponent(result, element.Component.Component);
                }
            }
            if (state.Doors != null)
            {
                for (int i = 0; i < state.Doors.Count; i++)
                {
                    DoorState door = state.Doors[i];
                    if (door == null)
                        continue;
                    AddCoroutineComponent(result, door.Door);
                    if (door.Generic != null)
                        AddCoroutineComponent(result, door.Generic.Component);
                    if (door.Parts == null)
                        continue;
                    for (int p = 0; p < door.Parts.Count; p++)
                    {
                        if (door.Parts[p] != null && door.Parts[p].Snapshot != null)
                            AddCoroutineComponent(result, door.Parts[p].Snapshot.Component);
                    }
                }
            }
            return result;
        }

        static void AddCoroutineComponent(List<Component> into, Component component)
        {
            if (component == null || !GameBridge.IsAlive(component))
                return;
            if (!into.Contains(component))
                into.Add(component);
        }
    }
}
