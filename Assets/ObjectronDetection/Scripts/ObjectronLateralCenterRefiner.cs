// Shifts a 3D box laterally (camera-right plane) so its center aligns with Meta viewport anchor.
// Preserves depth along the view axis — fixes left/right drift without changing orientation.

using Mediapipe;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronLateralCenterRefiner
    {
        private const float MinShiftM = 0.004f;
        private const float MaxShiftPerFrameM = 0.22f;

        public static bool TryRefine(
            Vector3[] corners,
            ObjectAnnotation annotation,
            NormalizedRect stageOneRect,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager raycast,
            Pose cameraPose,
            ObjectronPlacementOptions options,
            out float lateralShiftM)
        {
            lateralShiftM = 0f;
            if (corners == null || corners.Length < 9 || annotation == null || cameraAccess == null)
            {
                return false;
            }

            options ??= new ObjectronPlacementOptions();
            if (!ObjectronViewportCenterAnchor.TryMeasureFromAnnotation(
                    annotation,
                    stageOneRect,
                    corners[0],
                    cameraAccess,
                    raycast,
                    cameraPose,
                    options.MirrorInferenceHorizontal,
                    out var anchor)
                || !anchor.Success)
            {
                return false;
            }

            var camFwd = cameraPose.rotation * Vector3.forward;
            var current = corners[0];
            var delta = anchor.WorldCenter - current;
            var lateral = delta - Vector3.Dot(delta, camFwd) * camFwd;
            lateralShiftM = lateral.magnitude;

            if (lateralShiftM < MinShiftM)
            {
                return false;
            }

            if (lateralShiftM > MaxShiftPerFrameM)
            {
                lateral = lateral.normalized * MaxShiftPerFrameM;
                lateralShiftM = MaxShiftPerFrameM;
            }

            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] += lateral;
            }

            var alignErr = ObjectronViewportCenterAnchor.ComputeStageAlignErrorPx(
                annotation,
                stageOneRect,
                cameraAccess,
                options.MirrorInferenceHorizontal);

            QuestObjectronLogger.World(
                $"lateral_anchor id={annotation.ObjectId} shift_m={lateralShiftM:F3} " +
                $"depth_m={anchor.DepthM:F2} ray_hit={anchor.RaycastHit} " +
                $"vp=({anchor.NormViewportCenter.x:F3},{anchor.NormViewportCenter.y:F3}) " +
                $"stage_align_px={alignErr:F0}");
            return true;
        }
    }
}
