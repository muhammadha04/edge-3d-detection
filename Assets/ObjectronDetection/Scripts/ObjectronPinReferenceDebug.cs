// Logged when user pins the 3D box (B). Search logcat: PIN_REF_DEBUG

using Mediapipe;
using UnityEngine;

namespace QuestObjectron
{
    public readonly struct ObjectronPinSnapshot
    {
        public readonly int ObjectId;
        public readonly PlacementMethod Method;
        public readonly Pose CameraPose;
        public readonly Vector3[] Corners;
        public readonly Vector3 ModelTranslationCam;
        public readonly Vector3 ModelHalfExtents;
        public readonly Vector3 ModelRotationEuler;
        public readonly bool DepthOk;
        public readonly Vector2 DepthFrustumCamM;
        public readonly float DepthRayM;

        public ObjectronPinSnapshot(
            int objectId,
            PlacementMethod method,
            Pose cameraPose,
            Vector3[] corners,
            ObjectAnnotation annotation,
            ObjectronPlacementDebugReport? debugReport)
        {
            ObjectId = objectId;
            Method = method;
            CameraPose = cameraPose;
            Corners = corners != null ? (Vector3[])corners.Clone() : null;
            ModelTranslationCam = ObjectronPlacementDebug.GetModelTranslationCam(annotation);
            ModelHalfExtents = ObjectronPlacementDebug.GetModelHalfExtents(annotation);
            ModelRotationEuler = ObjectronPlacementDebug.GetModelRotationEuler(annotation);
            if (debugReport.HasValue)
            {
                var r = debugReport.Value;
                DepthOk = r.DepthMeasureOk;
                DepthFrustumCamM = r.DepthCamSizeM;
                DepthRayM = r.DepthRayM;
            }
            else
            {
                DepthOk = false;
                DepthFrustumCamM = default;
                DepthRayM = 0f;
            }
        }
    }

    public static class ObjectronPinReferenceDebug
    {
        public static void LogPinReference(ObjectronPinSnapshot snapshot)
        {
            if (snapshot.Corners == null || snapshot.Corners.Length < 9)
            {
                QuestObjectronLogger.BoxProjDebug("PIN_REF_DEBUG skipped — no corners");
                return;
            }

            var corners = snapshot.Corners;
            var camEuler = snapshot.CameraPose.rotation.eulerAngles;
            var camFwd = snapshot.CameraPose.rotation * Vector3.forward;
            var camUp = snapshot.CameraPose.rotation * Vector3.up;

            ObjectronBoxValidation.TryGetExtentMeters(corners, out var extent);
            ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(corners, out var edges);
            ObjectronBoxMetrics.TryGetCameraPlaneEdgeLengthsMeters(corners, snapshot.CameraPose, out var camPlane);

            QuestObjectronLogger.BoxProjDebug($"PIN_REF_DEBUG === user pinned id={snapshot.ObjectId} method={snapshot.Method} ===");
            QuestObjectronLogger.BoxProjDebug(
                $"PIN_REF_CAMERA pos=({snapshot.CameraPose.position.x:F3},{snapshot.CameraPose.position.y:F3},{snapshot.CameraPose.position.z:F3}) " +
                $"euler=({camEuler.x:F0},{camEuler.y:F0},{camEuler.z:F0}) fwd=({camFwd.x:F2},{camFwd.y:F2},{camFwd.z:F2})");
            QuestObjectronLogger.BoxProjDebug(
                $"PIN_REF_MODEL t_cam=({snapshot.ModelTranslationCam.x:F3},{snapshot.ModelTranslationCam.y:F3},{snapshot.ModelTranslationCam.z:F3}) " +
                $"half=({snapshot.ModelHalfExtents.x:F3},{snapshot.ModelHalfExtents.y:F3},{snapshot.ModelHalfExtents.z:F3}) " +
                $"rot_euler=({snapshot.ModelRotationEuler.x:F1},{snapshot.ModelRotationEuler.y:F1},{snapshot.ModelRotationEuler.z:F1})");
            QuestObjectronLogger.BoxProjDebug(
                $"PIN_REF_VIZ center=({corners[0].x:F3},{corners[0].y:F3},{corners[0].z:F3}) extent_m={extent:F3} " +
                $"edges_xyz=({edges.x:F3},{edges.y:F3},{edges.z:F3}) cam_plane_wh=({camPlane.x:F3},{camPlane.y:F3})");
            if (snapshot.DepthOk)
            {
                QuestObjectronLogger.BoxProjDebug(
                    $"PIN_REF_DEPTH frustum_cam=({snapshot.DepthFrustumCamM.x:F3},{snapshot.DepthFrustumCamM.y:F3}) ray_m={snapshot.DepthRayM:F3} " +
                    $"cam_plane_vs_frustum w={ObjectronBoxMetrics.OversizePercent(camPlane.x, snapshot.DepthFrustumCamM.x):F1}% " +
                    $"h={ObjectronBoxMetrics.OversizePercent(camPlane.y, snapshot.DepthFrustumCamM.y):F1}%");
            }

            LogCornerRow("PIN_REF_CORNERS", corners);
            LogTableFace(corners);

            var modelRot = GetModelRotationWorld(snapshot.ModelRotationEuler, snapshot.CameraPose);
            var modelUp = modelRot * Vector3.up;
            var modelFwd = modelRot * Vector3.forward;
            QuestObjectronLogger.BoxProjDebug(
                $"PIN_REF_ORIENT model_up_world=({modelUp.x:F2},{modelUp.y:F2},{modelUp.z:F2}) " +
                $"dot_up_world={Vector3.Dot(modelUp, Vector3.up):F2} dot_up_camFwd={Vector3.Dot(modelUp, camFwd):F2} " +
                $"dot_fwd_camFwd={Vector3.Dot(modelFwd, camFwd):F2}");
            QuestObjectronLogger.BoxProjDebug(
                "PIN_REF_HINT blue_dots=table_face (lowest dot with world up); compare to cup base when pinned");
        }

