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
        /// <summary>Mesh localScale after user alignment (~1.0–1.3). v2+: use this directly instead of box-size ratio.</summary>
        public float[] calibratedMeshLocalScale;
        public float[] referenceModelScaleM;
        public float[] referenceBoxEdgeLengthsM;
        /// <summary>v3+: rotation offset was captured from spawn pose (trustworthy). Older saves may be false.</summary>
        public bool spawnRelativeRotation;
        /// <summary>v4+: user upright correction on top of world-up + box yaw (not tilted box axes).</summary>
        public bool hasMeshUprightPreset;
        public float[] meshUprightCorrectionQuat;

        public struct SpawnReference
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LocalScale;
            public bool IsValid;
        }

        public static ObjectronScanCalibrationRecord CreateUprightPreset(
            int objectId,
            Vector3[] detectionCorners,
            Vector3 detectionModelTranslation,
            Vector3 detectionModelScale,
            Vector3 detectionModelRotationEuler,
            Pose detectionCameraPose,
            Transform scanTransform,
            Bounds scanMeshBoundsLocal,
            Quaternion uprightBaseRotation)
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

            var finalRotation = scanTransform.rotation;
            var finalScale = scanTransform.lossyScale;
            var uprightCorrection = Quaternion.Inverse(uprightBaseRotation) * finalRotation;
            ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(detectionCorners, out var referenceEdges);

            return new ObjectronScanCalibrationRecord
            {
                version = "4",
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
                scanRotationQuat = Quat(finalRotation),
                scanLocalScale = Vec(scanTransform.localScale),
                scanLossyScale = Vec(finalScale),
                scanMeshBoundsCenterLocal = Vec(scanMeshBoundsLocal.center),
                scanMeshBoundsSizeLocal = Vec(scanMeshBoundsLocal.size),
                scanToDetectionPosition = Vec(Vector3.zero),
                scanToDetectionRotationQuat = Quat(uprightCorrection),
                scanToDetectionScaleRatio = Vec(Vector3.one),
                calibratedMeshLocalScale = Vec(finalScale),
                spawnRelativeRotation = true,
                hasMeshUprightPreset = true,
                meshUprightCorrectionQuat = Quat(uprightCorrection),
                referenceModelScaleM = Vec(detectionModelScale),
                referenceBoxEdgeLengthsM = Vec(referenceEdges),
            };
        }

        public static ObjectronScanCalibrationRecord Create(
            int objectId,
            Vector3[] detectionCorners,
            Vector3 detectionModelTranslation,
            Vector3 detectionModelScale,
            Vector3 detectionModelRotationEuler,
            Pose detectionCameraPose,
            Transform scanTransform,
            Bounds scanMeshBoundsLocal,
            SpawnReference spawnReference)
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

            var finalRotation = scanTransform.rotation;
            var finalScale = scanTransform.lossyScale;
            Quaternion rotationOffset;
            if (spawnReference.IsValid)
            {
                // Delta from spawn pose: user only rotates/scales after spawn at the detection box.
                rotationOffset = Quaternion.Inverse(spawnReference.Rotation) * finalRotation;
            }
            else
            {
                rotationOffset = Quaternion.Inverse(detectionRotation) * finalRotation;
            }

            ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(detectionCorners, out var referenceEdges);
            var scaleRatio = new Vector3(
                detectionSize.x > 0.0001f ? finalScale.x / detectionSize.x : 1f,
                detectionSize.y > 0.0001f ? finalScale.y / detectionSize.y : 1f,
                detectionSize.z > 0.0001f ? finalScale.z / detectionSize.z : 1f);

            return new ObjectronScanCalibrationRecord
            {
                version = "3",
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
                scanRotationQuat = Quat(finalRotation),
                scanLocalScale = Vec(scanTransform.localScale),
                scanLossyScale = Vec(finalScale),
                scanMeshBoundsCenterLocal = Vec(scanMeshBoundsLocal.center),
                scanMeshBoundsSizeLocal = Vec(scanMeshBoundsLocal.size),
                scanToDetectionPosition = Vec(Vector3.zero),
                scanToDetectionRotationQuat = Quat(rotationOffset),
                scanToDetectionScaleRatio = Vec(scaleRatio),
                calibratedMeshLocalScale = Vec(finalScale),
                spawnRelativeRotation = spawnReference.IsValid,
                referenceModelScaleM = Vec(detectionModelScale),
                referenceBoxEdgeLengthsM = Vec(referenceEdges),
            };
        }

        public Pose ApplyToDetection(Vector3[] detectionCorners)
        {
            return TryApplyMeshPlacement(detectionCorners, out var placement)
                ? new Pose(placement.Position, placement.Rotation)
                : default;
        }

        public static bool TryGetSpawnPlacement(Vector3[] detectionCorners, out ObjectronScanMeshPlacement placement)
        {
            placement = default;
            if (detectionCorners == null || detectionCorners.Length < 9)
            {
                return false;
            }

            if (!ObjectronOrientedBoxFitter.TryFitTransform(
                    detectionCorners, out var center, out var rotation, out _))
            {
                center = detectionCorners[0];
                rotation = Quaternion.identity;
            }

            placement = new ObjectronScanMeshPlacement
            {
                Position = center,
                Rotation = rotation,
                LocalScale = Vector3.one,
                IsValid = true,
            };
            return true;
        }

        public bool TryApplyMeshPlacement(
            Vector3[] detectionCorners,
            Vector3? currentModelScale,
            out ObjectronScanMeshPlacement placement,
            bool applyUserCalibration)
        {
            placement = default;
            if (detectionCorners == null || detectionCorners.Length < 9)
            {
                return false;
            }

            if (!TryGetUprightSpawnPlacement(detectionCorners, out placement))
            {
                return false;
            }

            if (applyUserCalibration && HasUprightPreset())
            {
                placement.Rotation = placement.Rotation * GetUprightCorrection();
                placement.LocalScale = GetCalibratedMeshLocalScale();
                return true;
            }

            if (!applyUserCalibration || !ShouldApplyUserCalibration())
            {
                return true;
            }

            placement.Rotation = placement.Rotation * ToQuat(scanToDetectionRotationQuat);
            placement.LocalScale = GetCalibratedMeshLocalScale();
            return true;
        }

        public static bool TryGetUprightSpawnPlacement(Vector3[] detectionCorners, out ObjectronScanMeshPlacement placement)
        {
            return ObjectronScanMeshUpright.TryGetUprightSpawnPlacement(detectionCorners, out placement);
        }

        public bool HasUprightPreset() =>
            hasMeshUprightPreset
            && meshUprightCorrectionQuat != null
            && meshUprightCorrectionQuat.Length >= 4;

        public Quaternion GetUprightCorrection()
        {
            if (HasUprightPreset())
            {
                return ToQuat(meshUprightCorrectionQuat);
            }

            if (ShouldApplyUserCalibration())
            {
                return ToQuat(scanToDetectionRotationQuat);
            }

            return Quaternion.identity;
        }

        /// <summary>Apply saved rotation + scale from a v3/v4 spawn-relative device calibration.</summary>
        public bool ShouldApplyUserCalibration()
        {
            if (HasUprightPreset())
            {
                return true;
            }

            if (!HasRelativeTransform() || !spawnRelativeRotation)
            {
                return false;
            }

            return version == "3" || version == "4";
        }

        /// <summary>Only apply saved rotation for v3 calibrations captured from spawn pose (not legacy bad offsets).</summary>
        public bool ShouldApplySavedRotation() => ShouldApplyUserCalibration();

        public float GetRotationOffsetDegrees()
        {
            if (scanToDetectionRotationQuat == null || scanToDetectionRotationQuat.Length < 4)
            {
                return 0f;
            }

            return Quaternion.Angle(Quaternion.identity, ToQuat(scanToDetectionRotationQuat));
        }

        public bool TryApplyMeshPlacement(
            Vector3[] detectionCorners,
            Vector3? currentModelScale,
            out ObjectronScanMeshPlacement placement)
        {
            return TryApplyMeshPlacement(detectionCorners, currentModelScale, out placement, applyUserCalibration: true);
        }

        public bool TryApplyMeshPlacement(Vector3[] detectionCorners, out ObjectronScanMeshPlacement placement)
        {
            return TryApplyMeshPlacement(detectionCorners, null, out placement, applyUserCalibration: true);
        }

        private Vector3 ResolveMeshLocalScale(Vector3[] detectionCorners, Vector3? currentModelScale)
        {
            var baseScale = GetCalibratedMeshLocalScale();

            // v2+ stores the user-tuned mesh scale directly; skip box-edge ratio and model-scale drift.
            if (version == "2" || version == "3" || version == "4")
            {
                return baseScale;
            }

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

        /// <summary>User scale delta relative to spawn (spawn is always 1.0). Used when saving calibration.</summary>
        public float GetMeshScaleFactor()
        {
            var saved = GetCalibratedMeshLocalScale();
            return (saved.x + saved.y + saved.z) / 3f;
        }

        public Vector3 GetMeshBoundsCenterLocal()
        {
            if (scanMeshBoundsCenterLocal != null && scanMeshBoundsCenterLocal.Length >= 3)
            {
                return ToVec3(scanMeshBoundsCenterLocal);
            }

            return Vector3.zero;
        }

        private static bool TryGetDetectionPose(Vector3[] corners, out Vector3 center, out Quaternion rotation)
        {
            if (!TryGetSpawnPlacement(corners, out var placement))
            {
                center = default;
                rotation = Quaternion.identity;
                return false;
            }

            center = placement.Position;
            rotation = placement.Rotation;
            return true;
        }

        public bool HasRelativeTransform() =>
            scanToDetectionRotationQuat != null
            && scanToDetectionRotationQuat.Length >= 4
            && (calibratedMeshLocalScale != null && calibratedMeshLocalScale.Length >= 3
                || scanLossyScale != null && scanLossyScale.Length >= 3
                || scanLocalScale != null && scanLocalScale.Length >= 3);

        public string ToDebugString()
        {
            return JsonUtility.ToJson(this, true);
        }

        public static void ReadAnnotationModelVectors(
            Mediapipe.ObjectAnnotation ann,
            Vector3[] corners,
            out Vector3 translation,
            out Vector3 scale,
            out Vector3 rotationEuler)
        {
            translation = Vector3.zero;
            scale = Vector3.zero;
            rotationEuler = Vector3.zero;

            if (ann?.Translation != null && ann.Translation.Count >= 3)
            {
                translation = new Vector3(ann.Translation[0], ann.Translation[1], ann.Translation[2]);
            }

            if (ann?.Scale != null && ann.Scale.Count >= 3)
            {
                scale = new Vector3(ann.Scale[0], ann.Scale[1], ann.Scale[2]);
            }
            else if (ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(corners, out var edgeMeters))
            {
                scale = edgeMeters;
            }

            if (ann?.Rotation != null && ann.Rotation.Count >= 9)
            {
                var r = ann.Rotation;
                var rot = new Matrix4x4(
                    new Vector4(r[0], r[1], r[2], 0f),
                    new Vector4(r[3], r[4], r[5], 0f),
                    new Vector4(r[6], r[7], r[8], 0f),
                    new Vector4(0f, 0f, 0f, 1f));
                rotationEuler = rot.rotation.eulerAngles;
            }
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
