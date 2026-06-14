// World-up chair placement: ignore detection box tilt (pitch/roll), keep yaw only.

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronScanMeshUpright
    {
        /// <summary>Box yaw on the floor plane; mesh Y aligns with world up (no box tilt).</summary>
        public static Quaternion GetUprightFacingRotation(Quaternion detectionBoxRotation)
        {
            var forward = detectionBoxRotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = detectionBoxRotation * Vector3.right;
                forward.y = 0f;
            }

            if (forward.sqrMagnitude < 0.0001f)
            {
                return Quaternion.identity;
            }

            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        public static bool TryGetUprightSpawnPlacement(Vector3[] detectionCorners, out ObjectronScanMeshPlacement placement)
        {
            if (!ObjectronScanCalibrationRecord.TryGetSpawnPlacement(detectionCorners, out placement))
            {
                return false;
            }

            placement.Rotation = GetUprightFacingRotation(placement.Rotation);
            return true;
        }

        /// <summary>Rotate mesh so its longest local axis aligns closest to world up (starting hint).</summary>
        public static void ApplyUprightHintRotation(Transform meshRoot, Bounds meshBoundsLocal)
        {
            if (meshRoot == null)
            {
                return;
            }

            var size = meshBoundsLocal.size;
            var localUpAxis = Vector3.up;
            if (size.x >= size.y && size.x >= size.z)
            {
                localUpAxis = Vector3.right;
            }
            else if (size.z >= size.y && size.z >= size.x)
            {
                localUpAxis = Vector3.forward;
            }

            var worldAxis = meshRoot.rotation * localUpAxis;
            var align = Quaternion.FromToRotation(worldAxis, Vector3.up);
            meshRoot.rotation = align * meshRoot.rotation;
        }
    }
}
