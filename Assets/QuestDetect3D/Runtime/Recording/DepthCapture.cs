// Reads back the Quest environment depth (left eye) as linear metres with the matrices needed to reproject every
// pixel to the world on the PC:
//   ndc_x = 2 (col + 0.5) / W − 1,   ndc_y = 2 (row + 0.5) / H − 1,   ndc_z = ZB.x / depth_m − ZB.y
//   world = (M⁻¹ · [ndc_x, ndc_y, ndc_z, 1]) / w        with M = reprojection_matrix (world → depth clip space)
// (Meta's EnvironmentOcclusion.cginc does the forward direction: uv = (clip.xy / clip.w + 1) / 2.)
// The copy runs in a compute shader; the readback is asynchronous (no main-thread stall).
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestDetect3D.Recording
{
    public sealed class DepthFrame
    {
        public int Size;
        public float[] Metres;
        public float[] Reprojection = new float[16]; // row-major
        public Vector4 ZBufferParams;
        public float CaptureRealtime;
        public long CaptureUnixMs;
    }

    public sealed class DepthCapture : IDisposable
    {
        private static readonly int DepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int ReprojectionId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
        private static readonly int ZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        private static readonly int OutId = Shader.PropertyToID("_Out");
        private static readonly int SizeId = Shader.PropertyToID("_Size");

        private readonly ComputeShader m_shader;
        private readonly int m_kernel;
        private ComputeBuffer m_buffer;
        private int m_size;
        private bool m_pending;

        public DepthCapture()
        {
            m_shader = Resources.Load<ComputeShader>("QD3D_DepthCopy");
            if (m_shader != null)
            {
                m_kernel = m_shader.FindKernel("CopyDepth");
            }
            else
            {
                Log.Error("ann_depth_shader_missing Resources/QD3D_DepthCopy.compute");
            }
        }

        public bool IsPending => m_pending;
        public string LastError { get; private set; } = "";

        public bool IsAvailable => m_shader != null && SystemInfo.supportsComputeShaders &&
                                   Shader.GetGlobalTexture(DepthTextureId) is RenderTexture rt && rt != null && rt.IsCreated();

        public bool TryCapture(Action<DepthFrame> onReady)
        {
            if (m_pending || m_shader == null)
            {
                return false;
            }

            var texture = Shader.GetGlobalTexture(DepthTextureId) as RenderTexture;
            if (texture == null || !texture.IsCreated())
            {
                LastError = "no environment depth texture";
                return false;
            }

            var size = texture.width;
            if (m_buffer == null || m_size != size)
            {
                m_buffer?.Dispose();
                m_buffer = new ComputeBuffer(size * size, sizeof(float));
                m_size = size;
            }

            var frame = new DepthFrame
            {
                Size = size,
                ZBufferParams = Shader.GetGlobalVector(ZBufferParamsId),
                CaptureRealtime = Time.realtimeSinceStartup,
                CaptureUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var matrices = Shader.GetGlobalMatrixArray(ReprojectionId);
            if (matrices == null || matrices.Length == 0)
            {
                LastError = "no reprojection matrices";
                return false;
            }

            var m = matrices[0];
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    frame.Reprojection[4 * r + c] = m[r, c];
                }
            }

            m_shader.SetTexture(m_kernel, DepthTextureId, texture);
            m_shader.SetVector(ZBufferParamsId, frame.ZBufferParams);
            m_shader.SetInt(SizeId, size);
            m_shader.SetBuffer(m_kernel, OutId, m_buffer);
            var groups = (size + 7) / 8;
            m_shader.Dispatch(m_kernel, groups, groups, 1);
            m_pending = true;
            AsyncGPUReadback.Request(m_buffer, request =>
            {
                m_pending = false;
                if (request.hasError)
                {
                    LastError = "depth readback error";
                    return;
                }

                frame.Metres = request.GetData<float>().ToArray();
                onReady(frame);
            });
            return true;
        }

        /// <summary>Metres → uint16 millimetres (0 = invalid, clamped to 65.535 m).</summary>
        public static ushort[] ToMillimetres(float[] metres)
        {
            var mm = new ushort[metres.Length];
            for (var i = 0; i < metres.Length; i++)
            {
                var v = metres[i];
                mm[i] = v > 0f && !float.IsNaN(v) && !float.IsInfinity(v) ? (ushort)Mathf.Clamp(Mathf.RoundToInt(v * 1000f), 0, 65535) : (ushort)0;
            }

            return mm;
        }

        public void Dispose()
        {
            m_buffer?.Dispose();
            m_buffer = null;
        }
    }
}
