// Snap chair box bottom to Meta Scene API floor via downward environment raycast.

using System.Collections.Generic;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronFloorPlaneSnap
    {
        private const float RayStartLiftM = 0.05f;
        private const float MaxTiltBeforeDeg = 50f;
        private const float MaxTiltLevelOnlyDeg = 30f;
        private const float MaxTiltCorrectDeg = 25f;
        private const float MaxLiftPerFrameM = 0.06f;
        private const float MaxRawLiftRejectM = 0.25f;
        private const float MaxGatedLiftM = 0.08f;
        private const int MinRayHits = 2;
        private const float MinModelEdgeM = 0.08f;
        private const float FloorYSmoothLerp = 0.35f;
        private const float UprightDotDownThreshold = 0.75f;

        private static readonly Dictionary<int, float> s_smoothedFloorYByObject = new();

        /// <summary>
        /// Levels box bottom to world down and snaps to scene floor Y from Meta EnvironmentRaycast (Scene API).
        /// </summary>
        public static bool TrySnapBoxToFloor(
            EnvironmentRayCastSampleManager raycast,
            int objectId,
            Vector3[] corners,
            Vector3 modelHalfExtents,
            out Vector3[] snapped,
            out float liftM,
            out int rayHits)
        {
            snapped = null;
            liftM = 0f;
            rayHits = 0;

            if (corners == null || corners.Length < 9)
            {
                LogSkip(objectId, "bad_corners", liftM, rayHits);
                return false;
            }

            if (!HasSaneModelExtents(modelHalfExtents))
            {
                LogSkip(objectId, "bad_extents", liftM, rayHits);
                return false;
            }

            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(corners, out var floorFace))
            {
                LogSkip(objectId, "no_floor_face", liftM, rayHits);
                return false;
            }

            var faceOutBefore = ObjectronBoxGeometry.GetFaceOutwardNormal(corners, floorFace);
            var tiltBeforeDeg = Vector3.Angle(faceOutBefore, Vector3.down);
            var dotDown = Vector3.Dot(faceOutBefore, Vector3.down);

            if (tiltBeforeDeg >= MaxTiltBeforeDeg)
            {
                LogSkip(objectId, $"tilt_before={tiltBeforeDeg:F1}deg", liftM, rayHits);
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

            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(working, out floorFace))
            {
                LogSkip(objectId, "floor_face_lost", liftM, rayHits);
                return false;
            }

            if (dotDown < UprightDotDownThreshold && tiltBeforeDeg > 8f)
            {
                LogSkip(objectId, $"not_upright dot_down={dotDown:F2}", liftM, rayHits);
                return false;
            }

            var floorYBefore = GetFaceAverageY(working, floorFace);
            var targetY = floorYBefore;

            if (raycast == null || !raycast.HasScenePermission())
            {
                LogSkip(objectId, "no_scene_permission", liftM, rayHits);
                return false;
            }

            var hits = new List<Vector3>(6);
            foreach (var idx in floorFace)
            {
                var origin = working[idx] + Vector3.up * RayStartLiftM;
                var hit = raycast.Raycast(new Ray(origin, Vector3.down));
                if (hit.HasValue)
                {
                    hits.Add(hit.Value);
                }
            }

            var faceCenter = GetFaceCenter(working, floorFace);
            var centerHit = raycast.Raycast(new Ray(faceCenter + Vector3.up * RayStartLiftM, Vector3.down));
            if (centerHit.HasValue)
            {
                hits.Add(centerHit.Value);
            }

            rayHits = hits.Count;
            if (rayHits < MinRayHits)
            {
                LogSkip(objectId, $"ray_hits={rayHits}", liftM, rayHits);
                return false;
            }

            var sumY = 0f;
            foreach (var h in hits)
            {
                sumY += h.y;
            }

            targetY = sumY / hits.Count;

            var rawLiftM = targetY - floorYBefore;
            if (Mathf.Abs(rawLiftM) > MaxRawLiftRejectM)
            {
                LogSkip(objectId, $"raw_lift={rawLiftM * 1000f:F0}mm", rawLiftM, rayHits);
                return false;
            }

            if (!s_smoothedFloorYByObject.TryGetValue(objectId, out var smoothedY))
            {
                smoothedY = targetY;
            }
            else
            {
                smoothedY = Mathf.Lerp(smoothedY, targetY, FloorYSmoothLerp);
            }

            s_smoothedFloorYByObject[objectId] = smoothedY;

            var predictedLiftM = smoothedY - floorYBefore;
            if (Mathf.Abs(predictedLiftM) > MaxGatedLiftM)
            {
                LogSkip(objectId, $"predicted_lift={predictedLiftM * 1000f:F0}mm", predictedLiftM, rayHits);
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

                working[0] = GetFaceCenter(working, floorFace);
            }

            if (!ObjectronBoxValidation.HasValidExtents(working))
            {
                LogSkip(objectId, "invalid_extents_after", liftM, rayHits);
                return false;
            }

            snapped = working;
            QuestObjectronLogger.World(
                $"floor_snap id={objectId} lift_mm={liftM * 1000f:F0} raw_lift_mm={rawLiftM * 1000f:F0} " +
                $"floor_y={smoothedY:F3} ray_hits={rayHits} tilt_before={tiltBeforeDeg:F1}deg");
            return true;
        }

        public static void ClearSmoothedFloorY(int objectId) => s_smoothedFloorYByObject.Remove(objectId);

        public static void ClearAllSmoothedFloorY() => s_smoothedFloorYByObject.Clear();

        private static bool HasSaneModelExtents(Vector3 halfExtents)
        {
            var minEdge = 2f * Mathf.Min(halfExtents.x, Mathf.Min(halfExtents.y, halfExtents.z));
            return minEdge > MinModelEdgeM;
        }

        private static void LogSkip(int objectId, string reason, float liftM, int rayHits)
        {
            QuestObjectronLogger.World(
                $"floor_snap_skip id={objectId} reason={reason} lift_mm={liftM * 1000f:F0} ray_hits={rayHits}");
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
