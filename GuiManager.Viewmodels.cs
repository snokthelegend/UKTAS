using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// "Hide viewmodels": hides renderers under the current weapon and arm roots. Only
    /// the renderers are hidden; weapon selection, firing, animation, colliders and input
    /// keep running exactly as before.
    ///
    /// Renderer.forceRenderingOff is used instead of Renderer.enabled because the game
    /// re-enables weapon renderers during draw/swing animations. forceRenderingOff is a
    /// render-only flag, so the game scripts cannot make a hidden viewmodel flash for a
    /// frame, and it does not trigger visibility callbacks that can disturb gameplay.
    /// </summary>
    public sealed partial class GuiManager
    {
        readonly Dictionary<Renderer, bool> _viewmodelWasForcedOff = new Dictionary<Renderer, bool>();
        readonly HashSet<int> _viewmodelSeen = new HashSet<int>();
        int _viewmodelScanFrame = -1000;

        void ApplyViewmodelVisibility()
        {
            if (!_plugin.HideViewmodels.Value)
            {
                RestoreViewmodels();
                return;
            }

            Transform weapon = CurrentWeaponRoot();
            Transform arm = CurrentArmRoot();
            if (Time.frameCount != _viewmodelScanFrame)
            {
                _viewmodelScanFrame = Time.frameCount;
                CollectViewmodels(weapon, arm);
            }

            foreach (KeyValuePair<Renderer, bool> pair in _viewmodelWasForcedOff)
            {
                Renderer renderer = pair.Key;
                if (renderer != null)
                    renderer.forceRenderingOff = true;
            }
        }

        void CollectViewmodels(Transform weapon, Transform arm)
        {
            _viewmodelSeen.Clear();
            AddViewmodelRenderers(weapon);
            AddViewmodelRenderers(arm);
        }

        void AddViewmodelRenderers(Transform root)
        {
            if (root == null)
                return;
            Renderer[] renderers;
            try
            {
                renderers = root.GetComponentsInChildren<Renderer>(true);
            }
            catch
            {
                return;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !_viewmodelSeen.Add(renderer.GetInstanceID()))
                    continue;
                // Remember the game's own flag so turning the toggle off cannot make a
                // renderer visible that the game intentionally had hidden.
                if (!_viewmodelWasForcedOff.ContainsKey(renderer))
                    _viewmodelWasForcedOff.Add(renderer, renderer.forceRenderingOff);
            }
        }

        Transform CurrentWeaponRoot()
        {
            object gun = _bridge.GunControl;
            if (gun == null)
                return null;
            return TransformOf(_bridge.GetMember(gun, new[] { "currentWeapon", "weapon" }));
        }

        Transform CurrentArmRoot()
        {
            object fists = _bridge.FistControl;
            if (fists == null)
                return null;
            return TransformOf(_bridge.GetMember(fists, new[] { "currentArmObject", "currentArm" }));
        }

        static Transform TransformOf(object value)
        {
            Component component = value as Component;
            if (component != null)
                return component.transform;
            GameObject gameObject = value as GameObject;
            return gameObject != null ? gameObject.transform : null;
        }

        void RestoreViewmodels()
        {
            foreach (KeyValuePair<Renderer, bool> pair in _viewmodelWasForcedOff)
            {
                Renderer renderer = pair.Key;
                if (renderer != null)
                    renderer.forceRenderingOff = pair.Value;
            }
            _viewmodelWasForcedOff.Clear();
            _viewmodelSeen.Clear();
            _viewmodelScanFrame = -1000;
        }

        /// <summary>Scene change: the old renderers are gone; start a fresh scan.</summary>
        void ResetViewmodels()
        {
            RestoreViewmodels();
        }
    }
}