        private static void LogCornerRow(string tag, Vector3[] corners)
        {
            for (var i = 1; i <= 8; i++)
            {
                var c = corners[i];
                QuestObjectronLogger.BoxProjDebug(
                    $"{tag} c{i}=({c.x:F3},{c.y:F3},{c.z:F3}) y={c.y:F3}");
            }
        }

        private static void LogTableFace(Vector3[] corners)
        {
            if (!ObjectronBoxGeometry.TryGetTableCornerIndices(corners, out var table))
            {
                return;
            }

            var normal = ObjectronBoxGeometry.GetFaceOutwardNormal(corners, table);
            QuestObjectronLogger.BoxProjDebug(
                $"PIN_REF_TABLE_FACE indices={table[0]},{table[1]},{table[2]},{table[3]} " +
                $"outward=({normal.x:F2},{normal.y:F2},{normal.z:F2}) dot_outward_up={Vector3.Dot(normal, Vector3.up):F2} (want negative)");

            for (var i = 0; i < 4; i++)
            {
                var idx = table[i];
                var c = corners[idx];
                QuestObjectronLogger.BoxProjDebug(
                    $"PIN_REF_TABLE_CORNER i={idx} pos=({c.x:F3},{c.y:F3},{c.z:F3}) (blue dot target)");
            }

            var axisRight = ObjectronBoxGeometry.GetBoxAxisWorld(corners, table[0], table[1]);
            var axisOther = ObjectronBoxGeometry.GetBoxAxisWorld(corners, table[0], table[2]);
            QuestObjectronLogger.BoxProjDebug(
                $"PIN_REF_TABLE_EDGES e01=({axisRight.x:F2},{axisRight.y:F2},{axisRight.z:F2}) " +
                $"e02=({axisOther.x:F2},{axisOther.y:F2},{axisOther.z:F2})");
        }

        private static Quaternion GetModelRotationWorld(Vector3 modelEuler, Pose cameraPose)
        {
            return cameraPose.rotation * Quaternion.Euler(modelEuler);
        }
    }
}
