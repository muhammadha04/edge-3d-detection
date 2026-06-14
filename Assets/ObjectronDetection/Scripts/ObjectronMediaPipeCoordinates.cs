// MediaPipe Objectron coordinate systems — see objectron.md "Coordinate Systems".
// Object frame: +x right, +y up, +z front (origin at box center).
// Camera frame: +x right, +y up, -z toward scene.
// landmarks_3d = rotation * scale * unit_box + translation

using Mediapipe;
using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronMediaPipeCoordinates
    {
        /// <summary>Lift2DFrameAnnotationTo3DCalculator defaults in ObjectronGpuSubgraph (NDC focal length).</summary>
        public static readonly Vector2 DefaultNdcFocalLength = new(2.0975f, 1.5731f);

        public static readonly Vector2 DefaultNdcPrincipalPoint = Vector2.zero;

        /// <summary>Eight unit-box corners in object frame (±1), matching Objectron keypoint ids 1–8.</summary>
        public static readonly Vector3[] UnitBoxCorners =
        {
            new(-1f, -1f, -1f), new(1f, -1f, -1f), new(-1f, 1f, -1f), new(1f, 1f, -1f),
            new(-1f, -1f, 1f), new(1f, -1f, 1f), new(-1f, 1f, 1f), new(1f, 1f, 1f),
        };

        public static Vector3 GetScale(ObjectAnnotation annotation)
        {
            if (annotation?.Scale != null && annotation.Scale.Count >= 3)
            {
                return new Vector3(
                    Mathf.Abs(annotation.Scale[0]),
                    Mathf.Abs(annotation.Scale[1]),
                    Mathf.Abs(annotation.Scale[2]));
            }

            return new Vector3(0.08f, 0.08f, 0.12f);
        }

        public static Vector3 GetHalfExtents(ObjectAnnotation annotation)
        {
            var scale = GetScale(annotation);
            return scale * 0.5f;
        }

        public static bool TryGetTranslation(ObjectAnnotation annotation, out Vector3 translationCam)
        {
            translationCam = default;
            if (annotation?.Translation == null || annotation.Translation.Count < 3)
            {
                return false;
            }

            translationCam = new Vector3(
                annotation.Translation[0],
                annotation.Translation[1],
                annotation.Translation[2]);
            return true;
        }

        public static Matrix4x4 GetRotationMatrix(ObjectAnnotation annotation)
        {
            if (annotation?.Rotation == null || annotation.Rotation.Count < 9)
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

        /// <summary>Build 9-point box in MediaPipe camera frame from rotation, scale, translation.</summary>
        public static bool TryBuildCameraFrameBox(
            ObjectAnnotation annotation,
            out Vector3[] cornersCam)
        {
            cornersCam = null;
            if (!TryGetTranslation(annotation, out var translation))
            {
                return false;
            }

            var rotation = GetRotationMatrix(annotation);
            var half = GetHalfExtents(annotation);
            cornersCam = new Vector3[9];
            cornersCam[0] = translation;

            for (var i = 0; i < 8; i++)
            {
                var local = Vector3.Scale(UnitBoxCorners[i], half);
                cornersCam[i + 1] = translation + rotation.MultiplyPoint3x4(local);
            }

            return ObjectronBoxValidation.HasValidExtents(cornersCam);
        }

        /// <summary>Project camera-frame 3D point to NDC (objectron.md).</summary>
        public static Vector3 ProjectCameraToNdc(
            Vector3 cameraPoint,
            Vector2 ndcFocal,
            Vector2 ndcPrincipal)
        {
            var z = cameraPoint.z;
            if (Mathf.Abs(z) < 1e-6f)
            {
                return new Vector3(0f, 0f, 0f);
            }

            return new Vector3(
                -ndcFocal.x * cameraPoint.x / z + ndcPrincipal.x,
                -ndcFocal.y * cameraPoint.y / z + ndcPrincipal.y,
                1f / z);
        }

        /// <summary>NDC to pixel (upper-left origin).</summary>
        public static Vector2 NdcToPixel(Vector3 ndc, int imageWidth, int imageHeight)
        {
            return new Vector2(
                (1f + ndc.x) * 0.5f * imageWidth,
                (1f - ndc.y) * 0.5f * imageHeight);
        }

        /// <summary>Pixel to NDC.</summary>
        public static Vector2 PixelToNdc(Vector2 pixel, int imageWidth, int imageHeight)
        {
            return new Vector2(
                pixel.x / imageWidth * 2f - 1f,
                1f - pixel.y / imageHeight * 2f);
        }

        /// <summary>Convert pixel-space fx to NDC (objectron.md).</summary>
        public static float FocalPixelToNdc(float fxPixel, int imageWidth) =>
            fxPixel * 2f / imageWidth;

        public static float PrincipalPixelToNdc(float pxPixel, int imageWidth) =>
            -pxPixel * 2f / imageWidth + 1f;

        /// <summary>Normalized [0,1] landmark to pixel (MediaPipe landmarks_2d).</summary>
        public static Vector2 NormalizedLandmarkToPixel(float xNorm, float yNorm, int imageWidth, int imageHeight) =>
            new(xNorm * imageWidth, yNorm * imageHeight);

        public static bool TryBuildFromLiftedKeypoints3D(
            ObjectAnnotation annotation,
            out Vector3[] cornersCam)
        {
            cornersCam = null;
            if (annotation?.Keypoints == null || annotation.Keypoints.Count == 0)
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

                buffer[idx] = new Vector3(kp.Point3D.X, kp.Point3D.Y, kp.Point3D.Z);
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
                    buffer[0] = t;
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

            if (!has[0] || !ObjectronBoxValidation.HasValidExtents(buffer))
            {
                return false;
            }

            cornersCam = buffer;
            return true;
        }
    }
}
