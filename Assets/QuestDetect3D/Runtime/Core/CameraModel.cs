// Pinhole model of the Passthrough camera at the moment a frame was captured.
// Mirrors MRUK PassthroughCameraAccess.ViewportPointToRay / WorldToViewportPoint exactly
// (camera local frame: +x right, +y up, +z forward; viewport origin bottom-left),
// but is a plain value type so it can be cached per frame and unit-tested.
using UnityEngine;

namespace QuestDetect3D
{
    public readonly struct CameraModel
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector2 Focal;
        public readonly Vector2 Principal;
        public readonly Rect SensorCrop;
        public readonly Vector2Int Resolution;

        public CameraModel(Vector3 position, Quaternion rotation, Vector2 focal, Vector2 principal, Rect sensorCrop,
            Vector2Int resolution)
        {
            Position = position;
            Rotation = rotation;
            Focal = focal;
            Principal = principal;
            SensorCrop = sensorCrop;
            Resolution = resolution;
        }

        /// <summary>Build from PCA intrinsics (pixel units of the sensor) and the current stream resolution.</summary>
        public static CameraModel FromIntrinsics(
            Vector3 position,
            Quaternion rotation,
            Vector2 focalPx,
            Vector2 principalPx,
            Vector2Int sensorResolution,
            Vector2Int currentResolution)
        {
            Rect crop;
            if (sensorResolution.x > 0 && sensorResolution.y > 0)
            {
                var sx = currentResolution.x / (float)sensorResolution.x;
                var sy = currentResolution.y / (float)sensorResolution.y;
                var m = Mathf.Max(sx, sy);
                sx /= m;
                sy /= m;
                crop = new Rect(
                    sensorResolution.x * (1f - sx) * 0.5f,
                    sensorResolution.y * (1f - sy) * 0.5f,
                    sensorResolution.x * sx,
                    sensorResolution.y * sy);
            }
            else
            {
                crop = new Rect(0f, 0f, currentResolution.x, currentResolution.y);
            }

            return new CameraModel(position, rotation, focalPx, principalPx, crop, currentResolution);
        }

        public Vector3 Forward => Rotation * Vector3.forward;

        /// <summary>Viewport-to-pixel scale used for residuals (pixels of the delivered image).</summary>
        public float PixelsPerViewportX => Resolution.x;
        public float PixelsPerViewportY => Resolution.y;

        public Vector3 ViewportToLocalDirection(Vector2 viewport)
        {
            return new Vector3(
                (SensorCrop.x + SensorCrop.width * viewport.x - Principal.x) / Focal.x,
                (SensorCrop.y + SensorCrop.height * viewport.y - Principal.y) / Focal.y,
                1f);
        }

        public Ray ViewportToRay(Vector2 viewport)
        {
            return new Ray(Position, Rotation * ViewportToLocalDirection(viewport));
        }

        /// <summary>World point → viewport (bottom-left origin). False when the point is behind the camera.</summary>
        public bool WorldToViewport(Vector3 world, out Vector2 viewport)
        {
            var local = Quaternion.Inverse(Rotation) * (world - Position);
            if (local.z < 0.02f)
            {
                viewport = default;
                return false;
            }

            var sensorX = local.x / local.z * Focal.x + Principal.x;
            var sensorY = local.y / local.z * Focal.y + Principal.y;
            viewport = new Vector2(
                (sensorX - SensorCrop.x) / SensorCrop.width,
                (sensorY - SensorCrop.y) / SensorCrop.height);
            return true;
        }

        public bool IsValid => Focal.x > 1f && Focal.y > 1f && SensorCrop.width > 1f && Resolution.x > 0;
    }
}
