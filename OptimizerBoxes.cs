using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>What an optimizer box means for an attempt's trajectory.</summary>
    public enum OptimBoxKind
    {
        /// <summary>Green. Reaching it is the objective ("Reach Goal Box Fastest").</summary>
        Goal,
        /// <summary>Red. Any attempt that touches it is invalid.</summary>
        KeepOut,
        /// <summary>Yellow. Every enabled waypoint must be touched at least once for an attempt to be valid.</summary>
        Waypoint
    }

    /// <summary>
    /// Axis-aligned box in world space. Min and Max are the two opposite corners, so the
    /// box stays valid while the user drags either side past the other.
    /// </summary>
    public sealed class OptimBox
    {
        public OptimBoxKind Kind;
        public string Name = string.Empty;
        public bool Enabled = true;
        public Vector3 Min;
        public Vector3 Max;

        public OptimBox(OptimBoxKind kind, Vector3 a, Vector3 b)
        {
            Kind = kind;
            SetCorners(a, b);
        }

        public Vector3 Center
        {
            get { return (Min + Max) * 0.5f; }
        }

        public Vector3 Size
        {
            get { return Max - Min; }
        }

        public void SetCorners(Vector3 a, Vector3 b)
        {
            Min = Vector3.Min(a, b);
            Max = Vector3.Max(a, b);
        }

        public bool Contains(Vector3 p)
        {
            return p.x >= Min.x && p.x <= Max.x &&
                   p.y >= Min.y && p.y <= Max.y &&
                   p.z >= Min.z && p.z <= Max.z;
        }

        /// <summary>Distance from p to the closest point of the box (0 when inside).</summary>
        public float DistanceTo(Vector3 p)
        {
            Vector3 closest = new Vector3(
                Mathf.Clamp(p.x, Min.x, Max.x),
                Mathf.Clamp(p.y, Min.y, Max.y),
                Mathf.Clamp(p.z, Min.z, Max.z));
            return Vector3.Distance(p, closest);
        }

        /// <summary>
        /// Slab test of the segment a→b against the box. Returns true when the segment touches
        /// the box, and sets <paramref name="fraction"/> to the first entry point along the
        /// segment in [0, 1] (0 when a is already inside). This is how the sub-tick entry
        /// time of a goal box is found between two simulated ticks.
        /// </summary>
        public bool SegmentIntersects(Vector3 a, Vector3 b, out float fraction)
        {
            float tEnter = 0f;
            float tExit = 1f;
            Vector3 d = b - a;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = Component(a, axis);
                float delta = Component(d, axis);
                float lo = Component(Min, axis);
                float hi = Component(Max, axis);
                if (Mathf.Abs(delta) < 1e-9f)
                {
                    if (origin < lo || origin > hi)
                    {
                        fraction = 0f;
                        return false;
                    }
                    continue;
                }
                float t1 = (lo - origin) / delta;
                float t2 = (hi - origin) / delta;
                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                }
                tEnter = Mathf.Max(tEnter, t1);
                tExit = Mathf.Min(tExit, t2);
                if (tEnter > tExit)
                {
                    fraction = 0f;
                    return false;
                }
            }
            fraction = tEnter;
            return true;
        }

        static float Component(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        }

        public override string ToString()
        {
            return Kind + " " + (string.IsNullOrEmpty(Name) ? "box" : Name) +
                   " [" + Min.x.ToString("0.##") + "," + Min.y.ToString("0.##") + "," + Min.z.ToString("0.##") +
                   " .. " + Max.x.ToString("0.##") + "," + Max.y.ToString("0.##") + "," + Max.z.ToString("0.##") + "]";
        }
    }

    /// <summary>The set of user boxes the optimizer checks against. Order is kept for the GUI list.</summary>
    public sealed class OptimBoxSet
    {
        public readonly List<OptimBox> Boxes = new List<OptimBox>();

        public OptimBox Add(OptimBoxKind kind, Vector3 a, Vector3 b)
        {
            OptimBox box = new OptimBox(kind, a, b);
            box.Name = kind + " " + (CountOf(kind) + 1);
            Boxes.Add(box);
            return box;
        }

        public void Remove(OptimBox box)
        {
            Boxes.Remove(box);
        }

        public int CountOf(OptimBoxKind kind)
        {
            int n = 0;
            for (int i = 0; i < Boxes.Count; i++)
                if (Boxes[i].Kind == kind)
                    n++;
            return n;
        }

        public IEnumerable<OptimBox> Enabled(OptimBoxKind kind)
        {
            for (int i = 0; i < Boxes.Count; i++)
                if (Boxes[i].Kind == kind && Boxes[i].Enabled)
                    yield return Boxes[i];
        }

        /// <summary>Sphere-free box test: does the segment prev→cur, or the point cur, touch box?</summary>
        public static bool Touches(OptimBox box, Vector3 prev, Vector3 cur, bool first, out float fraction)
        {
            if (first && box.Contains(prev))
            {
                fraction = 0f;
                return true;
            }
            if (box.Contains(cur))
            {
                fraction = 1f;
                return true;
            }
            return box.SegmentIntersects(prev, cur, out fraction);
        }
    }
}
