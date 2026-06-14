// Depth / scene-ray refinement for Objectron 3D boxes (Meta MultiObjectDetection sizing pattern).

using Mediapipe;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public readonly struct DepthPlacementMeasure
    {
        public readonly bool Success;
        public readonly bool RaycastHit;
        public readonly float RayDistanceM;
        public readonly Vector3 WorldCenter;
        public readonly Vector2 CameraLocalSizeM;
        public readonly UnityEngine.Rect NormViewport;

        public DepthPlacementMeasure(
            bool success,
            bool raycastHit,
            float rayDistanceM,
            Vector3 worldCenter,
            Vector2 cameraLocalSizeM,
            UnityEngine.Rect normViewport)
        {
            Success = success;
            RaycastHit = raycastHit;
            RayDistanceM = rayDistanceM;
            WorldCenter = worldCenter;
            CameraLocalSizeM = cameraLocalSizeM;
            NormViewport = normViewport;
        }
    }

    public static class ObjectronDepthPlacementRefiner
    {
        public static bool TryGetViewportRectFromKeypoints(
            ObjectAnnotation annotation,
            bool mirrorHorizontal,
            out UnityEngine.Rect normViewport)
        {
            normViewport = default;
            if (annotation?.Keypoints == null || annotation.Keypoints.Count == 0)
            {
                return false;
            }

            var minX = 1f;
            var minY = 1f;
            var maxX = 0f;
            var maxY = 0f;
            var count = 0;
            foreach (var kp in annotation.Keypoints)
            {
                if (kp.Point2D == null)
                {
                    continue;
                }

                var x = mirrorHorizontal ? 1f - kp.Point2D.X : kp.Point2D.X;
                var y = kp.Point2D.Y;
                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
                count++;
            }

            if (count < 2)
            {
                return false;
            }

            normViewport = new UnityEngine.Rect(minX, 1f - maxY, maxX - minX, maxY - minY);
            return normViewport.width > 0.001f && normViewport.height > 0.001f;
        }

        public static UnityEngine.Rect OverlayRectToNormViewport(ObjectronOverlayRect rect, bool mirrorHorizontal)
        {
            var xmin = rect.XCenter - rect.Width * 0.5f;
            var ymin = rect.YCenter - rect.Height * 0.5f;
            if (mirrorHorizontal)
            {
                xmin = 1f - xmin - rect.Width;
            }

            return new UnityEngine.Rect(xmin, 1f - ymin - rect.Height, rect.Width, rect.Height);
        }

        /// <summary>
        /// Scene raycast at 2D box center + plane intersection for physical width/height (camera-local meters).
        /// </summary>
        public static bool TryMeasureViewportRect(
            UnityEngine.Rect normViewport,
            PassthroughCameraAccess cameraAccess,
            EnvironmentRayCastSampleManager raycast,
            Pose cameraPose,
            out DepthPlacementMeasure measure)
        {
            measure = default;
            if (cameraAccess == null || !cameraAccess.IsPlaying)
            {
                return false;
            }

            var centerVp = normViewport.center;
            var centerRay = cameraAccess.ViewportPointToRay(centerVp, cameraPose);
            Vector3? hitWorld = null;
            if (raycast != null && raycast.HasScenePermission())
            {
                hitWorld = raycast.Raycast(centerRay);
            }

            var raycastHit = hitWorld.HasValue;
            float distance;
            if (raycastHit)
            {
                distance = Vector3.Distance(cameraPose.position, hitWorld.Value);
            }
            else
            {
                distance = 0.75f;
            }

            var worldCenter = raycastHit ? hitWorld.Value : centerRay.GetPoint(distance);
            var normal = (worldCenter - cameraPose.position).normalized;
            var plane = new Plane(normal, worldCenter);

            var minRay = cameraAccess.ViewportPointToRay(normViewport.min, cameraPose);
            var maxRay = cameraAccess.ViewportPointToRay(normViewport.max, cameraPose);
            if (!plane.Raycast(minRay, out var tMin) || !plane.Raycast(maxRay, out var tMax))
            {
                return false;
            }

            var minWorld = minRay.GetPoint(tMin);
            var maxWorld = maxRay.GetPoint(tMax);
            var invRot = Quaternion.Inverse(cameraPose.rotation);
            var topLeftLocal = invRot * (minWorld - cameraPose.position);
            var bottomRightLocal = invRot * (maxWorld - cameraPose.position);
            var size = new Vector2(
                Mathf.Abs(bottomRightLocal.x - topLeftLocal.x),
                Mathf.Abs(bottomRightLocal.y - topLeftLocal.y));

            if (size.x < 0.01f || size.y < 0.01f)
            {
                return false;
            }

            measure = new DepthPlacementMeasure(
                true,
                raycastHit,
                distance,
                worldCenter,
                size,
                normViewport);
            return true;
        }

        /// <summary>
        /// Objectron pose in camera space, re-centered on depth hit, scaled so oriented box projects to the 2D mask frustum.
        /// </summary>
        public static bool TryBuildModelOrientedMaskBox(
            ObjectAnnotation annotation,
            DepthPlacementMeasure depth,
            Pose cameraPose,
            ObjectronPlacementOptions options,
            out Vector3[] corners,
            out float scaleX,
            out float scaleY,
            out float scaleZ)
        {
            corners = null;
            scaleX = 1f;
            scaleY = 1f;
            scaleZ = 1f;
            options ??= new ObjectronPlacementOptions();
            if (!depth.Success)
            {
                return false;
            }

            var half = GetHalfExtents(annotation);
            var rotRaw = GetRotationMatrix(annotation);

            if (options.UseUnityCameraFrame)
            {
                var rotUnity = GetProcessedRotation(rotRaw, options, useUnityFrame: true);
                if (!TryBuildModelOrientedMaskBoxVariant(
                        half,
                        rotUnity,
                        depth,
                        cameraPose,
                        options,
                        out corners,
                        out scaleX,
                        out scaleY,
                        out scaleZ,
                        out _))
                {
                    return false;
                }

                LogFrameCorrect("unity_frame", rotUnity, cameraPose, 0f, 0f);
                return ObjectronBoxValidation.HasValidExtents(corners);
            }

            if (!options.AutoPickLegacyRotationFrame)
            {
                var rotRawOnly = GetProcessedRotation(rotRaw, options, useUnityFrame: false);
                if (!TryBuildModelOrientedMaskBoxVariant(
                        half,
                        rotRawOnly,
                        depth,
                        cameraPose,
                        options,
                        out corners,
                        out scaleX,
                        out scaleY,
                        out scaleZ,
                        out _))
                {
                    return false;
                }

                LogFrameCorrect("mediapipe_raw", rotRawOnly, cameraPose, 0f, 0f);
                return ObjectronBoxValidation.HasValidExtents(corners);
            }

            var scoreRaw = float.NegativeInfinity;
            var scoreFlip = float.NegativeInfinity;
            Vector3[] cornersRaw = null;
            Vector3[] cornersFlip = null;
            var sxRaw = 1f;
            var syRaw = 1f;
            var szRaw = 1f;
            var sxFlip = 1f;
            var syFlip = 1f;
            var szFlip = 1f;

            var rotLegacyRaw = GetProcessedRotation(rotRaw, options, useUnityFrame: false);
            if (TryBuildModelOrientedMaskBoxVariant(
                    half,
                    rotLegacyRaw,
                    depth,
                    cameraPose,
                    options,
                    out cornersRaw,
                    out sxRaw,
                    out syRaw,
                    out szRaw,
                    out scoreRaw))
            {
                var rotWorld = cameraPose.rotation * rotLegacyRaw.rotation;
                scoreRaw += ObjectronMediaPipeCameraFrame.OrientationScore(rotWorld, cameraPose);
            }

            var rotLegacyFlip = GetProcessedRotation(
                ObjectronMediaPipeCameraFrame.ToUnityCameraLocal(rotRaw),
                options,
                useUnityFrame: false);
            if (TryBuildModelOrientedMaskBoxVariant(
                    half,
                    rotLegacyFlip,
                    depth,
                    cameraPose,
                    options,
                    out cornersFlip,
                    out sxFlip,
                    out syFlip,
                    out szFlip,
                    out scoreFlip))
            {
                var rotWorld = cameraPose.rotation * rotLegacyFlip.rotation;
                scoreFlip += ObjectronMediaPipeCameraFrame.OrientationScore(rotWorld, cameraPose);
            }

            var useFlip = scoreFlip > scoreRaw + 0.05f;
            var picked = useFlip ? cornersFlip : cornersRaw;
            if (picked == null)
            {
                picked = useFlip ? cornersRaw : cornersFlip;
                useFlip = !useFlip;
            }

            if (picked == null)
            {
                return false;
            }

            corners = picked;
            if (useFlip)
            {
                scaleX = sxFlip;
                scaleY = syFlip;
                scaleZ = szFlip;
            }
            else
            {
                scaleX = sxRaw;
                scaleY = syRaw;
                scaleZ = szRaw;
            }

            if (Mathf.Abs(scoreRaw - scoreFlip) > 0.05f)
            {
                LogFrameCorrect(
                    useFlip ? "yz_flip" : "raw",
                    useFlip ? rotLegacyFlip : rotLegacyRaw,
                    cameraPose,
                    scoreRaw,
                    scoreFlip);
            }

            return ObjectronBoxValidation.HasValidExtents(corners);
        }

        private static Matrix4x4 GetProcessedRotation(
            Matrix4x4 rawRotation,
            ObjectronPlacementOptions options,
            bool useUnityFrame)
        {
            options ??= new ObjectronPlacementOptions();
            if (useUnityFrame)
            {
                options.ApplyAnnotationPose(Vector3.zero, rawRotation, out _, out var rot);
                return rot;
            }

            if (options.Mirror3DLocalXWhenFlipped && options.MirrorInferenceHorizontal)
            {
                return ObjectronMediaPipeCameraFrame.MirrorCameraLocalX(rawRotation);
            }

            return rawRotation;
        }

        private static void LogFrameCorrect(
            string branch,
            Matrix4x4 rotCam,
            Pose cameraPose,
            float scoreRaw,
            float scoreFlip)
        {
            var rotWorld = cameraPose.rotation * rotCam.rotation;
            var modelUp = rotWorld * Vector3.up;
            var camFwd = cameraPose.rotation * Vector3.forward;
            var scorePart = scoreRaw != 0f || scoreFlip != 0f
                ? $" score_raw={scoreRaw:F2} score_flip={scoreFlip:F2}"
                : "";
            QuestObjectronLogger.BoxProjDebug(
                $"FRAME_CORRECT picked={branch}{scorePart} " +
                $"dot_up_world={Vector3.Dot(modelUp, Vector3.up):F2} dot_up_camFwd={Vector3.Dot(modelUp, camFwd):F2}");
        }

        private static bool TryBuildModelOrientedMaskBoxVariant(
            Vector3 half,
            Matrix4x4 rotCam,
            DepthPlacementMeasure depth,
            Pose cameraPose,
            ObjectronPlacementOptions options,
            out Vector3[] corners,
            out float scaleX,
            out float scaleY,
            out float scaleZ,
            out float camPlaneFitScore)
        {
            corners = null;
            scaleX = 1f;
            scaleY = 1f;
            scaleZ = 1f;
            camPlaneFitScore = float.NegativeInfinity;

            options ??= new ObjectronPlacementOptions();
            var rotWorld = cameraPose.rotation * rotCam.rotation;
            var camRight = cameraPose.rotation * Vector3.right;
            var camUp = cameraPose.rotation * Vector3.up;

            var projHalfW = ProjectedHalfExtent(half, rotWorld, camRight);
            var projHalfH = ProjectedHalfExtent(half, rotWorld, camUp);
            if (projHalfW < 0.001f || projHalfH < 0.001f)
            {
                return false;
            }

            var targetHalfW = depth.CameraLocalSizeM.x * 0.5f;
            var targetHalfH = depth.CameraLocalSizeM.y * 0.5f;
            scaleX = Mathf.Clamp(targetHalfW / projHalfW, 0.35f, 2.5f);
            scaleY = Mathf.Clamp(targetHalfH / projHalfH, 0.35f, 2.5f);
            var avgScale = (scaleX + scaleY) * 0.5f;
            var scaleAsym = Mathf.Abs(scaleX - scaleY) / Mathf.Max(avgScale, 0.01f);
            if (scaleAsym > 0.28f)
            {
                scaleX = avgScale;
                scaleY = avgScale;
            }

            scaleZ = Mathf.Clamp(avgScale, 0.35f, 2.5f);

            var scaledHalf = new Vector3(half.x * scaleX, half.y * scaleY, half.z * scaleZ);
            var placementPose = options.GetPlacementPose(cameraPose);
            var depthTranslationCam = Quaternion.Inverse(placementPose.rotation)
                * (depth.WorldCenter - placementPose.position);

            corners = new Vector3[9];
            corners[0] = depth.WorldCenter;
            for (var i = 0; i < 8; i++)
            {
                var local = Vector3.Scale(s_localCornerUnit[i], scaledHalf);
                var camPoint = depthTranslationCam + rotCam.MultiplyPoint3x4(local);
                corners[i + 1] = ObjectronWorldOrientation.CameraLocalToWorld(
                    camPoint,
                    cameraPose,
                    options.CompensateHeadRoll);
            }

            if (!ObjectronBoxValidation.HasValidExtents(corners))
            {
                corners = null;
                return false;
            }

            if (ObjectronBoxMetrics.TryGetCameraPlaneEdgeLengthsMeters(corners, cameraPose, out var plane))
            {
                var fitW = 1f - Mathf.Abs(plane.x - depth.CameraLocalSizeM.x) / Mathf.Max(depth.CameraLocalSizeM.x, 0.01f);
                var fitH = 1f - Mathf.Abs(plane.y - depth.CameraLocalSizeM.y) / Mathf.Max(depth.CameraLocalSizeM.y, 0.01f);
                camPlaneFitScore = Mathf.Clamp(fitW, -1f, 1f) + Mathf.Clamp(fitH, -1f, 1f);
            }

            return true;
        }

        /// <summary>
        /// Re-center and scale model-oriented box to match depth-measured size on the view plane.
        /// </summary>
        public static bool TryRefineOrientedBox(
            ObjectAnnotation annotation,
            Vector3[] rawCorners,
            DepthPlacementMeasure depth,
            Pose cameraPose,
            ObjectronPlacementOptions options,
            out Vector3[] refinedCorners,
            out float uniformScaleAvg,
            out float scaleX,
            out float scaleY,
            out float scaleZ)
        {
            refinedCorners = null;
            uniformScaleAvg = 1f;
            scaleX = 1f;
            scaleY = 1f;
            scaleZ = 1f;
            if (rawCorners == null || rawCorners.Length < 9 || !depth.Success)
            {
                return false;
            }

            if (!TryBuildModelOrientedMaskBox(
                    annotation,
                    depth,
                    cameraPose,
                    options,
                    out refinedCorners,
                    out scaleX,
                    out scaleY,
                    out scaleZ))
            {
                return false;
            }

            uniformScaleAvg = (scaleX + scaleY) * 0.5f;
            return true;
        }

        private static float ProjectedHalfExtent(Vector3 half, Quaternion rotWorld, Vector3 camAxis)
        {
            var axisX = rotWorld * Vector3.right;
            var axisY = rotWorld * Vector3.up;
            var axisZ = rotWorld * Vector3.forward;
            return Mathf.Abs(Vector3.Dot(axisX, camAxis)) * half.x
                   + Mathf.Abs(Vector3.Dot(axisY, camAxis)) * half.y
                   + Mathf.Abs(Vector3.Dot(axisZ, camAxis)) * half.z;
        }

        private static readonly Vector3[] s_localCornerUnit =
        {
            new(-1f, -1f, -1f), new(1f, -1f, -1f), new(-1f, 1f, -1f), new(1f, 1f, -1f),
            new(-1f, -1f, 1f), new(1f, -1f, 1f), new(-1f, 1f, 1f), new(1f, 1f, 1f),
        };

        private static bool TryGetTranslation(ObjectAnnotation annotation, out Vector3 translationCam)
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

    }
}
