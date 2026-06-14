// Maps Objectron 2D detections to world space using Meta PassthroughCameraAccess.ViewportPointToRay.
// Keeps depth from the model / scene raycast but anchors lateral position to the camera image.

using Mediapipe;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public readonly struct ViewportAnchorMeasure
    {
        public readonly bool Success;
        public readonly Vector2 NormViewportCenter;
        public readonly Vector3 WorldCenter;
        public readonly bool RaycastHit;
        public readonly float DepthM;

        public ViewportAnchorMeasure(
            bool success,
            Vector2 normViewportCenter,
            Vector3 worldCenter,
            bool raycastHit,
            float depthM)
        {
            Success = success;
            NormViewportCenter = normViewportCenter;
            WorldCenter = worldCenter;
            RaycastHit = raycastHit;
            DepthM = depthM;
        }
    }

    public static class ObjectronViewportCenterAnchor
    {
        private const float StageOneBlendWeight = 0.4f;

        public static bool TryGetNormViewportCenter(
            ObjectAnnotation annotation,
            NormalizedRect stageOneRect,
            bool mirrorHorizontal,
            out Vector2 normCenter)
        {
            normCenter = default;
            var hasKp = TryGetKeypointCentroid(annotation, mirrorHorizontal, out var kpCenter);
            var hasStage = TryGetStageOneCenter(stageOneRect, mirrorHorizontal, out var stageCenter);

            if (hasKp && hasStage)
            {
                normCenter = Vector2.Lerp(kpCenter, stageCenter, StageOneBlendWeight);
                return true;
            }

            if (hasKp)
            {
                normCenter = kpCenter;
                return true;
            }

            if (hasStage)
            {
                normCenter = stageCenter;
                return true;
            }

            return ObjectronDepthPlacementRefiner.TryGetViewportRectFromKeypoints(
                annotation,
                mirrorHorizontal,
                out var rect)
                && rect.width > 0.001f
                && rect.height > 0.001f
                && AssignRectCenter(rect, out normCenter);
        }

        /// <summary>
        /// World point on the view ray through normCenter at the given depth (meters from camera).
        /// Lateral-only: depth is fixed along camera forward; viewport ray intersects that depth plane.
        /// </summary>
        public static bool TryMeasureAtDepth(
            Vector2 normViewportCenter,
            float depthM,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager raycast,
            Pose cameraPose,
            out ViewportAnchorMeasure measure)
        {
            measure = default;
            if (cameraAccess == null || !cameraAccess.IsPlaying || depthM < 0.05f)
            {
                return false;
            }

            var ray = cameraAccess.ViewportPointToRay(normViewportCenter, cameraPose);
            var camFwd = cameraPose.rotation * Vector3.forward;
            var plane = new Plane(camFwd, cameraPose.position + camFwd * depthM);
            if (!plane.Raycast(ray, out var t) || t < 0.01f)
            {
                return false;
            }

            var worldCenter = ray.GetPoint(t);
            measure = new ViewportAnchorMeasure(
                true,
                normViewportCenter,
                worldCenter,
                false,
                depthM);
            return true;
        }

        public static bool TryMeasureFromAnnotation(
            ObjectAnnotation annotation,
            NormalizedRect stageOneRect,
            Vector3 referenceWorldCenter,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager raycast,
            Pose cameraPose,
            bool mirrorHorizontal,
            out ViewportAnchorMeasure measure)
        {
            measure = default;
            if (!TryGetNormViewportCenter(annotation, stageOneRect, mirrorHorizontal, out var normCenter))
            {
                return false;
            }

            var camFwd = cameraPose.rotation * Vector3.forward;
            var depthM = Vector3.Dot(referenceWorldCenter - cameraPose.position, camFwd);
            if (depthM < 0.05f)
            {
                depthM = Vector3.Distance(cameraPose.position, referenceWorldCenter);
            }

            return TryMeasureAtDepth(
                normCenter,
                depthM,
                cameraAccess,
                raycast,
                cameraPose,
                out measure);
        }

        public static float ComputeStageAlignErrorPx(
            ObjectAnnotation annotation,
            NormalizedRect stageOneRect,
            PassthroughCameraAccess cameraAccess,
            bool mirrorHorizontal)
        {
            if (stageOneRect == null
                || cameraAccess == null
                || !cameraAccess.IsPlaying
                || !ObjectronMediaPipeCoordinates.TryBuildCameraFrameBox(annotation, out var cornersCam))
            {
                return float.MaxValue;
            }

            if (!ObjectronPcaIntrinsics.TryGetNdcIntrinsics(
                    cameraAccess,
                    out var ndcFocal,
                    out var ndcPrincipal,
                    out var w,
                    out var h))
            {
                return float.MaxValue;
            }

            var ndcCenter = ObjectronMediaPipeCoordinates.ProjectCameraToNdc(
                cornersCam[0],
                ndcFocal,
                ndcPrincipal);
            var pixelCenter = ObjectronMediaPipeCoordinates.NdcToPixel(ndcCenter, w, h);

            if (!TryGetStageOneCenter(stageOneRect, mirrorHorizontal, out var stageNorm))
            {
                return float.MaxValue;
            }

            var stagePixel = new Vector2(stageNorm.x * w, stageNorm.y * h);
            return Vector2.Distance(pixelCenter, stagePixel);
        }

        private static bool AssignRectCenter(UnityEngine.Rect rect, out Vector2 normCenter)
        {
            normCenter = rect.center;
            return true;
        }

        private static bool TryGetKeypointCentroid(
            ObjectAnnotation annotation,
            bool mirrorHorizontal,
            out Vector2 normCenter)
        {
            normCenter = default;
            if (annotation?.Keypoints == null || annotation.Keypoints.Count == 0)
            {
                return false;
            }

            var sumX = 0f;
            var sumY = 0f;
            var count = 0;
            foreach (var kp in annotation.Keypoints)
            {
                if (kp.Point2D == null)
                {
                    continue;
                }

                var x = mirrorHorizontal ? 1f - kp.Point2D.X : kp.Point2D.X;
                sumX += x;
                sumY += kp.Point2D.Y;
                count++;
            }

            if (count < 2)
            {
                return false;
            }

            normCenter = new Vector2(sumX / count, 1f - sumY / count);
            return true;
        }

        private static bool TryGetStageOneCenter(
            NormalizedRect stageOneRect,
            bool mirrorHorizontal,
            out Vector2 normCenter)
        {
            normCenter = default;
            if (stageOneRect == null)
            {
                return false;
            }

            var x = stageOneRect.XCenter;
            if (mirrorHorizontal)
            {
                x = 1f - x;
            }

            normCenter = new Vector2(x, 1f - stageOneRect.YCenter);
            return true;
        }
    }
}
