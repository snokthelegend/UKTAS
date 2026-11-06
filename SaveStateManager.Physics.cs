using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Hazard cooldown module.
    ///
    /// ULTRAKILL stores the per-source damage cooldowns (fire zones, burning voxels, lava,
    /// hurt zones) in a HurtCooldownCollection on NewMovement. Every entry is a timestamp:
    /// load without it and a hazard that had just hurt the player is ready again one frame
    /// later, or - for a hazard the player was standing in - never re-arms cleanly.
    /// The collection is a plain game class, so the deep field path can copy it wholesale;
    /// it is captured as a named field here because the player snapshot is Lightweight in
    /// some rolling-history captures and a cooldown is gameplay, not decoration.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        sealed class HazardCooldownState
        {
            public Component Owner;
            public FieldInfo Field;
            public FieldState Value;
        }

        sealed class PhysicsModuleState
        {
            public List<HazardCooldownState> Cooldowns = new List<HazardCooldownState>();
        }

        partial void CapturePhysics(Savestate state)
        {
            PhysicsModuleState physics = new PhysicsModuleState();
            Component player = _bridge.PlayerComponent;
            if (player != null)
            {
                FieldPlan[] plans = GetCaptureFields(player.GetType());
                DeepContext context = null;
                for (int i = 0; i < plans.Length; i++)
                {
                    FieldPlan plan = plans[i];
                    if (plan == null || plan.Field == null)
                        continue;
                    if (!IsHazardCooldownField(plan.Field))
                        continue;
                    try
                    {
                        FieldState value = CaptureField(plan, player, plan.Field.GetValue(player), ref context);
                        if (value != null)
                            physics.Cooldowns.Add(new HazardCooldownState
                            {
                                Owner = player,
                                Field = plan.Field,
                                Value = value
                            });
                    }
                    catch (Exception ex)
                    {
                        state.Warnings.Add("hazard cooldown " + plan.Field.Name + ": " + ex.Message);
                    }
                }
            }
            SetExt(state, "Physics", physics);
        }

        partial void RestorePhysics(Savestate state, ref int restored, ref int missing)
        {
            PhysicsModuleState physics = GetExt<PhysicsModuleState>(state, "Physics");
            if (physics == null)
                return;
            Component player = Resolve(_bridge.PlayerComponent);
            for (int i = 0; i < physics.Cooldowns.Count; i++)
            {
                HazardCooldownState cooldown = physics.Cooldowns[i];
                if (cooldown == null || cooldown.Value == null)
                    continue;
                Component owner = Resolve(cooldown.Owner) ?? player;
                if (owner == null)
                {
                    missing++;
                    continue;
                }
                try
                {
                    ApplyField(owner, cooldown.Value);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Hazard cooldown restore skipped: " + ex.Message);
                }
            }
        }

        static bool IsHazardCooldownField(FieldInfo field)
        {
            string typeName = field.FieldType.Name;
            if (typeName.IndexOf("HurtCooldown", StringComparison.Ordinal) >= 0)
                return true;
            string name = field.Name;
            return name.IndexOf("hurtCooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("hazardCooldown", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
