// World alignment: remove headset roll so 3D boxes stay upright on real objects.

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronWorldOrientation
    {
        private const float AlignedDotThreshold = 0.92f;

        /// <summary>
        /// Camera pose with roll removed — pitch/yaw kept, up aligned to world +Y.
        /// Use when transforming model camera-space output to world space.
        /// </summary>
        public static Pose GetRollCompensatedPose(Pose cameraPose)
        {
            var leveled = RemoveCameraRoll(cameraPose.rotation);
            return new Pose(cameraPose.position, leveled);
        }

        /// <summary>Rotation with headset roll removed (pitch/yaw kept, up aligned to world +Y).</summary>
        public static Quaternion RemoveCameraRoll(Quaternion cameraRotation)
        {
            var forward = cameraRotation * Vector3.forward;
            var flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f)
            {
                return cameraRotation;
            }

            flatForward.Normalize();
            return Quaternion.LookRotation(flatForward, Vector3.up);
        }

        /// <summary>Map a camera-local point (meters) to world space, optionally without headset roll.</summary>
        public static Vector3 CameraLocalToWorld(
            Vector3 cameraLocalMeters,
            Pose cameraPose,
            bool compensateHeadRoll)
        {
            var pose = compensateHeadRoll ? GetRollCompensatedPose(cameraPose) : cameraPose;
            return pose.position + pose.rotation * cameraLocalMeters;
        }

        /// <summary>
        /// Rotate box corners in-place so the table face normal points down (world gravity).
        /// </summary>
        public static bool TryAlignBoxToGravity(Vector3[] corners)
        {
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(corners, out var face))
            {
                return false;
            }

            var outward = ObjectronBoxGeometry.GetFaceOutwardNormal(corners, face);
            var targetDown = Vector3.down;
            if (outward.sqrMagnitude < 1e-6f)
            {
                return false;
            }

            outward.Normalize();
            if (Vector3.Dot(outward, targetDown) >= AlignedDotThreshold)
            {
                return false;
            }

            var delta = Quaternion.FromToRotation(outward, targetDown);
            var center = corners[0];
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = center + delta * (corners[i] - center);
            }

            return true;
        }

        public static float GetHeadRollDegrees(Pose cameraPose)
        {
            var camUp = cameraPose.rotation * Vector3.up;
            var projected = Vector3.ProjectOnPlane(camUp, Vector3.up);
            if (projected.sqrMagnitude < 1e-6f)
            {
                return 0f;
            }

            return Vector3.SignedAngle(Vector3.up, camUp, cameraPose.rotation * Vector3.forward);
        }
    }
}
