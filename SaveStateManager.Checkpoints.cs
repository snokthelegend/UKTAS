using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// One cached norefresh checkpoint: the complete game state at the moment a run reached
    /// the marker, plus the script's own view and input state, keyed by the checksum of the
    /// script above the marker.
    /// </summary>
    public sealed class NorefreshEntry
    {
        public string Hash = string.Empty;
        public string Label = string.Empty;
        /// <summary>Absolute tick the entry resumes at (the framebulk after the marker).</summary>
        public int Tick;
        /// <summary>Handle of the active scene the state was taken in. A reloaded level has a new handle.</summary>
        public int SceneHandle;
        public Savestate State;
        public float Pitch;
        public float Yaw;
        public TasButtons Buttons;
        public double Time;
    }

    /// <summary>
    /// The norefresh cache, in memory. An entry is valid only for the level instance it was
    /// taken in: a savestate refers to that instance's objects. Additive scene loads do not
    /// change the active scene, so they do not invalidate anything.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        readonly Dictionary<string, NorefreshEntry> _norefresh = new Dictionary<string, NorefreshEntry>(StringComparer.Ordinal);

        public int NorefreshCount { get { return _norefresh.Count; } }

        static int ActiveSceneHandle()
        {
            return SceneManager.GetActiveScene().handle;
        }

        /// <summary>The entry for this checksum, but only when it was taken in the level that is loaded now.</summary>
        public NorefreshEntry GetNorefresh(string hash)
        {
            NorefreshEntry entry;
            if (string.IsNullOrEmpty(hash) || !_norefresh.TryGetValue(hash, out entry))
                return null;
            return entry.SceneHandle == ActiveSceneHandle() ? entry : null;
        }

        public bool HasNorefresh(string hash)
        {
            return GetNorefresh(hash) != null;
        }

        /// <summary>Capture the current state as the checkpoint for <paramref name="hash"/>, replacing a stale one.</summary>
        public bool CaptureNorefresh(string hash, string label, int tick, float pitch, float yaw, TasButtons buttons, double time)
        {
            if (string.IsNullOrEmpty(hash) || HasNorefresh(hash))
                return false;
            Savestate state = CaptureTransient("norefresh " + label);
            if (state == null)
                return false;
            state.Tick = tick;
            _norefresh[hash] = new NorefreshEntry
            {
                Hash = hash,
                Label = label ?? string.Empty,
                Tick = tick,
                SceneHandle = ActiveSceneHandle(),
                State = state,
                Pitch = pitch,
                Yaw = yaw,
                Buttons = buttons,
                Time = time
            };
            return true;
        }

        /// <summary>Restore a cached checkpoint. The caller has already checked it belongs to the loaded level.</summary>
        public SaveResult RestoreNorefresh(NorefreshEntry entry)
        {
            if (entry == null || entry.State == null)
                return SaveResult.Fail("No norefresh state is cached.");
            return Restore(entry.State);
        }

        /// <summary>
        /// Drop entries whose checksum is no longer in the script (the author changed something
        /// above them) and entries from a level instance that is no longer loaded.
        /// </summary>
        public void PruneNorefresh(IEnumerable<string> validHashes)
        {
            HashSet<string> valid = new HashSet<string>(validHashes, StringComparer.Ordinal);
            int handle = ActiveSceneHandle();
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, NorefreshEntry> pair in _norefresh)
            {
                if (!valid.Contains(pair.Key) || pair.Value.SceneHandle != handle)
                    stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++)
                _norefresh.Remove(stale[i]);
        }

        public void ClearNorefresh()
        {
            _norefresh.Clear();
        }
    }
}
