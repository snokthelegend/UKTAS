using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Style and stats module.
    ///
    /// StyleHUD and StyleCalculator keep their freshness timers and rank bookkeeping in
    /// nested collections (rank tables, per-weapon freshness), and StatsManager keeps the
    /// checkpoint/secret references the results screen reads. The core system capture copies
    /// the scalar fields of those scripts; this module stores the whole component snapshot of
    /// each one - including the nested collections the deep path understands - and restores
    /// it as a unit, so a style rank that was about to drop does not come back full.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        static readonly string[] StyleTypeNames =
        {
            "StyleHUD", "StyleCalculator", "CoinChainCache", "StyleManager", "StatsManager",
            "StyleBonus", "StyleRank"
        };

        sealed class StyleModuleState
        {
            public List<ComponentSnapshot> Parts = new List<ComponentSnapshot>();
        }

        partial void CaptureStyle(Savestate state)
        {
            StyleModuleState style = new StyleModuleState();
            for (int t = 0; t < StyleTypeNames.Length; t++)
            {
                Type type = _bridge.FindType(StyleTypeNames[t]);
                // StyleRank resolves to a plain class: FindObjectsOfType returns null for it.
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                    continue;
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
                catch { continue; }
                if (found == null)
                    continue;
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || IsTemplateClone(component.gameObject))
                        continue;
                    try
                    {
                        ComponentSnapshot snapshot = CaptureComponent(component, RefCap);
                        if (snapshot != null)
                            style.Parts.Add(snapshot);
                    }
                    catch (Exception ex)
                    {
                        state.Warnings.Add(component.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            SetExt(state, "Style", style);
        }

        partial void RestoreStyle(Savestate state, ref int restored, ref int missing)
        {
            StyleModuleState style = GetExt<StyleModuleState>(state, "Style");
            if (style == null)
                return;
            for (int i = 0; i < style.Parts.Count; i++)
            {
                ComponentSnapshot snapshot = style.Parts[i];
                if (snapshot == null)
                    continue;
                Component component = Resolve(snapshot.Component);
                if (component == null)
                {
                    missing++;
                    continue;
                }
                try
                {
                    RestoreComponent(snapshot);
                    // StyleHUD only redraws its rank text from its own update; ask it once.
                    if (component.GetType().Name == "StyleHUD")
                        TryInvoke(component, "UpdateHud");
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Style restore skipped: " + ex.Message);
                }
            }
        }
    }
}
