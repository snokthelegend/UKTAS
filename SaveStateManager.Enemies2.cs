using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace UltrakillTAS
{
    /// <summary>
    /// In-place extra enemy state: child active flags, local poses and collider/renderer
    /// flags, keyed by hierarchy path. Every enemy that still exists is restored where it is.
    /// One that no longer exists is counted as missing; this module never instantiates prefabs
    /// and never edits boss-bar/UI arrays.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        sealed class EnemyChildCollider
        {
            public string Path;
            public int Index;
            public string TypeName;
            public bool Enabled;
        }

        sealed class EnemyChildState
        {
            public string Path;
            public string Name;
            /// <summary>Sibling index at capture, used to put a rebuilt child back where it was.</summary>
            public int SiblingIndex;
            public bool ActiveSelf;
            public Vector3 LocalPosition;
            public Vector3 LocalRotationEuler;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public List<EnemyChildCollider> Colliders;
        }

        sealed class EnemyChildRenderer
        {
            public string Path;
            public int Index;
            public bool Enabled;
        }

        sealed class EnemyExtraState
        {
            public Component Identifier;
            public GameObject Root;
            public string Key;
            public string DisplayName;
            public string IdentifierType;
            public List<EnemyChildState> Children;
            public List<EnemyChildCollider> Colliders;
            public List<EnemyChildRenderer> Renderers;
        }

        const int MaxEnemyChildObjects = 4096;

        /// <summary>
        /// A deactivated clone from GetOrCreateTemplate. It must never be captured as a live
        /// enemy, restored, or swept as an orphan.
        /// </summary>
        internal static bool IsTemplateClone(GameObject go)
        {
            return go != null && go.name.StartsWith("[TAS template]", StringComparison.Ordinal);
        }

        partial void CaptureEnemies2(Savestate state)
        {
            if (_identifierType == null)
                return;

            List<EnemyExtraState> extras = new List<EnemyExtraState>();
            // The core enemy capture already ran (CaptureModules is called after it), so this
            // reuses its list instead of scanning every EnemyIdentifier a second time.
            List<EnemyState> enemies = state.Enemies;
            if (enemies == null)
            {
                SetExt(state, "Enemies2", extras);
                return;
            }
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyState enemy = enemies[i];
                // Every enemy now carries a complete state, including dormant and dead
                // actors. Their child rigs and templates are what let them wake correctly.
                if (enemy == null)
                    continue;
                Component identifier = enemy.Identifier;
                if (identifier == null)
                    continue;
                GameObject root = enemy.Root != null ? enemy.Root : identifier.gameObject;
                if (root == null || IsTemplateClone(root))
                    continue;

                try
                {
                    EnemyExtraState extra = new EnemyExtraState
                    {
                        Identifier = identifier,
                        Root = root,
                        DisplayName = CleanObjectName(root.name),
                        Key = identifier.GetType().Name + "|" + CleanObjectName(root.name),
                        IdentifierType = identifier.GetType().Name
                    };
                    // A light rolling-history capture skips the per-child walk (it can be
                    // thousands of transforms in a boss arena); slot saves carry the full
                    // child state.
                    if (!_lightCapture)
                    {
                        extra.Children = CaptureEnemyChildren(root);
                        extra.Colliders = CaptureEnemyColliders(root);
                        extra.Renderers = CaptureEnemyRenderers(root);
                    }
                    extras.Add(extra);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("enemy extra " + identifier.name + ": " + ex.Message);
                }
            }
            // Gore scanning is a full-scene pass: slot saves and start states take it, the
            // per-tick rolling history (which restores during undo only) does not.
            if (!_lightCapture)
                CaptureGoreIds(state);
            SetExt(state, "Enemies2", extras);
        }

        partial void RestoreEnemies2(Savestate state, ref int restored, ref int missing)
        {
            List<EnemyExtraState> extras = GetExt<List<EnemyExtraState>>(state, "Enemies2");
            if (extras == null)
                return;

            for (int i = 0; i < extras.Count; i++)
            {
                EnemyExtraState extra = extras[i];
                if (extra == null)
                    continue;
                Component identifier = Resolve(extra.Identifier);
                GameObject root = identifier != null ? identifier.gameObject : null;
                if (root == null)
                {
                    // The enemy no longer exists. Count it as missing and leave the scene
                    // alone: no prefab lookup, no re-instantiation, no UI edits.
                    missing++;
                    continue;
                }

                try
                {
                    RestoreEnemyChildren(extra, root);
                    RestoreEnemyChildColliders(extra.Colliders, root.transform);
                    RestoreEnemyChildRenderers(extra.Renderers, root.transform);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Enemy child restore skipped: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // Capture
        // ------------------------------------------------------------------

        static List<EnemyChildState> CaptureEnemyChildren(GameObject root)
        {
            List<EnemyChildState> children = new List<EnemyChildState>(16);
            if (root == null)
                return children;
            Transform rootTransform = root.transform;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length && children.Count < MaxEnemyChildObjects; i++)
            {
                Transform transform = transforms[i];
                if (transform == null || ReferenceEquals(transform, rootTransform))
                    continue;
                children.Add(new EnemyChildState
                {
                    Path = EnemyPath(rootTransform, transform),
                    Name = transform.name,
                    SiblingIndex = transform.GetSiblingIndex(),
                    ActiveSelf = transform.gameObject.activeSelf,
                    LocalPosition = transform.localPosition,
                    LocalRotation = transform.localRotation,
                    LocalScale = transform.localScale
                });
            }
            if (children.Count >= MaxEnemyChildObjects)
                NoteCap("enemy child objects", MaxEnemyChildObjects);
            return children;
        }

        static List<EnemyChildCollider> CaptureEnemyColliders(GameObject root)
        {
            List<EnemyChildCollider> colliders = new List<EnemyChildCollider>(8);
            if (root == null)
                return colliders;
            Transform rootTransform = root.transform;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null)
                    continue;
                Collider[] found = transform.GetComponents<Collider>();
                string path = EnemyPath(rootTransform, transform);
                for (int c = 0; c < found.Length; c++)
                {
                    Collider collider = found[c];
                    if (collider == null)
                        continue;
                    colliders.Add(new EnemyChildCollider
                    {
                        Path = path,
                        Index = c,
                        TypeName = collider.GetType().Name,
                        Enabled = collider.enabled
                    });
                }
            }
            return colliders;
        }

        static List<EnemyChildRenderer> CaptureEnemyRenderers(GameObject root)
        {
            List<EnemyChildRenderer> renderers = new List<EnemyChildRenderer>(8);
            if (root == null)
                return renderers;
            Transform rootTransform = root.transform;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null)
                    continue;
                Renderer[] found = transform.GetComponents<Renderer>();
                string path = EnemyPath(rootTransform, transform);
                for (int r = 0; r < found.Length; r++)
                {
                    if (found[r] == null)
                        continue;
                    renderers.Add(new EnemyChildRenderer
                    {
                        Path = path,
                        Index = r,
                        Enabled = found[r].enabled
                    });
                }
            }
            return renderers;
        }

        static void RestoreEnemyChildRenderers(List<EnemyChildRenderer> renderers, Transform root)
        {
            if (renderers == null || root == null)
                return;
            for (int i = 0; i < renderers.Count; i++)
            {
                EnemyChildRenderer saved = renderers[i];
                if (saved == null)
                    continue;
                Transform target = ResolveEnemyPath(root, saved.Path);
                if (target == null)
                    continue;
                try
                {
                    Renderer[] found = target.GetComponents<Renderer>();
                    if (saved.Index < 0 || saved.Index >= found.Length)
                        continue;
                    if (found[saved.Index] != null && found[saved.Index].enabled != saved.Enabled)
                        found[saved.Index].enabled = saved.Enabled;
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // Restore
        // ------------------------------------------------------------------

        void RestoreEnemyChildren(EnemyExtraState extra, GameObject root)
        {
            if (extra == null || root == null)
                return;
            Transform rootTransform = root.transform;

            // Only children that still exist are restored in place. A child destroyed after
            // the save (limb, shield, hitbox) stays gone: rebuilding it from a clone is what
            // corrupted boss bars and other UI that holds references into the hierarchy.
            if (extra.Children != null)
            {
                for (int i = 0; i < extra.Children.Count; i++)
                {
                    EnemyChildState child = extra.Children[i];
                    if (child == null)
                        continue;
                    Transform target = ResolveEnemyPath(rootTransform, child.Path);
                    if (target == null)
                        continue;
                    try
                    {
                        target.localPosition = child.LocalPosition;
                        target.localRotation = child.LocalRotation;
                        target.localScale = child.LocalScale;
                        if (target.gameObject.activeSelf != child.ActiveSelf)
                            target.gameObject.SetActive(child.ActiveSelf);
                    }
                    catch
                    {
                    }
                }
            }
        }

        static void RestoreEnemyChildColliders(List<EnemyChildCollider> colliders, Transform root)
        {
            if (colliders == null || root == null)
                return;
            for (int i = 0; i < colliders.Count; i++)
            {
                EnemyChildCollider saved = colliders[i];
                if (saved == null)
                    continue;
                Transform target = ResolveEnemyPath(root, saved.Path);
                if (target == null)
                    continue;
                try
                {
                    Collider[] found = target.GetComponents<Collider>();
                    for (int c = 0; c < found.Length; c++)
                    {
                        Collider collider = found[c];
                        if (collider == null)
                            continue;
                        bool sameSlot = c == saved.Index || collider.GetType().Name == saved.TypeName;
                        if (!sameSlot)
                            continue;
                        if (collider.enabled != saved.Enabled)
                            collider.enabled = saved.Enabled;
                    }
                }
                catch
                {
                }
            }
        }


        // ------------------------------------------------------------------
        // Hierarchy paths
        // ------------------------------------------------------------------

        /// <summary>
        /// A stable hierarchy path: "name#occurrence" per level, where occurrence counts the
        /// siblings that share that name. Sibling indices were used before, but an enemy's
        /// hierarchy gains and loses children while it fights and dies (effects, gibs, limbs),
        /// and every child after the change then resolved to the wrong transform. Saved poses,
        /// local scales and collider/renderer flags were written onto unrelated objects, which
        /// is how an enemy could come back stretched or with its model still torn apart. A name
        /// path keeps pointing at the same object while other siblings come and go.
        /// </summary>
        static string EnemyPath(Transform root, Transform target)
        {
            if (root == null || target == null || ReferenceEquals(root, target))
                return string.Empty;
            List<string> segments = new List<string>(4);
            Transform current = target;
            while (current != null && !ReferenceEquals(current, root))
            {
                Transform parent = current.parent;
                if (parent == null)
                    return string.Empty;
                int occurrence = 0;
                int index = current.GetSiblingIndex();
                for (int i = 0; i < index; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling != null && sibling.name == current.name)
                        occurrence++;
                }
                segments.Add(current.name + "#" + occurrence.ToString(CultureInfo.InvariantCulture));
                current = parent;
            }
            if (current == null)
                return string.Empty;
            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        /// <summary>Resolve a name#occurrence path. Null means the child is gone (rebuild it).</summary>
        static Transform ResolveEnemyPath(Transform root, string path)
        {
            if (root == null)
                return null;
            if (string.IsNullOrEmpty(path))
                return root;
            string[] parts = path.Split('/');
            Transform current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int hash = part.LastIndexOf('#');
                if (hash <= 0)
                    return null;
                string name = part.Substring(0, hash);
                int occurrence;
                if (!int.TryParse(part.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out occurrence) ||
                    occurrence < 0)
                    return null;
                Transform next = null;
                int seen = 0;
                for (int c = 0; c < current.childCount; c++)
                {
                    Transform child = current.GetChild(c);
                    if (child == null || child.name != name)
                        continue;
                    if (seen++ == occurrence)
                    {
                        next = child;
                        break;
                    }
                }
                if (next == null)
                    return null;
                current = next;
            }
            return current;
        }


        /// <summary>
        /// Gore objects (gibs, severed limbs, blood pools, ragdolls) present when the state was
        /// captured. A load destroys the ones the death created afterwards, so a revived enemy
        /// is not surrounded by its own corpse, and a gibbed enemy is not left looking dead.
        /// </summary>
        void CaptureGoreIds(Savestate state)
        {
            HashSet<int> ids = new HashSet<int>();
            List<Type> types = GoreTypes();
            for (int t = 0; t < types.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(types[t], true); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    Transform root = component.transform.root;
                    ids.Add(component.gameObject.GetInstanceID());
                    if (root != null)
                        ids.Add(root.gameObject.GetInstanceID());
                }
            }
            SetExt(state, "GoreIds", ids);
        }

        /// <summary>
        /// Destroy gore spawned after the save. Objects that existed at capture are kept, as are
        /// saved enemy roots, the player rig and this tool's templates.
        /// </summary>
        internal void CleanupGoreAfterRestore(Savestate state)
        {
            HashSet<int> saved = GetExt<HashSet<int>>(state, "GoreIds");
            if (saved == null)
                return;
            Component player = _bridge.PlayerComponent;
            Transform playerTransform = player != null ? player.transform : null;
            List<Type> types = GoreTypes();
            HashSet<GameObject> seen = new HashSet<GameObject>();
            int removed = 0;
            for (int t = 0; t < types.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(types[t], true); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    Transform rootTransform = component.transform.root;
                    if (rootTransform == null)
                        continue;
                    GameObject root = rootTransform.gameObject;
                    if (!seen.Add(root))
                        continue;
                    if (saved.Contains(root.GetInstanceID()) || saved.Contains(component.gameObject.GetInstanceID()))
                        continue;
                    if (IsTemplateClone(root) || state.EnemyRoots.Contains(root) ||
                        state.ProjectileRoots.Contains(root))
                        continue;
                    if (playerTransform != null && rootTransform.IsChildOf(playerTransform))
                        continue;
                    UnityEngine.Object.Destroy(root);
                    removed++;
                }
            }
            if (removed > 0)
                _plugin.LogInfo("Savestate load removed " + removed + " gore object(s) spawned after the save.");
        }

        /// <summary>
        /// Optimizer restores run thousands of times and their world never has gore from a
        /// death (the master is captured before the segment), so the sweep is skipped there.
        /// </summary>
        bool _skipGoreSweep;

        /// <summary>The tick, script time and script the capture belongs to, supplied by TasEngine.</summary>
        public Func<int> TickProvider;
        public Func<double> TimeProvider;
        public Func<string> ScriptProvider;

        static List<Type> _goreTypes;

        /// <summary>
        /// The game's gore component types, resolved once from the assembly that owns
        /// EnemyIdentifier (the mod assembly has none of them).
        /// </summary>
        List<Type> GoreTypes()
        {
            if (_goreTypes != null)
                return _goreTypes;
            _goreTypes = new List<Type>();
            if (_identifierType == null)
                return _goreTypes;
            Type[] all;
            try { all = _identifierType.Assembly.GetTypes(); }
            catch { return _goreTypes; }
            for (int i = 0; i < all.Length; i++)
            {
                Type type = all[i];
                if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract)
                    continue;
                string name = type.Name;
                if (name.IndexOf("Gib", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Bloodstain", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("BloodPool", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Gore", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _goreTypes.Add(type);
            }
            return _goreTypes;
        }
    }
}
