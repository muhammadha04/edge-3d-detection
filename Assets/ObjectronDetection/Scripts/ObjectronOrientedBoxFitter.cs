// Fit a unit cube transform to Objectron corners 1–8 (index 0 = center).

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronOrientedBoxFitter
    {
        public static bool TryFitTransform(Vector3[] corners, out Vector3 center, out Quaternion rotation, out Vector3 size)
        {
            center = default;
            rotation = Quaternion.identity;
            size = default;
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            var c1 = corners[1];
            var c2 = corners[2];
            var c3 = corners[3];
            var c5 = corners[5];
            var right = c2 - c1;
            var up = c3 - c1;
            var forward = c5 - c1;
            size = new Vector3(right.magnitude, up.magnitude, forward.magnitude);
            if (size.x < 0.01f || size.y < 0.01f || size.z < 0.01f)
            {
                return false;
            }

            center = corners[0];
            rotation = Quaternion.LookRotation(forward.normalized, up.normalized);
            return true;
        }
    }
}
