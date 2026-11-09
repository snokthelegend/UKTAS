using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// The input state a checkpoint resumes with. The savestate restores the injected button
    /// mask (SaveStateManager.Timing), but a 'click' queued by the last tick before the
    /// checkpoint and the scripted pointer live in the engine, not in the game, so they are
    /// kept here: without them the resumed tick either keeps a click held that continuous
    /// playback would release, or aims with a reset pointer.
    /// </summary>
    public struct NorefreshInput
    {
        /// <summary>Resolved buttons of the tick before the checkpoint (the "previous" mask).</summary>
        public TasButtons Buttons;
        /// <summary>Pointer buttons a 'click' queued for release at the resumed tick.</summary>
        public int ReleaseButtons;
        public bool PointerActive;
        public Vector2 PointerPosition;
        /// <summary>Pointer buttons held at capture, as TasEngine's ButtonLeft/Right/Middle bits.</summary>
        public int PointerButtons;
        public int PointerGlideTicks;
        public Vector2 PointerGlideTarget;
    }

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
        /// <summary>
        /// Seconds of Update-side clock the captured frame was past its boundary, already rewound
        /// out of the state. Kept so the level timer, which is copied rather than rebased, can be
        /// rewound by the same amount after the restore.
        /// </summary>
        public double ClockRewind;
        public NorefreshInput Input;
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
        public bool CaptureNorefresh(string hash, string label, int tick, float pitch, float yaw, double time,
                                     NorefreshInput input, double clockRewind)
        {
            if (string.IsNullOrEmpty(hash) || HasNorefresh(hash))
                return false;
            Savestate state = CaptureTransient("norefresh " + label);
            if (state == null)
                return false;
            // Key the state to the script's checkpoint tick, not to whichever frame happened
            // to capture it. The resume must start at exactly this tick and script time so
            // the first tick's physics step and input edge match continuous playback.
            state.Tick = tick;
            state.ScriptTime = time;
            state.HasTimeline = tick > 0;
            // A capture offset that looks the boundary up late takes the state at the end of that
            // tick's own frame: its Update accounting (level timer, weapon recharge, style
            // freshness) is already inside the snapshot while its physics step is still pending.
            // The resume runs that tick from the boundary, so the extra frame is rewound here by
            // shifting the capture clocks forward: every TimeSince stamp is then rebased as one
            // frame older than it was captured, which is where continuous playback has it.
            if (clockRewind > 0.0)
            {
                state.GameTime += (float)clockRewind;
                if (state.UnscaledTime != 0f)
                    state.UnscaledTime += (float)clockRewind;
                if (state.InputTime != 0.0)
                    state.InputTime += clockRewind;
            }
            // Airborne coins are the first thing a resume loses if the projectile pass skips
            // them; say how many the checkpoint carries so the load's audit can be compared.
            int coins = 0;
            for (int i = 0; i < state.Projectiles.Count; i++)
            {
                if (state.Projectiles[i] != null && state.Projectiles[i].TypeName == "Coin")
                    coins++;
            }
            _plugin.LogInfo("Norefresh checkpoint '" + (label ?? string.Empty) + "' at tick " + tick +
                            " carries " + coins + " coin(s).");
            _norefresh[hash] = new NorefreshEntry
            {
                Hash = hash,
                Label = label ?? string.Empty,
                Tick = tick,
                SceneHandle = ActiveSceneHandle(),
                State = state,
                Pitch = pitch,
                Yaw = yaw,
                Buttons = input.Buttons,
                Time = time,
                ClockRewind = clockRewind,
                Input = input
            };
            return true;
        }

        /// <summary>Restore a cached checkpoint. The caller has already checked it belongs to the loaded level.</summary>
        public SaveResult RestoreNorefresh(NorefreshEntry entry)
        {
            if (entry == null || entry.State == null)
                return SaveResult.Fail("No norefresh state is cached.");
            // A norefresh resume is a user load too, so it gets the opt-in map checkpoint reset
            // and, independently of that option, the arena trigger re-arm.
            _arenaResetForThisRestore = true;
            SaveResult result;
            try { result = Restore(entry.State, true); }
            finally { _arenaResetForThisRestore = false; }
            if (result.Success && entry.ClockRewind > 0.0)
                RewindLevelTimer(entry.ClockRewind);
            return result;
        }

        /// <summary>
        /// StatsManager.seconds is the level clock the game timer and the run's framecount read,
        /// and it is a plain float: a restore copies it verbatim instead of rebasing it like the
        /// TimeSince stamps. A checkpoint captured one frame past its boundary therefore has to
        /// have it rewound by that frame, or the whole resumed run counts one frame more than
        /// continuous playback from the checkpoint on.
        /// </summary>
        void RewindLevelTimer(double seconds)
        {
            try
            {
                object stats = _bridge.StatsManager;
                if (stats == null)
                    return;
                object value = _bridge.GetMember(stats, new[] { "seconds" });
                if (!(value is float))
                    return;
                float timer = (float)value - (float)seconds;
                if (timer < 0f)
                    timer = 0f;
                _bridge.SetMember(stats, new[] { "seconds" }, timer);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Norefresh level clock rewind skipped: " + ex.Message);
            }
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
