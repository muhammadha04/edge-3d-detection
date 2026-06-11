// MediaPipe Objectron 3D pose is in a camera frame (Y-down, Z-forward). Unity PCA camera is Y-up, Z-forward.

using UnityEngine;

namespace QuestObjectron
{
    public static class ObjectronMediaPipeCameraFrame
    {
        private static readonly Matrix4x4 s_flipYz = Matrix4x4.Scale(new Vector3(1f, -1f, -1f));
        private static readonly Matrix4x4 s_mirrorX = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));

        public static Vector3 ToUnityCameraLocal(Vector3 mediaPipeCam) =>
            s_flipYz.MultiplyPoint3x4(mediaPipeCam);

        public static Matrix4x4 ToUnityCameraLocal(Matrix4x4 mediaPipeRotation) =>
            s_flipYz * mediaPipeRotation * s_flipYz;

        public static Vector3 MirrorCameraLocalX(Vector3 cameraLocal) =>
            new(-cameraLocal.x, cameraLocal.y, cameraLocal.z);

        public static Matrix4x4 MirrorCameraLocalX(Matrix4x4 rotationCam) =>
            s_mirrorX * rotationCam * s_mirrorX;

        /// <summary>Apply axis options to raw MediaPipe translation / rotation.</summary>
        public static void ApplyAnnotationPose(
            Vector3 rawTranslation,
            Matrix4x4 rawRotation,
            ObjectronPlacementOptions options,
            out Vector3 translationCam,
            out Matrix4x4 rotationCam)
        {
            options ??= new ObjectronPlacementOptions();
            translationCam = rawTranslation;
            rotationCam = rawRotation;

            if (options.UseUnityCameraFrame)
            {
                translationCam = ToUnityCameraLocal(translationCam);
                rotationCam = ToUnityCameraLocal(rotationCam);
            }

            if (options.Mirror3DLocalXWhenFlipped && options.MirrorInferenceHorizontal)
            {
                translationCam = MirrorCameraLocalX(translationCam);
                rotationCam = MirrorCameraLocalX(rotationCam);
            }
        }

        public static bool IsBadWorldOrientation(
            Vector3[] corners,
            Pose cameraPose,
            ObjectronPlacementOptions options)
        {
            options ??= new ObjectronPlacementOptions();
            if (corners == null || corners.Length < 9)
            {
                return true;
            }

            var boxUp = (corners[3] - corners[1]).normalized;
            if (boxUp.sqrMagnitude < 0.01f)
            {
                return true;
            }

            var camFwd = cameraPose.rotation * Vector3.forward;
            return Vector3.Dot(boxUp, Vector3.up) < ObjectronPlacementOptions.MinDotUpWorld
                   || Vector3.Dot(boxUp, camFwd) > ObjectronPlacementOptions.MaxDotUpCamFwd;
        }

        public static float OrientationScore(Quaternion rotWorld, Pose cameraPose)
        {
            var modelUp = rotWorld * Vector3.up;
            var camFwd = cameraPose.rotation * Vector3.forward;
            return Vector3.Dot(modelUp, Vector3.up) - 0.75f * Mathf.Abs(Vector3.Dot(modelUp, camFwd));
        }
    }
}
