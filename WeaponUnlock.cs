using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace UltrakillTAS
{
    /// <summary>
    /// In-memory weapon and arm ownership overrides, plus the shared gear and loadout vocabulary
    /// used by the script headers ("weapons ..." and "loadout ...").
    ///
    /// ULTRAKILL decides which weapons exist in two places: GameProgressSaver.CheckGear(key),
    /// which re-reads the save file on every call, and the "weapon.&lt;key&gt;" preference read
    /// through PrefsManager.GetInt. Two Harmony patches answer those reads from memory instead:
    ///   - <see cref="UnlockAll"/> reports every weapon and arm as owned.
    ///   - <see cref="OwnedOverride"/> reports exactly the listed keys as owned (a script's
    ///     "weapons" header). It wins over UnlockAll so a script plays with its own gear.
    /// Nothing is ever written to a save file, and nothing is patched while both are off.
    /// </summary>
    public static class WeaponUnlock
    {
        /// <summary>
        /// Every gear key GameProgressSaver.CheckGear is asked about for weapons and arms:
        /// variants 0-2 per gun (rev = revolver, sho = shotgun, nai = nailgun, rai = railcannon,
        /// rock = rocket launcher), the alt-fire unlocks and the arms. Order is the canonical
        /// header order.
        /// </summary>
        public static readonly string[] GearKeys =
        {
            "rev0", "rev1", "rev2", "revalt",
            "sho0", "sho1", "sho2", "shoalt",
            "nai0", "nai1", "nai2", "naialt",
            "rai0", "rai1", "rai2",
            "rock0", "rock1", "rock2",
            "arm1", "arm2", "arm3"
        };

        /// <summary>ForcedLoadout field names, in canonical header order.</summary>
        public static readonly string[] LoadoutFields =
        {
            "revolver", "altRevolver", "shotgun", "altShotgun",
            "nailgun", "altNailgun", "railcannon", "rocketLauncher", "arm"
        };

        static readonly HashSet<string> GearSet = new HashSet<string>(GearKeys, StringComparer.Ordinal);

        static bool _unlockAll;
        static HashSet<string> _ownedOverride;
        static Harmony _harmony;
        static bool _patched;
        static bool _gaveUp;

        /// <summary>Report every weapon and arm as owned (in memory only).</summary>
        public static bool UnlockAll
        {
            get { return _unlockAll; }
            set
            {
                _unlockAll = value;
                Refresh();
            }
        }

        /// <summary>
        /// Exactly these gear keys are owned (a script's "weapons" header). Null = no override: the
        /// player's real ownership (or <see cref="UnlockAll"/>) applies.
        /// </summary>
        public static HashSet<string> OwnedOverride
        {
            get { return _ownedOverride; }
            set
            {
                _ownedOverride = value;
                Refresh();
            }
        }

        /// <summary>True once the Harmony patches are installed.</summary>
        public static bool Patched { get { return _patched; } }

        public static bool IsGearKey(string key)
        {
            return key != null && GearSet.Contains(key);
        }

        /// <summary>Case-insensitive lookup of a ForcedLoadout field name. False for an unknown name.</summary>
        public static bool TryNormalizeLoadoutField(string name, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(name))
                return false;
            for (int i = 0; i < LoadoutFields.Length; i++)
            {
                if (string.Equals(LoadoutFields[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = LoadoutFields[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>VariantOption number to its header character: 0 i (IfEquipped), 1 o (ForceOn), 2 x (ForceOff).</summary>
        public static char OptionToChar(int option)
        {
            return option == 1 ? 'o' : (option == 2 ? 'x' : 'i');
        }

        /// <summary>Header character (i, o or x, any case) to its VariantOption number.</summary>
        public static bool TryOptionFromChar(char c, out int option)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'i': option = 0; return true;
                case 'o': option = 1; return true;
                case 'x': option = 2; return true;
                default: option = 0; return false;
            }
        }

        /// <summary>
        /// Finds a game type by simple name, including static classes (GameBridge.FindType skips
        /// abstract types, and a C# static class is abstract in IL). Game assembly only.
        /// </summary>
        public static Type FindGameType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
                return null;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < assemblies.Length; a++)
                {
                    if (!assemblies[a].GetName().Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal))
                        continue;
                    Type direct = assemblies[a].GetType(simpleName, false);
                    if (direct != null)
                        return direct;
                    Type[] types;
                    try
                    {
                        types = assemblies[a].GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types;
                    }
                    for (int t = 0; types != null && t < types.Length; t++)
                    {
                        if (types[t] != null && types[t].Name == simpleName)
                            return types[t];
                    }
                }
            }
            catch
            {
                // the caller reports a missing type
            }
            return null;
        }

        /// <summary>
        /// Installs the patches the first time an override is needed. Safe to call every frame or
        /// scene: it returns at once when nothing is needed, already patched, or patching failed.
        /// </summary>
        public static void Refresh()
        {
            if (_patched || _gaveUp)
                return;
            if (!_unlockAll && _ownedOverride == null)
                return;
            TryPatch();
        }

        static void TryPatch()
        {
            Plugin plugin = Plugin.State;
            try
            {
                Type saver = FindGameType("GameProgressSaver");
                Type prefs = FindGameType("PrefsManager");
                if (saver == null || prefs == null)
                    return; // game assembly not ready: Refresh tries again later

                MethodInfo checkGear = saver.GetMethod("CheckGear",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(string) }, null);
                MethodInfo getInt = prefs.GetMethod("GetInt",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(int) }, null);
                if (checkGear == null)
                {
                    _gaveUp = true;
                    if (!ReferenceEquals(plugin, null))
                        plugin.LogWarning("GameProgressSaver.CheckGear(string) not found; weapon unlock is unavailable.");
                    return;
                }

                _harmony = new Harmony("com.tas.ultrakill.weapons");
                _harmony.Patch(checkGear, prefix: new HarmonyMethod(
                    typeof(WeaponUnlock).GetMethod("CheckGearPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                if (getInt != null)
                {
                    _harmony.Patch(getInt, postfix: new HarmonyMethod(
                        typeof(WeaponUnlock).GetMethod("GetIntPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                }
                else if (!ReferenceEquals(plugin, null))
                {
                    plugin.LogWarning("PrefsManager.GetInt(string, int) not found; disabled weapon preferences are not overridden.");
                }
                _patched = true;
                if (!ReferenceEquals(plugin, null))
                    plugin.LogInfo("Weapon ownership patches installed (in-memory only, save files untouched).");
            }
            catch (Exception ex)
            {
                _gaveUp = true;
                if (!ReferenceEquals(plugin, null))
                    plugin.LogWarning("Could not patch weapon ownership: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Harmony prefix on GameProgressSaver.CheckGear(string): answers weapon and arm gear keys.
        static bool CheckGearPrefix(string __0, ref int __result)
        {
            HashSet<string> owned = _ownedOverride;
            if (owned == null && !_unlockAll)
                return true;
            if (__0 == null || !GearSet.Contains(__0))
                return true;
            // Arms keep the real save ownership. Granting an arm the save does not have makes
            // FistControl.CheckFist instantiate a prefab that is not loaded, which throws inside
            // ResetFists and leaves the fist HUD panels off.
            if (__0.StartsWith("arm", StringComparison.Ordinal))
                return true;
            __result = owned != null ? (owned.Contains(__0) ? 1 : 0) : 1;
            return false;
        }

        // Harmony postfix on PrefsManager.GetInt(string, int): a weapon the player switched off in
        // the settings (weapon.<key> = 0) still counts as enabled. A nonzero value is left alone
        // because 2 selects the alt variant. Runs on every pref read, so it exits early.
        static void GetIntPostfix(string __0, ref int __result)
        {
            if (__result != 0 || __0 == null)
                return;
            HashSet<string> owned = _ownedOverride;
            if (owned == null && !_unlockAll)
                return;
            if (!__0.StartsWith("weapon.", StringComparison.Ordinal))
                return;
            if (owned != null)
            {
                if (owned.Contains(__0.Substring("weapon.".Length)))
                    __result = 1;
                return;
            }
            __result = 1;
        }
    }
}
