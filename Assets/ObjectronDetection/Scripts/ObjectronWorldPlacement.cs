// Maps Objectron 2D keypoints to world space using PCA rays and optional MRUK depth.

using System.Collections.Generic;
using Mediapipe;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public enum PlacementMethod
    {
        None,
        Keypoint3D,
        Keypoint2DRaycast,
        TranslationBox,
        DepthRefinedBox,
        /// <summary>Camera-aligned box from 2D mask frustum + scene depth (matches overlay size).</summary>
        MaskAlignedBox,
        /// <summary>Objectron model rotation + depth center; half-extents scaled to match 2D mask on camera plane.</summary>
        ModelOrientedMaskBox,
        /// <summary>Model-oriented box leveled on table plane + scene raycast vertical snap.</summary>
        TableSnappedBox,
    }

    public readonly struct PlacementOutput
    {
        public readonly int ObjectId;
        public readonly Vector3[] Corners;
        public readonly PlacementMethod Method;
        public readonly ObjectronPlacementDebugReport? DebugReport;

        public PlacementOutput(
            int objectId,
            Vector3[] corners,
            PlacementMethod method,
            ObjectronPlacementDebugReport? debugReport)
        {
            ObjectId = objectId;
            Corners = corners;
            Method = method;
            DebugReport = debugReport;
        }
    }

    public readonly struct PlacementResult
    {
        public readonly PlacementMethod Method;
        public readonly Vector3[] Corners;

        public PlacementResult(PlacementMethod method, Vector3[] corners)
        {
            Method = method;
            Corners = corners;
        }
    }

    public static class ObjectronBoxValidation
    {
        public const float MinExtentMeters = 0.01f;

        /// <summary>Objectron layout: index 0 = center, 1–8 = corners. Uses diagonal 1–5 span.</summary>
        public static bool TryGetExtentMeters(Vector3[] corners, out float extentMeters)
        {
            extentMeters = 0f;
            if (corners == null || corners.Length < 9)
            {
                return false;
            }

            extentMeters = Vector3.Distance(corners[1], corners[5]);
            if (extentMeters < MinExtentMeters)
            {
                extentMeters = MaxPairwiseCornerDistance(corners);
            }

            return extentMeters >= MinExtentMeters;
        }

        public static bool HasValidExtents(Vector3[] corners, float minMeters = MinExtentMeters)
        {
            return TryGetExtentMeters(corners, out var extent) && extent >= minMeters;
        }

        private static float MaxPairwiseCornerDistance(Vector3[] corners)
        {
            var max = 0f;
            for (var i = 1; i <= 8; i++)
            {
                for (var j = i + 1; j <= 8; j++)
                {
                    max = Mathf.Max(max, Vector3.Distance(corners[i], corners[j]));
                }
            }

            return max;
        }
    }

    public class ObjectronWorldPlacement
    {
        private static readonly Vector3[] s_localCornerUnit =
        {
            new(-1f, -1f, -1f), new(1f, -1f, -1f), new(-1f, 1f, -1f), new(1f, 1f, -1f),
            new(-1f, -1f, 1f), new(1f, -1f, 1f), new(-1f, 1f, 1f), new(1f, 1f, 1f),
        };

        private readonly PassthroughCameraAccess m_cameraAccess;
        private readonly EnvironmentRayCastSampleManager m_raycast;
        private readonly ObjectronPlacementOptions m_options;
        private readonly float m_smoothing;

        private readonly Dictionary<int, Vector3[]> m_smoothedCorners = new();
        private int m_lastMediaPipeObjectId = -1;

        public ObjectronPlacementOptions Options => m_options;

        public ObjectronWorldPlacement(
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

        public void SetEnableTableSnap(bool enable) => m_options.EnableTableSnap = enable;

        public List<Vector3[]> Place(
            FrameAnnotation frame,
            Pose cameraPose,
            IReadOnlyList<ObjectronOverlayRect> overlayRects = null)
        {
            var outputs = PlaceDetailed(frame, cameraPose, overlayRects);
            var results = new List<Vector3[]>(outputs.Count);
            foreach (var output in outputs)
            {
                if (output.Corners != null)
                {
                    results.Add(output.Corners);
                }
            }

            return results;
        }

        public List<PlacementOutput> PlaceDetailed(
            FrameAnnotation frame,
            Pose cameraPose,
            IReadOnlyList<ObjectronOverlayRect> overlayRects = null)
        {
            var results = new List<PlacementOutput>();
            if (frame?.Annotations == null)
            {
                return results;
            }

            for (var index = 0; index < frame.Annotations.Count; index++)
            {
                var annotation = frame.Annotations[index];
                ObjectronOverlayRect? overlayRect = null;
                if (overlayRects != null && index < overlayRects.Count)
                {
                    overlayRect = overlayRects[index];
                }

                var output = PlaceOne(annotation, cameraPose, overlayRect);
                if (output.Corners == null)
                {
                    QuestObjectronLogger.World($"objectId={annotation.ObjectId} placement=failed");
                    continue;
                }

                var smoothKey = GetStableSmoothKey(annotation.ObjectId, frame.Annotations.Count);
                var corners = Smooth(smoothKey, output.Corners);
                if (m_options.CompensateHeadRoll)
                {
                    ObjectronWorldOrientation.TryAlignBoxToGravity(corners);
                }

                ObjectronBoxValidation.TryGetExtentMeters(corners, out var extent);
                QuestObjectronLogger.World(
                    $"objectId={annotation.ObjectId} method={output.Method} center={corners[0]:F2} extent={extent:F3}m");

                if (output.DebugReport.HasValue)
                {
                    ObjectronPlacementDebug.LogIfChanged(output.DebugReport.Value);
                }

                results.Add(new PlacementOutput(annotation.ObjectId, corners, output.Method, output.DebugReport));
            }

            return results;
        }

        private PlacementOutput PlaceOne(
            ObjectAnnotation annotation,
            Pose cameraPose,
            ObjectronOverlayRect? overlayRect)
        {
            var placed = TryPlaceAnnotation(annotation, cameraPose);
            if (placed.Corners == null)
            {
                return new PlacementOutput(-1, null, PlacementMethod.None, null);
            }

            var rawCorners = placed.Corners;
            ObjectronBoxValidation.TryGetExtentMeters(rawCorners, out var rawExtent);
            var rawCenter = rawCorners[0];

            UnityEngine.Rect normViewport;
            var hasRect = overlayRect.HasValue;
            if (hasRect)
            {
                normViewport = ObjectronDepthPlacementRefiner.OverlayRectToNormViewport(
                    overlayRect.Value,
                    m_options.MirrorInferenceHorizontal);
            }
            else if (!ObjectronDepthPlacementRefiner.TryGetViewportRectFromKeypoints(
                         annotation,
                         m_options.MirrorInferenceHorizontal,
                         out normViewport))
            {
                return new PlacementOutput(
                    annotation.ObjectId,
                    rawCorners,
                    placed.Method,
                    BuildDebugReport(annotation, cameraPose, placed.Method, placed.Method, rawCenter, rawExtent,
                        rawCenter, rawExtent, 1f, default, false));
            }

            if (!ObjectronDepthPlacementRefiner.TryMeasureViewportRect(
                    normViewport,
                    m_cameraAccess,
                    m_raycast,
                    cameraPose,
                    out var depthMeasure))
            {
                return new PlacementOutput(
                    annotation.ObjectId,
                    rawCorners,
                    placed.Method,
                    BuildDebugReport(annotation, cameraPose, placed.Method, placed.Method, rawCenter, rawExtent,
                        rawCenter, rawExtent, 1f, depthMeasure, false));
            }

            var modelHalf = ObjectronPlacementDebug.GetModelHalfExtents(annotation);
            var scaleAvg = 1f;
            var scaleX = 1f;
            var scaleY = 1f;
            var scaleZ = 1f;
            ObjectronDepthPlacementRefiner.TryRefineOrientedBox(
                annotation,
                rawCorners,
                depthMeasure,
                cameraPose,
                m_options,
                out var modelScaled,
                out scaleAvg,
                out scaleX,
                out scaleY,
                out scaleZ);

            Vector3[] finalCorners;
            Vector3[] maskCorners = null;
            PlacementMethod finalMethod;
            var modelOrientedOk = ObjectronDepthPlacementRefiner.TryBuildModelOrientedMaskBox(
                annotation,
                depthMeasure,
                cameraPose,
                m_options,
                out finalCorners,
                out scaleX,
                out scaleY,
                out scaleZ)
                && ObjectronBoxValidation.HasValidExtents(finalCorners);

            if (modelOrientedOk
                && m_options.UseMaskWhenBadOrientation
                && m_options.IsBadWorldOrientation(finalCorners, cameraPose))
            {
                QuestObjectronLogger.BoxProjDebug(
                    "ORIENT_GATE rejected ModelOrientedMaskBox → MaskAlignedBox");
                modelOrientedOk = false;
            }

            if (modelOrientedOk)
            {
                scaleAvg = (scaleX + scaleY) * 0.5f;
                finalMethod = PlacementMethod.ModelOrientedMaskBox;
                if (m_options.EnableTableSnap
                    && ObjectronTablePlaneSnap.TrySnapBoxToTable(
                        m_raycast,
                        annotation.ObjectId,
                        finalCorners,
                        modelHalf,
                        out var snapped,
                        out _,
                        out _,
                        out _,
                        out _)
                    && ObjectronBoxValidation.HasValidExtents(snapped))
                {
                    finalCorners = snapped;
                    finalMethod = PlacementMethod.TableSnappedBox;
                }
            }
            else if (ObjectronBoxProjectionDebug.TryBuildMaskAlignedCorners(
                         depthMeasure,
                         cameraPose,
                         modelHalf.z,
                         out maskCorners)
                     && ObjectronBoxValidation.HasValidExtents(maskCorners))
            {
                finalCorners = maskCorners;
                finalMethod = PlacementMethod.MaskAlignedBox;
            }
            else if (modelScaled != null && ObjectronBoxValidation.HasValidExtents(modelScaled))
            {
                finalCorners = modelScaled;
                finalMethod = PlacementMethod.DepthRefinedBox;
            }
            else
            {
                ObjectronBoxProjectionDebug.LogComparison(
                    annotation.ObjectId,
                    normViewport,
                    m_cameraAccess,
                    depthMeasure,
                    annotation,
                    rawCorners,
                    rawCorners,
                    null,
                    scaleX,
                    scaleY,
                    scaleZ,
                    scaleAvg,
                    cameraPose,
                    placed.Method);
                return new PlacementOutput(
                    annotation.ObjectId,
                    rawCorners,
                    placed.Method,
                    BuildDebugReport(annotation, cameraPose, placed.Method, placed.Method, rawCenter, rawExtent,
                        rawCenter, rawExtent, scaleAvg, depthMeasure, true));
            }

            ObjectronBoxValidation.TryGetExtentMeters(finalCorners, out var refinedExtent);
            var refinedCenter = finalCorners[0];
            ObjectronBoxProjectionDebug.LogComparison(
                annotation.ObjectId,
                normViewport,
                m_cameraAccess,
                depthMeasure,
                annotation,
                rawCorners,
                finalCorners,
                modelScaled,
                scaleX,
                scaleY,
                scaleZ,
                scaleAvg,
                cameraPose,
                finalMethod);
            var debug = BuildDebugReport(
                annotation,
                cameraPose,
                placed.Method,
                finalMethod,
                rawCenter,
                rawExtent,
                refinedCenter,
                refinedExtent,
                scaleAvg,
                depthMeasure,
                true);
            return new PlacementOutput(annotation.ObjectId, finalCorners, finalMethod, debug);
        }

        private static ObjectronPlacementDebugReport BuildDebugReport(
            ObjectAnnotation annotation,
            Pose cameraPose,
            PlacementMethod rawMethod,
            PlacementMethod finalMethod,
            Vector3 rawCenter,
            float rawExtent,
            Vector3 refinedCenter,
            float refinedExtent,
            float uniformScale,
            DepthPlacementMeasure depthMeasure,
            bool depthMeasureOk)
        {
            var hmd = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            return new ObjectronPlacementDebugReport(
                annotation.ObjectId,
                rawMethod,
                finalMethod,
                cameraPose,
                ObjectronPlacementDebug.GetModelTranslationCam(annotation),
                ObjectronPlacementDebug.GetModelHalfExtents(annotation),
                ObjectronPlacementDebug.GetModelRotationEuler(annotation),
                rawCenter,
                rawExtent,
                refinedCenter,
                refinedExtent,
                uniformScale,
                depthMeasureOk,
                depthMeasure.RaycastHit,
                depthMeasure.RayDistanceM,
                depthMeasure.CameraLocalSizeM,
                depthMeasure.NormViewport,
                hmd,
                Vector3.Distance(rawCenter, refinedCenter),
                refinedExtent - rawExtent);
        }

        public PlacementResult TryPlaceAnnotation(ObjectAnnotation annotation, Pose cameraPose)
        {
            if (TryFromKeypoint3D(annotation, cameraPose, out var from3D))
            {
                return new PlacementResult(PlacementMethod.Keypoint3D, from3D);
            }

            if (TryFromKeypoint2D(annotation, cameraPose, out var from2D))
            {
                return new PlacementResult(PlacementMethod.Keypoint2DRaycast, from2D);
            }

            if (TryFromTranslationBox(annotation, cameraPose, out var fromPose))
            {
                return new PlacementResult(PlacementMethod.TranslationBox, fromPose);
            }

            return new PlacementResult(PlacementMethod.None, null);
        }

        /// <summary>Raw model pose only — skips keypoint raycasts and depth refinement.</summary>
        public PlacementResult TryPlaceTranslationBoxOnly(ObjectAnnotation annotation, Pose cameraPose)
        {
            if (TryFromTranslationBox(annotation, cameraPose, out var corners))
            {
                return new PlacementResult(PlacementMethod.TranslationBox, corners);
            }

            return new PlacementResult(PlacementMethod.None, null);
        }

        private bool TryFromKeypoint3D(ObjectAnnotation annotation, Pose cameraPose, out Vector3[] corners)
        {
            corners = null;
            if (annotation.Keypoints == null || annotation.Keypoints.Count < 1)
            {
                return false;
            }

            var buffer = new Vector3[9];
            var has = new bool[9];
            var count = 0;
            foreach (var kp in annotation.Keypoints)
            {
                if (kp.Point3D == null)
                {
                    continue;
                }

                var idx = kp.Id;
                if (idx < 0 || idx > 8)
                {
                    continue;
                }

                var cam = new Vector3(kp.Point3D.X, kp.Point3D.Y, kp.Point3D.Z);
                cam = ApplyCameraLocalPoint(cam);
                buffer[idx] = CameraToWorld(cam, cameraPose);
                if (!has[idx])
                {
                    count++;
                }

                has[idx] = true;
            }

            if (count < 4)
            {
                return false;
            }

            if (!has[0])
            {
                if (TryGetTranslation(annotation, out var t))
                {
                    buffer[0] = CameraToWorld(t, cameraPose);
                    has[0] = true;
                }
                else
                {
                    for (var i = 1; i <= 8; i++)
                    {
                        if (has[i])
                        {
                            buffer[0] = buffer[i];
                            has[0] = true;
                            break;
                        }
                    }
                }
            }

            if (!has[0])
            {
                return false;
            }

            if (!ObjectronBoxValidation.HasValidExtents(buffer))
            {
                QuestObjectronLogger.World(
                    $"objectId={annotation.ObjectId} keypoint3d degenerate — fallback to translation box");
                return false;
            }

            corners = buffer;
            return true;
        }

        private bool TryFromKeypoint2D(ObjectAnnotation annotation, Pose cameraPose, out Vector3[] corners)
        {
            corners = new Vector3[9];
            var has = new bool[9];
            if (annotation.Keypoints == null || annotation.Keypoints.Count < 4)
            {
                corners = null;
                return false;
            }

            var filled = 0;
            foreach (var kp in annotation.Keypoints)
            {
                if (kp.Point2D == null)
                {
                    continue;
                }

                var idx = kp.Id;
                if (idx < 0 || idx > 8)
                {
                    continue;
                }

                var x = m_options.MirrorInferenceHorizontal ? 1f - kp.Point2D.X : kp.Point2D.X;
                var uv = new Vector2(x, 1f - kp.Point2D.Y);
                var world = RaycastToWorld(uv, cameraPose);
                if (!world.HasValue)
                {
                    continue;
                }

                corners[idx] = world.Value;
                if (!has[idx])
                {
                    filled++;
                }

                has[idx] = true;
            }

            if (filled < 4)
            {
                corners = null;
                return false;
            }

            if (!has[0])
            {
                for (var i = 1; i <= 8; i++)
                {
                    if (has[i])
                    {
                        corners[0] = corners[i];
                        has[0] = true;
                        break;
                    }
                }
            }

            if (!has[0] || !ObjectronBoxValidation.HasValidExtents(corners))
            {
                corners = null;
                return false;
            }

            return true;
        }

        private bool TryFromTranslationBox(ObjectAnnotation annotation, Pose cameraPose, out Vector3[] corners)
        {
            corners = null;
            if (!TryGetTranslationRaw(annotation, out _))
            {
                return false;
            }

            var half = GetHalfExtents(annotation);
            GetProcessedPose(annotation, out var translationCam, out var rot);
            corners = new Vector3[9];
            corners[0] = CameraToWorld(translationCam, cameraPose);

            for (var i = 0; i < 8; i++)
            {
                var local = Vector3.Scale(s_localCornerUnit[i], half);
                var camPoint = translationCam + rot.MultiplyPoint3x4(local);
                corners[i + 1] = CameraToWorld(camPoint, cameraPose);
            }

            return true;
        }

        private Vector3 ApplyCameraLocalPoint(Vector3 mediaPipeCam)
        {
            m_options.ApplyAnnotationPose(
                mediaPipeCam,
                Matrix4x4.identity,
                out var cam,
                out _);
            return cam;
        }

        private void GetProcessedPose(ObjectAnnotation annotation, out Vector3 translationCam, out Matrix4x4 rotationCam)
        {
            TryGetTranslationRaw(annotation, out var rawT);
            var rawR = GetRotationMatrix(annotation);
            m_options.ApplyAnnotationPose(rawT, rawR, out translationCam, out rotationCam);
        }

        private static bool TryGetTranslationRaw(ObjectAnnotation annotation, out Vector3 translationCam)
        {
            translationCam = default;
            if (annotation.Translation == null || annotation.Translation.Count < 3)
            {
                return false;
            }

            translationCam = new Vector3(
                annotation.Translation[0],
                annotation.Translation[1],
                annotation.Translation[2]);
            return true;
        }

        private bool TryGetTranslation(ObjectAnnotation annotation, out Vector3 translationCam)
        {
            if (!TryGetTranslationRaw(annotation, out var raw))
            {
                translationCam = default;
                return false;
            }

            translationCam = ApplyCameraLocalPoint(raw);
            return true;
        }

        private static Vector3 GetHalfExtents(ObjectAnnotation annotation)
        {
            if (annotation.Scale != null && annotation.Scale.Count >= 3)
            {
                return new Vector3(
                    Mathf.Abs(annotation.Scale[0]) * 0.5f,
                    Mathf.Abs(annotation.Scale[1]) * 0.5f,
                    Mathf.Abs(annotation.Scale[2]) * 0.5f);
            }

            return new Vector3(0.04f, 0.04f, 0.06f);
        }

        private static Matrix4x4 GetRotationMatrix(ObjectAnnotation annotation)
        {
            if (annotation.Rotation == null || annotation.Rotation.Count < 9)
            {
                return Matrix4x4.identity;
            }

            var r = annotation.Rotation;
            return new Matrix4x4(
                new Vector4(r[0], r[1], r[2], 0f),
                new Vector4(r[3], r[4], r[5], 0f),
                new Vector4(r[6], r[7], r[8], 0f),
                new Vector4(0f, 0f, 0f, 1f));
        }

        private Vector3 CameraToWorld(Vector3 cameraLocalMeters, Pose cameraPose)
        {
            var pose = m_options.GetPlacementPose(cameraPose);
            return pose.position + pose.rotation * cameraLocalMeters;
        }

        private int GetStableSmoothKey(int mediaPipeObjectId, int annotationCount)
        {
            if (annotationCount > 1)
            {
                return mediaPipeObjectId;
            }

            m_lastMediaPipeObjectId = mediaPipeObjectId;
            return 0;
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

        private Vector3? RaycastToWorld(Vector2 normalizedViewport, Pose cameraPose)
        {
            var ray = m_cameraAccess.ViewportPointToRay(normalizedViewport, cameraPose);

            if (m_raycast != null && m_raycast.HasScenePermission())
            {
                var hit = m_raycast.Raycast(ray);
                if (hit.HasValue)
                {
                    return hit.Value;
                }
            }

            const float fallbackDistance = 0.75f;
            return ray.GetPoint(fallbackDistance);
        }
    }
}
