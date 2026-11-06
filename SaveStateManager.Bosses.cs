using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Boss module.
    ///
    /// Boss fights are driven by intro scripts and by transient helpers the boss spawns
    /// during an attack. The core enemy capture restores the boss itself; this module keeps
    /// the intro drivers (StatueIntroChecker, SisyphusPrimeIntro, PowerVoiceController,
    /// Pincer, ...) and the helper instances (beams, snakes, limbs) that decide whether the
    /// fight has started, which voice line is playing and what is currently on screen.
    ///
    /// Helpers that appeared after the save are destroyed, like the core transient sweep,
    /// so an attack that began in the discarded timeline does not keep hitting the player.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        /// <summary>Scripts that gate the intro / boss sequence itself.</summary>
        static readonly string[] BossIntroTypeNames =
        {
            "StatueIntroChecker", "SisyphusPrimeIntro", "MinosPrimeIntro", "PowerVoiceController",
            "Pincer", "BossHealthBar", "BossDoor", "BossBar", "BossMusic", "BossSequence"
        };

        /// <summary>Attack helpers a boss spawns; instances created after the save are orphans.</summary>
        static readonly string[] BossHelperTypeNames =
        {
            "GeryonBeam", "GeryonBeamAttack", "PowerLimb", "SisyphusSnake", "MinosSnake",
            "SisyphusPrimeSnake", "MinosHand", "GeryonEye", "SisyphusArm"
        };

        sealed class BossHelperState
        {
            public GameObject Root;
            public string TypeName;
            public bool ActiveSelf;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        sealed class BossModuleState
        {
            public List<ComponentSnapshot> Intro = new List<ComponentSnapshot>();
            public List<BossHelperState> Helpers = new List<BossHelperState>();
            public bool Captured;
        }

        partial void CaptureBosses(Savestate state)
        {
            BossModuleState boss = new BossModuleState { Captured = true };

            for (int t = 0; t < BossIntroTypeNames.Length; t++)
            {
                Type type = _bridge.FindType(BossIntroTypeNames[t]);
                if (type == null)
                    continue;
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || IsTemplateClone(component.gameObject))
                        continue;
                    try
                    {
                        ComponentSnapshot snapshot = CaptureComponent(component, RefCap);
                        if (snapshot != null)
                            boss.Intro.Add(snapshot);
                    }
                    catch (Exception ex)
                    {
                        state.Warnings.Add(component.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            for (int t = 0; t < BossHelperTypeNames.Length; t++)
            {
                Type type = _bridge.FindType(BossHelperTypeNames[t]);
                if (type == null)
                    continue;
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(type); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || IsTemplateClone(component.gameObject))
                        continue;
                    GameObject root = component.gameObject;
                    boss.Helpers.Add(new BossHelperState
                    {
                        Root = root,
                        TypeName = component.GetType().Name,
                        ActiveSelf = root.activeSelf,
                        Position = root.transform.position,
                        Rotation = root.transform.rotation
                    });
                }
            }

            SetExt(state, "Bosses", boss);
        }

        partial void RestoreBosses(Savestate state, ref int restored, ref int missing)
        {
            BossModuleState boss = GetExt<BossModuleState>(state, "Bosses");
            if (boss == null || !boss.Captured)
                return;

            for (int i = 0; i < boss.Intro.Count; i++)
            {
                ComponentSnapshot snapshot = boss.Intro[i];
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
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Boss intro restore skipped: " + ex.Message);
                }
            }

            int removed = SweepBossHelpers(boss);
            if (removed > 0)
                _plugin.LogInfo("Savestate removed " + removed + " boss helper(s) spawned after the save.");

            for (int i = 0; i < boss.Helpers.Count; i++)
            {
                BossHelperState helper = boss.Helpers[i];
                if (helper == null || helper.Root == null)
                    continue;
                try
                {
                    helper.Root.transform.position = helper.Position;
                    helper.Root.transform.rotation = helper.Rotation;
                    if (helper.Root.activeSelf != helper.ActiveSelf)
                        helper.Root.SetActive(helper.ActiveSelf);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Destroy helper instances that are not in the saved set. Only the helper types are
        /// scanned, the player is skipped and prefab assets are never touched.
        /// </summary>
        int SweepBossHelpers(BossModuleState boss)
        {
            HashSet<GameObject> saved = new HashSet<GameObject>();
            for (int i = 0; i < boss.Helpers.Count; i++)
            {
                if (boss.Helpers[i] != null && boss.Helpers[i].Root != null)
                    saved.Add(boss.Helpers[i].Root);
            }

            Transform player = _bridge.PlayerTransform;
            int removed = 0;
            for (int t = 0; t < BossHelperTypeNames.Length; t++)
            {
                Type type = _bridge.FindType(BossHelperTypeNames[t]);
                if (type == null)
                    continue;
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(type); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    GameObject root = component.gameObject;
                    if (saved.Contains(root) || !root.scene.IsValid() || IsTemplateClone(root))
                        continue;
                    if (player != null && root.transform.IsChildOf(player))
                        continue;
                    UnityEngine.Object.Destroy(root);
                    removed++;
                }
            }
            return removed;
        }
    }
}
