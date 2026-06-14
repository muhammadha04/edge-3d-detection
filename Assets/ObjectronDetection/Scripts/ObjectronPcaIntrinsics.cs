// Quest PCA camera intrinsics for MediaPipe Objectron projection.
// See PassthroughCameraAccess.Intrinsics (FocalLength, PrincipalPoint, SensorResolution):
// https://developers.meta.com/horizon/reference/mruk/v85/class_meta_x_r_passthrough_camera_access/

using Meta.XR;
using UnityEngine;

namespace QuestObjectron
{
    public readonly struct PcaIntrinsicsSnapshot
    {
        public readonly Vector2 FocalLengthPx;
        public readonly Vector2 PrincipalPointPx;
        public readonly Vector2Int Resolution;
        public readonly Vector2 NdcFocal;
        public readonly Vector2 NdcPrincipal;

        public PcaIntrinsicsSnapshot(
            Vector2 focalLengthPx,
            Vector2 principalPointPx,
            Vector2Int resolution,
            Vector2 ndcFocal,
            Vector2 ndcPrincipal)
        {
            FocalLengthPx = focalLengthPx;
            PrincipalPointPx = principalPointPx;
            Resolution = resolution;
            NdcFocal = ndcFocal;
            NdcPrincipal = ndcPrincipal;
        }
    }

    public static class ObjectronPcaIntrinsics
    {
        private static PcaIntrinsicsSnapshot? s_cached;
        private static bool s_loggedOnce;

        /// <summary>
        /// Reads static sensor intrinsics once PCA is playing.
        /// Values are in pixel space for the current inference resolution.
        /// </summary>
        public static bool TryGetSnapshot(
            PassthroughCameraAccess cameraAccess,
            out PcaIntrinsicsSnapshot snapshot)
        {
            snapshot = default;
            if (cameraAccess == null || !cameraAccess.IsPlaying)
            {
                return false;
            }

            var intrinsics = cameraAccess.Intrinsics;
            var res = cameraAccess.CurrentResolution;
            var w = Mathf.Max(1, res.x);
            var h = Mathf.Max(1, res.y);

            var focalPx = intrinsics.FocalLength;
            var principalPx = intrinsics.PrincipalPoint;

            // Scale intrinsics if sensor resolution differs from the texture we feed Objectron.
            var sensor = intrinsics.SensorResolution;
            if (sensor.x > 0 && sensor.y > 0 && (sensor.x != w || sensor.y != h))
            {
                var sx = w / (float)sensor.x;
                var sy = h / (float)sensor.y;
                focalPx = new Vector2(focalPx.x * sx, focalPx.y * sy);
                principalPx = new Vector2(principalPx.x * sx, principalPx.y * sy);
            }

            var ndcFocal = new Vector2(
                ObjectronMediaPipeCoordinates.FocalPixelToNdc(focalPx.x, w),
                ObjectronMediaPipeCoordinates.FocalPixelToNdc(focalPx.y, h));
            var ndcPrincipal = new Vector2(
                ObjectronMediaPipeCoordinates.PrincipalPixelToNdc(principalPx.x, w),
                ObjectronMediaPipeCoordinates.PrincipalPixelToNdc(principalPx.y, h));

            snapshot = new PcaIntrinsicsSnapshot(
                focalPx,
                principalPx,
                new Vector2Int(w, h),
                ndcFocal,
                ndcPrincipal);

            if (!s_loggedOnce)
            {
                s_loggedOnce = true;
                s_cached = snapshot;
                QuestObjectronLogger.Boot(
                    $"pca_intrinsics res={w}x{h} fx={focalPx.x:F1} fy={focalPx.y:F1} " +
                    $"cx={principalPx.x:F1} cy={principalPx.y:F1} " +
                    $"ndc_f=({ndcFocal.x:F4},{ndcFocal.y:F4}) " +
                    $"default_ndc_f=({ObjectronMediaPipeCoordinates.DefaultNdcFocalLength.x:F4}," +
                    $"{ObjectronMediaPipeCoordinates.DefaultNdcFocalLength.y:F4})");
            }

            return true;
        }

        public static bool TryGetNdcIntrinsics(
            PassthroughCameraAccess cameraAccess,
            out Vector2 ndcFocal,
            out Vector2 ndcPrincipal,
            out int imageWidth,
            out int imageHeight)
        {
            ndcFocal = ObjectronMediaPipeCoordinates.DefaultNdcFocalLength;
            ndcPrincipal = ObjectronMediaPipeCoordinates.DefaultNdcPrincipalPoint;
            imageWidth = 1280;
            imageHeight = 960;

            if (!TryGetSnapshot(cameraAccess, out var snap))
            {
                return false;
            }

            ndcFocal = snap.NdcFocal;
            ndcPrincipal = snap.NdcPrincipal;
            imageWidth = snap.Resolution.x;
            imageHeight = snap.Resolution.y;
            return true;
        }

        /// <summary>Project MediaPipe camera-frame point to PCA viewport [0,1] (bottom-left origin).</summary>
        public static bool TryProjectCameraToViewport(
            Vector3 cameraPoint,
            PassthroughCameraAccess cameraAccess,
            Pose cameraPose,
            out Vector2 viewport)
        {
            viewport = default;
            if (cameraAccess == null || !cameraAccess.IsPlaying)
            {
                return false;
            }

            if (!TryGetNdcIntrinsics(
                    cameraAccess,
                    out var ndcFocal,
                    out var ndcPrincipal,
                    out var w,
                    out var h))
            {
                return false;
            }

            var ndc = ObjectronMediaPipeCoordinates.ProjectCameraToNdc(
                cameraPoint,
                ndcFocal,
                ndcPrincipal);
            var pixel = ObjectronMediaPipeCoordinates.NdcToPixel(ndc, w, h);
            viewport = new Vector2(pixel.x / w, pixel.y / h);
            return true;
        }
    }
}
