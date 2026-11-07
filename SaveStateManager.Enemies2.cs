using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Extra enemy state.
    ///
    /// The core enemy capture restores an enemy that still exists. This module covers the
    /// two cases it cannot:
    ///
    ///  - The enemy was destroyed (gibbed, dissolved, removed by a cleanup) after the save.
    ///    A deactivated template of the enemy is stored at capture time and instantiated on
    ///    load, then the saved identifier fields, health, behaviours, animator and hitboxes
    ///    are written onto the new instance.
    ///  - The enemy survived but one of its child objects did not (a shield, a wing, a melee
    ///    hitbox that was destroyed or switched off). Child active flags, local poses and
    ///    colliders are kept by hierarchy path, and a missing child is rebuilt from the same
    ///    template.
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
            public GameObject Template;
            public bool HasTemplate;
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
                // Dormant and dead enemies are Lightweight: they have no pose or children
                // worth rebuilding, and cloning every sleeping arena actor is wasted work.
                if (enemy == null || enemy.Lightweight)
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
                    // thousands of transforms in a boss arena) and keeps only the rebuild
                    // recipe; slot saves always carry the full child state.
                    if (!_lightCapture)
                    {
                        extra.Children = CaptureEnemyChildren(root);
                        extra.Colliders = CaptureEnemyColliders(root);
                        extra.Renderers = CaptureEnemyRenderers(root);
                    }
                    extra.Template = GetOrCreateTemplate(identifier, root);
                    extra.HasTemplate = extra.Template != null;
                    extras.Add(extra);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("enemy extra " + identifier.name + ": " + ex.Message);
                }
            }
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
                    root = RebuildEnemy(state, extra);
                    if (root == null)
                    {
                        missing++;
                        continue;
                    }
                    identifier = Resolve(extra.Identifier);
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

            // Children can disappear from the hierarchy in any order; walk the saved list and
            // recreate what is missing before writing poses, so a parent rebuilt at index N
            // exists for the children that hang off it.
            if (extra.Children != null)
            {
                for (int i = 0; i < extra.Children.Count; i++)
                {
                    EnemyChildState child = extra.Children[i];
                    if (child == null || string.IsNullOrEmpty(child.Path))
                        continue;
                    if (ResolveEnemyPath(rootTransform, child.Path) != null)
                        continue;
                    RebuildEnemyChild(extra, rootTransform, child);
                }

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

        /// <summary>Instantiate the saved enemy template and put its whole saved state back.</summary>
        GameObject RebuildEnemy(Savestate state, EnemyExtraState extra)
        {
            if (extra == null || !extra.HasTemplate || extra.Template == null)
                return null;
            EnemyState enemy = FindEnemyState(state, extra.Identifier);
            if (enemy == null)
                return null;

            GameObject instance;
            try
            {
                instance = UnityEngine.Object.Instantiate(extra.Template);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Enemy respawn failed: " + ex.Message);
                return null;
            }

            try
            {
                instance.name = string.IsNullOrEmpty(extra.DisplayName) ? "enemy" : extra.DisplayName;
                instance.transform.position = enemy.Position;
                instance.transform.rotation = enemy.Rotation;
                instance.SetActive(false);

                Component identifier = FindComponentByName(instance, extra.IdentifierType);
                if (identifier == null)
                {
                    UnityEngine.Object.Destroy(instance);
                    return null;
                }

                ApplyFields(identifier, enemy.Fields);
                if (enemy.HasHealth)
                    SetMember(identifier, HealthNames, enemy.Health);
                SetMember(identifier, DeadNames, enemy.Dead);
                SetMember(identifier, BleedNames, enemy.Bleed);
                ApplyReferences(identifier, enemy.References);
                RestoreListRefs(identifier, enemy.ListRefs);
                RestoreRebuiltBehaviours(enemy, instance);
                RestoreRebuiltAnimators(enemy, instance);

                // Activate after the saved values are in, so Awake/OnEnable see them, then
                // write them once more because some game scripts reset fields there.
                instance.SetActive(enemy.ActiveSelf);
                ApplyFields(identifier, enemy.Fields);
                ApplyReferences(identifier, enemy.References);

                Rigidbody body = instance.GetComponent<Rigidbody>();
                if (body != null && enemy.Body != null)
                    RestoreBodyOn(enemy.Body, body);

                RestoreEnemyChildColliders(extra.Colliders, instance.transform);
                RestoreEnemyChildRenderers(extra.Renderers, instance.transform);

                // Later loads of this state restore the new instance in place, and the
                // orphan sweep keeps it because its root is now part of the saved set.
                enemy.Identifier = identifier;
                enemy.Root = instance;
                extra.Identifier = identifier;
                extra.Root = instance;
                state.EnemyRoots.Add(instance);
                _plugin.LogInfo("Savestate rebuilt enemy '" + instance.name + "' from its template.");
                return instance;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Enemy respawn skipped: " + ex.Message);
                return null;
            }
        }

        static EnemyState FindEnemyState(Savestate state, Component identifier)
        {
            if (state == null || state.Enemies == null || identifier == null)
                return null;
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                EnemyState enemy = state.Enemies[i];
                if (enemy != null && ReferenceEquals(enemy.Identifier, identifier))
                    return enemy;
            }
            return null;
        }

        static Component FindComponentByName(GameObject root, string typeName)
        {
            if (root == null || string.IsNullOrEmpty(typeName))
                return null;
            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component != null && component.GetType().Name == typeName)
                    return component;
            }
            return null;
        }

        /// <summary>Behaviours are matched to the new instance by type, in their saved order.</summary>
        static void RestoreRebuiltBehaviours(EnemyState enemy, GameObject root)
        {
            if (enemy.Behaviours == null || root == null)
                return;
            MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);
            Dictionary<Type, int> used = new Dictionary<Type, int>();
            for (int i = 0; i < enemy.Behaviours.Count; i++)
            {
                ComponentSnapshot snapshot = enemy.Behaviours[i];
                if (snapshot == null)
                    continue;
                Type type = null;
                try
                {
                    if (!ReferenceEquals(snapshot.Component, null))
                        type = snapshot.Component.GetType();
                }
                catch
                {
                }
                if (type == null)
                    continue;

                int skip;
                if (!used.TryGetValue(type, out skip))
                    skip = 0;
                int ordinal = 0;
                MonoBehaviour target = null;
                for (int c = 0; c < components.Length; c++)
                {
                    MonoBehaviour component = components[c];
                    if (component == null || component.GetType() != type)
                        continue;
                    if (ordinal >= skip)
                    {
                        target = component;
                        break;
                    }
                    ordinal++;
                }
                if (target == null)
                    continue;
                used[type] = skip + 1;
                ApplyFields(target, snapshot.Fields);
                ApplyReferences(target, snapshot.References);
                RestoreListRefs(target, snapshot.ListRefs);
            }
        }

        static void RestoreRebuiltAnimators(EnemyState enemy, GameObject root)
        {
            if (root == null)
                return;
            Animator primary = FindAnimator(root);
            ApplyAnimatorSnapshot(primary, enemy.Animator);
            if (enemy.ExtraAnimators == null || enemy.ExtraAnimators.Count == 0)
                return;
            Animator[] animators = root.GetComponentsInChildren<Animator>(true);
            List<Animator> others = new List<Animator>(animators.Length);
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] != null && !ReferenceEquals(animators[i], primary))
                    others.Add(animators[i]);
            }
            int count = Math.Min(others.Count, enemy.ExtraAnimators.Count);
            for (int i = 0; i < count; i++)
                ApplyAnimatorSnapshot(others[i], enemy.ExtraAnimators[i]);
        }

        static void ApplyAnimatorSnapshot(Animator target, AnimatorState snapshot)
        {
            if (target == null || snapshot == null || target.runtimeAnimatorController == null)
                return;
            RestoreAnimator(new AnimatorState
            {
                Animator = target,
                Enabled = snapshot.Enabled,
                Speed = snapshot.Speed,
                LayerCount = snapshot.LayerCount,
                StateHashes = snapshot.StateHashes,
                NormalizedTimes = snapshot.NormalizedTimes,
                LayerWeights = snapshot.LayerWeights,
                Parameters = snapshot.Parameters
            });
        }

        /// <summary>BodyState points at the destroyed body, so the values are written onto the new one.</summary>
        static void RestoreBodyOn(BodyState snapshot, Rigidbody body)
        {
            if (snapshot == null || body == null)
                return;
            try
            {
                body.constraints = snapshot.Constraints;
                body.useGravity = snapshot.UseGravity;
                body.detectCollisions = snapshot.DetectCollisions;
                body.drag = snapshot.Drag;
                body.angularDrag = snapshot.AngularDrag;
                body.isKinematic = snapshot.IsKinematic;
                if (!body.isKinematic)
                {
                    body.velocity = snapshot.Velocity;
                    body.angularVelocity = snapshot.AngularVelocity;
                    if (snapshot.Sleeping)
                        body.Sleep();
                    else
                        body.WakeUp();
                }
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Hierarchy paths
        // ------------------------------------------------------------------

        static string EnemyPath(Transform root, Transform target)
        {
            if (root == null || target == null || ReferenceEquals(root, target))
                return string.Empty;
            List<int> indexes = new List<int>(4);
            Transform current = target;
            while (current != null && !ReferenceEquals(current, root))
            {
                indexes.Add(current.GetSiblingIndex());
                current = current.parent;
            }
            if (current == null)
                return string.Empty;
            indexes.Reverse();
            System.Text.StringBuilder builder = new System.Text.StringBuilder(8);
            for (int i = 0; i < indexes.Count; i++)
            {
                if (i > 0)
                    builder.Append('/');
                builder.Append(indexes[i]);
            }
            return builder.ToString();
        }

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
                int index;
                if (!int.TryParse(parts[i], out index))
                    return null;
                if (index < 0 || index >= current.childCount)
                    return null;
                current = current.GetChild(index);
                if (current == null)
                    return null;
            }
            return current;
        }

        Transform RebuildEnemyChild(EnemyExtraState extra, Transform root, EnemyChildState child)
        {
            if (extra == null || extra.Template == null || root == null || child == null)
                return null;
            string path = child.Path;
            int split = path.LastIndexOf('/');
            string parentPath = split >= 0 ? path.Substring(0, split) : string.Empty;
            string indexText = split >= 0 ? path.Substring(split + 1) : path;
            int index;
            if (!int.TryParse(indexText, out index))
                return null;

            Transform parent = ResolveEnemyPath(root, parentPath);
            Transform templateParent = ResolveEnemyPath(extra.Template.transform, parentPath);
            if (parent == null || templateParent == null || index < 0 || index >= templateParent.childCount)
                return null;

            Transform templateChild = templateParent.GetChild(index);
            if (templateChild == null)
                return null;

            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(templateChild.gameObject);
                clone.name = child.Name;
                clone.SetActive(false);
                clone.transform.SetParent(parent, false);
                clone.transform.localPosition = child.LocalPosition;
                clone.transform.localRotation = child.LocalRotation;
                clone.transform.localScale = child.LocalScale;
                if (clone.activeSelf != child.ActiveSelf)
                    clone.SetActive(child.ActiveSelf);
                return clone.transform;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Enemy child rebuild skipped: " + ex.Message);
                return null;
            }
        }
    }
}
