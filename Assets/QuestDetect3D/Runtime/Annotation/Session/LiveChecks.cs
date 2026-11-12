// Live check while annotating: a grid of depth rays from the head into the box (expanded by 10 cm). Depth points
// that belong to the space around the box but fall OUTSIDE it are drawn red: a part of the object poking out
// (or clutter touching it). Points inside are drawn faint green. Points live 2.5 s and are re-classified every
// frame against the current box, so editing a face immediately turns red points green.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class LiveChecks
    {
        public const float Margin = 0.10f;
        public const float OutsideTolerance = 0.01f;
        private const int RaysPerFrame = 40;
        private const int MaxPoints = 900;
        private const float Lifetime = 2.5f;

        private readonly List<Vector3> m_points = new List<Vector3>(MaxPoints);
        private readonly List<float> m_times = new List<float>(MaxPoints);
        private readonly System.Random m_rng = new System.Random(11);

        public int OutsideCount { get; private set; }
        public int InsideCount { get; private set; }
        public float MaxOutsideCm { get; private set; }

        public void Clear()
        {
            m_points.Clear();
            m_times.Clear();
            OutsideCount = InsideCount = 0;
            MaxOutsideCm = 0f;
        }

        /// <summary>Cast this frame's rays, then classify and draw all live points against <paramref name="box"/> (world).</summary>
        public void Update(AnnotationMode mode, UprightBox box, float floorWorldY)
        {
            var now = Time.realtimeSinceStartup;
            var depth = mode.Context.Depth;
            var head = mode.Context.Head;
            if (depth != null && depth.IsAvailable && head != null)
            {
                var origin = head.position;
                for (var i = 0; i < RaysPerFrame; i++)
                {
                    var lx = ((float)m_rng.NextDouble() - 0.5f) * (box.Size.x + 2f * Margin);
                    var lz = ((float)m_rng.NextDouble() - 0.5f) * (box.Size.z + 2f * Margin);
                    var ly = (float)m_rng.NextDouble() * (box.Size.y + Margin);
                    var target = box.BottomCenter + box.Right * lx + Vector3.up * ly + box.Forward * lz;
                    if (!depth.Raycast(new Ray(origin, target - origin), 6f, out var p, out _))
                    {
                        continue;
                    }

                    if (p.y < floorWorldY + 0.02f || !BoxEdit.Contains(box, p, Margin))
                    {
                        continue; // floor or background
                    }

                    if (m_points.Count >= MaxPoints)
                    {
                        m_points.RemoveAt(0);
                        m_times.RemoveAt(0);
                    }

                    m_points.Add(p);
                    m_times.Add(now);
                }
            }

            var keep = 0;
            OutsideCount = InsideCount = 0;
            MaxOutsideCm = 0f;
            var red = new Color(1f, 0.15f, 0.15f, 1f);
            var green = new Color(0.3f, 1f, 0.45f, 0.45f);
            for (var i = 0; i < m_points.Count; i++)
            {
                if (now - m_times[i] > Lifetime)
                {
                    continue;
                }

                m_points[keep] = m_points[i];
                m_times[keep] = m_times[i];
                keep++;
                var p = m_points[i];
                var d = BoxEdit.OutsideDistance(box, p);
                if (d > OutsideTolerance)
                {
                    OutsideCount++;
                    MaxOutsideCm = Mathf.Max(MaxOutsideCm, d * 100f);
                    mode.Draw.Point(p, red, 0.012f);
                }
                else
                {
                    InsideCount++;
                    if ((i & 3) == 0)
                    {
                        mode.Draw.Point(p, green, 0.006f);
                    }
                }
            }

            m_points.RemoveRange(keep, m_points.Count - keep);
            m_times.RemoveRange(keep, m_times.Count - keep);
        }
    }
}
