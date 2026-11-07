using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace UltrakillTAS
{
    /// <summary>
    /// Tick-driven hitstops.
    ///
    /// The game's TimeController (global::TimeController) starts a stop like this:
    ///
    ///   HitStop(length):  if (length > currentStop) { currentStop = length; Time.timeScale = 0;
    ///                     StartCoroutine(TimeIsStopped(length, false)); }
    ///   TrueStop(length): same, plus SetFloat("allPitch", 0) on every mixer when controlPitch.
    ///   TimeIsStopped:    yield return new WaitForSecondsRealtime(length); ContinueTime(length, trueStop);
    ///   ContinueTime:     if (length >= currentStop) { Time.timeScale = timeScale * timeScaleModifier;
    ///                     pitch back to 1 for a true stop; currentStop = 0; }
    ///
    /// The only wall-clock part is the WaitForSecondsRealtime. The tool never writes
    /// Time.timeScale: HitStop/TrueStop always run their own body (the game sets its scale
    /// to 0 and starts TimeIsStopped). While the TAS clock is active, each accepted stop
    /// also becomes a virtual timer that loses exactly one tick's frametime per simulated
    /// (frozen) tick and calls the game's own ContinueTime when it runs out, and the
    /// realtime coroutine's ContinueTime call is skipped. The end of a stop therefore
    /// lands on the same tick on every run, at any playback speed, in fast-forward and
    /// after a savestate load, and the scale is restored by the game itself. Without an
    /// active TAS the game's methods run untouched.
    /// </summary>
    public static class TimeControllerHitstopPatch
    {
        /// <summary>A stop that the game would be waiting out in TimeIsStopped.</summary>
        public sealed class VirtualStop
        {
            public global::TimeController Owner;
            public float Length;
            public bool TrueStop;
            public double Remaining;
            // Started during the current frame: its wait begins with the next tick.
            public bool Fresh;
        }

        /// <summary>Savestate copy of every piece of hitstop state.</summary>
        public sealed class Snapshot
        {
            public float CurrentStop;
            public bool HasPitch;
            public float Pitch;
            public bool HasFlash;
            public bool FlashActive;
            public double FlashRemaining;
            public bool Holding;
            public List<VirtualStop> Stops = new List<VirtualStop>();
        }

        // A float length summed down by double frametimes: 0.1f is 0.10000000149, which
        // must end after 6 ticks of 1/60 s, not 7.
        const double EndEpsilon = 1e-6;

        static readonly List<VirtualStop> Stops = new List<VirtualStop>();

        static Harmony _harmony;
        static Plugin _plugin;
        static TimeController _clock;
        static AccessTools.FieldRef<global::TimeController, float> _currentStop;
        static AccessTools.FieldRef<global::TimeController, GameObject> _parryFlash;
        static AccessTools.FieldRef<global::TimeController, AudioMixer[]> _audmix;
        static MethodInfo _continueTime;
        static bool _failureLogged;
        // True while a virtual timer is calling the game's ContinueTime.
        static bool _virtualContinue;

        // Game time (Time.time) at which the pending Invoke("HideFlash", 0.1f) fires.
        static global::TimeController _flashOwner;
        static double _flashHideAt = double.NaN;

        public static bool Installed { get; private set; }

        public static void Install(Plugin plugin, TimeController clock)
        {
            _plugin = plugin;
            _clock = clock;
            if (Installed)
                return;
            try
            {
                Type type = typeof(global::TimeController);
                _currentStop = AccessTools.FieldRefAccess<global::TimeController, float>("currentStop");
                _parryFlash = AccessTools.FieldRefAccess<global::TimeController, GameObject>("parryFlash");
                _audmix = AccessTools.FieldRefAccess<global::TimeController, AudioMixer[]>("audmix");
                _continueTime = AccessTools.Method(type, "ContinueTime", new[] { typeof(float), typeof(bool) });
                MethodInfo hitStop = AccessTools.Method(type, "HitStop", new[] { typeof(float) });
                MethodInfo trueStop = AccessTools.Method(type, "TrueStop", new[] { typeof(float) });
                MethodInfo parryFlash = AccessTools.Method(type, "ParryFlash", Type.EmptyTypes);
                if (_continueTime == null || hitStop == null || trueStop == null)
                {
                    plugin.LogWarning("Hitstop patch skipped: TimeController.HitStop/TrueStop/ContinueTime not found.");
                    return;
                }

                _harmony = new Harmony("com.tas.ultrakill.hitstop");
                BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                HarmonyMethod stopPrefix = new HarmonyMethod(typeof(TimeControllerHitstopPatch).GetMethod("StopPrefix", flags));
                _harmony.Patch(hitStop, prefix: stopPrefix,
                               postfix: new HarmonyMethod(typeof(TimeControllerHitstopPatch).GetMethod("HitStopPostfix", flags)));
                _harmony.Patch(trueStop, prefix: stopPrefix,
                               postfix: new HarmonyMethod(typeof(TimeControllerHitstopPatch).GetMethod("TrueStopPostfix", flags)));
                _harmony.Patch(_continueTime, prefix: new HarmonyMethod(typeof(TimeControllerHitstopPatch).GetMethod("ContinueTimePrefix", flags)));
                if (parryFlash != null)
                    _harmony.Patch(parryFlash, postfix: new HarmonyMethod(typeof(TimeControllerHitstopPatch).GetMethod("ParryFlashPostfix", flags)));
                Installed = true;
                plugin.LogInfo("Hitstop patch installed: game stops count down per TAS tick.");
            }
            catch (Exception ex)
            {
                plugin.LogWarning("Hitstop patch failed: " + ex.Message);
            }
        }

        static bool TasDriven { get { return _clock != null && _clock.Active; } }

        static void StopPrefix(global::TimeController __instance, out float __state)
        {
            __state = float.NaN;
            if (!TasDriven || __instance == null)
                return;
            try { __state = _currentStop(__instance); }
            catch (Exception ex) { LogFailure("Hitstop timer not registered: " + ex.Message); }
        }

        static void HitStopPostfix(global::TimeController __instance, float length, float __state)
        {
            BeginStop(__instance, length, false, __state);
        }

        static void TrueStopPostfix(global::TimeController __instance, float length, float __state)
        {
            BeginStop(__instance, length, true, __state);
        }

        /// <summary>
        /// While the TAS clock is active only a virtual timer may end a stop: the game's
        /// TimeIsStopped coroutine still waits on the wall clock, and its ContinueTime call
        /// is skipped. Without an active TAS the call runs, and the virtual timer for the
        /// same stop is dropped so it cannot end the stop a second time.
        /// </summary>
        static bool ContinueTimePrefix(global::TimeController __instance, float length)
        {
            if (_virtualContinue)
                return true;
            if (TasDriven)
                return false;
            for (int i = Stops.Count - 1; i >= 0; i--)
            {
                VirtualStop stop = Stops[i];
                if (ReferenceEquals(stop.Owner, __instance) && stop.Length == length)
                    Stops.RemoveAt(i);
            }
            return true;
        }

        static void ParryFlashPostfix(global::TimeController __instance)
        {
            if (__instance == null || !__instance.parryFlashEnabled)
                return;
            _flashOwner = __instance;
            _flashHideAt = Time.timeAsDouble + 0.1;
        }

        /// <summary>
        /// Runs after the game's own HitStop/TrueStop body, which sets currentStop, pitch
        /// and Time.timeScale itself. A stop the game accepted (currentStop rose to this
        /// length) gets a virtual timer.
        /// </summary>
        static void BeginStop(global::TimeController owner, float length, bool trueStop, float before)
        {
            if (!TasDriven || owner == null || float.IsNaN(before))
                return;
            try
            {
                float current = _currentStop(owner);
                if (!(length > before) || current != length)
                    return;
                Stops.Add(new VirtualStop { Owner = owner, Length = length, TrueStop = trueStop, Remaining = length, Fresh = true });
            }
            catch (Exception ex)
            {
                LogFailure("Hitstop timer not registered: " + ex.Message);
            }
        }

        /// <summary>
        /// True while a virtual stop is the one that ends the game's current stop
        /// (vanilla ContinueTime only acts when length >= currentStop).
        /// </summary>
        public static bool Holding
        {
            get
            {
                for (int i = 0; i < Stops.Count; i++)
                {
                    VirtualStop stop = Stops[i];
                    if (stop.Owner == null)
                        continue;
                    float current = _currentStop(stop.Owner);
                    if (current > 0f && stop.Length >= current)
                        return true;
                }
                return false;
            }
        }

        public static bool HasStops { get { return Stops.Count > 0; } }

        /// <summary>
        /// End of a rendered frame under the TAS clock. On a frozen hitstop tick every
        /// timer started before this frame loses the tick's frametime; on any frame, stops
        /// started during it stop being fresh.
        /// </summary>
        public static void EndFrame(bool hitstopTick, double seconds)
        {
            if (Stops.Count == 0)
                return;
            if (hitstopTick)
                AdvanceTick(seconds);
            for (int i = 0; i < Stops.Count; i++)
                Stops[i].Fresh = false;
        }

        /// <summary>One simulated tick of a stop: every pending timer loses the tick's frametime.</summary>
        public static void AdvanceTick(double seconds)
        {
            if (Stops.Count == 0 || double.IsNaN(seconds) || seconds <= 0.0)
                return;
            int i = 0;
            while (i < Stops.Count)
            {
                VirtualStop stop = Stops[i];
                // Unity stops a coroutine when its object is destroyed or deactivated.
                if (stop.Owner == null || !stop.Owner.gameObject.activeInHierarchy)
                {
                    Stops.RemoveAt(i);
                    continue;
                }
                if (stop.Fresh)
                {
                    i++;
                    continue;
                }
                stop.Remaining -= seconds;
                if (stop.Remaining <= EndEpsilon)
                {
                    Stops.RemoveAt(i);
                    Continue(stop);
                    continue;
                }
                i++;
            }
        }

        /// <summary>
        /// Without an active TAS, timers left over from one fall back to the game's own
        /// realtime wait so a run released mid-stop cannot stay frozen.
        /// </summary>
        public static void AdvanceRealtime(float unscaledDelta)
        {
            if (Stops.Count == 0)
                return;
            for (int i = 0; i < Stops.Count; i++)
                Stops[i].Fresh = false;
            AdvanceTick(unscaledDelta > 0f ? unscaledDelta : 0.0);
        }

        static void Continue(VirtualStop stop)
        {
            _virtualContinue = true;
            try { _continueTime.Invoke(stop.Owner, new object[] { stop.Length, stop.TrueStop }); }
            catch (Exception ex) { LogFailure("Hitstop end failed: " + ex.Message); }
            finally { _virtualContinue = false; }
        }

        static global::TimeController Game
        {
            get
            {
                try { return MonoSingleton<global::TimeController>.Instance; }
                catch { return null; }
            }
        }

        public static Snapshot Capture()
        {
            global::TimeController owner = Game;
            if (!Installed || owner == null)
                return null;
            Snapshot snap = new Snapshot();
            try
            {
                snap.CurrentStop = _currentStop(owner);
                snap.Holding = Holding;
                for (int i = 0; i < Stops.Count; i++)
                {
                    VirtualStop stop = Stops[i];
                    if (stop.Owner == null)
                        continue;
                    snap.Stops.Add(new VirtualStop { Length = stop.Length, TrueStop = stop.TrueStop, Remaining = stop.Remaining, Fresh = stop.Fresh });
                }

                AudioMixer[] mixers = _audmix(owner);
                float pitch;
                if (mixers != null && mixers.Length > 0 && mixers[0] != null && mixers[0].GetFloat("allPitch", out pitch))
                {
                    snap.HasPitch = true;
                    snap.Pitch = pitch;
                }

                GameObject flash = _parryFlash(owner);
                if (flash != null)
                {
                    snap.HasFlash = true;
                    snap.FlashActive = flash.activeSelf;
                    double remaining = (_flashOwner == owner && !double.IsNaN(_flashHideAt))
                        ? _flashHideAt - Time.timeAsDouble
                        : 0.0;
                    snap.FlashRemaining = remaining > 0.0 ? remaining : 0.0;
                }
            }
            catch (Exception ex)
            {
                LogFailure("Hitstop capture incomplete: " + ex.Message);
            }
            return snap;
        }

        /// <summary>
        /// Put the saved hitstop back. Timers of the discarded timeline are always dropped.
        /// Returns true when the state was taken inside a stop: currentStop and the timers
        /// are restored exactly, so the stop runs its remaining ticks and ends through
        /// ContinueTime as usual. Time.timeScale is never written, so the remaining ticks
        /// run at whatever scale the game currently has. The caller must then skip the
        /// generic hitstop reset.
        /// </summary>
        public static bool Restore(Snapshot snap)
        {
            // Stops of the discarded timeline. The tool never writes Time.timeScale, so a
            // live stop the restored state does not continue is ended through the game's
            // own ContinueTime, which puts the game's scale back (its realtime coroutine's
            // call is skipped while the TAS is active, so nothing else would).
            List<VirtualStop> discarded = new List<VirtualStop>(Stops);
            Stops.Clear();
            global::TimeController owner = Game;
            if (owner == null || !Installed)
            {
                EndDiscarded(discarded);
                return false;
            }
            if (snap == null)
            {
                // No hitstop record (older state): nothing can end a restored stop.
                EndDiscarded(discarded);
                try { _currentStop(owner) = 0f; } catch { }
                return false;
            }

            bool midStop = snap.Holding && snap.CurrentStop > 0f && snap.Stops.Count > 0;
            if (!midStop)
                EndDiscarded(discarded);
            try
            {
                if (midStop)
                {
                    _currentStop(owner) = snap.CurrentStop;
                    for (int i = 0; i < snap.Stops.Count; i++)
                    {
                        VirtualStop stop = snap.Stops[i];
                        Stops.Add(new VirtualStop { Owner = owner, Length = stop.Length, TrueStop = stop.TrueStop, Remaining = stop.Remaining, Fresh = stop.Fresh });
                    }
                    // Time.timeScale is not written: it is 0 only if the game's own stop
                    // on the discarded timeline still holds it there.
                }
                else
                {
                    // A stop with no timer behind it (one started on the game's realtime
                    // coroutine, which a savestate cannot hold) would block every shorter
                    // stop forever.
                    _currentStop(owner) = 0f;
                }

                if (snap.HasPitch && owner.controlPitch)
                    owner.SetAllPitch(snap.Pitch);

                if (snap.HasFlash)
                {
                    GameObject flash = _parryFlash(owner);
                    owner.CancelInvoke("HideFlash");
                    _flashOwner = null;
                    _flashHideAt = double.NaN;
                    if (flash != null)
                        flash.SetActive(snap.FlashActive);
                    // The flash only ever shows with a HideFlash pending, so an active
                    // flash always gets its hide back (at once when its time is unknown).
                    if (snap.FlashActive)
                    {
                        owner.Invoke("HideFlash", (float)snap.FlashRemaining);
                        _flashOwner = owner;
                        _flashHideAt = Time.timeAsDouble + snap.FlashRemaining;
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailure("Hitstop restore incomplete: " + ex.Message);
            }
            return midStop;
        }

        /// <summary>End stops of a discarded timeline through the game's own ContinueTime.</summary>
        static void EndDiscarded(List<VirtualStop> discarded)
        {
            for (int i = 0; i < discarded.Count; i++)
            {
                VirtualStop stop = discarded[i];
                if (stop.Owner != null)
                    Continue(stop);
            }
        }

        static void LogFailure(string message)
        {
            if (_failureLogged || ReferenceEquals(_plugin, null))
                return;
            _failureLogged = true;
            _plugin.LogWarning(message);
        }
    }
}
