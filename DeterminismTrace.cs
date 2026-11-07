using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Per-tick dump of the player and every moving rigidbody, started when a run reaches
    /// tick 0. Two runs of the same script should write identical rows; the first row that
    /// differs is the first tick where the game's physics diverged. Each row is the state
    /// at the start of the tick, so it shows the result of the previous tick's physics.
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

        public void Row(int tick, GameBridge bridge)
        {
            if (tick == 0)
                Begin();
            if (_writer == null || _rows >= MaxRows)
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

            StringBuilder sb = new StringBuilder(256);
            sb.Append(tick.ToString(Inv)).Append(';');
            sb.Append(Time.time.ToString("R", Inv)).Append(';');
            sb.Append(Time.fixedTime.ToString("R", Inv)).Append(';');
            sb.Append(Vec(position)).Append(';');
            sb.Append(Vec(velocity)).Append(';');
            sb.Append(count.ToString(Inv)).Append(';');
            sb.Append(sum.ToString("X16", Inv)).Append(';');
            sb.Append(enemyCount.ToString(Inv)).Append(';');
            sb.Append(enemySum.ToString("X16", Inv)).Append(';');
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
                _writer.WriteLine("tick;time;fixedTime;playerPos;playerVel;rigidbodies;checksum;enemies;enemyChecksum;movingBodies");
                _rows = 0;
            }
            catch
            {
                _writer = null;
            }
        }

        public void Close()
        {
            if (_writer == null)
                return;
            try { _writer.Dispose(); }
            catch { }
            _writer = null;
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
