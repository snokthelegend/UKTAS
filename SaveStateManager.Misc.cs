using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Miscellaneous determinism state.
    ///
    /// SeaBodies simulates the ocean surface with Unity.Mathematics.Random streams kept in
    /// plain fields. Those fields are structs from another assembly, so the generic field
    /// plans leave them alone; they are copied by value here (the struct is self-contained
    /// and contains no native handles), which keeps the wave pattern identical after a load.
    ///
    /// The other half of determinism verification lives in DeterminismTrace, which now also
    /// hashes the enemy and animator state of the row.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        sealed class RandomFieldState
        {
            public Component Owner;
            public FieldInfo Field;
            public object Value;
            public bool IsArray;
            public Array Values;
        }

        sealed class MiscModuleState
        {
            public List<RandomFieldState> Randoms = new List<RandomFieldState>();
        }

        static Type _mathRandomType;
        static bool _mathRandomSearched;

        partial void CaptureMisc(Savestate state)
        {
            MiscModuleState misc = new MiscModuleState();
            Type randomType = FindMathRandomType();
            Type seaType = randomType != null ? _bridge.FindType("SeaBodies") : null;
            if (seaType != null)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(seaType, true); }
                catch { found = new UnityEngine.Object[0]; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || IsTemplateClone(component.gameObject))
                        continue;
                    try
                    {
                        CaptureRandomFields(component, randomType, misc);
                    }
                    catch (Exception ex)
                    {
                        state.Warnings.Add("SeaBodies random: " + ex.Message);
                    }
                }
            }
            SetExt(state, "Misc", misc);
        }

        partial void RestoreMisc(Savestate state, ref int restored, ref int missing)
        {
            MiscModuleState misc = GetExt<MiscModuleState>(state, "Misc");
            if (misc == null)
                return;
            for (int i = 0; i < misc.Randoms.Count; i++)
            {
                RandomFieldState random = misc.Randoms[i];
                if (random == null || random.Field == null)
                    continue;
                Component owner = Resolve(random.Owner);
                if (owner == null)
                {
                    missing++;
                    continue;
                }
                try
                {
                    if (random.IsArray)
                        random.Field.SetValue(owner, random.Values != null ? random.Values.Clone() : null);
                    else
                        random.Field.SetValue(owner, random.Value);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Random state restore skipped: " + ex.Message);
                }
            }
        }

        static void CaptureRandomFields(Component owner, Type randomType, MiscModuleState misc)
        {
            FieldInfo[] fields = owner.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsLiteral)
                    continue;
                if (field.FieldType == randomType)
                {
                    misc.Randoms.Add(new RandomFieldState
                    {
                        Owner = owner,
                        Field = field,
                        Value = field.GetValue(owner)
                    });
                }
                else if (field.FieldType.IsArray && field.FieldType.GetElementType() == randomType)
                {
                    misc.Randoms.Add(new RandomFieldState
                    {
                        Owner = owner,
                        Field = field,
                        IsArray = true,
                        Values = field.GetValue(owner) as Array
                    });
                }
            }
        }

        /// <summary>Unity.Mathematics may not be referenced by the project; find it by name.</summary>
        static Type FindMathRandomType()
        {
            if (_mathRandomSearched)
                return _mathRandomType;
            _mathRandomSearched = true;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type[] types;
                    try { types = assemblies[i].GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    catch { continue; }
                    if (types == null)
                        continue;
                    for (int t = 0; t < types.Length; t++)
                    {
                        Type type = types[t];
                        if (type != null && type.FullName == "Unity.Mathematics.Random")
                        {
                            _mathRandomType = type;
                            return type;
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
