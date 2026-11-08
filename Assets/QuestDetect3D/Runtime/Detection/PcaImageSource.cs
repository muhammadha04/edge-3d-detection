// Minimal MediaPipe ImageSource describing the Passthrough camera stream.
// MediaPipe only reads rotation / flip from it to configure its ImageTransformationCalculator.
// The image is fed UNMIRRORED and unrotated: GraphRunner then only flips it vertically
// (Unity textures are bottom-up), so the model sees the true, upright camera image.
using System.Collections;
using Mediapipe.Unity;
using Meta.XR;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class PcaImageSource : ImageSource
    {
        private PassthroughCameraAccess m_camera;
        private bool m_playing;

        public void Bind(PassthroughCameraAccess cameraAccess)
        {
            m_camera = cameraAccess;
            if (cameraAccess != null && cameraAccess.IsPlaying)
            {
                var r = cameraAccess.CurrentResolution;
                resolution = new ResolutionStruct(r.x, r.y, 30);
            }
        }

        public override string sourceName => "QuestPassthroughCamera";
        public override string[] sourceCandidateNames => new[] { sourceName };

        public override ResolutionStruct[] availableResolutions => new[]
        {
            new ResolutionStruct(1280, 960, 30),
        };

        public override bool isPrepared => m_camera != null && m_camera.IsPlaying;
        public override bool isPlaying => m_playing;
        public override RotationAngle rotation => RotationAngle.Rotation0;

        public override bool isHorizontallyFlipped
        {
            get => false;
            set { }
        }

        public override void SelectSource(int sourceId) { }

        public override IEnumerator Play()
        {
            while (m_camera == null || !m_camera.IsPlaying)
            {
                yield return null;
            }

            Bind(m_camera);
            m_playing = true;
        }

        public override IEnumerator Resume()
        {
            m_playing = true;
            yield break;
        }

        public override void Pause() => m_playing = false;
        public override void Stop() => m_playing = false;

        public override Texture GetCurrentTexture() =>
            m_camera != null && m_camera.IsPlaying ? m_camera.GetTexture() : null;
    }
}
