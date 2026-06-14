// Two-stage Objectron placement: stage-1 SSD crop (multi_box_rects) + stage-2 BoxLandmark + EPnP lift.
// World placement follows MediaPipe objectron.md coordinate systems without depth/mask heuristics.

using System.Collections.Generic;
using Mediapipe;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public class ObjectronTwoStagePlacement
    {
        private readonly PassthroughCameraAccess m_cameraAccess;
        private readonly EnvironmentRayCastSampleManager m_raycast;
        private readonly ObjectronPlacementOptions m_options;
        private readonly float m_smoothing;

        private readonly Dictionary<int, Vector3[]> m_smoothedCorners = new();

        public ObjectronPlacementOptions Options => m_options;

        public ObjectronTwoStagePlacement(
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager raycast,
            ObjectronPlacementOptions options,
            float smoothing = 0.35f)
        {
            m_cameraAccess = cameraAccess;
            m_raycast = raycast;
            m_options = options ?? new ObjectronPlacementOptions();
            m_smoothing = Mathf.Clamp01(smoothing);
        }

        public void SetMirrorHorizontal(bool mirror) => m_options.MirrorInferenceHorizontal = mirror;

        public List<PlacementOutput> PlaceDetailed(
            FrameAnnotation frame,
            Pose cameraPose,
            IReadOnlyList<NormalizedRect> stageOneRects = null)
        {
            var results = new List<PlacementOutput>();
            if (frame?.Annotations == null)
            {
                return results;
            }

            for (var index = 0; index < frame.Annotations.Count; index++)
            {
                var annotation = frame.Annotations[index];
                NormalizedRect stageOneRect = null;
                if (stageOneRects != null && index < stageOneRects.Count)
                {
                    stageOneRect = stageOneRects[index];
                }

                var output = PlaceOne(annotation, cameraPose, stageOneRect);
                if (output.Corners == null)
                {
                    QuestObjectronLogger.World($"twostage objectId={annotation.ObjectId} placement=failed");
                    continue;
                }

                var corners = Smooth(annotation.ObjectId, output.Corners);

                if (m_options.CompensateHeadRoll)
                {
                    ObjectronWorldOrientation.TryAlignBoxToGravity(corners);
                }

                if (m_options.ConstrainUprightOnTable)
                {
                    ObjectronWorldOrientation.TryConstrainUprightOnTable(corners);
                }

                var finalMethod = output.Method;
                if (m_options.EnableFloorSnap)
                {
                    var modelHalf = ObjectronMediaPipeCoordinates.GetHalfExtents(annotation);
                    if (!ObjectronFloorPlaneSnap.TrySnapBoxToFloor(
                            m_raycast,
                            annotation.ObjectId,
                            corners,
                            modelHalf,
                            out var floored,
                            out _,
                            out _))
                    {
                        QuestObjectronLogger.World(
                            $"twostage objectId={annotation.ObjectId} placement=floor_snap_failed");
                        continue;
                    }

                    System.Array.Copy(floored, corners, floored.Length);
                    finalMethod = PlacementMethod.FloorSnappedBox;
                }

                ObjectronBoxValidation.TryGetExtentMeters(corners, out var extent);
                QuestObjectronLogger.World(
                    $"twostage objectId={annotation.ObjectId} method={finalMethod} center={corners[0]:F2} extent={extent:F3}m");

                results.Add(new PlacementOutput(annotation.ObjectId, corners, finalMethod, output.DebugReport));
            }

            return results;
        }

        /// <summary>Cheap stage-2 pose → world (no depth refine, no floor snap).</summary>
        public PlacementOutput PlaceOneTrack(
            ObjectAnnotation annotation,
            Pose cameraPose,
            NormalizedRect stageOneRect = null)
        {
            return PlaceOne(annotation, cameraPose, stageOneRect);
        }

        public float ComputeStageAlignErrorPx(
            ObjectAnnotation annotation,
            Vector3[] cornersCam,
            NormalizedRect stageOneRect)
        {
            if (stageOneRect == null || m_cameraAccess == null || !m_cameraAccess.IsPlaying
                || cornersCam == null || cornersCam.Length < 1)
            {
                return float.MaxValue;
            }

            var res = m_cameraAccess.CurrentResolution;
            var w = Mathf.Max(1, Mathf.RoundToInt(res.x));
            var h = Mathf.Max(1, Mathf.RoundToInt(res.y));
            var ndcCenter = ObjectronMediaPipeCoordinates.ProjectCameraToNdc(
                cornersCam[0],
                ObjectronMediaPipeCoordinates.DefaultNdcFocalLength,
                ObjectronMediaPipeCoordinates.DefaultNdcPrincipalPoint);
            var pixelCenter = ObjectronMediaPipeCoordinates.NdcToPixel(ndcCenter, w, h);

            var rectCenterX = stageOneRect.XCenter;
            if (m_options.MirrorInferenceHorizontal)
            {
                rectCenterX = 1f - rectCenterX;
            }

            var rectPixel = new Vector2(rectCenterX * w, stageOneRect.YCenter * h);
            return Vector2.Distance(pixelCenter, rectPixel);
        }

        public float TryGetStageAlignErrorPx(ObjectAnnotation annotation, NormalizedRect stageOneRect)
        {
            if (!ObjectronMediaPipeCoordinates.TryBuildFromLiftedKeypoints3D(annotation, out var cornersCam)
                && !ObjectronMediaPipeCoordinates.TryBuildCameraFrameBox(annotation, out cornersCam))
            {
                return float.MaxValue;
            }

            return ComputeStageAlignErrorPx(annotation, cornersCam, stageOneRect);
        }

        private PlacementOutput PlaceOne(
            ObjectAnnotation annotation,
            Pose cameraPose,
            NormalizedRect stageOneRect)
        {
            Vector3[] cornersCam;
            PlacementMethod method;

            if (ObjectronMediaPipeCoordinates.TryBuildFromLiftedKeypoints3D(annotation, out cornersCam))
            {
                method = PlacementMethod.TwoStageLifted3D;
            }
            else if (ObjectronMediaPipeCoordinates.TryBuildCameraFrameBox(annotation, out cornersCam))
            {
                method = PlacementMethod.TwoStageCanonical;
            }
            else
            {
                return new PlacementOutput(-1, null, PlacementMethod.None, null);
            }

            LogStageAlignment(annotation, cornersCam, stageOneRect);

            var cornersWorld = CameraFrameBoxToWorld(cornersCam, cameraPose);
            var debug = BuildDebugReport(annotation, cameraPose, method, cornersWorld);
            return new PlacementOutput(annotation.ObjectId, cornersWorld, method, debug);
        }

        private Vector3[] CameraFrameBoxToWorld(Vector3[] cornersCam, Pose cameraPose)
        {
            var world = new Vector3[cornersCam.Length];
            for (var i = 0; i < cornersCam.Length; i++)
            {
                var cam = ApplyCameraFrameConversion(cornersCam[i]);
                world[i] = CameraLocalToWorld(cam, cameraPose);
            }

            return world;
        }

        private Vector3 ApplyCameraFrameConversion(Vector3 mediaPipeCam)
        {
            m_options.ApplyAnnotationPose(
                mediaPipeCam,
                Matrix4x4.identity,
                out var cam,
                out _);
            return cam;
        }

        private Vector3 CameraLocalToWorld(Vector3 cameraLocalMeters, Pose cameraPose)
        {
            var pose = m_options.GetPlacementPose(cameraPose);
            return pose.position + pose.rotation * cameraLocalMeters;
        }

        private void LogStageAlignment(
            ObjectAnnotation annotation,
            Vector3[] cornersCam,
            NormalizedRect stageOneRect)
        {
            if (stageOneRect == null || m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            var res = m_cameraAccess.CurrentResolution;
            var w = Mathf.Max(1, Mathf.RoundToInt(res.x));
            var h = Mathf.Max(1, Mathf.RoundToInt(res.y));
            var ndcFocal = ObjectronMediaPipeCoordinates.DefaultNdcFocalLength;
            var ndcPrincipal = ObjectronMediaPipeCoordinates.DefaultNdcPrincipalPoint;

            var ndcCenter = ObjectronMediaPipeCoordinates.ProjectCameraToNdc(
                cornersCam[0], ndcFocal, ndcPrincipal);
            var pixelCenter = ObjectronMediaPipeCoordinates.NdcToPixel(ndcCenter, w, h);

            var rectCenterX = stageOneRect.XCenter;
            var rectCenterY = stageOneRect.YCenter;
            if (m_options.MirrorInferenceHorizontal)
            {
                rectCenterX = 1f - rectCenterX;
            }

            var rectPixel = new Vector2(rectCenterX * w, rectCenterY * h);
            var errPx = Vector2.Distance(pixelCenter, rectPixel);
            QuestObjectronLogger.Detect(
                $"twostage_align id={annotation.ObjectId} stage1_rect=({rectPixel.x:F0},{rectPixel.y:F0}) " +
                $"stage2_proj=({pixelCenter.x:F0},{pixelCenter.y:F0}) err_px={errPx:F0}");
        }

        private static ObjectronPlacementDebugReport? BuildDebugReport(
            ObjectAnnotation annotation,
            Pose cameraPose,
            PlacementMethod method,
            Vector3[] cornersWorld)
        {
            if (cornersWorld == null || cornersWorld.Length < 9)
            {
                return null;
            }

            ObjectronBoxValidation.TryGetExtentMeters(cornersWorld, out var extent);
            var hmd = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            return new ObjectronPlacementDebugReport(
                annotation.ObjectId,
                method,
                method,
                cameraPose,
                ObjectronPlacementDebug.GetModelTranslationCam(annotation),
                ObjectronMediaPipeCoordinates.GetHalfExtents(annotation),
                ObjectronPlacementDebug.GetModelRotationEuler(annotation),
                cornersWorld[0],
                extent,
                cornersWorld[0],
                extent,
                1f,
                false,
                false,
                0f,
                Vector2.zero,
                default,
                hmd,
                0f,
                0f);
        }

        private Vector3[] Smooth(int objectId, Vector3[] corners)
        {
            if (!m_smoothedCorners.TryGetValue(objectId, out var prev))
            {
                m_smoothedCorners[objectId] = (Vector3[])corners.Clone();
                return corners;
            }

            for (var i = 0; i < corners.Length; i++)
            {
                prev[i] = Vector3.Lerp(prev[i], corners[i], 1f - m_smoothing);
            }

            return (Vector3[])prev.Clone();
        }
    }
}
