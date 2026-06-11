// World alignment: remove headset roll so 3D boxes stay upright on real objects.

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronWorldOrientation
    {
        private const float AlignedDotThreshold = 0.92f;

        private static readonly Vector3[] s_localCornerUnit =
        {
            new(-1f, -1f, -1f), new(1f, -1f, -1f), new(-1f, 1f, -1f), new(1f, 1f, -1f),
            new(-1f, -1f, 1f), new(1f, -1f, 1f), new(-1f, 1f, 1f), new(1f, 1f, 1f),
        };

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

        /// <summary>
        /// Rebuild box upright on a horizontal plane: world +Y is vertical, yaw from the model
        /// (longer horizontal edge), no camera billboard. For cups on a flat table.
        /// </summary>
        public static bool TryConstrainUprightOnTable(Vector3[] corners)
        {
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            if (!ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(corners, out var edgeLens))
            {
                return false;
            }

            var axisX = corners[2] - corners[1];
            var axisY = corners[3] - corners[1];
            var axisZ = corners[5] - corners[1];
            var axes = new[] { axisX, axisY, axisZ };
            var lens = new[] { edgeLens.x, edgeLens.y, edgeLens.z };

            var vertIdx = 0;
            var bestVertDot = 0f;
            for (var i = 0; i < 3; i++)
            {
                var len = axes[i].magnitude;
                if (len < 0.001f)
                {
                    return false;
                }

                var dot = Mathf.Abs(Vector3.Dot(axes[i] / len, Vector3.up));
                if (dot > bestVertDot)
                {
                    bestVertDot = dot;
                    vertIdx = i;
                }
            }

            var horizA = -1;
            var horizB = -1;
            for (var i = 0; i < 3; i++)
            {
                if (i == vertIdx)
                {
                    continue;
                }

                if (horizA < 0)
                {
                    horizA = i;
                }
                else
                {
                    horizB = i;
                }
            }

            var yawIdx = lens[horizA] >= lens[horizB] ? horizA : horizB;
            var sideIdx = horizA == yawIdx ? horizB : horizA;
            var flatYaw = Vector3.ProjectOnPlane(axes[yawIdx], Vector3.up);
            if (flatYaw.sqrMagnitude < 1e-6f)
            {
                flatYaw = Vector3.ProjectOnPlane(axes[sideIdx], Vector3.up);
            }

            if (flatYaw.sqrMagnitude < 1e-6f)
            {
                return false;
            }

            flatYaw.Normalize();
            var worldRot = Quaternion.LookRotation(flatYaw, Vector3.up);
            var halfLocal = new Vector3(lens[sideIdx], lens[vertIdx], lens[yawIdx]) * 0.5f;
            var center = corners[0];
            for (var i = 0; i < s_localCornerUnit.Length; i++)
            {
                corners[i + 1] = center + worldRot * Vector3.Scale(s_localCornerUnit[i], halfLocal);
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
