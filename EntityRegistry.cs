using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// Stable entity IDs for the look_at statement and the entity inspector.
    ///
    /// ULTRAKILL has no stable object ID, so one is defined here. Every enemy, projectile
    /// and pickup/breakable component in the level scene is collected, including inactive
    /// ones (most wave enemies are placed in the level, inactive), and the list is sorted
    /// by a fixed key: the component's type name (ordinal), then its position in the
    /// hierarchy (sibling index of every transform from the scene root down), then its
    /// index among components of the same type on that GameObject. IDs 1..N follow that
    /// order.
    ///
    /// Two things make those numbers the same on every run. The scene is walked through
    /// its own root hierarchy (roots, then children in sibling order) rather than through
    /// FindObjectsOfType, whose enumeration order is not guaranteed, and the list is built
    /// at the first frame the level scene is visible (sceneLoaded for a single load, the
    /// per-frame check for the addressed loads that never raise it). Building it only when
    /// a script line first asks for an ID was what made the numbers drift: by then the
    /// level had spawned or destroyed objects, so the set being numbered was different
    /// each run. A savestate load keeps the IDs because it restores objects in place.
    ///
    /// Limit: objects created after the load (spawned projectiles, enemies a spawner
    /// instantiates, objects a savestate respawns) are not in that list. The inspector
    /// gives them IDs from 10000 upward in the order it first sees them; those IDs are
    /// stable for the rest of the scene but not across runs or savestate loads, so
    /// scripts should only aim at IDs below 10000.
    /// </summary>
    public sealed class EntityRegistry
    {
        public const int FirstSpawnedId = 10000;

        public sealed class Entity
        {
            public int Id;
            public Component Component;
            public string TypeName = string.Empty;
            public string Category = string.Empty;
            public bool Spawned;
        }

        /// <summary>One assigned ID and the object it names, carried inside a savestate.</summary>
        public sealed class EntityIdState
        {
            public int Id;
            public Component Component;
            /// <summary>Instance id of the component's GameObject at capture, for rebuilt objects.</summary>
            public int ObjectId;
            public string TypeName = string.Empty;
            public string Category = string.Empty;
            public bool Spawned;
        }

        /// <summary>
        /// The whole numbering at one revision. Immutable and shared: a savestate holds this
        /// reference, so capturing one per tick costs nothing while the IDs do not change.
        /// </summary>
        public sealed class EntityIdSnapshot
        {
            public int Revision;
            public EntityIdState[] Entries;
        }

        // Verified in Assembly-CSharp.dll. A name that is missing on a newer build is
        // skipped with one logged warning.
        static readonly string[] EnemyTypeNames = { "EnemyIdentifier" };
        static readonly string[] ProjectileTypeNames =
        {
            "Projectile", "Coin", "Nail", "Harpoon", "Grenade", "Cannonball", "Magnet",
            "GasolineProjectile", "Landmine", "ThrownSword", "BlackHoleProjectile", "Chainsaw"
        };
        static readonly string[] ObjectTypeNames = { "ItemIdentifier", "Breakable" };

        // Inspector state fields: label, then the member names tried in order.
        static readonly string[] StateLabels =
        {
            "hp", "dead", "type", "dmg", "friendly", "player", "parried", "rocket", "exploded",
            "item", "picked", "broken"
        };
        static readonly string[][] StateCandidates =
        {
            new[] { "health", "hp", "currentHealth" },
            new[] { "dead", "isDead" },
            new[] { "enemyType" },
            new[] { "damage" },
            new[] { "friendly" },
            new[] { "playerBullet" },
            new[] { "parried" },
            new[] { "rocket" },
            new[] { "exploded" },
            new[] { "itemType" },
            new[] { "pickedUp" },
            new[] { "broken" }
        };
        static readonly string[] EnemyLinkNames = { "eid" };

        readonly Plugin _plugin;
        readonly GameBridge _bridge;
        readonly List<Type> _types = new List<Type>(16);
        readonly List<string> _typeCategories = new List<string>(16);
        readonly HashSet<string> _warnedNames = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<Type, string> _categoryByType = new Dictionary<Type, string>(16);
        bool _typesResolved;
        Type _linkType;
        bool _pickWarned;

        readonly List<Entity> _level = new List<Entity>(256);
        readonly List<Entity> _spawned = new List<Entity>(64);
        readonly Dictionary<int, Entity> _byObject = new Dictionary<int, Entity>(256);
        Scene _scene;
        bool _built;
        int _idRevision;
        EntityIdSnapshot _idSnapshot;

        /// <summary>
        /// Set by SaveStateManager: the live replacement of an object a load rebuilt from an
        /// intact copy (a broken glass or crate). Without it an ID would stay on the object
        /// that was destroyed. Arguments are the GameObject's instance id at capture and the
        /// component type; the result may be null.
        /// </summary>
        public Func<int, Type, Component> RebuiltLookup;

        readonly RaycastHit[] _hits = new RaycastHit[64];
        readonly StringBuilder _stateText = new StringBuilder(128);

        public EntityRegistry(Plugin plugin, GameBridge bridge)
        {
            _plugin = plugin;
            _bridge = bridge;
        }

        /// <summary>Number of IDs assigned when the level loaded (IDs 1..LevelCount).</summary>
        public int LevelCount { get { return _level.Count; } }

        /// <summary>The level scene: the one the player is in, else the active scene.</summary>
        Scene LevelScene()
        {
            Transform player = _bridge.PlayerTransform;
            if (player != null)
                return player.gameObject.scene;
            return SceneManager.GetActiveScene();
        }

        /// <summary>Called from SceneManager.sceneLoaded, for single and additive loads.</summary>
        public void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Never let a failure here stop the engine's own scene-load handling.
            try
            {
                if (!scene.IsValid() || !scene.isLoaded)
                    return;
                Transform player = _bridge.PlayerTransform;
                bool playerHere = player != null && player.gameObject.scene.handle == scene.handle;
                // A single load replaces the world, so its scene is numbered now, after
                // Awake and before Start. An additive scene is only the level when the
                // player is in it; other additive scenes leave the level list alone.
                if (mode != LoadSceneMode.Single && !playerHere)
                    return;
                Rebuild(scene);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Entity IDs: rebuild failed: " + ex.Message);
            }
        }

        /// <summary>
        /// One cheap check per frame: numbers the level as soon as its scene is visible,
        /// and only then. ULTRAKILL loads levels through Addressables, which does not
        /// always raise sceneLoaded, so without this the list was first built whenever a
        /// script line or the inspector happened to ask for an ID - after the run had
        /// already spawned and destroyed objects, which shifted the numbers. Building it
        /// on the level's first frame keeps that set the same on every run.
        /// </summary>
        public void Poll()
        {
            Scene scene = LevelScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return;
            if (_built && _scene.IsValid() && _scene.isLoaded && _scene.handle == scene.handle)
                return;
            try
            {
                Rebuild(scene);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Entity IDs: rebuild failed: " + ex.Message);
            }
        }

        void EnsureBuilt()
        {
            Poll();
        }

        void ResolveTypes()
        {
            if (_typesResolved)
                return;
            _types.Clear();
            _typeCategories.Clear();
            _categoryByType.Clear();
            AddTypes(EnemyTypeNames, "enemy");
            AddTypes(ProjectileTypeNames, "projectile");
            AddTypes(ObjectTypeNames, "object");
            _linkType = _bridge.FindType("EnemyIdentifierIdentifier");
            if (_linkType == null && _warnedNames.Add("EnemyIdentifierIdentifier"))
                _plugin.LogWarning("Entity IDs: game type 'EnemyIdentifierIdentifier' not found; enemy hitboxes are matched by hierarchy only.");
            // FindType misses while the game assembly is still loading; try again later.
            _typesResolved = _types.Count > 0;
        }

        void AddTypes(string[] names, string category)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Type type = _bridge.FindType(names[i]);
                if (type == null)
                {
                    if (_warnedNames.Add(names[i]))
                        _plugin.LogWarning("Entity IDs: game type '" + names[i] + "' not found; it is skipped.");
                    continue;
                }
                if (_types.Contains(type))
                    continue;
                _types.Add(type);
                _typeCategories.Add(category);
                _categoryByType[type] = category;
            }
        }

        /// <summary>
        /// Category of a component's type: an exact match, else the nearest listed base
        /// type (FindObjectsOfType included subclasses, and so does this).
        /// </summary>
        bool TryGetCategory(Type type, out string category)
        {
            if (_categoryByType.TryGetValue(type, out category))
                return true;
            for (Type baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (_categoryByType.TryGetValue(baseType, out category))
                    return true;
            }
            category = string.Empty;
            return false;
        }

        void Rebuild(Scene scene)
        {
            _level.Clear();
            _spawned.Clear();
            _byObject.Clear();
            InvalidateIds();
            _scene = scene;
            _built = false;
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            ResolveTypes();
            // The game types are not in memory yet: Poll retries until they are, so the
            // list is never built from an empty type set (which made every entity a
            // 'spawned' one with an ID from 10000 that moved between runs).
            if (_types.Count == 0)
                return;

            // Walk the scene's own hierarchy instead of FindObjectsOfType: root order and
            // sibling order are fixed for a given scene, while FindObjectsOfType returns
            // objects in an order Unity does not guarantee. Inactive objects are included.
            List<Component> components = new List<Component>(512);
            GameObject[] roots;
            try
            {
                roots = scene.GetRootGameObjects();
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Entity IDs: scene root scan failed: " + ex.Message);
                return;
            }
            for (int r = 0; r < roots.Length; r++)
            {
                GameObject root = roots[r];
                if (root == null)
                    continue;
                Component[] found;
                try
                {
                    found = root.GetComponentsInChildren<Component>(true);
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Entity IDs: scan of '" + root.name + "' failed: " + ex.Message);
                    continue;
                }
                for (int i = 0; i < found.Length; i++)
                    components.Add(found[i]);
            }

            List<KeyValuePair<string, Entity>> keyed = new List<KeyValuePair<string, Entity>>(components.Count);
            for (int i = 0; i < components.Count; i++)
            {
                Component component = components[i];
                if (component == null || component.gameObject.scene.handle != scene.handle)
                    continue;
                string category;
                if (!TryGetCategory(component.GetType(), out category))
                    continue;
                Entity entity = new Entity
                {
                    Component = component,
                    TypeName = component.GetType().Name,
                    Category = category
                };
                keyed.Add(new KeyValuePair<string, Entity>(SortKey(component), entity));
            }

            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            for (int i = 0; i < keyed.Count; i++)
            {
                Entity entity = keyed[i].Value;
                entity.Id = i + 1;
                _level.Add(entity);
                int objectId = entity.Component.gameObject.GetInstanceID();
                if (!_byObject.ContainsKey(objectId))
                    _byObject.Add(objectId, entity);
            }
            _built = true;
            _plugin.LogInfo("Entity IDs: " + _level.Count + " entities in scene '" + scene.name + "' (IDs 1-" +
                            _level.Count + "; objects spawned later get IDs from " + FirstSpawnedId + ").");
        }

        /// <summary>Type name, then the sibling index of every transform from the root down, then the component index.</summary>
        static string SortKey(Component component)
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append(component.GetType().Name).Append('\u0001');
            List<int> path = new List<int>(16);
            for (Transform t = component.transform; t != null; t = t.parent)
                path.Add(t.GetSiblingIndex());
            for (int i = path.Count - 1; i >= 0; i--)
                sb.Append(path[i].ToString("D6", CultureInfo.InvariantCulture)).Append('/');
            Component[] same = component.gameObject.GetComponents(component.GetType());
            int index = Array.IndexOf(same, component);
            sb.Append('#').Append(index.ToString("D3", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
        /// The numbering as one immutable snapshot, so a savestate carries the IDs with it.
        /// The array is built once per revision and shared by every state captured after it:
        /// a per-tick history snapshot only takes a reference. Destroyed entries are kept -
        /// they hold the place of an ID that was in use, so the numbers after them never move.
        /// </summary>
        public EntityIdSnapshot CaptureIds()
        {
            EnsureBuilt();
            if (_idSnapshot != null && _idSnapshot.Revision == _idRevision)
                return _idSnapshot;

            List<EntityIdState> list = new List<EntityIdState>(_level.Count + _spawned.Count);
            for (int i = 0; i < _level.Count; i++)
                AddIdState(list, _level[i]);
            for (int i = 0; i < _spawned.Count; i++)
                AddIdState(list, _spawned[i]);
            _idSnapshot = new EntityIdSnapshot { Revision = _idRevision, Entries = list.ToArray() };
            return _idSnapshot;
        }

        static void AddIdState(List<EntityIdState> into, Entity entity)
        {
            if (entity == null)
                return;
            into.Add(new EntityIdState
            {
                Id = entity.Id,
                Component = entity.Component,
                ObjectId = entity.Component != null ? entity.Component.gameObject.GetInstanceID() : 0,
                TypeName = entity.TypeName,
                Category = entity.Category,
                Spawned = entity.Spawned
            });
        }

        void InvalidateIds()
        {
            _idRevision++;
            _idSnapshot = null;
        }

        /// <summary>
        /// Puts a savestate's numbering back. The level is not reloaded by a savestate, so the
        /// objects the state names are the same instances; a load that removed an object (a
        /// spawned enemy cleaned up as an orphan) leaves that ID dead instead of handing the
        /// number to whatever the inspector picks next. Lookups for a dead ID report the
        /// entity as destroyed, which is what look_at already does for a missing entity.
        /// </summary>
        public void RestoreIds(EntityIdSnapshot saved)
        {
            if (saved == null || saved.Entries == null || saved.Entries.Length == 0)
                return;
            // Scrubbing and undo restore the same state over and over; the registry already
            // describes it, so there is nothing to rebuild.
            if (_idSnapshot != null && ReferenceEquals(_idSnapshot.Entries, saved.Entries))
                return;
            // Build first when this scene was never numbered, so _scene/_built describe it.
            EnsureBuilt();
            _level.Clear();
            _spawned.Clear();
            _byObject.Clear();
            EntityIdState[] entries = saved.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                EntityIdState state = entries[i];
                if (state == null)
                    continue;
                Component component = state.Component;
                // A breakable that was rebuilt from its intact copy is a new object: move
                // the ID onto it, or look_at would name an object that no longer exists.
                if (component == null && (object)component != null && RebuiltLookup != null)
                {
                    try { component = RebuiltLookup(state.ObjectId, component.GetType()); }
                    catch { component = null; }
                }
                Entity entity = new Entity
                {
                    Id = state.Id,
                    Component = component,
                    TypeName = state.TypeName,
                    Category = state.Category,
                    Spawned = state.Spawned
                };
                if (state.Spawned)
                {
                    while (_spawned.Count < state.Id - FirstSpawnedId)
                        _spawned.Add(new Entity { Id = FirstSpawnedId + _spawned.Count });
                    _spawned.Add(entity);
                }
                else
                {
                    while (_level.Count < state.Id - 1)
                        _level.Add(new Entity { Id = _level.Count + 1 });
                    _level.Add(entity);
                }
                if (entity.Component != null && GameBridge.IsAlive(entity.Component))
                    _byObject[entity.Component.gameObject.GetInstanceID()] = entity;
            }
            // The restored layout is the snapshot's own: cache it under a fresh revision.
            _idSnapshot = new EntityIdSnapshot { Revision = ++_idRevision, Entries = entries };
        }

        /// <summary>The entity with this ID, or null when the ID is unknown.</summary>
        public Entity Find(int id)
        {
            EnsureBuilt();
            if (id >= 1 && id <= _level.Count)
                return _level[id - 1];
            int spawned = id - FirstSpawnedId;
            if (spawned >= 0 && spawned < _spawned.Count)
                return _spawned[spawned];
            return null;
        }

        /// <summary>World position of a live entity's transform.</summary>
        public bool TryGetPosition(int id, out Vector3 position)
        {
            Entity entity = Find(id);
            if (entity == null || !GameBridge.IsAlive(entity.Component))
            {
                position = Vector3.zero;
                return false;
            }
            position = entity.Component.transform.position;
            return true;
        }

        /// <summary>
        /// Position of a live, active entity for look_at. Inactive objects (a pooled coin,
        /// a dormant wave enemy) count as gone, so a name line retargets instead of aiming
        /// at something that is not in the world.
        /// </summary>
        public bool TryGetAimPosition(int id, out Vector3 position)
        {
            return TryGetAimPosition(id, string.Empty, out position);
        }

        /// <summary>
        /// Aim point for a live, active entity. With no hitbox this is the center of the
        /// target's active collider bounds (or the game's override center), instead of the
        /// transform pivot, which is at the feet for most enemies. A hitbox name selects a
        /// matching active child collider, transform, or EnemyIdentifier limb; "head"
        /// prefers the game's weakPoint object.
        /// </summary>
        public bool TryGetAimPosition(int id, string hitbox, out Vector3 position)
        {
            Entity entity = Find(id);
            if (entity == null || entity.Component == null ||
                !GameBridge.IsAlive(entity.Component) ||
                !entity.Component.gameObject.activeInHierarchy)
            {
                position = Vector3.zero;
                return false;
            }
            return TryGetAimPoint(entity.Component, hitbox, out position);
        }

        bool TryGetAimPoint(Component target, string hitbox, out Vector3 position)
        {
            position = target.transform.position;
            string wanted = NormalizeHitboxName(hitbox);
            if (wanted.Length == 0 || wanted == "center" || wanted == "body" ||
                wanted == "torso" || wanted == "chest" || wanted == "middle")
                return TryGetTargetCenter(target, out position);

            // ULTRAKILL marks some enemies' head/weak point explicitly. Prefer that over
            // guessing from child names, but still fall through when it is not active.
            if (wanted == "head" || wanted == "weakpoint")
            {
                Transform weakPoint = LookAtTransformOf(_bridge.GetMember(target, new[] { "weakPoint" }));
                if (weakPoint != null && weakPoint.gameObject.activeInHierarchy &&
                    TryGetTransformPoint(weakPoint, out position))
                    return true;
            }

            Collider[] colliders = null;
            try { colliders = target.GetComponentsInChildren<Collider>(true); }
            catch { colliders = null; }
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                        continue;
                    if (!HitboxNameMatches(collider.name, wanted) &&
                        !HitboxNameMatches(collider.gameObject.name, wanted))
                        continue;
                    position = collider.bounds.center;
                    return true;
                }
            }

            Transform[] transforms = null;
            try { transforms = target.GetComponentsInChildren<Transform>(true); }
            catch { transforms = null; }
            if (transforms != null)
            {
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    if (transform == null || !transform.gameObject.activeInHierarchy)
                        continue;
                    if (!HitboxNameMatches(transform.name, wanted))
                        continue;
                    if (TryGetTransformPoint(transform, out position))
                        return true;
                }
            }

            // Limbs can sit outside the enemy's own hierarchy and point back to it.
            Array limbs = _bridge.GetMember(target, new[] { "limbs" }) as Array;
            if (limbs != null)
            {
                for (int i = 0; i < limbs.Length; i++)
                {
                    Component limb = limbs.GetValue(i) as Component;
                    if (limb == null || !limb.gameObject.activeInHierarchy)
                        continue;
                    if (!HitboxNameMatches(limb.gameObject.name, wanted))
                        continue;
                    if (TryGetTransformPoint(limb.transform, out position))
                        return true;
                }
            }
            return false;
        }

        bool TryGetTargetCenter(Component target, out Vector3 position)
        {
            // overrideCenter is the game's own "body center" for enemies that set one.
            Transform overrideCenter = LookAtTransformOf(_bridge.GetMember(target, new[] { "overrideCenter" }));
            if (overrideCenter != null && overrideCenter.gameObject.activeInHierarchy &&
                TryGetTransformPoint(overrideCenter, out position))
                return true;
            if (TryGetColliderCenter(target, out position))
                return true;
            if (TryGetRendererCenter(target, out position))
                return true;
            position = target.transform.position;
            return true;
        }

        bool TryGetTransformPoint(Transform transform, out Vector3 position)
        {
            if (transform == null)
            {
                position = Vector3.zero;
                return false;
            }
            if (TryGetColliderCenter(transform, out position))
                return true;
            if (TryGetRendererCenter(transform, out position))
                return true;
            position = transform.position;
            return true;
        }

        static bool TryGetColliderCenter(Component target, out Vector3 center)
        {
            center = Vector3.zero;
            Collider[] colliders;
            try { colliders = target.GetComponentsInChildren<Collider>(true); }
            catch { return false; }
            bool any = false;
            Bounds bounds = new Bounds();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                if (!any)
                {
                    bounds = collider.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }
            if (!any)
                return false;
            center = bounds.center;
            return true;
        }

        static bool TryGetRendererCenter(Component target, out Vector3 center)
        {
            center = Vector3.zero;
            Renderer[] renderers;
            try { renderers = target.GetComponentsInChildren<Renderer>(true); }
            catch { return false; }
            bool any = false;
            Bounds bounds = new Bounds();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            if (!any)
                return false;
            center = bounds.center;
            return true;
        }

        static Transform LookAtTransformOf(object value)
        {
            Transform transform = value as Transform;
            if (transform != null)
                return transform;
            GameObject gameObject = value as GameObject;
            if (gameObject != null)
                return gameObject.transform;
            Component component = value as Component;
            return component != null ? component.transform : null;
        }

        static string NormalizeHitboxName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            StringBuilder builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        static bool HitboxNameMatches(string candidate, string wanted)
        {
            string normalized = NormalizeHitboxName(candidate);
            if (normalized.Length == 0)
                return false;
            if (normalized == wanted)
                return true;
            return wanted.Length >= 3 && normalized.Length > wanted.Length &&
                   normalized.StartsWith(wanted, StringComparison.Ordinal);
        }

        /// <summary>
        /// The closest live, active entity matching <paramref name="name"/>, measured from
        /// <paramref name="origin"/>. A name matches, in order of preference: the
        /// component type (coin, nail, EnemyIdentifier), an enemy's type (v2, filth,
        /// swordsmachine), the object's name, or a category (enemy, projectile, object).
        /// Backs "look_at v2"-style lines: the caller pins the returned ID for the tick
        /// it was resolved on, so the same state always picks the same object.
        /// </summary>
        public bool TryFindNearest(string name, Vector3 origin, out Entity nearest, out float distance)
        {
            nearest = null;
            distance = float.MaxValue;
            if (string.IsNullOrEmpty(name))
                return false;
            EnsureBuilt();
            if (_types.Count == 0)
                return false;

            // A component type name ("coin") can only be answered by that type, and a
            // category ("enemy") only by its types. Restricting the scan matters when a name
            // line is waiting for a target: the unlimited version ran FindObjectsOfType for
            // every entity type on every tick, which lagged badly after a norefresh restore.
            int onlyType = -1;
            for (int t = 0; t < _types.Count; t++)
            {
                if (string.Equals(_types[t].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    onlyType = t;
                    break;
                }
            }
            bool onlyCategory = string.Equals(name, "enemy", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "projectile", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "object", StringComparison.OrdinalIgnoreCase);

            // A live scan, not the level list: coins, nails, grenades and spawned enemies
            // are created during play and never appear in the numbering built at scene load;
            // they only enter _spawned if the inspector happened to pick them.
            Transform player = _bridge.PlayerTransform;
            Component bestEntity = null;
            string bestCategory = null;
            int bestRank = int.MaxValue;
            float bestSquared = float.MaxValue;
            HashSet<int> seen = new HashSet<int>();
            for (int t = 0; t < _types.Count; t++)
            {
                if (onlyType >= 0 && t != onlyType)
                    continue;
                if (onlyType < 0 && onlyCategory &&
                    !string.Equals(_typeCategories[t], name, StringComparison.OrdinalIgnoreCase))
                    continue;
                UnityEngine.Object[] found;
                // Only active objects can be a look_at target; skipping inactive ones here
                // also avoids collecting every pooled coin and dormant wave enemy.
                try { found = UnityEngine.Object.FindObjectsOfType(_types[t], false); }
                catch { continue; }
                string category = _typeCategories[t];
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || !GameBridge.IsAlive(component) ||
                        !component.gameObject.activeInHierarchy ||
                        !seen.Add(component.GetInstanceID()))
                        continue;
                    // The player carries no look_at target of its own.
                    if (player != null && component.transform.IsChildOf(player))
                        continue;
                    int rank = MatchRank(component, category, name);
                    if (rank < 0)
                        continue;
                    float squared = (component.transform.position - origin).sqrMagnitude;
                    if (rank < bestRank || (rank == bestRank && squared < bestSquared))
                    {
                        bestRank = rank;
                        bestSquared = squared;
                        bestEntity = component;
                        bestCategory = category;
                    }
                }
            }
            if (bestEntity != null)
            {
                nearest = ResolveEntity(bestEntity, bestCategory);
                distance = Mathf.Sqrt(bestSquared);
            }
            return nearest != null;
        }

        /// <summary>
        /// How well a component answers to a look_at name. Lower is better: exact component
        /// type (coin), exact enemy type or object name (v2, V2Second, Filth), a prefix of
        /// the enemy type (v2 also finds V2Second), then the category (enemy, projectile,
        /// object). -1 means no match.
        /// </summary>
        int MatchRank(Component component, string category, string name)
        {
            if (string.Equals(component.GetType().Name, name, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (string.Equals(category, "enemy", StringComparison.Ordinal))
            {
                string enemyType = EnemySubtype(component);
                if (enemyType.Length > 0)
                {
                    if (string.Equals(enemyType, name, StringComparison.OrdinalIgnoreCase))
                        return 1;
                    if (enemyType.Length > name.Length &&
                        enemyType.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        return 2;
                }
            }

            // The HUD shows the object's name, so accept it as an alias too.
            string objectName = component.gameObject.name;
            if (!string.IsNullOrEmpty(objectName) &&
                string.Equals(objectName, name, StringComparison.OrdinalIgnoreCase))
                return 1;

            if (string.Equals(category, name, StringComparison.OrdinalIgnoreCase))
                return 3;
            return -1;
        }

        /// <summary>
        /// An enemy's type name from EnemyIdentifier.enemyType ("V2", "Filth",
        /// "V2Second"), or its overrideFullName when a build leaves the enum unset.
        /// Empty for anything that is not an enemy.
        /// </summary>
        public string EnemySubtype(Component component)
        {
            if (component == null)
                return string.Empty;
            object value = _bridge.GetMember(component, new[] { "enemyType" });
            string text = value != null ? value.ToString() : string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                object fullName = _bridge.GetMember(component, new[] { "overrideFullName" });
                if (fullName is string)
                    text = (string)fullName;
            }
            return text ?? string.Empty;
        }

        /// <summary>The numbered entity for a live component, numbering it as spawned if it is new.</summary>
        Entity ResolveEntity(Component component, string category)
        {
            int objectId = component.gameObject.GetInstanceID();
            Entity entity;
            if (_byObject.TryGetValue(objectId, out entity) && GameBridge.IsAlive(entity.Component))
                return entity;
            return RegisterSpawned(component, category);
        }

        public bool TryGetVelocity(Entity entity, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (entity == null || !GameBridge.IsAlive(entity.Component))
                return false;
            Rigidbody body = entity.Component.GetComponent<Rigidbody>();
            if (body == null)
                return false;
            velocity = body.velocity;
            return true;
        }

        /// <summary>
        /// The nearest entity along a ray from the camera. A raycast runs first; a thin
        /// sphere cast catches small projectiles it missed. Trigger colliders count (many
        /// projectiles use them). Level geometry does not block the pick; occluded reports
        /// whether a solid non-entity collider was hit before the entity.
        /// </summary>
        public Entity Pick(Vector3 origin, Vector3 direction, float maxDistance,
                           out float hitDistance, out bool occluded)
        {
            EnsureBuilt();
            hitDistance = 0f;
            occluded = false;
            float nearestSolid = float.MaxValue;
            Transform player = _bridge.PlayerTransform;
            Entity best = null;
            try
            {
                int count = Physics.RaycastNonAlloc(origin, direction, _hits, maxDistance, ~0, QueryTriggerInteraction.Collide);
                best = BestHit(count, player, ref hitDistance, ref nearestSolid);
                if (best == null)
                {
                    count = Physics.SphereCastNonAlloc(origin, 0.5f, direction, _hits, maxDistance, ~0, QueryTriggerInteraction.Collide);
                    best = BestHit(count, player, ref hitDistance, ref nearestSolid);
                }
            }
            catch (Exception ex)
            {
                if (!_pickWarned)
                {
                    _pickWarned = true;
                    _plugin.LogWarning("Entity inspector: pick failed: " + ex.Message);
                }
                return null;
            }
            occluded = best != null && nearestSolid < hitDistance - 0.01f;
            return best;
        }

        Entity BestHit(int count, Transform player, ref float hitDistance, ref float nearestSolid)
        {
            Entity best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count && i < _hits.Length; i++)
            {
                Collider collider = _hits[i].collider;
                if (collider == null)
                    continue;
                if (player != null && collider.transform.IsChildOf(player))
                    continue;
                float distance = _hits[i].distance;
                Entity entity = FromCollider(collider);
                if (entity == null)
                {
                    if (!collider.isTrigger && distance < nearestSolid)
                        nearestSolid = distance;
                    continue;
                }
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = entity;
                }
            }
            if (best != null)
                hitDistance = bestDistance;
            return best;
        }

        /// <summary>The entity a collider belongs to: the collider's object or an ancestor.</summary>
        Entity FromCollider(Collider collider)
        {
            Entity entity;
            for (Transform t = collider.transform; t != null; t = t.parent)
            {
                if (_byObject.TryGetValue(t.gameObject.GetInstanceID(), out entity) && GameBridge.IsAlive(entity.Component))
                    return entity;
            }

            // Enemy hitboxes can sit outside the enemy's hierarchy and point back at it.
            Component link = _linkType != null ? collider.GetComponent(_linkType) : null;
            if (link != null)
            {
                Component eid = _bridge.GetMember(link, EnemyLinkNames) as Component;
                if (eid != null && _byObject.TryGetValue(eid.gameObject.GetInstanceID(), out entity))
                    return entity;
                if (eid != null)
                    return RegisterSpawned(eid, "enemy");
            }

            // Not in the level list: something spawned after the load.
            for (Transform t = collider.transform; t != null; t = t.parent)
            {
                for (int i = 0; i < _types.Count; i++)
                {
                    Component component = t.GetComponent(_types[i]);
                    if (component != null)
                        return RegisterSpawned(component, _typeCategories[i]);
                }
            }
            return null;
        }

        Entity RegisterSpawned(Component component, string category)
        {
            int objectId = component.gameObject.GetInstanceID();
            Entity entity;
            if (_byObject.TryGetValue(objectId, out entity) && GameBridge.IsAlive(entity.Component))
                return entity;
            entity = new Entity
            {
                Id = FirstSpawnedId + _spawned.Count,
                Component = component,
                TypeName = component.GetType().Name,
                Category = category,
                Spawned = true
            };
            _spawned.Add(entity);
            _byObject[objectId] = entity;
            InvalidateIds();
            return entity;
        }

        /// <summary>HP and state fields the entity's component has, e.g. "hp 3  dead no  type Filth".</summary>
        public string DescribeState(Entity entity)
        {
            if (entity == null || !GameBridge.IsAlive(entity.Component))
                return string.Empty;
            _stateText.Length = 0;
            for (int i = 0; i < StateLabels.Length; i++)
            {
                object value = _bridge.GetMember(entity.Component, StateCandidates[i]);
                if (value == null)
                    continue;
                if (_stateText.Length > 0)
                    _stateText.Append("  ");
                _stateText.Append(StateLabels[i]).Append(' ');
                if (value is float)
                    _stateText.Append(((float)value).ToString("0.##", CultureInfo.InvariantCulture));
                else if (value is bool)
                    _stateText.Append((bool)value ? "yes" : "no");
                else
                    _stateText.Append(value.ToString());
            }
            return _stateText.ToString();
        }
    }
}
