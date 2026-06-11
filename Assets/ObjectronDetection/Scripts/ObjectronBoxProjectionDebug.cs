// Verbose 2D mask → 3D box comparison. Search logcat: BOX_PROJ_DEBUG

using Mediapipe;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronBoxProjectionDebug
    {
        private static string s_lastKey = "";
        private static float s_lastLogTime = -999f;

        public static void LogComparison(
            int objectId,
            UnityEngine.Rect normViewport,
            PassthroughCameraAccess cameraAccess,
            DepthPlacementMeasure depth,
            ObjectAnnotation annotation,
            Vector3[] rawCorners,
            Vector3[] finalCorners,
            Vector3[] modelScaledCorners,
            float scaleX,
            float scaleY,
            float scaleZ,
            float uniformScaleAvg,
            Pose cameraPose,
            PlacementMethod finalMethod,
            float minIntervalSeconds = 0.25f)
        {
            if (finalCorners == null || finalCorners.Length < 9)
            {
                return;
            }

            var key = $"{objectId}|{finalMethod}|{normViewport}|{depth.RayDistanceM:F3}";
            var now = Time.realtimeSinceStartup;
            if (key == s_lastKey && now - s_lastLogTime < minIntervalSeconds)
            {
                return;
            }

            s_lastKey = key;
            s_lastLogTime = now;

            var half = ObjectronPlacementDebug.GetModelHalfExtents(annotation);
            var modelFullCam = half * 2f;
            var kp2d = CountKeypoints2D(annotation);

            var pxRect = "n/a";
            if (cameraAccess != null && cameraAccess.IsPlaying)
            {
                var res = cameraAccess.CurrentResolution;
                var px = new UnityEngine.Rect(
                    normViewport.x * res.x,
                    normViewport.y * res.y,
                    normViewport.width * res.x,
                    normViewport.height * res.y);
                pxRect = $"({px.x:F0},{px.y:F0},{px.width:F0}x{px.height:F0}) res={res.x:F0}x{res.y:F0}";
            }

            ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(rawCorners, out var rawEdges);
            ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(finalCorners, out var finalEdges);
            ObjectronBoxMetrics.TryGetCameraPlaneEdgeLengthsMeters(finalCorners, cameraPose, out var finalCamPlane);

            Vector3 maskEdges = default;
            Vector3 maskCamPlane = default;
            var maskOk = TryBuildMaskAlignedCorners(depth, cameraPose, half.z, out var maskCorners)
                && ObjectronBoxMetrics.TryGetCameraPlaneEdgeLengthsMeters(maskCorners, cameraPose, out maskCamPlane);

            if (maskOk)
            {
                ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(maskCorners, out maskEdges);
            }

            var oversizeRotX = maskOk ? ObjectronBoxMetrics.OversizePercent(finalEdges.x, maskEdges.x) : 0f;
            var oversizeCamX = maskOk
                ? ObjectronBoxMetrics.OversizePercent(finalCamPlane.x, depth.CameraLocalSizeM.x)
                : 0f;
            var oversizeCamY = maskOk
                ? ObjectronBoxMetrics.OversizePercent(finalCamPlane.y, depth.CameraLocalSizeM.y)
                : 0f;

            QuestObjectronLogger.BoxProjDebug($"=== id={objectId} final={finalMethod} ===");
            QuestObjectronLogger.BoxProjDebug(
                $"2D_MASK norm=({normViewport.x:F4},{normViewport.y:F4},{normViewport.width:F4}x{normViewport.height:F4}) px={pxRect}");
            QuestObjectronLogger.BoxProjDebug(
                $"DEPTH_FRUSTUM ray_hit={depth.RaycastHit} ray_m={depth.RayDistanceM:F4} frustum_cam_m=({depth.CameraLocalSizeM.x:F4},{depth.CameraLocalSizeM.y:F4}) " +
                $"center_world=({depth.WorldCenter.x:F3},{depth.WorldCenter.y:F3},{depth.WorldCenter.z:F3})");
            QuestObjectronLogger.BoxProjDebug(
                $"MODEL_CAM full_m=({modelFullCam.x:F4},{modelFullCam.y:F4},{modelFullCam.z:F4}) half=({half.x:F4},{half.y:F4},{half.z:F4}) " +
                $"rot_euler=({ObjectronPlacementDebug.GetModelRotationEuler(annotation)}) kp2d={kp2d}");
            QuestObjectronLogger.BoxProjDebug(
                $"SCALE scale_x={scaleX:F4} scale_y={scaleY:F4} scale_z={scaleZ:F4} avg={uniformScaleAvg:F4} " +
                $"(depth/model width={SafeRatio(depth.CameraLocalSizeM.x, modelFullCam.x):F3} height={SafeRatio(depth.CameraLocalSizeM.y, modelFullCam.y):F3})");
            QuestObjectronLogger.BoxProjDebug(
                $"EDGES_M raw=({rawEdges.x:F4},{rawEdges.y:F4},{rawEdges.z:F4}) final=({finalEdges.x:F4},{finalEdges.y:F4},{finalEdges.z:F4}) " +
                $"mask=({(maskOk ? $"{maskEdges.x:F4},{maskEdges.y:F4},{maskEdges.z:F4}" : "n/a")})");
            QuestObjectronLogger.BoxProjDebug(
                $"CAM_PLANE_M final=({finalCamPlane.x:F4},{finalCamPlane.y:F4}) target_frustum=({depth.CameraLocalSizeM.x:F4},{depth.CameraLocalSizeM.y:F4}) " +
                $"oversize_cam_plane width={oversizeCamX:F1}% height={oversizeCamY:F1}% (use this; target ~0%)");
            QuestObjectronLogger.BoxProjDebug(
                $"OVERSIZE_% rotated_edges_vs_mask width={oversizeRotX:F1}% (misleading if model is tilted — ignore when |rot|>15%)");
            if (modelScaledCorners != null)
            {
                ObjectronBoxMetrics.TryGetCameraPlaneEdgeLengthsMeters(modelScaledCorners, cameraPose, out var modelCam);
                QuestObjectronLogger.BoxProjDebug(
                    $"MODEL_SCALED_CAM_PLANE=({modelCam.x:F4},{modelCam.y:F4}) scale=({scaleX:F2},{scaleY:F2},{scaleZ:F2})");
            }

            LogOrientation(finalMethod, annotation, cameraPose, finalCorners);
        }

        private static void LogOrientation(
            PlacementMethod finalMethod,
            ObjectAnnotation annotation,
            Pose cameraPose,
            Vector3[] finalCorners)
        {
            var camEuler = cameraPose.rotation.eulerAngles;
            var camUp = cameraPose.rotation * Vector3.up;
            var camFwd = cameraPose.rotation * Vector3.forward;
            var pitchFromHorizon = Vector3.Angle(camFwd, Vector3.ProjectOnPlane(camFwd, Vector3.up));

            if (finalMethod == PlacementMethod.ModelOrientedMaskBox
                || finalMethod == PlacementMethod.DepthRefinedBox)
            {
                var modelRot = GetModelRotationWorld(annotation, cameraPose);
                var modelUp = modelRot * Vector3.up;
                QuestObjectronLogger.BoxProjDebug(
                    $"ORIENT_MODEL cam_euler=({camEuler.x:F0},{camEuler.y:F0},{camEuler.z:F0}) pitch_from_horizon={pitchFromHorizon:F0}deg " +
                    $"model_up_world=({modelUp.x:F2},{modelUp.y:F2},{modelUp.z:F2}) " +
                    $"dot_up_world={Vector3.Dot(modelUp, Vector3.up):F2} dot_up_camFwd={Vector3.Dot(modelUp, camFwd):F2} " +
                    $"(up~1 floor-at-you~|camFwd| high)");
                return;
            }

            if (finalMethod == PlacementMethod.MaskAlignedBox)
            {
                var boxUp = (finalCorners[3] - finalCorners[1]).normalized;
                QuestObjectronLogger.BoxProjDebug(
                    $"ORIENT_MASK cam_up=({camUp.x:F2},{camUp.y:F2},{camUp.z:F2}) box_up=({boxUp.x:F2},{boxUp.y:F2},{boxUp.z:F2}) " +
                    $"dot_boxUp_worldUp={Vector3.Dot(boxUp, Vector3.up):F2} pitch={pitchFromHorizon:F0}deg (camera-aligned; face-at-you)");
            }
        }

        private static Quaternion GetModelRotationWorld(ObjectAnnotation annotation, Pose cameraPose)
        {
            if (annotation?.Rotation == null || annotation.Rotation.Count < 9)
            {
                return cameraPose.rotation;
            }

            var r = annotation.Rotation;
            var rotCam = new Matrix4x4(
                new Vector4(r[0], r[1], r[2], 0f),
                new Vector4(r[3], r[4], r[5], 0f),
                new Vector4(r[6], r[7], r[8], 0f),
                new Vector4(0f, 0f, 0f, 1f));
            return cameraPose.rotation * rotCam.rotation;
        }

        /// <summary>Camera-right/up/forward box from 2D mask frustum — reference for projection check.</summary>
        public static bool TryBuildMaskAlignedCorners(
            DepthPlacementMeasure depth,
            Pose cameraPose,
            float halfDepthMeters,
            out Vector3[] corners,
            bool compensateHeadRoll = false)
        {
            corners = null;
            if (!depth.Success)
            {
                return false;
            }

            var orientRot = compensateHeadRoll
                ? ObjectronWorldOrientation.RemoveCameraRoll(cameraPose.rotation)
                : cameraPose.rotation;
            var right = orientRot * Vector3.right;
            var up = orientRot * Vector3.up;
            var fwd = orientRot * Vector3.forward;
            var hw = depth.CameraLocalSizeM.x * 0.5f;
            var hh = depth.CameraLocalSizeM.y * 0.5f;
            var hd = Mathf.Max(halfDepthMeters, 0.01f);
            var c = depth.WorldCenter;

            corners = new Vector3[9];
            corners[0] = c;
            corners[1] = c - right * hw - up * hh - fwd * hd;
            corners[2] = c + right * hw - up * hh - fwd * hd;
            corners[3] = c - right * hw + up * hh - fwd * hd;
            corners[4] = c + right * hw + up * hh - fwd * hd;
            corners[5] = c - right * hw - up * hh + fwd * hd;
            corners[6] = c + right * hw - up * hh + fwd * hd;
            corners[7] = c - right * hw + up * hh + fwd * hd;
            corners[8] = c + right * hw + up * hh + fwd * hd;
            return true;
        }

        private static float SafeRatio(float a, float b) => b > 0.001f ? a / b : 0f;

        private static int CountKeypoints2D(ObjectAnnotation annotation)
        {
            if (annotation?.Keypoints == null)
            {
                return 0;
            }

            var n = 0;
            foreach (var kp in annotation.Keypoints)
            {
                if (kp.Point2D != null)
                {
                    n++;
                }
            }

            return n;
        }
    }
}
