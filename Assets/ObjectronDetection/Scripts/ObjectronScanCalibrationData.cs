// Serializable scan-vs-detection calibration record for aligning lab-chair.obj to Objectron boxes.

using System;
using UnityEngine;

namespace QuestObjectron
{
    public struct ObjectronScanMeshPlacement
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LocalScale;
        public bool IsValid;
    }

    [Serializable]
    public class ObjectronScanCalibrationRecord
    {
        public string version = "1";
        public string savedAtUtc;
        public string scanAssetPath = "Assets/lab-chair.obj";

        public int detectionObjectId;
        public float[] detectionCornersWorld;
        public float[] detectionCenterWorld;
        public float[] detectionRotationQuat;
        public float[] detectionBoxSizeM;
        public float[] detectionModelTranslation;
        public float[] detectionModelScale;
        public float[] detectionModelRotationEuler;
        public float[] detectionCameraPosition;
        public float[] detectionCameraRotationQuat;

        public float[] scanPositionWorld;
        public float[] scanRotationQuat;
        public float[] scanLocalScale;
        public float[] scanLossyScale;
        public float[] scanMeshBoundsCenterLocal;
        public float[] scanMeshBoundsSizeLocal;

        public float[] scanToDetectionPosition;
        public float[] scanToDetectionRotationQuat;
        public float[] scanToDetectionScaleRatio;

        public static ObjectronScanCalibrationRecord Create(
            int objectId,
            Vector3[] detectionCorners,
            Vector3 detectionModelTranslation,
            Vector3 detectionModelScale,
            Vector3 detectionModelRotationEuler,
            Pose detectionCameraPose,
            Transform scanTransform,
            Bounds scanMeshBoundsLocal)
        {
            if (detectionCorners == null || detectionCorners.Length < 9 || scanTransform == null)
            {
                return null;
            }

            if (!ObjectronOrientedBoxFitter.TryFitTransform(
                    detectionCorners,
                    out var detectionCenter,
                    out var detectionRotation,
                    out var detectionSize))
            {
                detectionCenter = detectionCorners[0];
                detectionRotation = Quaternion.identity;
                detectionSize = Vector3.one;
            }

            var detectionPose = new Pose(detectionCenter, detectionRotation);
            var scanPose = new Pose(scanTransform.position, scanTransform.rotation);
            var relative = ComputeRelative(scanPose, detectionPose, scanTransform.lossyScale, detectionSize);

            return new ObjectronScanCalibrationRecord
            {
                savedAtUtc = DateTime.UtcNow.ToString("o"),
                detectionObjectId = objectId,
                detectionCornersWorld = Flatten(detectionCorners),
                detectionCenterWorld = Vec(detectionCenter),
                detectionRotationQuat = Quat(detectionRotation),
                detectionBoxSizeM = Vec(detectionSize),
                detectionModelTranslation = Vec(detectionModelTranslation),
                detectionModelScale = Vec(detectionModelScale),
                detectionModelRotationEuler = Vec(detectionModelRotationEuler),
                detectionCameraPosition = Vec(detectionCameraPose.position),
                detectionCameraRotationQuat = Quat(detectionCameraPose.rotation),
                scanPositionWorld = Vec(scanTransform.position),
                scanRotationQuat = Quat(scanTransform.rotation),
                scanLocalScale = Vec(scanTransform.localScale),
                scanLossyScale = Vec(scanTransform.lossyScale),
                scanMeshBoundsCenterLocal = Vec(scanMeshBoundsLocal.center),
                scanMeshBoundsSizeLocal = Vec(scanMeshBoundsLocal.size),
                scanToDetectionPosition = Vec(relative.position),
                scanToDetectionRotationQuat = Quat(relative.rotation),
                scanToDetectionScaleRatio = Vec(relative.scaleRatio),
            };
        }

        private static (Vector3 position, Quaternion rotation, Vector3 scaleRatio) ComputeRelative(
            Pose scanPose,
            Pose detectionPose,
            Vector3 scanLossyScale,
            Vector3 detectionSize)
        {
            var invDetection = Matrix4x4.TRS(detectionPose.position, detectionPose.rotation, Vector3.one).inverse;
            var relativePos = invDetection.MultiplyPoint3x4(scanPose.position);
            var relativeRot = Quaternion.Inverse(detectionPose.rotation) * scanPose.rotation;
            var scaleRatio = new Vector3(
                detectionSize.x > 0.0001f ? scanLossyScale.x / detectionSize.x : 1f,
                detectionSize.y > 0.0001f ? scanLossyScale.y / detectionSize.y : 1f,
                detectionSize.z > 0.0001f ? scanLossyScale.z / detectionSize.z : 1f);
            return (relativePos, relativeRot, scaleRatio);
        }

        public Pose ApplyToDetection(Vector3[] detectionCorners)
        {
            return TryApplyMeshPlacement(detectionCorners, out var placement)
                ? new Pose(placement.Position, placement.Rotation)
                : default;
        }

        public bool TryApplyMeshPlacement(Vector3[] detectionCorners, out ObjectronScanMeshPlacement placement)
        {
            placement = default;
            if (!HasRelativeTransform() || detectionCorners == null || detectionCorners.Length < 9)
            {
                return false;
            }

            if (!ObjectronOrientedBoxFitter.TryFitTransform(
                    detectionCorners,
                    out var center,
                    out var rotation,
                    out var detectionSize))
            {
                center = detectionCorners[0];
                rotation = Quaternion.identity;
                detectionSize = Vector3.one;
            }

            var detectionPose = new Pose(center, rotation);
            var relativePos = ToVec3(scanToDetectionPosition);
            var relativeRot = ToQuat(scanToDetectionRotationQuat);
            var scaleRatio = ToVec3(scanToDetectionScaleRatio);
            placement = new ObjectronScanMeshPlacement
            {
                Position = detectionPose.position + detectionPose.rotation * relativePos,
                Rotation = detectionPose.rotation * relativeRot,
                LocalScale = Vector3.Scale(detectionSize, scaleRatio),
                IsValid = true,
            };
            return true;
        }

        public bool HasRelativeTransform() =>
            scanToDetectionPosition != null
            && scanToDetectionPosition.Length >= 3
            && scanToDetectionRotationQuat != null
            && scanToDetectionRotationQuat.Length >= 4
            && scanToDetectionScaleRatio != null
            && scanToDetectionScaleRatio.Length >= 3;

        public string ToDebugString()
        {
            return JsonUtility.ToJson(this, true);
        }

        private static float[] Flatten(Vector3[] vectors)
        {
            var flat = new float[vectors.Length * 3];
            for (var i = 0; i < vectors.Length; i++)
            {
                flat[i * 3] = vectors[i].x;
                flat[i * 3 + 1] = vectors[i].y;
                flat[i * 3 + 2] = vectors[i].z;
            }

            return flat;
        }

        private static float[] Vec(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] Quat(Quaternion q) => new[] { q.x, q.y, q.z, q.w };

        private static Vector3 ToVec3(float[] values) =>
            values != null && values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : Vector3.zero;

        private static Quaternion ToQuat(float[] values) =>
            values != null && values.Length >= 4
                ? new Quaternion(values[0], values[1], values[2], values[3])
                : Quaternion.identity;
    }
}
