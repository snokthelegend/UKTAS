using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Per-tick dump of the player and every moving rigidbody, plus two explicit marks that
    /// answer where a norefresh resume stops matching continuous playback:
    ///
    ///   checkpoint : written by the run that caches the checkpoint, at the tick it resumes at
    ///                (start of the tick, before its commands run).
    ///   restored   : written right after RestoreNorefresh put the checkpoint back.
    ///
    /// Both rows hold position, velocity, camera angles, the fixed-step phase and a hash of
    /// UnityEngine.Random.state, so the two files can be diffed on the first resumed tick
    /// instead of waiting for the divergence to reach the player. <see cref="Restored"/>
    /// additionally compares the pair in memory and reports it.
    /// </summary>
    public sealed class DeterminismTrace
    {
        const int MaxRows = 6000;
        const int FlushEveryRows = 250;
        const int MaxBodies = 12;
        const int MaxCandidates = 300;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        StreamWriter _writer;
        int _rows;

        public string FilePath { get; private set; }

        /// <summary>The tick of the checkpoint mark currently held for comparison.</summary>
        struct StateSample
        {
            public int Tick;
            public Vector3 Position;
            public Vector3 Velocity;
            public float Pitch;
            public float Yaw;
            public double Phase;
            public double LivePhase;
            public bool Grounded;
            public ulong Rng;
            public bool Valid;
        }

        StateSample _checkpoint;

        /// <summary>Result of the last <see cref="Restored"/> comparison.</summary>
        public bool LastRestoreMatched { get; private set; }

        /// <summary>One ordinary per-tick row (start of the tick, before its input).</summary>
        public void Row(int tick, GameBridge bridge, double phase, int steps, float pitch, float yaw, ulong rng, bool grounded)
        {
            if (tick == 0)
                Begin();
            EnsureStarted();
            Write("tick", tick, bridge, phase, steps, pitch, yaw, rng, grounded);
        }

        /// <summary>The continuous-playback half of the resume comparison.</summary>
        public void Checkpoint(int tick, GameBridge bridge, double phase, int steps, float pitch, float yaw, ulong rng, bool grounded)
        {
            if (tick == 0)
                Begin();
            EnsureStarted();
            _checkpoint = Sample(tick, bridge, phase, pitch, yaw, rng, grounded);
            Write("checkpoint", tick, bridge, phase, steps, pitch, yaw, rng, grounded);
        }

        /// <summary>
        /// The resume half. Returns null when nothing could be compared, otherwise a one-line
        /// verdict with the deltas from the checkpoint mark at the same tick.
        /// </summary>
        public string Restored(int tick, GameBridge bridge, double phase, int steps, float pitch, float yaw, ulong rng, bool grounded)
        {
            if (tick == 0)
                Begin();
            EnsureStarted();
            StateSample restored = Sample(tick, bridge, phase, pitch, yaw, rng, grounded);
            Write("restored", tick, bridge, phase, steps, pitch, yaw, rng, grounded);

            if (!_checkpoint.Valid || _checkpoint.Tick != tick)
            {
                LastRestoreMatched = false;
                return "resumed at tick " + tick + ", but no checkpoint row for that tick was recorded " +
                       "during the first run (the trace was off then, or the run restarted)";
            }

            double position = MaxDelta(_checkpoint.Position, restored.Position);
            double velocity = MaxDelta(_checkpoint.Velocity, restored.Velocity);
            double angles = Math.Max(Math.Abs(_checkpoint.Pitch - restored.Pitch),
                                     Math.Abs(_checkpoint.Yaw - restored.Yaw));
            double modelDelta = Math.Abs(_checkpoint.Phase - restored.Phase);
            // The live remainder is what Unity's fixed loop will actually spend next; the model
            // value is only the savestate's copy of it. A mismatch here means the alignment did
            // not land and the resumed tick steps physics on a different boundary.
            double liveDelta = Math.Abs(_checkpoint.LivePhase - restored.LivePhase);
            bool rngSame = _checkpoint.Rng == restored.Rng && restored.Rng != 0UL;
            // A ground-contact flip across the resume is one of the ways a player gains height.
            bool groundSame = _checkpoint.Grounded == restored.Grounded;
            LastRestoreMatched = position < 1e-5 && velocity < 1e-4 && angles < 1e-4 &&
                                 modelDelta < 1e-7 && liveDelta < 1e-5 && rngSame && groundSame;
            return "tick " + tick + " restored: pos d" + F(position) + " vel d" + F(velocity) +
                   " camera d" + F(angles) + " model phase d" + F(modelDelta) +
                   "s live phase d" + F(liveDelta) + "s " +
                   (rngSame ? "rng same" : "rng DIFFERENT") +
                   (groundSame ? " ground same" : " GROUND DIFFERENT");
        }

        static StateSample Sample(int tick, GameBridge bridge, double phase, float pitch, float yaw, ulong rng, bool grounded)
        {
            StateSample sample = new StateSample
            {
                Tick = tick,
                Pitch = pitch,
                Yaw = yaw,
                Phase = phase,
                LivePhase = LivePhaseNow(),
                Grounded = grounded,
                Rng = rng
            };
            if (bridge != null)
            {
                bridge.TryGetPlayerPosition(out sample.Position);
                bridge.TryGetPlayerVelocity(out sample.Velocity);
            }
            sample.Valid = true;
            return sample;
        }

        void Write(string kind, int tick, GameBridge bridge, double phase, int steps, float pitch, float yaw, ulong rng, bool grounded)
        {
            if (_writer == null)
                return;
            if (_rows >= MaxRows)
                return;

            Vector3 position;
            Vector3 velocity;
            bridge.TryGetPlayerPosition(out position);
            bridge.TryGetPlayerVelocity(out velocity);

            int count = 0;
            ulong sum = 0;
            List<string> moving = new List<string>(32);
            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(typeof(Rigidbody));
            for (int i = 0; i < found.Length; i++)
            {
                Rigidbody body = found[i] as Rigidbody;
                if (body == null)
                    continue;
                Vector3 p = body.position;
                Vector3 v = body.velocity;
                count++;
                unchecked
                {
                    ulong h = 1469598103934665603UL;
                    h = (h ^ Bits(p.x)) * 1099511628211UL;
                    h = (h ^ Bits(p.y)) * 1099511628211UL;
                    h = (h ^ Bits(p.z)) * 1099511628211UL;
                    h = (h ^ Bits(v.x)) * 1099511628211UL;
                    h = (h ^ Bits(v.y)) * 1099511628211UL;
                    h = (h ^ Bits(v.z)) * 1099511628211UL;
                    // Addition is commutative, so the checksum ignores the order FindObjects returns.
                    sum += h;
                }
                if (v.sqrMagnitude > 0.0001f && moving.Count < MaxCandidates)
                    moving.Add(body.name.Replace(';', '_').Replace('|', '_') + "@" + Vec(p) + "v" + Vec(v));
            }
            moving.Sort(StringComparer.Ordinal);

            int enemyCount;
            ulong enemySum = EnemyChecksum(bridge, out enemyCount);

            StringBuilder sb = new StringBuilder(320);
            sb.Append(tick.ToString(Inv)).Append(';');
            sb.Append(kind).Append(';');
            sb.Append(Time.time.ToString("R", Inv)).Append(';');
            sb.Append(Time.fixedTime.ToString("R", Inv)).Append(';');
            sb.Append(phase.ToString("R", Inv)).Append(';');
            sb.Append(LivePhaseNow().ToString("R", Inv)).Append(';');
            sb.Append(steps.ToString(Inv)).Append(';');
            sb.Append(grounded ? "1" : "0").Append(';');
            sb.Append(pitch.ToString("R", Inv)).Append(';');
            sb.Append(yaw.ToString("R", Inv)).Append(';');
            sb.Append(Vec(position)).Append(';');
            sb.Append(Vec(velocity)).Append(';');
            sb.Append(count.ToString(Inv)).Append(';');
            sb.Append(sum.ToString("X16", Inv)).Append(';');
            sb.Append(enemyCount.ToString(Inv)).Append(';');
            sb.Append(enemySum.ToString("X16", Inv)).Append(';');
            sb.Append(rng.ToString("X16", Inv)).Append(';');
            for (int i = 0; i < moving.Count && i < MaxBodies; i++)
            {
                if (i > 0)
                    sb.Append('|');
                sb.Append(moving[i]);
            }
            _writer.WriteLine(sb.ToString());
            _rows++;
            if (_rows % FlushEveryRows == 0 || _rows >= MaxRows)
                _writer.Flush();
        }

        static Type _enemyType;

        /// <summary>
        /// Position, facing, active flag and primary animator clip of every enemy, accumulated
        /// order-independently. Two runs that restore a savestate differently diverge here
        /// before the divergence reaches the player's rigidbody, which makes the first bad
        /// reload visible on the row that caused it.
        /// </summary>
        static ulong EnemyChecksum(GameBridge bridge, out int count)
        {
            count = 0;
            if (bridge == null)
                return 0UL;
            if (_enemyType == null)
                _enemyType = bridge.FindType("EnemyIdentifier");
            if (_enemyType == null)
                return 0UL;

            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(_enemyType, true); }
            catch { return 0UL; }
            count = found.Length;

            ulong sum = 0UL;
            int limit = Math.Min(found.Length, 64);
            for (int i = 0; i < limit; i++)
            {
                Component component = found[i] as Component;
                if (component == null)
                    continue;
                Transform transform = component.transform;
                ulong h = 1469598103934665603UL;
                unchecked
                {
                    h = (h ^ Bits(transform.position.x)) * 1099511628211UL;
                    h = (h ^ Bits(transform.position.y)) * 1099511628211UL;
                    h = (h ^ Bits(transform.position.z)) * 1099511628211UL;
                    h = (h ^ Bits(transform.rotation.x)) * 1099511628211UL;
                    h = (h ^ Bits(transform.rotation.y)) * 1099511628211UL;
                    h = (h ^ Bits(transform.rotation.z)) * 1099511628211UL;
                    h = (h ^ Bits(transform.rotation.w)) * 1099511628211UL;
                    h = (h ^ (component.gameObject.activeSelf ? 1UL : 0UL)) * 1099511628211UL;
                }
                Animator animator = component.GetComponentInChildren<Animator>(true);
                if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 0)
                {
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                    unchecked
                    {
                        h = (h ^ (ulong)(uint)info.fullPathHash) * 1099511628211UL;
                        h = (h ^ Bits(info.normalizedTime)) * 1099511628211UL;
                    }
                }
                // Addition ignores the order FindObjectsOfType returns them in.
                sum += h;
            }
            return sum;
        }

        /// <summary>
        /// A resumed run starts at a nonzero tick: open the file on its first row so the rest of
        /// it is traced too, instead of waiting for a tick 0 that never comes.
        /// </summary>
        void EnsureStarted()
        {
            if (_writer == null)
                Begin();
        }

        void Begin()
        {
            Close();
            try
            {
                string directory = Path.GetDirectoryName(typeof(DeterminismTrace).Assembly.Location);
                FilePath = Path.Combine(directory, "determinism_trace_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + ".csv");
                // Buffered: a flush per tick put a disk write on every simulated frame.
                // Rows are flushed every FlushEveryRows and when the trace closes.
                _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false), 1 << 16) { AutoFlush = false };
                _writer.WriteLine("tick;kind;time;fixedTime;phase;livePhase;steps;grounded;pitch;yaw;playerPos;playerVel;" +
                                  "rigidbodies;checksum;enemies;enemyChecksum;rng;movingBodies");
                _rows = 0;
            }
            catch
            {
                _writer = null;
            }
            _checkpoint = default(StateSample);
        }

        public void Close()
        {
            if (_writer == null)
                return;
            try { _writer.Dispose(); }
            catch { }
            _writer = null;
        }

        /// <summary>
        /// Unity's own fixed-step remainder (Time.time - Time.fixedTime). This is the phase the
        /// next frame's fixed loop will spend, and the thing a load cannot write directly.
        /// </summary>
        static double LivePhaseNow()
        {
            double native = Time.fixedDeltaTime;
            if (native <= 0.0)
                return 0.0;
            double remainder = Time.timeAsDouble - Time.fixedTimeAsDouble;
            if (double.IsNaN(remainder) || remainder < 0.0)
                return 0.0;
            return remainder >= native ? remainder % native : remainder;
        }

        static double MaxDelta(Vector3 a, Vector3 b)
        {
            return Math.Max(Math.Abs(a.x - b.x), Math.Max(Math.Abs(a.y - b.y), Math.Abs(a.z - b.z)));
        }

        static string F(double value)
        {
            return value.ToString("0.######", Inv);
        }

        static ulong Bits(float value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
        }

        static string Vec(Vector3 v)
        {
            return v.x.ToString("R", Inv) + "," + v.y.ToString("R", Inv) + "," + v.z.ToString("R", Inv);
        }
    }
}
