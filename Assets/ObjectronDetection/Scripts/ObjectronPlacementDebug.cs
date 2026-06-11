// Searchable placement diagnostics for logcat: adb logcat -s QuestObj3D | findstr PLACEMENT_DEBUG

using Mediapipe;
using UnityEngine;

namespace QuestObjectron
{
    public readonly struct ObjectronPlacementDebugReport
    {
        public readonly int ObjectId;
        public readonly PlacementMethod RawMethod;
        public readonly PlacementMethod FinalMethod;
        public readonly Pose CameraPose;
        public readonly Vector3 ModelTranslationCam;
        public readonly Vector3 ModelHalfExtents;
        public readonly Vector3 ModelRotationEuler;
        public readonly Vector3 RawCenterWorld;
        public readonly float RawExtentM;
        public readonly Vector3 RefinedCenterWorld;
        public readonly float RefinedExtentM;
        public readonly float UniformScale;
        public readonly bool DepthMeasureOk;
        public readonly bool DepthRaycastHit;
        public readonly float DepthRayM;
        public readonly Vector2 DepthCamSizeM;
        public readonly UnityEngine.Rect NormViewport;
        public readonly Vector3 HmdPosition;
        public readonly float CenterErrorM;
        public readonly float ExtentDeltaM;

        public ObjectronPlacementDebugReport(
            int objectId,
            PlacementMethod rawMethod,
            PlacementMethod finalMethod,
            Pose cameraPose,
            Vector3 modelTranslationCam,
            Vector3 modelHalfExtents,
            Vector3 modelRotationEuler,
            Vector3 rawCenterWorld,
            float rawExtentM,
            Vector3 refinedCenterWorld,
            float refinedExtentM,
            float uniformScale,
            bool depthMeasureOk,
            bool depthRaycastHit,
            float depthRayM,
            Vector2 depthCamSizeM,
            UnityEngine.Rect normViewport,
            Vector3 hmdPosition,
            float centerErrorM,
            float extentDeltaM)
        {
            ObjectId = objectId;
            RawMethod = rawMethod;
            FinalMethod = finalMethod;
            CameraPose = cameraPose;
            ModelTranslationCam = modelTranslationCam;
            ModelHalfExtents = modelHalfExtents;
            ModelRotationEuler = modelRotationEuler;
            RawCenterWorld = rawCenterWorld;
            RawExtentM = rawExtentM;
            RefinedCenterWorld = refinedCenterWorld;
            RefinedExtentM = refinedExtentM;
            UniformScale = uniformScale;
            DepthMeasureOk = depthMeasureOk;
            DepthRaycastHit = depthRaycastHit;
            DepthRayM = depthRayM;
            DepthCamSizeM = depthCamSizeM;
            NormViewport = normViewport;
            HmdPosition = hmdPosition;
            CenterErrorM = centerErrorM;
            ExtentDeltaM = extentDeltaM;
        }

        public string ToLogLine()
        {
            var camEuler = CameraPose.rotation.eulerAngles;
            var camFwd = CameraPose.rotation * Vector3.forward;
            var vp = NormViewport;
            return
                $"PLACEMENT_DEBUG id={ObjectId} raw={RawMethod} final={FinalMethod} " +
                $"cam_pos=({CameraPose.position.x:F2},{CameraPose.position.y:F2},{CameraPose.position.z:F2}) " +
                $"cam_euler=({camEuler.x:F0},{camEuler.y:F0},{camEuler.z:F0}) cam_fwd=({camFwd.x:F2},{camFwd.y:F2},{camFwd.z:F2}) " +
                $"model_t_cam=({ModelTranslationCam.x:F3},{ModelTranslationCam.y:F3},{ModelTranslationCam.z:F3}) " +
                $"model_half=({ModelHalfExtents.x:F3},{ModelHalfExtents.y:F3},{ModelHalfExtents.z:F3}) " +
                $"model_rot_euler=({ModelRotationEuler.x:F0},{ModelRotationEuler.y:F0},{ModelRotationEuler.z:F0}) " +
                $"norm_rect=({vp.x:F3},{vp.y:F3},{vp.width:F3},{vp.height:F3}) " +
                $"depth_ok={DepthMeasureOk} ray_hit={DepthRaycastHit} ray_m={DepthRayM:F3} depth_size_cam=({DepthCamSizeM.x:F3},{DepthCamSizeM.y:F3}) " +
                $"raw_center=({RawCenterWorld.x:F2},{RawCenterWorld.y:F2},{RawCenterWorld.z:F2}) raw_extent_m={RawExtentM:F3} " +
                $"refined_center=({RefinedCenterWorld.x:F2},{RefinedCenterWorld.y:F2},{RefinedCenterWorld.z:F2}) refined_extent_m={RefinedExtentM:F3} " +
                $"scale={UniformScale:F3} center_err_m={CenterErrorM:F3} extent_delta_m={ExtentDeltaM:F3} " +
                $"hmd=({HmdPosition.x:F2},{HmdPosition.y:F2},{HmdPosition.z:F2}) " +
                $"center_y_delta_hmd={(RefinedCenterWorld.y - HmdPosition.y):F3}";
        }
    }

    public static class ObjectronPlacementDebug
    {
        private static string s_lastLine = "";
        private static float s_lastLogTime = -999f;

        public static void LogIfChanged(ObjectronPlacementDebugReport report, float minIntervalSeconds = 0.35f)
        {
            var line = report.ToLogLine();
            var now = Time.realtimeSinceStartup;
            if (line == s_lastLine && now - s_lastLogTime < minIntervalSeconds)
            {
                return;
            }

            s_lastLine = line;
            s_lastLogTime = now;
            QuestObjectronLogger.PlacementDebug(line);
        }

        public static Vector3 GetModelHalfExtents(ObjectAnnotation annotation)
        {
            if (annotation?.Scale == null || annotation.Scale.Count < 3)
            {
                return new Vector3(0.08f, 0.08f, 0.12f);
            }

            return new Vector3(
                Mathf.Abs(annotation.Scale[0]) * 0.5f,
                Mathf.Abs(annotation.Scale[1]) * 0.5f,
                Mathf.Abs(annotation.Scale[2]) * 0.5f);
        }

        public static Vector3 GetModelRotationEuler(ObjectAnnotation annotation)
        {
            if (annotation?.Rotation == null || annotation.Rotation.Count < 9)
            {
                return Vector3.zero;
            }

            var r = annotation.Rotation;
            var m = new Matrix4x4(
                new Vector4(r[0], r[1], r[2], 0f),
                new Vector4(r[3], r[4], r[5], 0f),
                new Vector4(r[6], r[7], r[8], 0f),
                new Vector4(0f, 0f, 0f, 1f));
            return m.rotation.eulerAngles;
        }

        public static Vector3 GetModelTranslationCam(ObjectAnnotation annotation)
        {
            if (annotation?.Translation == null || annotation.Translation.Count < 3)
            {
                return Vector3.zero;
            }

            return new Vector3(annotation.Translation[0], annotation.Translation[1], annotation.Translation[2]);
        }
    }
}
