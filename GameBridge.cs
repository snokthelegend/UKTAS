using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// Reflection bridge to ULTRAKILL's Assembly-CSharp.
    ///
    /// The mod deliberately does not compile against Assembly-CSharp: ULTRAKILL
    /// renames private members between patches, and a hard reference would turn
    /// every game update into a build failure. Instead every game member is
    /// resolved by name with candidate lists and cached. Verified members used
    /// here (from ULTRAKILL mod/TAS sources):
    ///
    ///   NewMovement.rb                      Rigidbody
    ///   CameraController.rotationX/rotationY public float (logical pitch/yaw)
    ///   CameraController.cam                 Camera
    ///   GunControl.currentSlot               int
    ///   InputManager.inputsDictionary         Dictionary&lt;string, KeyCode&gt;
    ///   InputManager.InputSource.&lt;Action&gt;.IsPressed / .PerformedFrame
    ///   OptionsManager.paused                bool
    ///
    /// Unverified members (hp, stamina, style, gun variation, ...) are attempted
    /// through a candidate list and reported as missing in the Diagnostics tab
    /// instead of throwing. Everything degrades gracefully.
    /// </summary>
    public sealed class GameBridge
    {
        readonly Plugin _plugin;

        Type _newMovementType;
        Type _cameraControllerType;
        Type _gunControlType;
        Type _inputManagerType;
        Type _optionsManagerType;
        Type _monoSingletonType;
        Type _baseInputType;
        Type _fistControlType;
        Type _sceneHelperType;
        bool _sceneHelperSearched;

        object _newMovement;
        object _cameraController;
        object _gunControl;
        object _inputManager;
        object _optionsManager;
        object _fistControl;

        readonly Dictionary<Type, Dictionary<string, MemberInfo>> _memberCache =
            new Dictionary<Type, Dictionary<string, MemberInfo>>();
        readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        readonly Dictionary<string, float> _typeMissTime = new Dictionary<string, float>();

        int _lastResolveFrame = -1000;
        string _sceneName = string.Empty;

        public bool TypesResolved { get; private set; }
        public string LastError { get; private set; }

        public GameBridge(Plugin plugin)
        {
            _plugin = plugin;
        }

        public Type NewMovementType { get { ResolveTypes(); return _newMovementType; } }
        public Type CameraControllerType { get { ResolveTypes(); return _cameraControllerType; } }
        public Type GunControlType { get { ResolveTypes(); return _gunControlType; } }
        public Type InputManagerType { get { ResolveTypes(); return _inputManagerType; } }
        public Type OptionsManagerType { get { ResolveTypes(); return _optionsManagerType; } }
        public Type BaseInputType { get { ResolveTypes(); return _baseInputType; } }

        public object NewMovement { get { return ResolveSingleton(ref _newMovement, _newMovementType ?? (_newMovementType = FindType("NewMovement"))); } }
        public object CameraController { get { return ResolveSingleton(ref _cameraController, _cameraControllerType ?? (_cameraControllerType = FindType("CameraController"))); } }
        public object GunControl { get { return ResolveSingleton(ref _gunControl, _gunControlType ?? (_gunControlType = FindType("GunControl"))); } }
        public object InputManager { get { return ResolveSingleton(ref _inputManager, _inputManagerType ?? (_inputManagerType = FindType("InputManager"))); } }
        public object OptionsManager { get { return ResolveSingleton(ref _optionsManager, _optionsManagerType ?? (_optionsManagerType = FindType("OptionsManager"))); } }
        public object FistControl { get { return ResolveSingleton(ref _fistControl, _fistControlType ?? (_fistControlType = FindType("FistControl"))); } }

        Type _weaponChargesType;
        object _weaponCharges;
        public object WeaponCharges { get { return ResolveSingleton(ref _weaponCharges, _weaponChargesType ?? (_weaponChargesType = FindType("WeaponCharges"))); } }

        Type _statsManagerType;
        object _statsManager;
        public object StatsManager { get { return ResolveSingleton(ref _statsManager, _statsManagerType ?? (_statsManagerType = FindType("StatsManager"))); } }

        /// <summary>
        /// The level timer the game shows the player: StatsManager.seconds, the float the
        /// Tab level-stats panel (LevelStats) and the end-of-level FinalRank screen print as m:ss.fff.
        /// False when there is no StatsManager (title screen, loading) or the field is missing.
        /// </summary>
        public bool TryGetLevelTime(out float seconds)
        {
            seconds = 0f;
            object stats = StatsManager;
            if (stats == null)
                return false;
            object value = GetMember(stats, new[] { "seconds" });
            if (value == null)
                return false;
            seconds = ToFloat(value, 0f);
            return true;
        }

        Type _challengeManagerType;
        object _challengeManager;
        public object ChallengeManager { get { return ResolveSingleton(ref _challengeManager, _challengeManagerType ?? (_challengeManagerType = FindType("ChallengeManager"))); } }

        /// <summary>One reading of the level stats, in the game's own terms. Ranks are letters D C B A S (P for the total).</summary>
        public sealed class LevelStatsInfo
        {
            public int Kills;
            public int Score;
            public float Seconds;
            public int Restarts;
            public string KillsRank = "--";
            public string ScoreRank = "--";
            public string TimeRank = "--";
            public string TotalRank = "--";
            /// <summary>Value needed for an S in each category (the P rank needs all three S); -1 when unknown.</summary>
            public int KillsForS = -1;
            public int ScoreForS = -1;
            public int TimeForS = -1;
            /// <summary>True while the end-of-level screen (FinalRank) is showing and the ranks come from it.</summary>
            public bool EndScreen;
            /// <summary>-1 unknown (no ChallengeManager), 0 not done, 1 done, 2 failed.</summary>
            public int Challenge = -1;
        }

        static readonly string[] RankLetters = { "D", "C", "B", "A", "S" };

        /// <summary>
        /// StatsManager.GetRanks without its side effect: how many of the thresholds the value
        /// has passed, in order (0 D .. 3 A, 4 S). Time is "less than or equal" (reverse), kills
        /// and style "greater than or equal". -1 when the thresholds are missing.
        /// </summary>
        static int RankIndex(int[] thresholds, float value, bool reverse)
        {
            if (thresholds == null || thresholds.Length == 0)
                return -1;
            int index = 0;
            while (index < thresholds.Length && (reverse ? value <= thresholds[index] : value >= thresholds[index]))
                index++;
            return Math.Min(index, 4);
        }

        static string RankLetter(int index)
        {
            return index >= 0 && index < RankLetters.Length ? RankLetters[index] : "--";
        }

        /// <summary>The visible text of a TMP_Text field on <paramref name="owner"/>, without rich-text tags; empty when missing.</summary>
        string ReadScreenText(object owner, string field)
        {
            object label = GetMember(owner, new[] { field });
            if (!IsAlive(label))
                return string.Empty;
            string text = GetMember(label, new[] { "text" }) as string;
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", string.Empty).Trim();
        }

        /// <summary>
        /// Level stats as the game's stats screens show them. Values come from StatsManager
        /// (kills, stylePoints, seconds, restarts) and its rank thresholds (killRanks, styleRanks,
        /// timeRanks); the ranks use StatsManager.GetRanks' rule, and the total follows
        /// GetFinalRank (sum of the three category ranks minus restarts; 12 is P; cheats show "_").
        /// Once the end screen is up (StatsManager.infoSent) the letters are read from the
        /// FinalRank labels instead. The challenge is ChallengeManager.challengeDone/challengeFailed,
        /// as LevelStats prints it. Major assists are not read. False without a StatsManager.
        /// </summary>
        public bool TryGetLevelStats(out LevelStatsInfo info)
        {
            info = null;
            object stats = StatsManager;
            if (stats == null)
                return false;
            object seconds = GetMember(stats, new[] { "seconds" });
            object kills = GetMember(stats, new[] { "kills" });
            object style = GetMember(stats, new[] { "stylePoints" });
            if (seconds == null || kills == null || style == null)
                return false;

            LevelStatsInfo result = new LevelStatsInfo();
            result.Seconds = ToFloat(seconds, 0f);
            result.Kills = ToInt(kills, 0);
            result.Score = ToInt(style, 0);
            result.Restarts = ToInt(GetMember(stats, new[] { "restarts" }), 0);

            int[] killRanks = GetMember(stats, new[] { "killRanks" }) as int[];
            int[] styleRanks = GetMember(stats, new[] { "styleRanks" }) as int[];
            int[] timeRanks = GetMember(stats, new[] { "timeRanks" }) as int[];
            int killIndex = RankIndex(killRanks, result.Kills, false);
            int scoreIndex = RankIndex(styleRanks, result.Score, false);
            int timeIndex = RankIndex(timeRanks, result.Seconds, true);
            result.KillsRank = RankLetter(killIndex);
            result.ScoreRank = RankLetter(scoreIndex);
            result.TimeRank = RankLetter(timeIndex);
            if (killRanks != null && killRanks.Length > 0)
                result.KillsForS = killRanks[killRanks.Length - 1];
            if (styleRanks != null && styleRanks.Length > 0)
                result.ScoreForS = styleRanks[styleRanks.Length - 1];
            if (timeRanks != null && timeRanks.Length > 0)
                result.TimeForS = timeRanks[timeRanks.Length - 1];

            // Total rank preview: GetFinalRank's rule (major assists left out).
            bool cheats = false;
            object assist = GetMember(stats, new[] { "asscon" });
            if (IsAlive(assist))
                cheats = ToBool(GetMember(assist, new[] { "cheatsEnabled" }), false);
            if (cheats)
            {
                result.TotalRank = "_";
            }
            else if (killIndex >= 0 && scoreIndex >= 0 && timeIndex >= 0)
            {
                int total = Math.Max(0, killIndex + scoreIndex + timeIndex - result.Restarts);
                if (total == 12)
                    result.TotalRank = "P";
                else
                    result.TotalRank = RankLetter(Math.Min(4, Mathf.RoundToInt(total / 3f)));
            }

            // End screen up: show exactly what the labels say.
            if (ToBool(GetMember(stats, new[] { "infoSent" }), false))
            {
                object screen = GetMember(stats, new[] { "fr" });
                if (IsAlive(screen))
                {
                    string timeText = ReadScreenText(screen, "timeRank");
                    string killsText = ReadScreenText(screen, "killsRank");
                    string styleText = ReadScreenText(screen, "styleRank");
                    string totalText = ReadScreenText(screen, "totalRank");
                    if (timeText.Length > 0) result.TimeRank = timeText;
                    if (killsText.Length > 0) result.KillsRank = killsText;
                    if (styleText.Length > 0) result.ScoreRank = styleText;
                    if (totalText.Length > 0) result.TotalRank = totalText;
                    result.EndScreen = true;
                }
            }

            object challenges = ChallengeManager;
            if (challenges != null)
            {
                bool done = ToBool(GetMember(challenges, new[] { "challengeDone" }), false);
                bool failed = ToBool(GetMember(challenges, new[] { "challengeFailed" }), false);
                result.Challenge = failed ? 2 : (done ? 1 : 0);
            }

            info = result;
            return true;
        }

        EntityRegistry _entities;

        /// <summary>Per-level entity IDs used by look_at and the entity inspector.</summary>
        public EntityRegistry Entities { get { return _entities ?? (_entities = new EntityRegistry(_plugin, this)); } }

        public Component PlayerComponent { get { return NewMovement as Component; } }
        public Transform PlayerTransform
        {
            get
            {
                Component component = PlayerComponent;
                return component != null ? component.transform : null;
            }
        }

        /// <summary>
        /// ULTRAKILL gates movement during elevator drops and intro cutscenes
        /// through NewMovement.activated. If the member is unavailable on a newer
        /// build, assume control is available rather than deadlocking startup.
        /// </summary>
        public bool PlayerControlActivated
        {
            get
            {
                object player = NewMovement;
                if (player == null)
                    return false;
                object value = GetMember(player, new[] { "activated", "active", "canMove", "movementEnabled" });
                return value == null || ToBool(value, true);
            }
        }

        public bool TryGetPlayerPosition(out Vector3 position)
        {
            Transform transform = PlayerTransform;
            if (transform == null)
            {
                position = Vector3.zero;
                return false;
            }
            position = transform.position;
            return true;
        }

        public float PlayerY
        {
            get
            {
                Vector3 position;
                return TryGetPlayerPosition(out position) ? position.y : 0f;
            }
        }

        public bool PlayerBelowKillPlane
        {
            get
            {
                Vector3 position;
                return TryGetPlayerPosition(out position) && position.y < _plugin.KillPlaneY.Value;
            }
        }

        public string SceneName
        {
            get
            {
                if (string.IsNullOrEmpty(_sceneName))
                    _sceneName = SceneManager.GetActiveScene().name;
                return _sceneName;
            }
        }

        /// <summary>ULTRAKILL's own scene loader, resolved lazily and only once.</summary>
        public Type SceneHelperType
        {
            get
            {
                if (!_sceneHelperSearched)
                {
                    _sceneHelperSearched = true;
                    _sceneHelperType = FindType("SceneHelper");
                }
                return _sceneHelperType;
            }
        }

        /// <summary>
        /// Scene name of the level the player is in. ULTRAKILL names level scenes
        /// "Level 1-4" and keeps the last one in SceneHelper.CurrentScene, which
        /// still answers after returning to a menu.
        /// </summary>
        public string CurrentLevelScene
        {
            get
            {
                string active = SceneName;
                if (LevelIdFromScene(active).Length > 0)
                    return active;
                string remembered = ReadStaticSceneHelperString("CurrentScene");
                return string.IsNullOrEmpty(remembered) ? active : remembered;
            }
        }

        /// <summary>Campaign level id of the current scene ("1-4"), or empty in a menu.</summary>
        public string CurrentLevelId
        {
            get { return LevelIdFromScene(CurrentLevelScene); }
        }

        /// <summary>Turn a scene name or level id into an id: "Level 1-4" / "1-4" -&gt; "1-4".</summary>
        public static string LevelIdFromScene(string scene)
        {
            if (string.IsNullOrEmpty(scene))
                return string.Empty;
            string text = scene.Trim();
            if (text.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - ".unity".Length).Trim();
            if (text.StartsWith("Level ", StringComparison.OrdinalIgnoreCase))
                text = text.Substring("Level ".Length).Trim();
            if (text.Length < 3 || !char.IsDigit(text[0]) || text.IndexOf('-') < 0)
                return string.Empty;
            return text;
        }

        string ReadStaticSceneHelperString(string propertyName)
        {
            try
            {
                Type type = SceneHelperType;
                if (type == null)
                    return null;
                PropertyInfo property = type.GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (property == null)
                    return null;
                return property.GetValue(null, null) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Clear cached singletons after a scene change so they are resolved again lazily.</summary>
        public void InvalidateScene()
        {
            _sceneName = SceneManager.GetActiveScene().name;
            _newMovement = null;
            _cameraController = null;
            _gunControl = null;
            _inputManager = null;
            _optionsManager = null;
            _fistControl = null;
            _gunSetter = null;
            _singletonMissFrame.Clear();
        }

        void ResolveTypes()
        {
            if (TypesResolved)
                return;
            if (Time.frameCount - _lastResolveFrame < 30)
                return;
            _lastResolveFrame = Time.frameCount;
            _newMovementType = _newMovementType ?? FindType("NewMovement");
            _cameraControllerType = _cameraControllerType ?? FindType("CameraController");
            _gunControlType = _gunControlType ?? FindType("GunControl");
            _inputManagerType = _inputManagerType ?? FindType("InputManager");
            _optionsManagerType = _optionsManagerType ?? FindType("OptionsManager");
            _monoSingletonType = _monoSingletonType ?? FindType("MonoSingleton`1");
            _baseInputType = _baseInputType ?? FindType("BaseInput");
            TypesResolved = _newMovementType != null && _cameraControllerType != null &&
                            _gunControlType != null && _inputManagerType != null;
        }

        object ResolveSingleton(ref object cache, Type type)
        {
            if (type == null)
                return null;
            if (IsAlive(cache))
                return cache;

            cache = null;

            // Nothing was found recently (main menu, loading): do not rerun a
            // scene-wide FindObjectOfType several times per frame.
            int missFrame;
            if (_singletonMissFrame.TryGetValue(type, out missFrame) && Time.frameCount - missFrame < 20)
                return null;

            try
            {
                if (_monoSingletonType == null)
                    _monoSingletonType = FindType("MonoSingleton`1");
                if (_monoSingletonType != null)
                {
                    Type closed = _monoSingletonType.MakeGenericType(type);
                    PropertyInfo property = closed.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (property != null)
                    {
                        object value = property.GetValue(null, null);
                        if (IsAlive(value))
                        {
                            cache = value;
                            return cache;
                        }
                    }
                }

                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(type);
                if (found != null)
                    cache = found;
            }
            catch (Exception ex)
            {
                LastError = "Singleton resolve failed for " + type.Name + ": " + ex.Message;
            }
            if (cache == null)
                _singletonMissFrame[type] = Time.frameCount;
            else
                _singletonMissFrame.Remove(type);
            return cache;
        }

        readonly Dictionary<Type, int> _singletonMissFrame = new Dictionary<Type, int>();

        public static bool IsAlive(object value)
        {
            if (value == null)
                return false;
            // A destroyed Unity object still has a managed reference, so use the
            // overloaded == operator instead of a plain null check.
            if (value is UnityEngine.Object)
                return (UnityEngine.Object)value != null;
            return true;
        }

        /// <summary>Find a type by simple name in every loaded assembly. Handles ReflectionTypeLoadException.</summary>
        public Type FindType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
                return null;
            Type cached;
            if (_typeCache.TryGetValue(simpleName, out cached) && cached != null)
                return cached;

            // A recent miss is not rescanned for a second. Misses used to be cached forever,
            // which hid the player for the whole session when a lookup ran before the game's
            // assembly had loaded.
            float lastMiss;
            // Each miss rescans every assembly (tens of milliseconds on the game's assemblies), so
            // misses are retried rarely. Scanning every second caused hitches during level loads.
            if (_typeMissTime.TryGetValue(simpleName, out lastMiss) && Time.unscaledTime - lastMiss < 5f)
                return null;

            // Game assemblies first: names like Door, Coin, Beam and Projectile also
            // exist in other libraries, and the first match must be ULTRAKILL's.
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Array.Sort(assemblies, (x, y) =>
            {
                int rx = x.GetName().Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal) ? 0 : 1;
                int ry = y.GetName().Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal) ? 0 : 1;
                return rx.CompareTo(ry);
            });
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }
                if (types == null)
                    continue;
                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type != null && type.Name == simpleName && !type.IsAbstract && !type.ContainsGenericParameters)
                    {
                        _typeCache[simpleName] = type;
                        return type;
                    }
                }
            }
            _typeMissTime[simpleName] = Time.unscaledTime;
            return null;
        }

        // ------------------------------------------------------------------
        // Generic member helpers
        // ------------------------------------------------------------------

        public MemberInfo FindMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;
            Dictionary<string, MemberInfo> map;
            if (!_memberCache.TryGetValue(type, out map))
            {
                map = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                _memberCache[type] = map;
            }
            MemberInfo cached;
            if (map.TryGetValue(name, out cached))
                return cached;

            Type current = type;
            while (current != null && current != typeof(MonoBehaviour) && current != typeof(object))
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    map[name] = field;
                    return field;
                }
                PropertyInfo property = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    map[name] = property;
                    return property;
                }
                current = current.BaseType;
            }
            map[name] = null;
            return null;
        }

        public MemberInfo FindMember(object target, string[] candidates)
        {
            if (target == null || candidates == null)
                return null;
            Type type = target.GetType();
            for (int i = 0; i < candidates.Length; i++)
            {
                MemberInfo member = FindMember(type, candidates[i]);
                if (member != null)
                    return member;
            }
            return null;
        }

        public object GetMemberValue(object target, MemberInfo member)
        {
            if (target == null || member == null)
                return null;
            try
            {
                FieldInfo field = member as FieldInfo;
                if (field != null)
                    return field.GetValue(target);
                PropertyInfo property = member as PropertyInfo;
                if (property != null && property.CanRead)
                    return property.GetValue(target, null);
            }
            catch (Exception ex)
            {
                LastError = "Get " + member.Name + " failed: " + ex.Message;
            }
            return null;
        }

        public object GetMember(object target, string[] candidates)
        {
            MemberInfo member = FindMember(target, candidates);
            return member == null ? null : GetMemberValue(target, member);
        }

        public bool SetMemberValue(object target, MemberInfo member, object value)
        {
            if (target == null || member == null)
                return false;
            try
            {
                FieldInfo field = member as FieldInfo;
                if (field != null)
                {
                    field.SetValue(target, Coerce(value, field.FieldType));
                    return true;
                }
                PropertyInfo property = member as PropertyInfo;
                if (property != null && property.CanWrite)
                {
                    property.SetValue(target, Coerce(value, property.PropertyType), null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = "Set " + member.Name + " failed: " + ex.Message;
            }
            return false;
        }

        public bool SetMember(object target, string[] candidates, object value)
        {
            MemberInfo member = FindMember(target, candidates);
            return member != null && SetMemberValue(target, member, value);
        }

        static object Coerce(object value, Type targetType)
        {
            if (value == null)
                return null;
            if (targetType.IsInstanceOfType(value))
                return value;
            if (targetType.IsEnum)
                return Enum.ToObject(targetType, Convert.ToInt64(value));
            if (targetType == typeof(float))
                return Convert.ToSingle(value);
            if (targetType == typeof(int))
                return Convert.ToInt32(value);
            if (targetType == typeof(bool))
                return Convert.ToBoolean(value);
            if (targetType == typeof(double))
                return Convert.ToDouble(value);
            return value;
        }

        public static float ToFloat(object value, float fallback)
        {
            if (value == null)
                return fallback;
            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return fallback;
            }
        }

        public static int ToInt(object value, int fallback)
        {
            if (value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        public static bool ToBool(object value, bool fallback)
        {
            if (value == null)
                return fallback;
            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return fallback;
            }
        }

        // ------------------------------------------------------------------
        // Player / camera / weapons
        // ------------------------------------------------------------------

        public Rigidbody PlayerRigidbody
        {
            get
            {
                object player = NewMovement;
                if (player == null)
                    return null;
                object body = GetMember(player, new[] { "rb", "body", "rigidbody", "rigidBody" });
                Rigidbody rigidbody = body as Rigidbody;
                if (rigidbody != null)
                    return rigidbody;
                Component component = player as Component;
                return component != null ? component.GetComponent<Rigidbody>() : null;
            }
        }

        /// <summary>The horizontal direction NewMovement drives a slide with, for resume diagnostics.</summary>
        public bool TryGetSlideDir(out Vector3 direction)
        {
            direction = Vector3.zero;
            object player = NewMovement;
            if (player == null)
                return false;
            object value = GetMember(player, new[] { "slideDir", "dodgeDir" });
            if (value == null)
                return false;
            direction = (Vector3)value;
            return true;
        }

        public bool TryGetPlayerVelocity(out Vector3 velocity)
        {
            Rigidbody body = PlayerRigidbody;
            if (body != null)
            {
                velocity = body.velocity;
                return true;
            }
            velocity = Vector3.zero;
            return false;
        }

        public bool SetPlayerVelocity(Vector3 velocity)
        {
            Rigidbody body = PlayerRigidbody;
            // Kinematic bodies reject velocity writes (Unity logs a warning and ignores it).
            if (body == null || body.isKinematic)
                return false;
            body.velocity = velocity;
            return true;
        }

        public bool TryGetCameraAngles(out float pitch, out float yaw)
        {
            pitch = 0f;
            yaw = 0f;
            object controller = CameraController;
            if (controller != null)
            {
                object p = GetMember(controller, new[] { "rotationX", "pitch", "xRot", "rotX" });
                object y = GetMember(controller, new[] { "rotationY", "yaw", "yRot", "rotY" });
                if (p != null && y != null)
                {
                    // Script pitch is positive = down; the game's rotationX is positive = up.
                    pitch = -ToFloat(p, 0f);
                    yaw = ToFloat(y, 0f);
                    return true;
                }

                object cameraValue = GetMember(controller, new[] { "cam", "camera", "camTransform" });
                Camera camera = cameraValue as Camera;
                Transform cameraTransform = cameraValue as Transform;
                if (camera != null)
                    cameraTransform = camera.transform;
                Transform player = PlayerTransform;
                if (cameraTransform != null && player != null)
                {
                    pitch = NormalizePitch(cameraTransform.eulerAngles.x);
                    yaw = player.eulerAngles.y;
                    return true;
                }
            }

            Transform playerTransform = PlayerTransform;
            if (playerTransform != null)
            {
                pitch = 0f;
                yaw = playerTransform.eulerAngles.y;
                return true;
            }
            return false;
        }

        public bool SetCameraAngles(float pitch, float yaw)
        {
            pitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            bool applied = false;
            object controller = CameraController;
            if (controller != null)
            {
                MemberInfo pitchMember = FindMember(controller, new[] { "rotationX", "pitch", "xRot", "rotX" });
                MemberInfo yawMember = FindMember(controller, new[] { "rotationY", "yaw", "yRot", "rotY" });
                if (pitchMember != null && yawMember != null)
                {
                    // Script pitch is positive = down; the game's rotationX is positive = up.
                    applied = SetMemberValue(controller, pitchMember, -pitch);
                    applied &= SetMemberValue(controller, yawMember, yaw);
                }
            }

            // Fallback for a game update that renames the logical angle fields.
            // Only used when the controller fields could not be written; writing
            // transforms while the controller is active would fight its own
            // smoothing on every frame.
            if (!applied)
            {
                Transform cameraTransform;
                if (TryGetCameraTransform(out cameraTransform))
                {
                    cameraTransform.localEulerAngles = new Vector3(pitch, 0f, 0f);
                    applied = true;
                }
                Transform player = PlayerTransform;
                if (player != null)
                {
                    player.rotation = Quaternion.Euler(0f, yaw, 0f);
                    applied = true;
                }
            }
            return applied;
        }

        /// <summary>
        /// Direct transform apply used only by the paused free-look path, where
        /// CameraController.Update/LateUpdate are intentionally skipped.
        /// </summary>
        public bool ApplyCameraTransform(float pitch, float yaw)
        {
            bool applied = false;
            Transform cameraTransform;
            if (TryGetCameraTransform(out cameraTransform))
            {
                cameraTransform.localEulerAngles = new Vector3(Mathf.Clamp(pitch, -89.9f, 89.9f), 0f, 0f);
                applied = true;
            }
            Transform player = PlayerTransform;
            if (player != null)
            {
                player.rotation = Quaternion.Euler(0f, yaw, 0f);
                applied = true;
            }
            return applied;
        }

        public bool TryGetCameraTransform(out Transform transform)
        {
            transform = null;
            object controller = CameraController;
            if (controller == null)
                return false;
            object cameraValue = GetMember(controller, new[] { "cam", "camera", "camTransform" });
            Camera camera = cameraValue as Camera;
            if (camera != null)
            {
                transform = camera.transform;
                return true;
            }
            transform = cameraValue as Transform;
            return transform != null;
        }

        static float NormalizePitch(float eulerX)
        {
            float value = eulerX;
            if (value > 180f)
                value -= 360f;
            return Mathf.Clamp(value, -89.9f, 89.9f);
        }

        public int GunSlot
        {
            get
            {
                object gun = GunControl;
                if (gun == null)
                    return -1;
                return ToInt(GetMember(gun, new[] { "currentSlotIndex", "currentSlot", "slot", "weaponIndex" }), -1);
            }
            set
            {
                object gun = GunControl;
                if (gun != null)
                    SetMember(gun, new[] { "currentSlotIndex", "currentSlot", "slot" }, value);
            }
        }

        public int GunVariation
        {
            get
            {
                object gun = GunControl;
                if (gun == null)
                    return -1;
                return ToInt(GetMember(gun, new[] { "currentVariationIndex", "currentVariation", "variation", "currentVariant", "variant" }), -1);
            }
            set
            {
                object gun = GunControl;
                if (gun != null)
                    SetMember(gun, new[] { "currentVariationIndex", "currentVariation", "currentVariant" }, value);
            }
        }

        /// <summary>Switch weapon by 1-based slot. Uses the game's own method when available.</summary>
        public bool SwitchWeaponSlot(int oneBasedSlot)
        {
            object gun = GunControl;
            if (gun == null)
                return false;

            string[] methodNames = { "SwitchWeapon", "ChangeWeapon", "SetWeapon", "SwitchGun", "ChangeGun" };
            for (int i = 0; i < methodNames.Length; i++)
            {
                MethodInfo method = FindMethod(gun.GetType(), methodNames[i], 1);
                if (method == null)
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;
                try
                {
                    object arg = parameters[0].ParameterType == typeof(int)
                        ? (object)(oneBasedSlot - 1)
                        : (object)oneBasedSlot;
                    method.Invoke(gun, new[] { arg });
                    return true;
                }
                catch
                {
                    // fall through to the field write
                }
            }

            GunSlot = oneBasedSlot - 1;
            return true;
        }

        public bool SetGunVariation(int oneBasedVariation)
        {
            object gun = GunControl;
            if (gun == null)
                return false;
            return SetMember(gun, new[] { "currentVariation", "variation", "currentVariant", "variant" },
                Math.Max(0, oneBasedVariation - 1));
        }

        /// <summary>
        /// Re-equip the equipped weapon variation through the game's own variation-bind path,
        /// GunControl.SwitchWeapon(currentSlot, variation). Passing the slot and variation that
        /// are already equipped still runs the game's deactivate/activate sequence, so the
        /// weapon's firing cooldown resets on this tick. Used only when the InputSource action
        /// state cannot be written (older builds or a renamed state wrapper).
        /// </summary>
        public bool TriggerVariationBind(int zeroBasedVariation)
        {
            object gun = GunControl;
            if (gun == null)
                return false;
            // GunControl.currentSlotIndex is already 1-based (SwitchWeapon clamps it 1..slots).
            int slot = ToInt(GetMember(gun, new[] { "currentSlotIndex", "currentSlot", "slot" }), -1);
            if (slot < 1)
                return false;
            MethodInfo switchWeapon = FindMethod(gun.GetType(), "SwitchWeapon", 5);
            if (switchWeapon == null)
            {
                LastError = "GunControl.SwitchWeapon(int, int?, bool, bool, bool) not found in this game version.";
                return false;
            }
            try
            {
                switchWeapon.Invoke(gun, new object[] { slot, (int?)zeroBasedVariation, false, false, false });
                return true;
            }
            catch (Exception ex)
            {
                LastError = "GunControl.SwitchWeapon failed: " + UnwrapMessage(ex);
                return false;
            }
        }

        /// <summary>
        /// The fist the player has equipped (0 = Feedbacker, 1 = Knuckleblaster, 3 = gold arm),
        /// or -1 when FistControl is not available.
        /// </summary>
        public int CurrentFistVariant
        {
            get
            {
                object fists = FistControl;
                return fists == null ? -1 : ToInt(GetMember(fists, new[] { "currentVarNum", "currentVar" }), -1);
            }
        }

        /// <summary>
        /// Fire the punch of the currently equipped arm. The punch is always the Punch component
        /// FistControl.currentPunch points at, never the Feedbacker directly, so the Knuckleblaster
        /// throws its heavy punch while it is equipped. The game's own gates are checked first
        /// (arm ready, fist cooldown spent, punch stamina available, FistControl activated).
        /// Punch.heldAction is pointed at the generic Punch action so the held-punch blast still
        /// works. This is the fallback for builds where InputSource.Punch cannot be written.
        /// </summary>
        public bool TryPunchCurrentArm()
        {
            object fists = FistControl;
            if (fists == null)
                return false;
            object punch = GetMember(fists, new[] { "currentPunch" });
            if (punch == null)
                return false;
            if (!ToBool(GetMember(punch, new[] { "ready" }), true))
                return false;
            if (ToFloat(GetMember(fists, new[] { "fistCooldown" }), 0f) > 0f)
                return false;
            object charges = WeaponCharges;
            if (charges != null && ToFloat(GetMember(charges, new[] { "punchStamina" }), 1f) < 1f)
                return false;
            if (!ToBool(GetMember(fists, new[] { "activated" }), true))
                return false;
            MethodInfo start = FindMethod(punch.GetType(), "PunchStart", 0);
            if (start == null)
            {
                LastError = "Punch.PunchStart() not found in this game version.";
                return false;
            }

            // Punch.BlastCheck reads heldAction to tell a held punch from a tap.
            object source = InputSource;
            object punchState = source != null ? GetMember(source, new[] { "Punch" }) : null;
            object action = punchState != null ? GetMember(punchState, new[] { "Action" }) : null;
            if (action != null)
                SetMember(punch, new[] { "heldAction" }, action);

            try
            {
                start.Invoke(punch, null);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Punch.PunchStart failed: " + UnwrapMessage(ex);
                return false;
            }
        }

        /// <summary>
        /// Make FistControl's live pointers match the arm that is actually active. Called after
        /// a savestate load: currentOrderNum/currentVarNum and the active arm object come from
        /// the save, while currentPunch is a component reference that a light capture can skip.
        /// Re-deriving it here keeps p routed to the equipped arm instead of a stale Feedbacker.
        /// </summary>
        public bool SyncFistControl()
        {
            object fists = FistControl;
            if (fists == null)
                return false;
            System.Collections.IList spawned = GetMember(fists, new[] { "spawnedArms" }) as System.Collections.IList;
            if (spawned == null || spawned.Count == 0)
                return false;
            int order = ToInt(GetMember(fists, new[] { "currentOrderNum" }), -1);
            if (order < 0 || order >= spawned.Count)
                return false;
            GameObject arm = spawned[order] as GameObject;
            if (arm == null)
            {
                Component component = spawned[order] as Component;
                arm = component != null ? component.gameObject : null;
            }
            if (arm == null)
                return false;
            Type punchType = FindType("Punch");
            Component punch = punchType != null ? arm.GetComponent(punchType) : null;
            if (punch == null && punchType != null)
                punch = arm.GetComponentInChildren(punchType);
            if (punch == null)
                return false;

            bool applied = SetMember(fists, new[] { "currentPunch" }, punch);
            System.Collections.IList nums = GetMember(fists, new[] { "spawnedArmNums" }) as System.Collections.IList;
            if (nums != null && order < nums.Count)
            {
                int variant = ToInt(nums[order], -1);
                if (variant >= 0)
                    SetMember(fists, new[] { "currentVarNum" }, variant);
            }
            return applied;
        }

        /// <summary>
        /// Equip a fist through the game's own path, FistControl.ForceArm(int variant, bool animation),
        /// the call FistControl.ResetFists makes. Variant numbers are the game's: 0 = Feedbacker
        /// (blue), 1 = Knuckleblaster (red). No animation and no sound. ForceArm goes through
        /// ArmChange, which also stores the arm in PlayerPrefs "CurArm" as a manual switch does.
        /// False (LastError set) when FistControl or ForceArm is missing or the arm is not in the
        /// player's loadout (not owned, or disabled in the arm settings).
        /// </summary>
        public bool EquipFist(int variant)
        {
            object fists = FistControl;
            if (fists == null)
            {
                LastError = "FistControl not found (no player loaded?).";
                return false;
            }
            MethodInfo method = FindMethod(fists.GetType(), "ForceArm", 2);
            ParameterInfo[] parameters = method != null ? method.GetParameters() : null;
            if (parameters == null || parameters[0].ParameterType != typeof(int) || parameters[1].ParameterType != typeof(bool))
            {
                LastError = "FistControl.ForceArm(int, bool) not found in this game version.";
                return false;
            }
            try
            {
                object result = method.Invoke(fists, new object[] { variant, false });
                if (result is bool && !(bool)result)
                {
                    LastError = "that arm is not in the current loadout (not owned, or disabled in the arm settings).";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "FistControl.ForceArm failed: " + UnwrapMessage(ex);
                return false;
            }
        }

        Type _prefsManagerType;
        object _prefsManager;
        PropertyInfo _prefsInstanceProperty;
        bool _prefsInstancePropertyLooked;

        /// <summary>
        /// True when the last GetDifficulty/SetDifficulty failure was "PrefsManager instance not
        /// available yet", which a later attempt (next scene load) can fix. False for a missing
        /// type or method, which no retry fixes.
        /// </summary>
        public bool PrefsManagerRetryable { get; private set; }

        /// <summary>
        /// The game's PrefsManager. In the game's IL it is a MonoSingleton&lt;PrefsManager&gt; with
        /// [ConfigureSingleton(PersistAutoInstance | DestroyDuplicates)]: MonoSingleton.Instance
        /// (GetOrCreateInstance) creates it on first access (new GameObject + AddComponent, then
        /// DontDestroyOnLoad and HideFlags DontSaveInEditor|DontSaveInBuild), which is how the game
        /// itself gets it (DifficultySelectButton.SetDifficulty calls MonoSingleton&lt;PrefsManager&gt;.Instance).
        /// The generic ResolveSingleton cannot reach that getter (FindType skips the abstract open
        /// generic MonoSingleton`1) and its FindObjectOfType fallback can miss the hidden DDOL object,
        /// so this goes through the same static getter the game uses, found on PrefsManager's base type.
        /// Null with <paramref name="error"/> naming the failed step. Never throws.
        /// </summary>
        object GetPrefsManager(out string error)
        {
            error = null;
            PrefsManagerRetryable = false;
            if (IsAlive(_prefsManager))
                return _prefsManager;
            _prefsManager = null;

            if (_prefsManagerType == null)
                _prefsManagerType = FindType("PrefsManager");
            if (_prefsManagerType == null)
            {
                error = "PrefsManager type not found";
                return null;
            }

            string detail = null;
            try
            {
                if (!_prefsInstancePropertyLooked)
                {
                    _prefsInstancePropertyLooked = true;
                    for (Type t = _prefsManagerType.BaseType; t != null; t = t.BaseType)
                    {
                        if (t.IsGenericType && t.Name == "MonoSingleton`1")
                        {
                            _prefsInstanceProperty = t.GetProperty("Instance",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            break;
                        }
                    }
                }

                if (_prefsInstanceProperty != null)
                {
                    object value = _prefsInstanceProperty.GetValue(null, null);
                    if (IsAlive(value))
                        _prefsManager = value;
                }

                // Fallback for a game version without the MonoSingleton getter: any live instance,
                // including hidden DontDestroyOnLoad ones FindObjectOfType skips (prefab assets
                // have no valid scene and are ignored).
                if (_prefsManager == null)
                {
                    UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(_prefsManagerType);
                    for (int i = 0; all != null && i < all.Length; i++)
                    {
                        Component component = all[i] as Component;
                        if (component != null && component.gameObject.scene.IsValid())
                        {
                            _prefsManager = component;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                detail = UnwrapMessage(ex);
                _prefsManager = null;
            }

            if (_prefsManager == null)
            {
                string scene;
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
                catch { scene = "?"; }
                error = "PrefsManager instance not available yet (scene: " + (string.IsNullOrEmpty(scene) ? "?" : scene) + ")" +
                        (detail != null ? ": " + detail : string.Empty);
                PrefsManagerRetryable = true;
            }
            return _prefsManager;
        }

        /// <summary>
        /// The game's current difficulty: PrefsManager.Instance.GetInt("difficulty", 0), the read
        /// every difficulty-dependent object makes in Awake/Start. -1 (LastError set) when unavailable.
        /// </summary>
        public int GetDifficulty()
        {
            try
            {
                string error;
                object prefs = GetPrefsManager(out error);
                if (prefs == null)
                {
                    LastError = error;
                    return -1;
                }
                MethodInfo method = FindMethod(prefs.GetType(), "GetInt", 2);
                ParameterInfo[] parameters = method != null ? method.GetParameters() : null;
                if (parameters == null || parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != typeof(int))
                {
                    LastError = "PrefsManager.GetInt not found";
                    return -1;
                }
                object result = method.Invoke(prefs, new object[] { "difficulty", 0 });
                return result is int ? (int)result : -1;
            }
            catch (Exception ex)
            {
                LastError = "PrefsManager.GetInt failed: " + UnwrapMessage(ex);
                return -1;
            }
        }

        /// <summary>
        /// Sets the game difficulty through the game's own path, PrefsManager.Instance.SetInt("difficulty", n),
        /// the call DifficultySelectButton.SetDifficulty makes. Numbers are GameDifficulty values
        /// (0 Harmless .. 4 Brutal). Like the menu it is saved to the game's prefs, and objects read it
        /// when they spawn, so it fully applies from the next scene load. False (LastError set) on failure.
        /// </summary>
        public bool SetDifficulty(int difficulty)
        {
            try
            {
                string error;
                object prefs = GetPrefsManager(out error);
                if (prefs == null)
                {
                    LastError = error;
                    return false;
                }
                MethodInfo method = FindMethod(prefs.GetType(), "SetInt", 2);
                ParameterInfo[] parameters = method != null ? method.GetParameters() : null;
                if (parameters == null || parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != typeof(int))
                {
                    LastError = "PrefsManager.SetInt not found";
                    return false;
                }
                method.Invoke(prefs, new object[] { "difficulty", difficulty });
                return true;
            }
            catch (Exception ex)
            {
                LastError = "PrefsManager.SetInt failed: " + UnwrapMessage(ex);
                return false;
            }
        }

        static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name && methods[i].GetParameters().Length == parameterCount)
                    return methods[i];
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Weapons and loadout
        // ------------------------------------------------------------------

        Type _gunSetterType;
        object _gunSetter;
        MethodInfo _checkGearMethod;
        MethodInfo _prefsGetIntMethod;
        static readonly string[] VariantMembers = { "blueVariant", "redVariant", "greenVariant" };

        /// <summary>GunSetter, the component that builds the weapon slots. Null in menus.</summary>
        public object GunSetter
        {
            get { return ResolveSingleton(ref _gunSetter, _gunSetterType ?? (_gunSetterType = FindType("GunSetter"))); }
        }

        /// <summary>GameProgressSaver.CheckGear(key) as the game sees it right now (patches included). -1 when unavailable.</summary>
        int ReadGear(string key)
        {
            if (_checkGearMethod == null)
            {
                Type saver = WeaponUnlock.FindGameType("GameProgressSaver");
                if (saver != null)
                {
                    _checkGearMethod = saver.GetMethod("CheckGear",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new[] { typeof(string) }, null);
                }
            }
            if (_checkGearMethod == null)
                return -1;
            return ToInt(_checkGearMethod.Invoke(null, new object[] { key }), -1);
        }

        /// <summary>The scene's PlayerLoadout (a level's forced start loadout), or null. Never throws.</summary>
        object FindPlayerLoadout()
        {
            try
            {
                Type type = FindType("PlayerLoadout");
                if (type == null)
                    return null;
                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(type);
                return found != null ? found : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads what the player has right now: the owned weapon and arm gear keys, and the
        /// forced-loadout variant codes. A weapon counts when CheckGear(key) &gt; 0 and its
        /// "weapon.&lt;key&gt;" preference is not 0 (alt-fire unlocks have no preference of their
        /// own). The values are the ones the game reads, so an active unlock or script override is
        /// included. The loadout comes from GunSetter.forcedLoadout (PlayerLoadout.loadout when
        /// there is no GunSetter); no forced loadout means the game defaults, "iii" per field.
        /// False with LastError set ("No player loaded." in a menu). Never throws.
        /// </summary>
        public bool TryCaptureLoadout(out List<string> weapons, out Dictionary<string, string> loadout)
        {
            weapons = null;
            loadout = null;
            try
            {
                if (NewMovement == null)
                {
                    LastError = "No player loaded.";
                    return false;
                }

                string error;
                object prefs = GetPrefsManager(out error);
                if (prefs == null)
                {
                    LastError = error;
                    return false;
                }
                if (_prefsGetIntMethod == null)
                    _prefsGetIntMethod = FindMethod(prefs.GetType(), "GetInt", 2);
                if (_prefsGetIntMethod == null)
                {
                    LastError = "PrefsManager.GetInt not found";
                    return false;
                }

                List<string> owned = new List<string>();
                for (int i = 0; i < WeaponUnlock.GearKeys.Length; i++)
                {
                    string key = WeaponUnlock.GearKeys[i];
                    int gear = ReadGear(key);
                    if (gear < 0)
                    {
                        LastError = "GameProgressSaver.CheckGear(string) not found in this game version.";
                        return false;
                    }
                    if (gear == 0)
                        continue;
                    if (!key.EndsWith("alt", StringComparison.Ordinal))
                    {
                        int pref = ToInt(_prefsGetIntMethod.Invoke(prefs, new object[] { "weapon." + key, 1 }), 1);
                        if (pref == 0)
                            continue;
                    }
                    owned.Add(key);
                }

                object setter = GunSetter;
                object forced;
                if (setter != null)
                {
                    forced = GetMember(setter, new[] { "forcedLoadout" });
                }
                else
                {
                    object playerLoadout = FindPlayerLoadout();
                    forced = playerLoadout != null ? GetMember(playerLoadout, new[] { "loadout" }) : null;
                }

                Dictionary<string, string> tokens = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < WeaponUnlock.LoadoutFields.Length; i++)
                {
                    string field = WeaponUnlock.LoadoutFields[i];
                    object setting = forced != null ? GetMember(forced, new[] { field }) : null;
                    char[] code = new char[3];
                    for (int v = 0; v < code.Length; v++)
                    {
                        object option = setting != null ? GetMember(setting, new[] { VariantMembers[v] }) : null;
                        code[v] = WeaponUnlock.OptionToChar(ToInt(option, 0));
                    }
                    tokens[field] = new string(code);
                }

                weapons = owned;
                loadout = tokens;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Loadout capture failed: " + UnwrapMessage(ex);
                weapons = null;
                loadout = null;
                return false;
            }
        }

        /// <summary>
        /// Writes loadout variant codes ("iox" per field) into the live ForcedLoadout objects:
        /// GunSetter.forcedLoadout, plus FistControl.forcedLoadout and PlayerLoadout.loadout when
        /// they are other objects. A missing GunSetter.forcedLoadout is created (and handed to the
        /// FistControl when that has none). Fields not in <paramref name="loadout"/> are untouched.
        /// Call <see cref="ResetWeaponSlots"/> afterwards so the slots follow. False with LastError set.
        /// </summary>
        public bool ApplyLoadout(Dictionary<string, string> loadout)
        {
            if (loadout == null || loadout.Count == 0)
                return true;
            object setter = GunSetter;
            if (setter == null)
            {
                LastError = "No player loaded.";
                return false;
            }
            try
            {
                List<object> targets = new List<object>();
                object primary = GetMember(setter, new[] { "forcedLoadout" });
                if (primary == null)
                {
                    primary = CreateForcedLoadout();
                    if (primary == null)
                    {
                        LastError = "ForcedLoadout could not be created in this game version.";
                        return false;
                    }
                    SetMember(setter, new[] { "forcedLoadout" }, primary);
                }
                targets.Add(primary);

                object fists = FistControl;
                if (fists != null)
                {
                    object fistLoadout = GetMember(fists, new[] { "forcedLoadout" });
                    if (fistLoadout == null)
                        SetMember(fists, new[] { "forcedLoadout" }, primary);
                    else if (!ContainsReference(targets, fistLoadout))
                        targets.Add(fistLoadout);
                }

                object playerLoadout = FindPlayerLoadout();
                object sceneLoadout = playerLoadout != null ? GetMember(playerLoadout, new[] { "loadout" }) : null;
                if (sceneLoadout != null && !ContainsReference(targets, sceneLoadout))
                    targets.Add(sceneLoadout);

                for (int t = 0; t < targets.Count; t++)
                {
                    foreach (KeyValuePair<string, string> pair in loadout)
                        WriteVariantCode(targets[t], pair.Key, pair.Value);
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Applying the loadout failed: " + UnwrapMessage(ex);
                return false;
            }
        }

        static bool ContainsReference(List<object> list, object value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], value))
                    return true;
            }
            return false;
        }

        /// <summary>A ForcedLoadout with every variant setting created (the game's class initializes none).</summary>
        object CreateForcedLoadout()
        {
            Type type = FindType("ForcedLoadout");
            if (type == null)
                return null;
            object loadout = Activator.CreateInstance(type);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                Type fieldType = fields[i].FieldType;
                if (fieldType.IsClass && fieldType.GetConstructor(Type.EmptyTypes) != null)
                    fields[i].SetValue(loadout, Activator.CreateInstance(fieldType));
            }
            return loadout;
        }

        void WriteVariantCode(object forced, string field, string code)
        {
            if (forced == null || string.IsNullOrEmpty(code) || code.Length != 3)
                return;
            MemberInfo member = FindMember(forced.GetType(), field);
            if (member == null)
                return;
            object setting = GetMemberValue(forced, member);
            if (setting == null)
            {
                FieldInfo settingField = member as FieldInfo;
                if (settingField == null || settingField.FieldType.GetConstructor(Type.EmptyTypes) == null)
                    return;
                setting = Activator.CreateInstance(settingField.FieldType);
                SetMemberValue(forced, member, setting);
            }
            for (int v = 0; v < VariantMembers.Length; v++)
            {
                int option;
                if (WeaponUnlock.TryOptionFromChar(code[v], out option))
                    SetMember(setting, new[] { VariantMembers[v] }, option);
            }
        }

        /// <summary>
        /// Rebuilds the weapon slots and the arms from the current ownership and loadout:
        /// GunSetter.ResetWeapons(true) (the call GunSetter.Start makes), then a guarded
        /// FistControl.ResetFists so unlocked or removed arms follow. False with LastError set when
        /// there is no GunSetter ("No player loaded.") or ResetWeapons is missing. An arm refresh
        /// failure is only logged.
        /// </summary>
        public bool ResetWeaponSlots()
        {
            object setter = GunSetter;
            if (setter == null)
            {
                LastError = "No player loaded.";
                return false;
            }
            try
            {
                MethodInfo method = FindMethod(setter.GetType(), "ResetWeapons", 1);
                ParameterInfo[] parameters = method != null ? method.GetParameters() : null;
                if (parameters == null || parameters[0].ParameterType != typeof(bool))
                {
                    LastError = "GunSetter.ResetWeapons(bool) not found in this game version.";
                    return false;
                }
                method.Invoke(setter, new object[] { true });
            }
            catch (Exception ex)
            {
                LastError = "GunSetter.ResetWeapons failed: " + UnwrapMessage(ex);
                return false;
            }

            RefreshArms();
            return true;
        }

        /// <summary>
        /// FistControl.ResetFists destroys every spawned arm before it instantiates the new set. A
        /// failed instantiate (the game's "The Object you want to instantiate is null." when an
        /// addressable arm prefab is not available) therefore leaves the player with no arm at all:
        /// no punch, no arm HUD icon and no fist of either colour. Two guards keep that from
        /// happening: the arms are only rebuilt when the live set differs from the set the game
        /// would spawn now, and every prefab the rebuild needs must load first. Never throws.
        /// </summary>
        void RefreshArms()
        {
            object fists = FistControl;
            if (fists == null)
                return;
            MethodInfo reset = FindMethod(fists.GetType(), "ResetFists", 0);
            if (reset == null)
                return;
            try
            {
                List<int> live = ReadSpawnedArmNumbers(fists);
                List<int> wanted = WantedArmNumbers(fists);
                // Without both lists there is no way to know whether a rebuild is needed, and a
                // blind rebuild is what destroys the arms, so leave them alone.
                if (live == null || wanted == null)
                    return;
                // One line per loadout apply: the arm state decides whether the HUD fist icon exists.
                _plugin.LogInfo("Arms: spawned [" + string.Join(",", live.ToArray()) + "], wanted [" +
                                string.Join(",", wanted.ToArray()) + "].");
                if (SameArmNumbers(live, wanted))
                    return;
                if (!ArmPrefabsLoaded(fists, wanted))
                {
                    _plugin.LogWarning("Arms left as they are: an arm prefab the game would spawn could " +
                                       "not be loaded, and ResetFists would destroy the current arms first.");
                    return;
                }
                reset.Invoke(fists, null);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("FistControl.ResetFists failed: " + UnwrapMessage(ex));
            }
        }

        /// <summary>The arm variant numbers currently spawned, in FistControl order, or null.</summary>
        List<int> ReadSpawnedArmNumbers(object fists)
        {
            try
            {
                object value = GetMember(fists, new[] { "spawnedArmNums" });
                System.Collections.IEnumerable sequence = value as System.Collections.IEnumerable;
                if (sequence == null)
                    return null;
                List<int> numbers = new List<int>(4);
                foreach (object item in sequence)
                    numbers.Add(ToInt(item, -1));
                return numbers;
            }
            catch (Exception ex)
            {
                LastError = "Spawned arms could not be read: " + UnwrapMessage(ex);
                return null;
            }
        }

        /// <summary>
        /// The arm variant numbers FistControl.ResetFists would spawn from the current weapon
        /// preferences, gear ownership and forced loadout (0 = Feedbacker, 1 = Knuckleblaster,
        /// 3 = gold arm; the hook arm is not a spawned object). Same order as the game. Null when
        /// the state cannot be read.
        /// </summary>
        List<int> WantedArmNumbers(object fists)
        {
            try
            {
                object forced = GetMember(fists, new[] { "forcedLoadout" });
                int blue = forced != null ? ReadArmVariant(forced, "blueVariant") : 0;
                int red = forced != null ? ReadArmVariant(forced, "redVariant") : 0;
                List<int> wanted = new List<int>(3);
                if ((ReadWeaponPref("arm0") == 1 && (forced == null || blue == 0)) || (forced != null && blue == 1))
                    wanted.Add(0);
                if (forced != null && red == 1)
                {
                    wanted.Add(1);
                }
                else if (!(forced != null && red == 2) && ReadWeaponPref("arm1") == 1 && ReadGear("arm1") == 1)
                {
                    wanted.Add(1);
                }
                if (ReadWeaponPref("arm3") == 1 && ReadGear("arm3") == 1)
                    wanted.Add(3);
                return wanted;
            }
            catch (Exception ex)
            {
                LastError = "Arm set could not be read: " + UnwrapMessage(ex);
                return null;
            }
        }

        /// <summary>One arm variant option of the forced loadout (0 IfEquipped, 1 ForceOn, 2 ForceOff).</summary>
        int ReadArmVariant(object forced, string variantName)
        {
            object setting = GetMember(forced, new[] { "arm" });
            object option = setting != null ? GetMember(setting, new[] { variantName }) : null;
            return ToInt(option, 0);
        }

        /// <summary>A "weapon.&lt;key&gt;" preference as the game reads it, or -1 when it cannot be read.</summary>
        int ReadWeaponPref(string key)
        {
            try
            {
                string error;
                object prefs = GetPrefsManager(out error);
                if (prefs == null)
                    return -1;
                if (_prefsGetIntMethod == null)
                    _prefsGetIntMethod = FindMethod(prefs.GetType(), "GetInt", 2);
                if (_prefsGetIntMethod == null)
                    return -1;
                return ToInt(_prefsGetIntMethod.Invoke(prefs, new object[] { "weapon." + key, 1 }), -1);
            }
            catch
            {
                return -1;
            }
        }

        static bool SameArmNumbers(List<int> left, List<int> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        /// <summary>Warms every arm prefab ResetFists would instantiate. False when one is missing.</summary>
        bool ArmPrefabsLoaded(object fists, List<int> wanted)
        {
            if (wanted == null)
                return false;
            for (int i = 0; i < wanted.Count; i++)
            {
                string field = wanted[i] == 0 ? "blueArm" : (wanted[i] == 1 ? "redArm" : "goldArm");
                object reference = GetMember(fists, new[] { field });
                if (reference == null)
                    return false;
                if (LoadArmPrefab(reference) == null)
                    return false;
            }
            return true;
        }

        /// <summary>AssetHelper.LoadPrefab(AssetReference): the load ResetFists itself performs.</summary>
        object LoadArmPrefab(object reference)
        {
            try
            {
                Type helper = WeaponUnlock.FindGameType("AssetHelper");
                if (helper == null)
                    return null;
                MethodInfo[] methods = helper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "LoadPrefab")
                        continue;
                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(reference))
                        continue;
                    // Unity's overloaded == turns a destroyed prefab into null here.
                    UnityEngine.Object prefab = methods[i].Invoke(null, new[] { reference }) as UnityEngine.Object;
                    return prefab != null ? prefab : null;
                }
            }
            catch (Exception ex)
            {
                LastError = "Arm prefab load failed: " + UnwrapMessage(ex);
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------

        public object InputSource
        {
            get
            {
                object manager = InputManager;
                if (manager == null)
                    return null;
                return GetMember(manager, new[] { "InputSource", "inputSource", "inputs", "Inputs" });
            }
        }

        public Dictionary<string, KeyCode> InputBindings
        {
            get
            {
                object manager = InputManager;
                if (manager == null)
                    return null;
                return GetMember(manager, new[] { "inputsDictionary", "inputDictionary", "bindings", "InputsDictionary" })
                       as Dictionary<string, KeyCode>;
            }
        }

        public object GetActionState(string actionName)
        {
            if (string.IsNullOrEmpty(actionName))
                return null;
            object source = InputSource;
            if (source == null)
                return null;
            MemberInfo member = FindMember(source.GetType(), actionName);
            if (member == null)
                return null;
            return GetMemberValue(source, member);
        }

        public bool IsGamePaused()
        {
            object options = OptionsManager;
            if (options == null)
                return false;
            object paused = GetMember(options, new[] { "paused", "isPaused", "Paused" });
            return ToBool(paused, false);
        }

        // ------------------------------------------------------------------
        // HUD values (best effort; the HUD prints n/a when a member is missing)
        // ------------------------------------------------------------------

        public bool TryGetHealth(out float health, out float hardDamage)
        {
            health = 0f;
            hardDamage = 0f;
            object player = NewMovement;
            if (player == null)
                return false;
            object hp = GetMember(player, new[] { "hp", "health", "currentHealth", "hitPoints" });
            object anti = GetMember(player, new[] { "antiHp", "antiHP", "hardDamage", "antiHealth" });
            if (hp == null)
                return false;
            health = ToFloat(hp, 0f);
            hardDamage = ToFloat(anti, 0f);
            return true;
        }

        public bool TryGetStamina(out float stamina)
        {
            stamina = 0f;
            object player = NewMovement;
            if (player == null)
                return false;
            // boostCharge is the dash meter (0-300, one dash per 100); the other names are
            // fallbacks for builds that renamed it.
            object value = GetMember(player, new[] { "boostCharge", "stamina", "staminaMeter", "staminaPoints", "stam" });
            if (value == null)
                return false;
            stamina = ToFloat(value, 0f);
            return true;
        }

        /// <summary>
        /// The local player's collision bounds right now, as a world-space size and a center offset
        /// from the player transform. Picks colliders exactly like the "Show player hitbox" overlay
        /// (NewMovement.playerCollider first, then the player's non-trigger colliders, then any), so
        /// the trajectory hover can draw the same box the player had on that tick. Read only.
        /// </summary>
        public bool TryGetPlayerHitboxBounds(out Vector3 center, out Vector3 size)
        {
            center = Vector3.zero;
            size = Vector3.zero;
            Component player = NewMovement as Component;
            if (player == null)
                return false;
            Vector3 origin = player.transform.position;

            Collider playerCollider = GetMember(player, new[] { "playerCollider" }) as Collider;
            if (playerCollider != null && playerCollider.enabled && playerCollider.gameObject.activeInHierarchy)
            {
                Bounds bounds = playerCollider.bounds;
                center = bounds.center - origin;
                size = bounds.size;
                return size.x > 0f && size.y > 0f && size.z > 0f;
            }

            Collider[] colliders;
            try
            {
                colliders = player.GetComponentsInChildren<Collider>(true);
            }
            catch
            {
                return false;
            }

            bool found = false;
            Bounds total = new Bounds(Vector3.zero, Vector3.zero);
            for (int pass = 0; pass < 2 && !found; pass++)
            {
                bool includeTriggers = pass == 1;
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                        continue;
                    if (!includeTriggers && collider.isTrigger)
                        continue;
                    if (!found)
                    {
                        total = collider.bounds;
                        found = true;
                    }
                    else
                    {
                        total.Encapsulate(collider.bounds);
                    }
                }
            }
            if (!found)
                return false;
            center = total.center - origin;
            size = total.size;
            return size.x > 0f && size.y > 0f && size.z > 0f;
        }

        /// <summary>Ground contact, slide, dash and jump flags straight from NewMovement.</summary>
        public bool TryGetMovementState(out bool onGround, out bool sliding, out bool dashing, out bool jumping)
        {
            onGround = sliding = dashing = jumping = false;
            object player = NewMovement;
            if (player == null)
                return false;
            object groundCheck = GetMember(player, new[] { "gc" });
            if (groundCheck != null)
                onGround = ToBool(GetMember(groundCheck, new[] { "onGround" }), false);
            sliding = ToBool(GetMember(player, new[] { "sliding" }), false);
            dashing = ToBool(GetMember(player, new[] { "boost" }), false);
            jumping = ToBool(GetMember(player, new[] { "jumping" }), false);
            return true;
        }

        /// <summary>
        /// Slide-recovery counter used by Super Slide Jumps. NewMovement.StopSlide resets it to 0;
        /// FixedUpdate increments it while not sliding, and TrySSJ accepts it while
        /// 0 &lt; framesSinceSlide &lt; ssjMaxFrames (the shipped value is 4, so frames 1-3).
        /// </summary>
        public bool TryGetSlideRecovery(out int framesSinceSlide, out int ssjMaxFrames)
        {
            framesSinceSlide = 0;
            ssjMaxFrames = 4;
            object player = NewMovement;
            if (player == null)
                return false;
            object frames = GetMember(player, new[] { "framesSinceSlide" });
            if (frames == null)
                return false;
            framesSinceSlide = ToInt(frames, 0);
            ssjMaxFrames = Mathf.Max(2, ToInt(GetMember(player, new[] { "ssjMaxFrames" }), 4));
            return true;
        }

        /// <summary>
        /// Extra NewMovement state the autofuncs gate on: jumpCooldown (set by Jump(), cleared
        /// by JumpReady 0.2 s later; a jump press during it does nothing) and boostCharge (the
        /// dash meter, 0-300; TryDash needs 100). Read before the game's Update, so it is the
        /// state this tick's inputs act on.
        /// </summary>
        public bool TryGetAutofuncState(out bool jumpCooldown, out float boostCharge)
        {
            jumpCooldown = false;
            boostCharge = 0f;
            object player = NewMovement;
            if (player == null)
                return false;
            jumpCooldown = ToBool(GetMember(player, new[] { "jumpCooldown" }), false);
            boostCharge = ToFloat(GetMember(player, new[] { "boostCharge" }), 0f);
            return true;
        }

        MethodInfo _layerMaskGet;
        object _lmdEnvironment;
        bool _layerMaskResolved;

        /// <summary>
        /// True when a Slide press this tick would start a ground slam. Mirrors
        /// NewMovement.HandleInputs/TryStartSlam: the press is only read airborne (!gc.onGround),
        /// and the slam needs no environment within 3 m below (ray from gc + up, down, 3 m,
        /// LayerMaskDefaults.Get(LMD.Environment)), fallTime &gt; 0.5, slamCooldown 0 and no slam
        /// already running (gc.heavyFall). Within 2 m of the ground the same press would start an
        /// airborne slide instead, which is why the autofunc does not just hold Slide in the air.
        /// </summary>
        public bool TryGetSlamReady(out bool ready)
        {
            ready = false;
            Component player = NewMovement as Component;
            if (player == null)
                return false;
            Component groundCheck = GetMember(player, new[] { "gc" }) as Component;
            if (groundCheck == null)
                return false;
            if (ToBool(GetMember(groundCheck, new[] { "onGround" }), true) ||
                ToBool(GetMember(groundCheck, new[] { "heavyFall" }), true))
                return true;
            if (ToFloat(GetMember(player, new[] { "fallTime" }), 0f) <= 0.5f ||
                ToFloat(GetMember(player, new[] { "slamCooldown" }), 1f) != 0f)
                return true;

            if (!_layerMaskResolved)
            {
                _layerMaskResolved = true;
                Type defaults = FindType("LayerMaskDefaults");
                Type lmd = FindType("LMD");
                if (defaults != null && lmd != null)
                {
                    _layerMaskGet = defaults.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { lmd }, null);
                    try { _lmdEnvironment = Enum.Parse(lmd, "Environment"); }
                    catch { _lmdEnvironment = null; }
                }
            }
            int mask = 0;
            if (_layerMaskGet != null && _lmdEnvironment != null)
            {
                try
                {
                    object value = _layerMaskGet.Invoke(null, new[] { _lmdEnvironment });
                    if (value is LayerMask)
                        mask = ((LayerMask)value).value;
                }
                catch { mask = 0; }
            }
            if (mask == 0)
                return false;

            Vector3 up = player.transform.up;
            ready = !Physics.Raycast(groundCheck.transform.position + up, -up, 3f, mask, QueryTriggerInteraction.Ignore);
            return true;
        }

        /// <summary>
        /// True while V1 is in a ground slam right now. GroundCheck.heavyFall is the game's own
        /// running-slam flag (the same one TryGetSlamReady treats as "a slam is already running"),
        /// so this is the current state, not a prediction: it is set when the slam starts and
        /// cleared when the slam ends or is cancelled.
        /// </summary>
        public bool TryGetSlamming(out bool slamming)
        {
            slamming = false;
            object player = NewMovement;
            if (player == null)
                return false;
            object groundCheck = GetMember(player, new[] { "gc" });
            if (groundCheck == null)
                return false;
            object value = GetMember(groundCheck, new[] { "heavyFall" });
            if (value == null)
                value = GetMember(player, new[] { "heavyFall" });
            if (value == null)
                return false;
            slamming = ToBool(value, false);
            return true;
        }

        /// <summary>
        /// True while V1 is pressed against a wall. NewMovement.wcGroup keeps one WallCheck per side
        /// and each sets its own onWall from its trigger contacts, so any active check reporting it
        /// means the wall is there. Read only; the instances are the live components of this scene.
        /// </summary>
        public bool TryGetTouchingWall(out bool onWall)
        {
            onWall = false;
            object player = NewMovement;
            if (player == null)
                return false;
            object group = GetMember(player, new[] { "wcGroup" });
            if (group == null)
                return false;
            object instances = GetMember(group, new[] { "instances", "checks", "walls" });
            System.Collections.IEnumerable list = instances as System.Collections.IEnumerable;
            if (list == null)
                return false;

            bool found = false;
            foreach (object check in list)
            {
                Component component = check as Component;
                if (component == null)
                    continue;
                found = true;
                if (ToBool(GetMember(component, new[] { "onWall" }), false))
                {
                    onWall = true;
                    break;
                }
            }
            return found;
        }

        Type _hookArmType;
        object _hookArm;
        Type _hookPointType;
        Type _coinType;

        /// <summary>
        /// True when a whiplash press would throw the hook this tick. HookArm.Update throws on a
        /// press only while HookArm.cooldown is 0 and the hook is not pulling (a press while
        /// pulling, state 3, cancels the pull instead), so "ready" is: equipped, state Ready (0),
        /// not returning, cooldown 0.
        /// </summary>
        public bool TryGetHookReady(out bool ready)
        {
            ready = false;
            object hook = ResolveSingleton(ref _hookArm, _hookArmType ?? (_hookArmType = FindType("HookArm")));
            if (hook == null)
                return false;
            bool equipped = ToBool(GetMember(hook, new[] { "equipped" }), false);
            int state = ToInt(GetMember(hook, new[] { "state" }), -1);
            bool returning = ToBool(GetMember(hook, new[] { "returning" }), true);
            float cooldown = ToFloat(GetMember(hook, new[] { "cooldown" }), 1f);
            ready = equipped && state == 0 && !returning && cooldown <= 0f;
            return true;
        }

        /// <summary>
        /// Closest active, unused coin (Coin.shot false) to <paramref name="origin"/>, as the aim
        /// point for autofunc 'c'. False when there is none.
        /// </summary>
        public bool TryFindNearestCoin(Vector3 origin, out Vector3 point)
        {
            point = Vector3.zero;
            Type type = _coinType ?? (_coinType = FindType("Coin"));
            if (type == null)
                return false;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, false); }
            catch { return false; }
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < found.Length; i++)
            {
                Component coin = found[i] as Component;
                if (coin == null || !coin.gameObject.activeInHierarchy)
                    continue;
                // A shot coin is already reflecting the beam (or breaking); it cannot be hit again.
                if (ToBool(GetMember(coin, new[] { "shot" }), false))
                    continue;
                Vector3 position = coin.transform.position;
                float squared = (position - origin).sqrMagnitude;
                if (squared < best)
                {
                    best = squared;
                    point = position;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>Airborne projectile classes the optimizer's aim lock can target.</summary>
        public static readonly string[] AimLockProjectileTypes = { "Grenade", "Rocket", "Coin", "Cannonball" };

        /// <summary>
        /// Live instance of <paramref name="typeName"/>. An active instance whose instance ID equals
        /// <paramref name="preferredId"/> wins; otherwise the nearest active one to <paramref name="origin"/>.
        /// A coin already shot is skipped. False when none exists.
        /// </summary>
        public bool TryFindProjectile(string typeName, int preferredId, Vector3 origin,
                                      out Vector3 point, out int instanceId)
        {
            point = Vector3.zero;
            instanceId = 0;
            Type type = FindType(typeName);
            if (type == null)
                return false;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, false); }
            catch { return false; }
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < found.Length; i++)
            {
                Component item = found[i] as Component;
                if (item == null || !item.gameObject.activeInHierarchy)
                    continue;
                int id = item.GetInstanceID();
                Vector3 position = item.transform.position;
                if (preferredId != 0 && id == preferredId)
                {
                    point = position;
                    instanceId = id;
                    return true;
                }
                if (string.Equals(typeName, "Coin", StringComparison.Ordinal) &&
                    ToBool(GetMember(item, new[] { "shot" }), false))
                    continue;
                float squared = (position - origin).sqrMagnitude;
                if (squared < best)
                {
                    best = squared;
                    point = position;
                    instanceId = id;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>Nearest live aim-lock target across <see cref="AimLockProjectileTypes"/>.</summary>
        public bool TryFindNearestAimLockProjectile(Vector3 origin, out string typeName, out int instanceId, out Vector3 point)
        {
            typeName = string.Empty;
            instanceId = 0;
            point = Vector3.zero;
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < AimLockProjectileTypes.Length; i++)
            {
                Vector3 p;
                int id;
                if (!TryFindProjectile(AimLockProjectileTypes[i], 0, origin, out p, out id))
                    continue;
                float squared = (p - origin).sqrMagnitude;
                if (squared < best)
                {
                    best = squared;
                    typeName = AimLockProjectileTypes[i];
                    instanceId = id;
                    point = p;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// One live coin read for the coin path visualizer and the cointhrow autofunc.
        /// Fields are read by name, so a game update that renames one only drops that field.
        /// </summary>
        public sealed class CoinInfo
        {
            public Component Component;
            public int Id;
            public string Name = string.Empty;
            public Vector3 Position;
            public bool Shot;
            public bool ShotByEnemy;
            public float Power;
            public int Ricochets;
            /// <summary>Where the coin is heading when it tracks an enemy (customTarget/target).</summary>
            public Vector3 TargetPoint;
            public bool HasTargetPoint;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with every active coin and returns the count. Pooled
        /// (inactive) coins are skipped; a punched coin is a new object, so it gets its own
        /// entry and therefore its own trail in the visualizer.
        /// </summary>
        public int CollectCoins(List<CoinInfo> into)
        {
            if (into == null)
                return 0;
            into.Clear();
            Type type = _coinType ?? (_coinType = FindType("Coin"));
            if (type == null)
                return 0;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, false); }
            catch { return 0; }

            for (int i = 0; i < found.Length; i++)
            {
                Component coin = found[i] as Component;
                if (coin == null || !coin.gameObject.activeInHierarchy)
                    continue;
                CoinInfo info = new CoinInfo
                {
                    Component = coin,
                    Id = coin.gameObject.GetInstanceID(),
                    Name = coin.gameObject.name,
                    Position = coin.transform.position,
                    Shot = ToBool(GetMember(coin, new[] { "shot", "wasShot", "hasBeenShot" }), false),
                    ShotByEnemy = ToBool(GetMember(coin, new[] { "shotByEnemy", "wasShotByEnemy" }), false),
                    Power = ToFloat(GetMember(coin, new[] { "power" }), 1f),
                    Ricochets = ToInt(GetMember(coin, new[] { "ricochets", "ricochet", "bounceCount" }), 0)
                };
                Vector3 target;
                if (TryGetCoinTarget(coin, out target))
                {
                    info.TargetPoint = target;
                    info.HasTargetPoint = true;
                }
                into.Add(info);
            }
            return into.Count;
        }

        /// <summary>
        /// Where a coin is flying to when it has a target: an EnemyTarget's position, a
        /// Transform or any component's transform. False when the coin has no target.
        /// </summary>
        public bool TryGetCoinTarget(Component coin, out Vector3 point)
        {
            point = Vector3.zero;
            object target = GetMember(coin, new[] { "target", "customTarget", "targetEnemy", "lockedTarget" });
            if (target == null)
                return false;
            Transform transform = target as Transform;
            if (transform == null)
            {
                Component component = target as Component;
                if (component != null)
                    transform = component.transform;
            }
            if (transform != null)
            {
                point = transform.position;
                return true;
            }
            object position = GetMember(target, new[] { "position", "targetPosition", "pos" });
            if (position is Vector3)
            {
                point = (Vector3)position;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Linear drag of the coin prefab, read from a live or pooled coin's Rigidbody, so the
        /// cointhrow simulation uses the game's drag instead of a guess. False when the level has
        /// no coin object at all.
        /// </summary>
        public bool TryGetCoinDrag(out float drag)
        {
            drag = 0f;
            Type type = _coinType ?? (_coinType = FindType("Coin"));
            if (type == null)
                return false;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
            catch { return false; }
            for (int i = 0; i < found.Length; i++)
            {
                Component coin = found[i] as Component;
                if (coin == null)
                    continue;
                Rigidbody body = coin.GetComponent<Rigidbody>();
                if (body == null)
                    continue;
                drag = body.drag;
                return true;
            }

            // No coin object in the level yet (before the first toss): read the coin prefab the
            // revolver throws, so the toss model still uses the prefab's drag.
            Type revolver = FindType("Revolver");
            if (revolver != null)
            {
                UnityEngine.Object[] weapons;
                try { weapons = UnityEngine.Object.FindObjectsOfType(revolver, true); }
                catch { weapons = null; }
                if (weapons != null)
                {
                    for (int i = 0; i < weapons.Length; i++)
                    {
                        object prefab = GetMember(weapons[i], new[] { "coin", "coinPrefab" });
                        GameObject go = prefab as GameObject;
                        if (go == null)
                        {
                            Component prefabComponent = prefab as Component;
                            if (prefabComponent != null)
                                go = prefabComponent.gameObject;
                        }
                        if (go == null)
                            continue;
                        Rigidbody prefabBody = go.GetComponent<Rigidbody>();
                        if (prefabBody == null)
                            continue;
                        drag = prefabBody.drag;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Velocity a freshly thrown coin inherits: the rocket V1 rides when riding one,
        /// otherwise V1's own rigidbody velocity. Revolver.ThrowCoin adds exactly one of them.
        /// </summary>
        public bool TryGetCoinInheritedVelocity(out Vector3 velocity)
        {
            object player = NewMovement;
            object rocket = player != null ? GetMember(player, new[] { "ridingRocket" }) : null;
            Component rocketComponent = rocket as Component;
            if (rocketComponent != null)
            {
                Rigidbody rocketBody = GetMember(rocket, new[] { "rb", "body", "rigidbody" }) as Rigidbody;
                if (rocketBody == null)
                    rocketBody = rocketComponent.GetComponent<Rigidbody>();
                if (rocketBody != null)
                {
                    velocity = rocketBody.velocity;
                    return true;
                }
            }
            return TryGetPlayerVelocity(out velocity);
        }

        /// <summary>
        /// Floor height directly below a point, from one downward sweep that skips V1's own
        /// hierarchy. False when nothing solid is below (skybox, death pit), so the cointhrow
        /// model never invents a floor to land on.
        /// </summary>
        public bool TryGetGroundBelow(Vector3 origin, out float height)
        {
            height = 0f;
            RaycastHit[] hits = Physics.RaycastAll(origin + Vector3.up * 0.1f, Vector3.down, 1000f, ~0, QueryTriggerInteraction.Ignore);
            Transform player = PlayerTransform;
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hit = hits[i].transform;
                if (hit == null || (player != null && hit.IsChildOf(player)))
                    continue;
                if (hits[i].distance < best)
                {
                    best = hits[i].distance;
                    height = hits[i].point.y;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// Closest active hook point (HookPoint.active) within <paramref name="range"/> of
        /// <paramref name="origin"/> with nothing of the hook's environment mask
        /// (HookArm.enviroMask) in between, as the aim point for autofunc 'w'.
        /// </summary>
        public bool TryFindHookPoint(Vector3 origin, float range, out Vector3 point)
        {
            point = Vector3.zero;
            Type type = _hookPointType ?? (_hookPointType = FindType("HookPoint"));
            if (type == null)
                return false;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, false); }
            catch { return false; }

            int mask = 0;
            object hook = ResolveSingleton(ref _hookArm, _hookArmType ?? (_hookArmType = FindType("HookArm")));
            object enviro = hook != null ? GetMember(hook, new[] { "enviroMask" }) : null;
            if (enviro is LayerMask)
                mask = ((LayerMask)enviro).value;

            float best = range * range;
            bool any = false;
            for (int i = 0; i < found.Length; i++)
            {
                Component hookPoint = found[i] as Component;
                if (hookPoint == null || !hookPoint.gameObject.activeInHierarchy)
                    continue;
                // An inactive hook point (used, waiting for its reactivation timer) cannot be caught.
                if (!ToBool(GetMember(hookPoint, new[] { "active" }), true))
                    continue;
                Vector3 position = hookPoint.transform.position;
                float squared = (position - origin).sqrMagnitude;
                if (squared > best)
                    continue;
                RaycastHit hit;
                if (mask != 0 && Physics.Linecast(origin, position, out hit, mask, QueryTriggerInteraction.Ignore) &&
                    !hit.transform.IsChildOf(hookPoint.transform))
                    continue;
                best = squared;
                point = position;
                any = true;
            }
            return any;
        }

        /// <summary>The equipped weapon's object name (e.g. "Revolver Pierce") and its slot/variation index.</summary>
        public bool TryGetWeapon(out string name, out int slot, out int variation)
        {
            name = string.Empty;
            slot = -1;
            variation = -1;
            object gun = GunControl;
            if (gun == null)
                return false;
            GameObject weapon = GetMember(gun, new[] { "currentWeapon" }) as GameObject;
            if (weapon != null)
                name = CleanWeaponName(weapon.name);
            slot = ToInt(GetMember(gun, new[] { "currentSlotIndex" }), -1);
            variation = ToInt(GetMember(gun, new[] { "currentVariationIndex" }), -1);
            return name.Length > 0 || slot >= 0;
        }

        static string CleanWeaponName(string raw)
        {
            string name = raw ?? string.Empty;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone > 0)
                name = name.Substring(0, clone).Trim();
            return name;
        }

        /// <summary>
        /// Read-only preview of the weapon GunControl.SwitchWeapon would equip if the player
        /// pressed the 1-based slot key now. Nothing is changed in the game. It mirrors the game's
        /// own rules: GunControl.Update calls SwitchWeapon(slot, null, useRetained, false, false)
        /// (useRetained is true only for slot 6); the slot is clamped to the slot count; pressing the
        /// slot that is already equipped follows the "WeaponRedrawBehaviour" preference (0 next
        /// variation, 1 first variation, 2 keep it); another slot uses the remembered variation
        /// (retainedVariations, when useRetained or GunControl.variationMemory) or variation 0.
        /// Name, slot and variation come back in the same form TryGetWeapon reports them.
        /// </summary>
        public bool TryPreviewWeaponSlot(int oneBasedSlot, out string name, out int slot, out int variation)
        {
            name = string.Empty;
            slot = -1;
            variation = -1;
            object gun = GunControl;
            if (gun == null)
                return false;
            System.Collections.IList slots = GetMember(gun, new[] { "slots" }) as System.Collections.IList;
            if (slots == null || slots.Count == 0)
                return false;

            int target = Mathf.Clamp(oneBasedSlot, 1, slots.Count);
            System.Collections.IList variations = slots[target - 1] as System.Collections.IList;
            if (variations == null || variations.Count == 0)
                return false;

            int current = ToInt(GetMember(gun, new[] { "currentSlotIndex" }), -1);
            int currentVariation = ToInt(GetMember(gun, new[] { "currentVariationIndex" }), 0);
            int chosen = 0;
            if (current == target)
            {
                int redraw = 0;
                string error;
                object prefs = GetPrefsManager(out error);
                if (prefs != null)
                {
                    if (_prefsGetIntMethod == null)
                        _prefsGetIntMethod = FindMethod(prefs.GetType(), "GetInt", 2);
                    if (_prefsGetIntMethod != null)
                    {
                        try { redraw = ToInt(_prefsGetIntMethod.Invoke(prefs, new object[] { "WeaponRedrawBehaviour", 0 }), 0); }
                        catch { redraw = 0; }
                    }
                }
                if (redraw == 0)
                    chosen = (currentVariation + 1) % variations.Count;
                else if (redraw == 2)
                    chosen = currentVariation;
            }
            else
            {
                bool memory = oneBasedSlot >= 6 || ToBool(GetMember(gun, new[] { "variationMemory" }), false);
                System.Collections.IDictionary retained = memory ? GetMember(gun, new[] { "retainedVariations" }) as System.Collections.IDictionary : null;
                if (retained != null && retained.Contains(target - 1))
                {
                    int remembered = ToInt(retained[target - 1], 0);
                    if (remembered >= 0 && remembered < variations.Count)
                        chosen = remembered;
                }
            }
            if (chosen < 0 || chosen >= variations.Count)
                chosen = 0;

            GameObject weapon = variations[chosen] as GameObject;
            if (weapon != null)
                name = CleanWeaponName(weapon.name);
            slot = target;
            variation = chosen;
            return name.Length > 0;
        }

        bool _shotgunReadyWarned;

        /// <summary>
        /// Shot signal for the 'G1' swap: the Shotgun component on the equipped weapon
        /// (GunControl.currentWeapon) and its gunReady flag. The game clears gunReady when
        /// the shotgun fires (also on a pump / core eject) and sets it again when the
        /// weapon can fire. False when no shotgun is equipped or the member is missing.
        /// </summary>
        public bool TryGetShotgunReady(out object shotgun, out bool ready)
        {
            shotgun = null;
            ready = false;
            object gun = GunControl;
            if (gun == null)
                return false;
            GameObject weapon = GetMember(gun, new[] { "currentWeapon" }) as GameObject;
            Type type = FindType("Shotgun");
            if (weapon == null || type == null)
                return false;
            Component component = weapon.GetComponent(type);
            if (component == null)
                component = weapon.GetComponentInChildren(type);
            if (component == null)
                return false;
            object value = GetMember(component, new[] { "gunReady" });
            if (value == null)
            {
                if (!_shotgunReadyWarned)
                {
                    _shotgunReadyWarned = true;
                    _plugin.LogWarning("Shotgun.gunReady not found; the G1 shotgun swap cannot see shots.");
                }
                return false;
            }
            shotgun = component;
            ready = ToBool(value, false);
            return true;
        }

        public bool TryGetStyle(out float style)
        {
            style = 0f;
            string[] typeNames = { "StyleHUD", "StyleCalculator", "StyleManager", "StyleBonus" };
            for (int i = 0; i < typeNames.Length; i++)
            {
                Type type = FindType(typeNames[i]);
                if (type == null)
                    continue;
                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(type);
                if (found == null)
                    continue;
                object value = GetMember(found, new[] { "style", "stylePoints", "styleMeter", "points", "score" });
                if (value != null)
                {
                    style = ToFloat(value, 0f);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Enemy / scene helpers
        // ------------------------------------------------------------------

        /// <summary>Number of live EnemyIdentifier components in the loaded scene.</summary>
        public int EnemyCount
        {
            get
            {
                Type type = FindType("EnemyIdentifier");
                if (type == null)
                    return 0;
                UnityEngine.Object[] found;
                try
                {
                    found = UnityEngine.Object.FindObjectsOfType(type, true);
                }
                catch
                {
                    return 0;
                }
                int count = 0;
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] != null)
                        count++;
                }
                return count;
            }
        }

        /// <summary>Reload the active mission, through the game's loader so Addressable scenes work.</summary>
        public bool RestartMission()
        {
            try
            {
                Type type = SceneHelperType;
                if (type != null)
                {
                    MethodInfo restart = type.GetMethod("RestartScene",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                        null, Type.EmptyTypes, null);
                    if (restart != null)
                    {
                        restart.Invoke(null, null);
                        _plugin.LogInfo("Restarting mission through SceneHelper.RestartScene().");
                        return true;
                    }
                }

                Scene scene = SceneManager.GetActiveScene();
                if (scene.buildIndex >= 0)
                    SceneManager.LoadScene(scene.buildIndex);
                else if (!string.IsNullOrEmpty(scene.name))
                    SceneManager.LoadScene(scene.name);
                else
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "RestartMission failed: " + UnwrapMessage(ex);
                return false;
            }
        }

        /// <summary>
        /// Load a campaign level by id ("1-4") or scene name ("Level 1-4").
        ///
        /// ULTRAKILL ships its levels as Addressable scenes: ULTRAKILL_Data has a
        /// single baked scene, so SceneManager.LoadScene can never find them. The
        /// game's own SceneHelper.LoadScene(string, bool) is the only path that
        /// works, and it is what this calls first.
        /// </summary>
        public bool LoadLevel(string levelName)
        {
            if (string.IsNullOrEmpty(levelName))
            {
                LastError = "No level name given.";
                return false;
            }

            string id = LevelIdFromScene(levelName);
            if (id.Length == 0)
            {
                LastError = "'" + levelName.Trim() + "' is not a level id (expected e.g. 1-4 or Level 1-4).";
                return false;
            }

            string scene = "Level " + id;
            if (LoadGameScene(scene))
            {
                _plugin.LogInfo("Loading level scene '" + scene + "'.");
                return true;
            }

            // Fallback for anything that really is in the Unity build settings.
            try
            {
                SceneManager.LoadScene(scene);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Could not load '" + scene + "': " + UnwrapMessage(ex);
                _plugin.LogWarning(LastError);
                return false;
            }
        }

        /// <summary>
        /// Load the title screen through the game's own scene loader (the same call its
        /// "Main Menu" button makes), so a script can start from a clean menu.
        /// </summary>
        public bool LoadMainMenu()
        {
            const string scene = "Main Menu";
            if (LoadGameScene(scene))
            {
                _plugin.LogInfo("Loading the main menu scene.");
                return true;
            }
            try
            {
                SceneManager.LoadScene(scene);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Could not load '" + scene + "': " + UnwrapMessage(ex);
                _plugin.LogWarning(LastError);
                return false;
            }
        }

        /// <summary>True while the title screen is up (OptionsManager.mainMenu).</summary>
        public bool IsMainMenu
        {
            get
            {
                object options = OptionsManager;
                if (options == null)
                    return false;
                return ToBool(GetMember(options, new[] { "mainMenu" }), false);
            }
        }

        bool LoadGameScene(string scene)
        {
            try
            {
                Type type = SceneHelperType;
                if (type == null)
                    return false;

                MethodInfo method = null;
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "LoadScene")
                        continue;
                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        method = methods[i];
                        break;
                    }
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(bool) && method == null)
                        method = methods[i];
                }
                if (method == null)
                    return false;

                object[] args = method.GetParameters().Length == 2
                    ? new object[] { scene, true }
                    : new object[] { scene };
                method.Invoke(null, args);
                _plugin.LogInfo("SceneHelper.LoadScene('" + scene + "') invoked; PendingScene now '" +
                                (ReadStaticSceneHelperString("PendingScene") ?? string.Empty) + "'.");
                return true;
            }
            catch (Exception ex)
            {
                LastError = "SceneHelper.LoadScene('" + scene + "') failed: " + UnwrapMessage(ex);
                _plugin.LogWarning(LastError);
                return false;
            }
        }

        static string UnwrapMessage(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex.Message;
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        public string BuildReport()
        {
            ResolveTypes();
            List<string> lines = new List<string>();
            lines.Add("ULTRAKILL TAS Tool - game bridge report");
            lines.Add("Unity: " + Application.unityVersion);
            lines.Add("Scene: " + SceneName);
            lines.Add("Level id: " + (CurrentLevelId.Length == 0 ? "(none)" : CurrentLevelId) +
                      "   SceneHelper.CurrentScene: " + (ReadStaticSceneHelperString("CurrentScene") ?? "(missing)"));
            lines.Add("Types:");
            lines.Add("  NewMovement: " + DescribeType(_newMovementType));
            lines.Add("  CameraController: " + DescribeType(_cameraControllerType));
            lines.Add("  GunControl: " + DescribeType(_gunControlType));
            lines.Add("  InputManager: " + DescribeType(_inputManagerType));
            lines.Add("  OptionsManager: " + DescribeType(_optionsManagerType));
            lines.Add("  BaseInput (legacy UI): " + DescribeType(_baseInputType));
            lines.Add("Singletons:");
            lines.Add("  player: " + (PlayerComponent != null ? "ok" : "not present (menu?)"));
            lines.Add("  camera: " + (CameraController != null ? "ok" : "missing"));
            lines.Add("  gun control: " + (GunControl != null ? "ok" : "missing"));
            lines.Add("  input manager: " + (InputManager != null ? "ok" : "missing"));
            lines.Add("  options: " + (OptionsManager != null ? "ok" : "missing"));

            object controller = CameraController;
            lines.Add("Camera members:");
            lines.Add("  rotationX: " + MemberStatus(controller, "rotationX"));
            lines.Add("  rotationY: " + MemberStatus(controller, "rotationY"));
            lines.Add("  cam: " + MemberStatus(controller, "cam"));

            object gun = GunControl;
            lines.Add("Gun members:");
            lines.Add("  currentSlot: " + MemberStatus(gun, "currentSlot"));
            lines.Add("  currentVariation: " + MemberStatus(gun, "currentVariation"));

            object player = NewMovement;
            lines.Add("Player members:");
            lines.Add("  rb: " + MemberStatus(player, "rb"));
            lines.Add("  hp: " + MemberStatus(player, "hp"));
            lines.Add("  antiHp: " + MemberStatus(player, "antiHp"));
            lines.Add("  stamina: " + MemberStatus(player, "stamina"));

            object source = InputSource;
            lines.Add("Input:");
            lines.Add("  InputSource: " + (source != null ? source.GetType().Name : "missing"));
            string[] actions = { "W", "A", "S", "D", "Jump", "Slide", "Dodge", "Fire1", "Fire2", "Punch", "Hook", "Slot1", "Slot2", "Slot3", "Slot4", "Slot5", "Slot6" };
            int resolved = 0;
            for (int i = 0; i < actions.Length; i++)
            {
                bool ok = source != null && FindMember(source.GetType(), actions[i]) != null;
                if (ok)
                    resolved++;
            }
            lines.Add("  action states resolved: " + resolved + "/" + actions.Length);
            lines.Add("  inputsDictionary: " + (InputBindings != null ? InputBindings.Count + " bindings" : "missing"));
            lines.Add("Misc:");
            lines.Add("  OptionsManager.paused: " + MemberStatus(OptionsManager, "paused"));
            lines.Add("  savestate scope: player, living enemies, doors, projectiles/coins");
            lines.Add("  last error: " + (string.IsNullOrEmpty(LastError) ? "none" : LastError));
            return string.Join("\n", lines.ToArray());
        }

        static string DescribeType(Type type)
        {
            return type == null ? "NOT FOUND" : type.FullName;
        }

        static string MemberStatus(object target, string name)
        {
            if (target == null)
                return "n/a (object missing)";
            Type current = target.GetType();
            while (current != null && current != typeof(MonoBehaviour) && current != typeof(object))
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return "field ok";
                PropertyInfo property = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null)
                    return "property ok";
                current = current.BaseType;
            }
            return "NOT FOUND";
        }
    }
}
