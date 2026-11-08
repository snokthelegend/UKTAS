using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Weapon and arm inventory module.
    ///
    /// GunControl and FistControl hang off the player rig and are captured generically there,
    /// but the inventory is a set of collections that the rolling history's light capture
    /// skips on purpose: <c>slots</c>, <c>slotDict</c>, <c>retainedVariations</c> and
    /// FistControl's <c>spawnedArmNums</c>. Without them a load can leave a weapon the player
    /// had already dropped, or lose a retained variation that the next weapon cycle needs.
    /// These fields are captured through the deep path even when a snapshot is light.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        static readonly string[] WeaponFieldNames =
        {
            "slots", "slotDict", "retainedVariations", "currentSlotIndex", "currentSlot",
            "currentVariationIndex", "currentVariation", "currentVariant", "currentWeapon",
            "spawnedArmNums", "currentOrderNum", "currentVarNum", "currentPunch",
            "currentArm", "arm", "holding", "hasArm", "currentFist"
        };

        sealed class WeaponPartState
        {
            public Component Component;
            public string TypeName;
            public List<FieldState> Named;
        }

        sealed class WeaponModuleState
        {
            public List<WeaponPartState> Parts = new List<WeaponPartState>();
        }

        partial void CaptureWeapons(Savestate state)
        {
            WeaponModuleState weapons = new WeaponModuleState();
            AddWeaponComponent(weapons, _bridge.GunControl as Component);
            AddWeaponComponent(weapons, _bridge.FistControl as Component);
            SetExt(state, "Weapons", weapons);
        }

        partial void RestoreWeapons(Savestate state, ref int restored, ref int missing)
        {
            WeaponModuleState weapons = GetExt<WeaponModuleState>(state, "Weapons");
            if (weapons == null)
                return;
            for (int i = 0; i < weapons.Parts.Count; i++)
            {
                WeaponPartState part = weapons.Parts[i];
                if (part == null)
                    continue;
                Component component = Resolve(part.Component);
                if (component == null)
                {
                    // The current one may have been replaced by a fresh instance after the
                    // save; match it by type before giving up.
                    component = FindWeaponComponent(part.TypeName);
                }
                if (component == null)
                {
                    missing++;
                    continue;
                }
                try
                {
                    ApplyFields(component, part.Named);
                    // The active arm's Punch component is the one p must fire. A light
                    // capture can skip the currentPunch reference, so derive it from the
                    // restored currentOrderNum and the arm objects before the next tick.
                    if (part.TypeName == "FistControl")
                        _bridge.SyncFistControl();
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Weapon inventory restore skipped: " + ex.Message);
                }
            }
        }

        void AddWeaponComponent(WeaponModuleState weapons, Component component)
        {
            if (component == null)
                return;
            List<FieldState> named = new List<FieldState>(8);
            DeepContext context = null;
            for (int n = 0; n < WeaponFieldNames.Length; n++)
            {
                FieldInfo field;
                try { field = NamedField(component.GetType(), WeaponFieldNames[n]); }
                catch { continue; }
                if (field == null || field.IsLiteral)
                    continue;
                if (field.IsInitOnly && !typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType) &&
                    !field.FieldType.IsValueType)
                    continue;
                try
                {
                    FieldPlan plan = ClassifyField(field, true);
                    if (plan == null)
                        continue;
                    // CaptureField does not consult the light-capture flag: inventory
                    // collections are always deep-copied.
                    FieldState value = CaptureField(plan, component, field.GetValue(component), ref context);
                    if (value != null)
                        named.Add(value);
                }
                catch
                {
                }
            }
            weapons.Parts.Add(new WeaponPartState
            {
                Component = component,
                TypeName = component.GetType().Name,
                Named = named
            });
        }

        Component FindWeaponComponent(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;
            Component current = typeName == "FistControl"
                ? _bridge.FistControl as Component
                : _bridge.GunControl as Component;
            if (current != null && current.GetType().Name == typeName)
                return current;

            Type type = _bridge.FindType(typeName);
            if (type == null)
                return null;
            try
            {
                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(type);
                return found as Component;
            }
            catch
            {
                return null;
            }
        }
    }
}
