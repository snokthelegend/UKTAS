using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// TEMPORARY diagnostic: writes the live fist/arm HUD state to a text file so the cause of a
    /// missing Knuckleblaster HUD icon can be read. Remove once the HUD issue is fixed.
    /// Output: BepInEx\uktas_fist_hud_dump.txt. Never throws.
    /// </summary>
    public static class FistHudDump
    {
        public static string Write(GameBridge bridge)
        {
            StringBuilder sb = new StringBuilder(4096);
            try
            {
                sb.AppendLine("Dump time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("Scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);

                object fists = bridge.FistControl;
                sb.AppendLine("== FistControl (" + (fists == null ? "NULL" : "found") + ")");
                if (fists != null)
                {
                    sb.AppendLine("currentVarNum: " + Show(bridge.GetMember(fists, new[] { "currentVarNum" })));
                    sb.AppendLine("currentOrderNum: " + Show(bridge.GetMember(fists, new[] { "currentOrderNum" })));
                    sb.AppendLine("spawnedArmNums: " + Show(bridge.GetMember(fists, new[] { "spawnedArmNums" })));
                    IEnumerable spawned = bridge.GetMember(fists, new[] { "spawnedArms" }) as IEnumerable;
                    sb.AppendLine("spawnedArms:");
                    if (spawned != null)
                        foreach (object arm in spawned)
                            sb.AppendLine("  " + DescribeObject(arm));
                    sb.AppendLine("fistPanels:");
                    IEnumerable panels = bridge.GetMember(fists, new[] { "fistPanels" }) as IEnumerable;
                    if (panels != null)
                    {
                        int i = 0;
                        foreach (object panel in panels)
                        {
                            sb.AppendLine("  [" + i + "] " + DescribeObject(panel));
                            i++;
                        }
                    }
                    object forced = bridge.GetMember(fists, new[] { "forcedLoadout" });
                    sb.AppendLine("forcedLoadout.arm: " + (forced == null ? "NULL" : Show(bridge.GetMember(bridge.GetMember(forced, new[] { "arm" }), new[] { "blueVariant" })) +
                        " / " + Show(bridge.GetMember(bridge.GetMember(forced, new[] { "arm" }), new[] { "redVariant" })) +
                        " / " + Show(bridge.GetMember(bridge.GetMember(forced, new[] { "arm" }), new[] { "greenVariant" }))));
                }

                sb.AppendLine("== PrefsManager");
                Type prefsType = bridge.FindType("PrefsManager");
                if (prefsType != null)
                {
                    UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(prefsType);
                    object prefs = all != null && all.Length > 0 ? all[0] : null;
                    if (prefs != null)
                    {
                        MethodInfo getBool = prefsType.GetMethod("GetBool", new[] { typeof(string), typeof(bool) });
                        if (getBool != null)
                            sb.AppendLine("armIcons: " + getBool.Invoke(prefs, new object[] { "armIcons", false }));
                        else
                            sb.AppendLine("armIcons: GetBool not found");
                    }
                    else
                    {
                        sb.AppendLine("PrefsManager instance not found");
                    }
                }
                else
                {
                    sb.AppendLine("PrefsManager type not found");
                }

                sb.AppendLine("== HudController instances");
                Type hudType = bridge.FindType("HudController");
                if (hudType == null)
                {
                    sb.AppendLine("HudController type not found");
                }
                else
                {
                    UnityEngine.Object[] huds = Resources.FindObjectsOfTypeAll(hudType);
                    sb.AppendLine("count: " + (huds == null ? 0 : huds.Length));
                    foreach (UnityEngine.Object hud in huds)
                    {
                        sb.AppendLine("-- " + DescribeObject(hud));
                        sb.AppendLine("   altHud: " + Show(bridge.GetMember(hud, new[] { "altHud" })));
                        sb.AppendLine("   armIcon: " + DescribeObject(bridge.GetMember(hud, new[] { "armIcon" })));
                        sb.AppendLine("   fistFill: " + DescribeObject(bridge.GetMember(hud, new[] { "fistFill" })));
                        sb.AppendLine("   fistBackground: " + DescribeObject(bridge.GetMember(hud, new[] { "fistBackground" })));
                        IEnumerable icons = bridge.GetMember(hud, new[] { "fistIcons" }) as IEnumerable;
                        sb.AppendLine("   fistIcons:");
                        if (icons != null)
                        {
                            int i = 0;
                            foreach (object icon in icons)
                            {
                                sb.AppendLine("     [" + i + "] " + DescribeObject(icon));
                                i++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("DUMP FAILED: " + ex);
            }

            string path = null;
            try
            {
                path = Path.Combine(BepInEx.Paths.BepInExRootPath, "uktas_fist_hud_dump.txt");
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                path = "write failed: " + ex.Message;
            }
            return path;
        }

        // TEMP: traces every SetActive(false) on a fist/arm HUD object with a stack trace.
        static HarmonyLib.Harmony _traceHarmony;
        static int _traceHits;

        public static void InstallTrace()
        {
            try
            {
                if (_traceHarmony != null)
                    return;
                _traceHarmony = new HarmonyLib.Harmony("com.tas.ultrakill.fisttrace");
                MethodInfo setActive = typeof(GameObject).GetMethod("SetActive", new[] { typeof(bool) });
                MethodInfo prefix = typeof(FistHudDump).GetMethod("SetActivePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                _traceHarmony.Patch(setActive, prefix: new HarmonyLib.HarmonyMethod(prefix));

                // TEMP: log the game methods that decide whether the fist panels are shown at all.
                PatchLogger("FistControl", "Start");
                PatchLogger("FistControl", "ResetFists");
                PatchLogger("HudController", "Start");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "uktas_fist_trace.txt"), "install failed: " + ex);
            }
        }

        static void PatchLogger(string typeName, string methodName)
        {
            try
            {
                Type type = HarmonyLib.AccessTools.TypeByName(typeName);
                MethodInfo method = type == null ? null : type.GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    Append("no method " + typeName + "." + methodName);
                    return;
                }
                string prefixName = null;
                if (typeName == "FistControl" && methodName == "Start") prefixName = "LogFistStart";
                else if (typeName == "FistControl" && methodName == "ResetFists") prefixName = "LogResetFists";
                else if (typeName == "HudController" && methodName == "Start") prefixName = "LogHudStart";
                MethodInfo logger = typeof(FistHudDump).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                _traceHarmony.Patch(method, prefix: new HarmonyLib.HarmonyMethod(logger));
            }
            catch (Exception ex)
            {
                Append("patch failed " + typeName + "." + methodName + ": " + ex.Message);
            }
        }

        static void LogFistStart()
        {
            Append("CALL FistControl.Start at " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        static void LogResetFists()
        {
            Append("CALL FistControl.ResetFists at " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        static void LogHudStart()
        {
            Append("CALL HudController.Start at " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        static void Append(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "uktas_fist_trace.txt"), text + "\n");
            }
            catch
            {
            }
        }

        static void SetActivePrefix(GameObject __instance, bool value)
        {
            if (__instance == null || _traceHits >= 80)
                return;
            string name = __instance.name;
            if (value)
            {
                // Log turning the HUD fist panel on, so a missing "on" shows up too.
                if (name == "FistPanel")
                    Append("SetActive(true) on FistPanel at " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return;
            }
            // Skip the spawned 3D arm clones (NoFist hides them every frame while not activated);
            // keep HUD objects, which is what is missing.
            if (name.EndsWith("(Clone)", StringComparison.Ordinal))
                return;
            // Per-frame noise from the 3D hook and revolver arm models; not the HUD.
            if (name.IndexOf("GreenArm", StringComparison.Ordinal) >= 0 || name.IndexOf("_NewArm", StringComparison.Ordinal) >= 0)
                return;
            if (name.IndexOf("Fist", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Arm", StringComparison.Ordinal) < 0)
                return;
            _traceHits++;
            try
            {
                string text = "---- SetActive(false) on " + name + " at " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "\n" +
                    new StackTrace(1, false).ToString() + "\n";
                File.AppendAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "uktas_fist_trace.txt"), text);
            }
            catch
            {
            }
        }

        static string Show(object value)
        {
            if (value == null)
                return "NULL";
            return value.ToString();
        }

        static string DescribeObject(object value)
        {
            if (value == null)
                return "NULL";
            GameObject go = value as GameObject;
            if (go == null)
            {
                Component c = value as Component;
                if (c != null)
                    go = c.gameObject;
            }
            if (go == null)
                return value.GetType().Name + " = " + value;
            string path = go.name;
            Transform t = go.transform.parent;
            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return path + "  activeSelf=" + go.activeSelf + " activeInHierarchy=" + go.activeInHierarchy;
        }
    }
}
