// M1 Touch / M2 Point: the ground-truth box from touched points (pure maths, unit-tested).
//   1. Two points on the object's front reference (e.g. the seat front edge) set the yaw: the front direction is
//      horizontal and perpendicular to that edge. Which of the two perpendiculars is "front" comes from a hint
//      point known to be in front of the object (the operator's head while touching the front edge); a touched
//      back-most point overrides the hint (it must lie behind the front edge).
//   2. Every touched point (front edge, extremes, extra points) must be inside the box. Each face goes to the
//      farthest point along its axis; the top to the highest point; the bottom to the floor.
// The extremes are labelled for guidance and the audit trail, but all points constrain all faces: a box that
// "contains the whole object" must contain every touched point of it.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public static class TouchBox
    {
        public const float MinFrontSpan = 0.05f;
        public const float MinSize = 0.01f;

        public static readonly string[] ExtremeLabels = { "front", "back", "left", "right", "top" };

        /// <summary>Yaw (radians, heading of the object front) from two points on its front reference.</summary>
        public static bool TryFrontYaw(Vector3 a, Vector3 b, Vector3 hintInFront, out float yaw, out string error)
        {
            yaw = 0f;
            var d = b - a;
            d.y = 0f;
            var span = d.magnitude;
            if (span < MinFrontSpan)
            {
                error = $"front points only {span * 100f:F1} cm apart horizontally (need ≥ {MinFrontSpan * 100f:F0} cm)";
                return false;
            }

            d /= span;
            var n = new Vector3(d.z, 0f, -d.x);
            var mid = (a + b) * 0.5f;
            var h = hintInFront - mid;
            h.y = 0f;
            if (Vector3.Dot(n, h) < 0f)
            {
                n = -n;
            }

            yaw = Mathf.Atan2(n.x, n.z);
            error = null;
            return true;
        }

        /// <summary>Flip the front by 180° when the touched back-most point lies in front of the front edge.</summary>
        public static float ResolveFrontWithBack(float yaw, Vector3 frontA, Vector3 frontB, Vector3 back)
        {
            var f = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            var mid = (frontA + frontB) * 0.5f;
            return Vector3.Dot(back - mid, f) > 0f ? RoomFrame.WrapPi(yaw + Mathf.PI) : yaw;
        }

        /// <summary>Tightest upright box with this yaw that contains every point and sits on <paramref name="floorY"/>.</summary>
        public static UprightBox FromPoints(float yaw, IReadOnlyList<Vector3> points, float floorY)
        {
            var f = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            var r = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            float minR = float.MaxValue, maxR = float.MinValue, minF = float.MaxValue, maxF = float.MinValue;
            var maxY = floorY;
            foreach (var p in points)
            {
                var pr = Vector3.Dot(p, r);
                var pf = Vector3.Dot(p, f);
                minR = Mathf.Min(minR, pr);
                maxR = Mathf.Max(maxR, pr);
                minF = Mathf.Min(minF, pf);
                maxF = Mathf.Max(maxF, pf);
                maxY = Mathf.Max(maxY, p.y);
            }

            if (points.Count == 0)
            {
                return new UprightBox(new Vector3(0f, floorY, 0f), yaw, new Vector3(MinSize, MinSize, MinSize));
            }

            var width = Mathf.Max(MinSize, maxR - minR);
            var depth = Mathf.Max(MinSize, maxF - minF);
            var height = Mathf.Max(MinSize, maxY - floorY);
            var cr = 0.5f * (minR + maxR);
            var cf = 0.5f * (minF + maxF);
            var bottom = r * cr + f * cf;
            bottom.y = floorY;
            return new UprightBox(bottom, yaw, new Vector3(width, height, depth));
        }

        /// <summary>Largest distance by which any point lies outside the box (0 = all inside). For self-checks.</summary>
        public static float MaxOutside(UprightBox box, IReadOnlyList<Vector3> points)
        {
            var worst = 0f;
            foreach (var p in points)
            {
                worst = Mathf.Max(worst, BoxEdit.OutsideDistance(box, p));
            }

            return worst;
        }
    }
}
