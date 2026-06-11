// Feeds Meta Passthrough Camera API frames into MediaPipe ImageSource.
//
// Quest PCA delivers a 1280×960 landscape sensor buffer while the user’s view is portrait.
// MediaPipe Objectron was trained on upright phone video; GraphRunner maps rotation via
// input_rotation = imageSource.rotation.Reverse(). Wrong m_rotation makes the mug appear
// only when you yaw the headset (image content slides through the model’s expected framing).
// Quest 3 left passthrough camera: default Rotation0 + horizontal flip (mug often only detects at
// head yaw ~-90° when Rotation270 is used). Press A to cycle; logcat logs rotation when 2D boxes appear.
// See Meta PassthroughCameraApiSamples / CameraToWorld.

using System.Collections;
using Mediapipe.Unity;
using Meta.XR;
using UnityEngine;

namespace QuestObjectron
{
    public class PassthroughImageSource : Mediapipe.Unity.ImageSource
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [Tooltip("Quest PCA 1280×960 landscape → portrait for Objectron. Default Rotation0 + flip. If detection only when head yaw ~-90°, try Rotation180.")]
        [SerializeField] private RotationAngle m_rotation = RotationAngle.Rotation0;

        [Tooltip("Mirror passthrough like a front-facing webcam if boxes look flipped or missing.")]
        [SerializeField] private bool m_horizontallyFlipped = true;

        private static readonly ResolutionStruct[] s_resolutions =
        {
            new ResolutionStruct(1280, 960, 30),
            new ResolutionStruct(640, 480, 30),
        };

        public void Bind(PassthroughCameraAccess cameraAccess) => m_cameraAccess = cameraAccess;

        /// <summary>Quest 3 PCA: prefer upright framing without requiring -90° head yaw.</summary>
        public void ApplyQuest3Defaults()
        {
            m_rotation = RotationAngle.Rotation0;
            m_horizontallyFlipped = true;
        }

        /// <summary>Heuristic from PCA extrinsic vs head forward (call once after camera is playing).</summary>
        public void TryAutoRotationFromCameraPose()
        {
            if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            var camPose = m_cameraAccess.GetCameraPose();
            var headFwd = GetHeadForward();
            var camFwd = camPose.rotation * Vector3.forward;
            var angle = Vector3.Angle(headFwd, camFwd);

            // Large misalignment between head and camera forward often means wrong Rotation270 for portrait use.
            if (angle > 60f && m_rotation == RotationAngle.Rotation270)
            {
                m_rotation = RotationAngle.Rotation0;
                m_horizontallyFlipped = true;
                QuestObjectronLogger.Boot(
                    $"auto_rotation: head_cam_angle={angle:F0}° → Rotation0 flip=true (was Rotation270)");
            }
        }

        private static Vector3 GetHeadForward()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.forward : Vector3.forward;
        }

        public override string sourceName => "QuestPassthroughCamera";
        public override string[] sourceCandidateNames => new[] { sourceName };
        public override ResolutionStruct[] availableResolutions => s_resolutions;
        public override bool isPrepared => m_cameraAccess != null && m_cameraAccess.IsPlaying;

        private bool _isPlaying;
        public override bool isPlaying => _isPlaying;

        public override RotationAngle rotation => m_rotation;

        public override bool isHorizontallyFlipped
        {
            get => m_horizontallyFlipped;
            set => m_horizontallyFlipped = value;
        }

        private static readonly RotationAngle[] s_rotationCycle =
        {
            RotationAngle.Rotation0,
            RotationAngle.Rotation90,
            RotationAngle.Rotation180,
            RotationAngle.Rotation270,
        };

        /// <summary>Cycle 0→90→180→270 (A button on Quest). Re-starts graph if already running.</summary>
        public void CycleRotation()
        {
            var idx = 0;
            for (var i = 0; i < s_rotationCycle.Length; i++)
            {
                if (s_rotationCycle[i] == m_rotation)
                {
                    idx = i;
                    break;
                }
            }

            m_rotation = s_rotationCycle[(idx + 1) % s_rotationCycle.Length];
            QuestObjectronLogger.Boot(
                $"rotation_cycled={m_rotation} flip={m_horizontallyFlipped} (check overlay_2d_boxes / 2d_boxes log after cycle)");
        }

        /// <summary>Log when 2D boxes appear under current rotation (called from detection manager).</summary>
        public void LogRotationWith2dHits(int boxCount)
        {
            if (boxCount <= 0)
            {
                return;
            }

            QuestObjectronLogger.Detect(
                $"rotation_ok: {m_rotation} flip={m_horizontallyFlipped} produced 2d_boxes={boxCount}");
        }

        public void ToggleHorizontalFlip()
        {
            m_horizontallyFlipped = !m_horizontallyFlipped;
            QuestObjectronLogger.Boot($"flip_toggled={m_horizontallyFlipped} rotation={m_rotation}");
        }

        public override void SelectSource(int sourceId) { }

        public override IEnumerator Play()
        {
            _isPlaying = false;
            if (m_cameraAccess == null)
            {
                QuestObjectronLogger.Err("PassthroughImageSource: camera access is null");
                yield break;
            }

            while (!m_cameraAccess.IsPlaying)
            {
                yield return null;
            }

            var res = m_cameraAccess.CurrentResolution;
            resolution = new ResolutionStruct(Mathf.RoundToInt(res.x), Mathf.RoundToInt(res.y), 30);
            TryAutoRotationFromCameraPose();
            _isPlaying = true;
            QuestObjectronLogger.Pca(
                $"open res={resolution.width}x{resolution.height} camera_rotation={m_rotation} flip={m_horizontallyFlipped}");
        }

        public override IEnumerator Resume()
        {
            _isPlaying = true;
            yield break;
        }

        public override void Pause() => _isPlaying = false;

        public override void Stop() => _isPlaying = false;

        public override Texture GetCurrentTexture()
        {
            if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                return null;
            }

            return m_cameraAccess.GetTexture();
        }
    }
}
