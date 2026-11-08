// Captures a Passthrough camera image together with the camera pose/intrinsics of that same image.
// Uses a non-blocking AsyncGPUReadback (MRUK docs: blocking copies read the previous frame).
using System;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestDetect3D
{
    public sealed class PcaFrameSource
    {
        private readonly PassthroughCameraAccess m_camera;
        private int m_frameId;
        private bool m_pending;
        private bool m_loggedFormat;

        public PcaFrameSource(PassthroughCameraAccess camera)
        {
            m_camera = camera;
        }

        public bool IsReady => m_camera != null && m_camera.IsPlaying;
        public bool IsPending => m_pending;
        public Vector2Int Resolution => m_camera != null ? m_camera.CurrentResolution : Vector2Int.zero;

        public bool TryCurrentCameraModel(out CameraModel model)
        {
            model = default;
            if (!IsReady)
            {
                return false;
            }

            var pose = m_camera.GetCameraPose();
            var intrinsics = m_camera.Intrinsics;
            model = CameraModel.FromIntrinsics(pose.position, pose.rotation, intrinsics.FocalLength,
                intrinsics.PrincipalPoint, intrinsics.SensorResolution, m_camera.CurrentResolution);
            return model.IsValid;
        }

        /// <summary>Request the latest image; <paramref name="onReady"/> runs on the main thread with RGBA32 bytes.</summary>
        public bool TryCapture(Action<NativeArray<byte>, FrameContext> onReady)
        {
            if (m_pending || !IsReady)
            {
                return false;
            }

            var texture = m_camera.GetTexture();
            if (texture == null || !TryCurrentCameraModel(out var model))
            {
                return false;
            }

            var context = new FrameContext
            {
                FrameId = ++m_frameId,
                Camera = model,
                CaptureRealtime = Time.realtimeSinceStartup,
            };

            var expectedBytes = model.Resolution.x * model.Resolution.y * 4;
            m_pending = true;
            AsyncGPUReadback.Request(texture, 0, request =>
            {
                m_pending = false;
                if (request.hasError)
                {
                    Log.Warn("pca_readback_error");
                    return;
                }

                var data = request.GetData<byte>();
                if (data.Length != expectedBytes)
                {
                    if (!m_loggedFormat)
                    {
                        m_loggedFormat = true;
                        Log.Error($"pca_readback_size bytes={data.Length} expected={expectedBytes} " +
                                  $"format={texture.graphicsFormat}");
                    }

                    return;
                }

                onReady(data, context);
            });
            return true;
        }
    }
}
