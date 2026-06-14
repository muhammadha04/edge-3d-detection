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
        /// <summary>Mesh localScale after user alignment (~1.0–1.3). v2: use this directly instead of box-size ratio.</summary>
        public float[] calibratedMeshLocalScale;
        public float[] referenceModelScaleM;
        public float[] referenceBoxEdgeLengthsM;

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
            ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(detectionCorners, out var referenceEdges);

            return new ObjectronScanCalibrationRecord
            {
                version = "2",
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
                calibratedMeshLocalScale = Vec(scanTransform.lossyScale),
                referenceModelScaleM = Vec(detectionModelScale),
                referenceBoxEdgeLengthsM = Vec(referenceEdges),
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
            return TryApplyMeshPlacement(detectionCorners, null, out placement);
        }

        public bool TryApplyMeshPlacement(
            Vector3[] detectionCorners,
            Vector3? currentModelScale,
            out ObjectronScanMeshPlacement placement)
        {
            placement = default;
            if (!HasRelativeTransform() || detectionCorners == null || detectionCorners.Length < 9)
            {
                return false;
            }

            if (!TryGetDetectionPose(detectionCorners, out var center, out var rotation))
            {
                return false;
            }

            var detectionPose = new Pose(center, rotation);
            var relativePos = ToVec3(scanToDetectionPosition);
            var relativeRot = ToQuat(scanToDetectionRotationQuat);
            placement = new ObjectronScanMeshPlacement
            {
                Position = detectionPose.position + detectionPose.rotation * relativePos,
                Rotation = detectionPose.rotation * relativeRot,
                LocalScale = ResolveMeshLocalScale(detectionCorners, currentModelScale),
                IsValid = true,
            };
            return true;
        }

        private Vector3 ResolveMeshLocalScale(Vector3[] detectionCorners, Vector3? currentModelScale)
        {
            var baseScale = GetCalibratedMeshLocalScale();

            if (currentModelScale.HasValue && referenceModelScaleM != null && referenceModelScaleM.Length >= 3)
            {
                var reference = ToVec3(referenceModelScaleM);
                var current = currentModelScale.Value;
                var refAvg = (reference.x + reference.y + reference.z) / 3f;
                var curAvg = (current.x + current.y + current.z) / 3f;
                if (refAvg > 0.001f && curAvg > 0.001f)
                {
                    var distanceFactor = curAvg / refAvg;
                    baseScale *= distanceFactor;
                }
            }

            return baseScale;
        }

        public Vector3 GetCalibratedMeshLocalScale()
        {
            if (calibratedMeshLocalScale != null && calibratedMeshLocalScale.Length >= 3)
            {
                return ToVec3(calibratedMeshLocalScale);
            }

            if (scanLossyScale != null && scanLossyScale.Length >= 3)
            {
                return ToVec3(scanLossyScale);
            }

            if (scanLocalScale != null && scanLocalScale.Length >= 3)
            {
                return ToVec3(scanLocalScale);
            }

            return Vector3.one;
        }

        private static bool TryGetDetectionPose(Vector3[] corners, out Vector3 center, out Quaternion rotation)
        {
            center = corners[0];
            if (ObjectronOrientedBoxFitter.TryFitTransform(corners, out _, out rotation, out _))
            {
                return true;
            }

            rotation = Quaternion.identity;
            return true;
        }

        public bool HasRelativeTransform() =>
            scanToDetectionPosition != null
            && scanToDetectionPosition.Length >= 3
            && scanToDetectionRotationQuat != null
            && scanToDetectionRotationQuat.Length >= 4
            && (calibratedMeshLocalScale != null && calibratedMeshLocalScale.Length >= 3
                || scanLossyScale != null && scanLossyScale.Length >= 3
                || scanLocalScale != null && scanLocalScale.Length >= 3);

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
