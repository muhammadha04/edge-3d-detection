// Per-session placement axis / quality options (no static globals).

using System;
using UnityEngine;

namespace QuestObjectron
{
    [Serializable]
    public class ObjectronPlacementOptions
    {
        public const float MinDotUpWorld = 0.7f;
        public const float MaxDotUpCamFwd = 0.4f;

        [Tooltip("Mirror MediaPipe X to match passthrough flip (2D + 3D).")]
        public bool MirrorInferenceHorizontal = true;

        [Tooltip("Convert MediaPipe camera frame (Y-down) to Unity PCA camera (Y-up).")]
        public bool UseUnityCameraFrame = true;

        [Tooltip("Extra negate of camera-local X when inference is mirrored.")]
        public bool Mirror3DLocalXWhenFlipped;

        [Tooltip("If model box up-vector is bad, use camera-aligned mask box instead.")]
        public bool UseMaskWhenBadOrientation = true;

        [Tooltip("When Unity frame is off: auto-pick raw vs YZ-flip rotation by score. Off = MediaPipe raw only.")]
        public bool AutoPickLegacyRotationFrame;

        public bool EnableTableSnap;

        [Tooltip("Remove headset roll when placing 3D box in world space (keeps mug upright when head is tilted).")]
        public bool CompensateHeadRoll = true;

        public Pose GetPlacementPose(Pose cameraPose) =>
            CompensateHeadRoll
                ? ObjectronWorldOrientation.GetRollCompensatedPose(cameraPose)
                : cameraPose;

        public string Summary =>
            $"mirror={MirrorInferenceHorizontal} unity_yup={UseUnityCameraFrame} " +
            $"mask_fallback={UseMaskWhenBadOrientation} mirror3d_x={Mirror3DLocalXWhenFlipped} " +
            $"auto_rot_pick={AutoPickLegacyRotationFrame} table_snap={EnableTableSnap} " +
            $"level_roll={CompensateHeadRoll}";

        public void ApplyAnnotationPose(
            Vector3 rawTranslation,
            Matrix4x4 rawRotation,
            out Vector3 translationCam,
            out Matrix4x4 rotationCam)
        {
            ObjectronMediaPipeCameraFrame.ApplyAnnotationPose(
                rawTranslation,
                rawRotation,
                this,
                out translationCam,
                out rotationCam);
        }

        public bool IsBadWorldOrientation(Vector3[] corners, Pose cameraPose) =>
            ObjectronMediaPipeCameraFrame.IsBadWorldOrientation(corners, cameraPose, this);
    }
}
