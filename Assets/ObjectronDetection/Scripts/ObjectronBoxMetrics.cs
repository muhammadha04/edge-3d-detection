// Edge-length metrics for Objectron 9-point corner arrays (0=center, 1-8=corners).

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronBoxMetrics
    {
        public static bool TryGetAxisEdgeLengthsMeters(Vector3[] corners, out Vector3 edgeMeters)
        {
            edgeMeters = default;
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            var right = corners[2] - corners[1];
            var up = corners[3] - corners[1];
            var forward = corners[5] - corners[1];
            edgeMeters = new Vector3(right.magnitude, up.magnitude, forward.magnitude);
            return edgeMeters.x > 0.001f && edgeMeters.y > 0.001f && edgeMeters.z > 0.001f;
        }

        public static float OversizePercent(float refined, float reference)
        {
            if (reference < 0.001f)
            {
                return 0f;
            }

            return (refined / reference - 1f) * 100f;
        }

        /// <summary>Edge lengths projected onto camera right / up / forward (apples-to-apples vs 2D frustum).</summary>
        public static bool TryGetCameraPlaneEdgeLengthsMeters(
            Vector3[] corners,
            Pose cameraPose,
            out Vector3 edgeMeters)
        {
            edgeMeters = default;
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            var right = cameraPose.rotation * Vector3.right;
            var up = cameraPose.rotation * Vector3.up;
            var forward = cameraPose.rotation * Vector3.forward;
            var eRight = corners[2] - corners[1];
            var eUp = corners[3] - corners[1];
            var eFwd = corners[5] - corners[1];
            edgeMeters = new Vector3(
                Mathf.Abs(Vector3.Dot(eRight, right)),
                Mathf.Abs(Vector3.Dot(eUp, up)),
                Mathf.Abs(Vector3.Dot(eFwd, forward)));
            return edgeMeters.x > 0.001f && edgeMeters.y > 0.001f;
        }
    }
}
