// Snap Objectron box bottom face to scene table via MRUK / scene raycast + level to horizontal.

using System.Collections.Generic;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronTablePlaneSnap
    {
        /// <summary>Off by default — ModelOrientedMaskBox was more stable on device.</summary>
        public const bool DefaultEnableTableSnap = false;

        private const float RayStartLiftM = 0.04f;
        private const float MaxTiltBeforeDeg = 40f;
        private const float MaxTiltLevelOnlyDeg = 25f;
        private const float MaxTiltCorrectDeg = 20f;
        private const float MaxLiftPerFrameM = 0.035f;
        private const float MaxRawLiftRejectM = 0.08f;
        private const float MaxGatedLiftM = 0.035f;
        private const int MinRayHits = 3;
        private const float MinModelEdgeM = 0.03f;
        private const float TableYSmoothLerp = 0.3f;
        private const float UprightDotDownThreshold = 0.85f;

        private static readonly Dictionary<int, float> s_smoothedTableYByObject = new();

        public static bool EnableTableSnap { get; set; } = DefaultEnableTableSnap;

        public static bool TrySnapBoxToTable(
            EnvironmentRayCastSampleManager raycast,
            int objectId,
            Vector3[] corners,
            Vector3 modelHalfExtents,
            out Vector3[] snapped,
            out float tiltBeforeDeg,
            out float tiltAfterDeg,
            out float liftM,
            out int rayHits)
        {
            snapped = null;
            tiltBeforeDeg = 0f;
            tiltAfterDeg = 0f;
            liftM = 0f;
            rayHits = 0;

            if (!EnableTableSnap)
            {
                return false;
            }

            if (corners == null || corners.Length < 9)
            {
                LogSkip(objectId, "bad_corners", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            if (!HasSaneModelExtents(modelHalfExtents))
            {
                LogSkip(objectId, "bad_extents", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(corners, out var tableFace))
            {
                LogSkip(objectId, "no_table_face", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            var faceOutBefore = ObjectronBoxGeometry.GetFaceOutwardNormal(corners, tableFace);
            tiltBeforeDeg = Vector3.Angle(faceOutBefore, Vector3.down);
            var dotDown = Vector3.Dot(faceOutBefore, Vector3.down);

            if (tiltBeforeDeg >= MaxTiltBeforeDeg)
            {
                LogSkip(objectId, $"tilt_before={tiltBeforeDeg:F1}deg", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            var working = (Vector3[])corners.Clone();
            var center = working[0];

            if (tiltBeforeDeg > 1f && tiltBeforeDeg < MaxTiltLevelOnlyDeg)
            {
                var levelRot = Quaternion.FromToRotation(faceOutBefore, Vector3.down);
                levelRot = Quaternion.RotateTowards(Quaternion.identity, levelRot, MaxTiltCorrectDeg);
                for (var i = 1; i < 9; i++)
                {
                    working[i] = center + levelRot * (working[i] - center);
                }
            }

            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(working, out tableFace))
            {
                LogSkip(objectId, "table_face_lost", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            var faceOutAfter = ObjectronBoxGeometry.GetFaceOutwardNormal(working, tableFace);
            tiltAfterDeg = Vector3.Angle(faceOutAfter, Vector3.down);

            if (dotDown < UprightDotDownThreshold && tiltBeforeDeg > 5f)
            {
                LogSkip(objectId, $"not_upright dot_down={dotDown:F2}", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            var tableYBefore = GetFaceAverageY(working, tableFace);
            var targetY = tableYBefore;
            if (raycast != null && raycast.HasScenePermission())
            {
                var hits = new List<Vector3>(5);
                foreach (var idx in tableFace)
                {
                    var origin = working[idx] + Vector3.up * RayStartLiftM;
                    var hit = raycast.Raycast(new Ray(origin, Vector3.down));
                    if (hit.HasValue)
                    {
                        hits.Add(hit.Value);
                    }
                }

                var faceCenter = GetFaceCenter(working, tableFace);
                var centerHit = raycast.Raycast(new Ray(faceCenter + Vector3.up * RayStartLiftM, Vector3.down));
                if (centerHit.HasValue)
                {
                    hits.Add(centerHit.Value);
                }

                rayHits = hits.Count;
                if (hits.Count >= MinRayHits)
                {
                    var sumY = 0f;
                    foreach (var h in hits)
                    {
                        sumY += h.y;
                    }

                    targetY = sumY / hits.Count;
                }
                else
                {
                    LogSkip(objectId, $"ray_hits={rayHits}", tiltBeforeDeg, liftM, rayHits);
                    return false;
                }
            }
            else
            {
                LogSkip(objectId, "no_scene_rays", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            var rawLiftM = targetY - tableYBefore;
            if (Mathf.Abs(rawLiftM) > MaxRawLiftRejectM)
            {
                LogSkip(objectId, $"raw_lift={rawLiftM * 1000f:F0}mm", tiltBeforeDeg, rawLiftM, rayHits);
                return false;
            }

            if (!s_smoothedTableYByObject.TryGetValue(objectId, out var smoothedY))
            {
                smoothedY = targetY;
            }
            else
            {
                smoothedY = Mathf.Lerp(smoothedY, targetY, TableYSmoothLerp);
            }

            s_smoothedTableYByObject[objectId] = smoothedY;

            var predictedLiftM = smoothedY - tableYBefore;
            if (Mathf.Abs(predictedLiftM) > MaxGatedLiftM)
            {
                LogSkip(objectId, $"predicted_lift={predictedLiftM * 1000f:F0}mm", tiltBeforeDeg, predictedLiftM, rayHits);
                return false;
            }

            liftM = Mathf.Clamp(predictedLiftM, -MaxLiftPerFrameM, MaxLiftPerFrameM);
            if (Mathf.Abs(liftM) > 0.0005f)
            {
                var delta = new Vector3(0f, liftM, 0f);
                for (var i = 1; i < 9; i++)
                {
                    working[i] += delta;
                }

                working[0] = GetFaceCenter(working, tableFace);
            }

            if (!ObjectronBoxValidation.HasValidExtents(working))
            {
                LogSkip(objectId, "invalid_extents_after", tiltBeforeDeg, liftM, rayHits);
                return false;
            }

            snapped = working;
            QuestObjectronLogger.BoxProjDebug(
                $"TABLE_SNAP id={objectId} tilt_before={tiltBeforeDeg:F1}deg tilt_after={tiltAfterDeg:F1}deg " +
                $"lift_m={liftM * 1000f:F0}mm raw_lift={rawLiftM * 1000f:F0}mm table_y={smoothedY:F3} ray_hits={rayHits}");
            return true;
        }

        public static void ClearSmoothedTableY(int objectId) => s_smoothedTableYByObject.Remove(objectId);

        public static void ClearAllSmoothedTableY() => s_smoothedTableYByObject.Clear();

        private static bool HasSaneModelExtents(Vector3 halfExtents)
        {
            var minEdge = 2f * Mathf.Min(halfExtents.x, Mathf.Min(halfExtents.y, halfExtents.z));
            return minEdge > MinModelEdgeM;
        }

        private static void LogSkip(int objectId, string reason, float tiltBeforeDeg, float liftM, int rayHits)
        {
            QuestObjectronLogger.BoxProjDebug(
                $"TABLE_SNAP_SKIP id={objectId} reason={reason} tilt_before={tiltBeforeDeg:F1}deg " +
                $"lift_m={liftM * 1000f:F0}mm ray_hits={rayHits}");
        }

        private static float GetFaceAverageY(Vector3[] corners, int[] face)
        {
            var sum = 0f;
            for (var i = 0; i < face.Length; i++)
            {
                sum += corners[face[i]].y;
            }

            return sum / face.Length;
        }

        private static Vector3 GetFaceCenter(Vector3[] corners, int[] face)
        {
            var c = Vector3.zero;
            for (var i = 0; i < face.Length; i++)
            {
                c += corners[face[i]];
            }

            return c / face.Length;
        }
    }
}
