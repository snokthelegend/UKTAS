using System;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Player module for state the rig capture does not own.
    ///
    /// The player is usually parented to whatever it stands on: an elevator cabin, a moving
    /// platform, a piston. Parent and local pose are part of that relationship, so a load
    /// that only writes a world position leaves the player either detached from the platform
    /// (and desynchronised as soon as it moves) or parented to a platform from the discarded
    /// timeline. The saved parent chain is restored, and the local pose is applied after it.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        sealed class PlayerExtraState
        {
            public bool HasPlayer;
            public Transform Parent;
            public GameObject ParentObject;
            public int SiblingIndex;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public bool HasPlatform;
            public Transform Platform;
            public bool IsKinematic;
        }

        partial void CapturePlayerExtra(Savestate state)
        {
            Component player = _bridge.PlayerComponent;
            if (player == null)
                return;

            PlayerExtraState extra = new PlayerExtraState { HasPlayer = true };
            Transform transform = player.transform;
            extra.Parent = transform.parent;
            extra.ParentObject = transform.parent != null ? transform.parent.gameObject : null;
            extra.SiblingIndex = transform.GetSiblingIndex();
            extra.LocalPosition = transform.localPosition;
            extra.LocalRotation = transform.localRotation;
            extra.LocalScale = transform.localScale;

            // NewMovement keeps the platform it is standing on in a field whose name differs
            // between builds; the parent is the authoritative relationship.
            extra.Platform = ReadTransform(player, "currentPlatform") ??
                             ReadTransform(player, "platform") ??
                             ReadTransform(player, "movingPlatform");
            extra.HasPlatform = extra.Platform != null;

            Rigidbody body = _bridge.PlayerRigidbody;
            extra.IsKinematic = body != null && body.isKinematic;

            SetExt(state, "PlayerExtra", extra);
        }

        partial void RestorePlayerExtra(Savestate state, ref int restored, ref int missing)
        {
            PlayerExtraState extra = GetExt<PlayerExtraState>(state, "PlayerExtra");
            if (extra == null || !extra.HasPlayer)
                return;
            Component player = _bridge.PlayerComponent;
            if (player == null)
            {
                missing++;
                return;
            }

            Transform transform = player.transform;
            try
            {
                Transform savedParent = extra.Parent;
                if (savedParent != null && !GameBridge.IsAlive(savedParent))
                {
                    savedParent = null;
                    // Prefer a live object with the same name under the same parent before
                    // giving up on the hierarchy: elevators rebuild their cabins.
                    savedParent = FindSurvivingParent(extra);
                }

                bool parentChanged = !ReferenceEquals(transform.parent, savedParent);
                if (parentChanged)
                {
                    // worldPositionStays: the saved world pose is applied below in local space.
                    transform.SetParent(savedParent, true);
                    if (savedParent != null && extra.SiblingIndex >= 0 &&
                        extra.SiblingIndex < savedParent.childCount)
                    {
                        try { transform.SetSiblingIndex(extra.SiblingIndex); }
                        catch { }
                    }
                }
                transform.localPosition = extra.LocalPosition;
                transform.localRotation = extra.LocalRotation;
                transform.localScale = extra.LocalScale;

                Rigidbody body = _bridge.PlayerRigidbody;
                if (body != null)
                {
                    // A parent move must not be fought by the rigidbody for one step.
                    if (parentChanged || extra.IsKinematic)
                        body.isKinematic = extra.IsKinematic;
                    if (_playerLoadHeld)
                    {
                        // The parent/world pose restore must not unfreeze the load hold.
                        if (!body.isKinematic)
                        {
                            body.velocity = Vector3.zero;
                            body.angularVelocity = Vector3.zero;
                        }
                        body.isKinematic = true;
                    }
                    else if (!body.isKinematic)
                        body.WakeUp();
                }
                // Parenting moves the player; nothing here may undo the slide the restore put
                // back. Re-apply the saved slide fields after the hierarchy work so an active
                // slide (sliding, crouching, slideDir) survives into the first resumed tick.
                if (state.Player != null)
                    ApplyNamedFields(player, state.Player.SlideFields);
                restored++;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Player hierarchy restore skipped: " + ex.Message);
            }
        }

        static Transform FindSurvivingParent(PlayerExtraState extra)
        {
            if (extra == null || extra.ParentObject == null)
                return null;
            // The saved parent is destroyed; find a live object of the same name in the scene
            // only when it was a scene object (never a prefab asset).
            try
            {
                if (!extra.ParentObject.scene.IsValid())
                    return null;
                string name = CleanObjectName(extra.ParentObject.name);
                GameObject[] roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == null)
                        continue;
                    Transform[] children = roots[i].GetComponentsInChildren<Transform>(true);
                    for (int c = 0; c < children.Length; c++)
                    {
                        Transform child = children[c];
                        if (child != null && child != extra.Parent && child.name == name)
                            return child;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        static Transform ReadTransform(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
                return null;
            try
            {
                System.Reflection.FieldInfo field = NamedField(target.GetType(), name);
                if (field == null)
                    return null;
                object value = field.GetValue(target);
                Transform transform = value as Transform;
                if (transform != null)
                    return transform;
                Component component = value as Component;
                return component != null ? component.transform : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
