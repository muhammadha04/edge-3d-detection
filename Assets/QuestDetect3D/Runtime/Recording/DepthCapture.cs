// Reads back the Quest environment depth (left eye) as linear metres with the matrices needed to reproject every
// pixel to the world on the PC:
//   ndc_x = 2 (col + 0.5) / W − 1,   ndc_y = 2 (row + 0.5) / H − 1,   ndc_z = ZB.x / depth_m − ZB.y
//   world = (M⁻¹ · [ndc_x, ndc_y, ndc_z, 1]) / w        with M = reprojection_matrix (world → depth clip space)
// (Meta's EnvironmentOcclusion.cginc does the forward direction: uv = (clip.xy / clip.w + 1) / 2.)
//
// Where the texture comes from: Meta's EnvironmentDepthManager raises an (internal) onDepthTextureUpdate event
// with the provider's RenderTexture every time a new depth frame arrives; we subscribe to it by reflection. As a
// fallback the global shader texture _EnvironmentDepthTexture is used. The texture is DEPTH-ONLY (no colour
// buffer), so it is bound to the compute shader with RenderTextureSubElement.Depth. A Texture2DArray (one slice per
// eye, OpenXR) uses kernel CopyDepth, a plain 2D texture CopyDepth2D. The readback is asynchronous.
// Every failure has a reason (LastError) that the recorder logs and shows on the Record screen.
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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
        private const float ReadbackTimeout = 2f;
        private static readonly int DepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int DepthTexture2DId = Shader.PropertyToID("_EnvironmentDepthTexture2D");
        private static readonly int ReprojectionId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
        private static readonly int ZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        private static readonly int OutId = Shader.PropertyToID("_Out");
        private static readonly int SizeId = Shader.PropertyToID("_Size");

        private readonly ComputeShader m_shader;
        private readonly int m_kernelArray = -1;
        private readonly int m_kernel2D = -1;
        private ComputeBuffer m_buffer;
        private int m_size;
        private bool m_pending;
        private float m_pendingSince;
        private RenderTexture m_eventTexture;
        private Component m_manager;
        private Delegate m_handler;
        private EventInfo m_event;

        /// <summary>Loads the compute shader and subscribes to the depth manager's texture updates.</summary>
        public DepthCapture()
        {
            m_shader = Resources.Load<ComputeShader>("QD3D_DepthCopy");
            if (m_shader != null)
            {
                m_kernelArray = m_shader.FindKernel("CopyDepth");
                m_kernel2D = m_shader.FindKernel("CopyDepth2D");
            }
            else
            {
                LastError = "compute shader Resources/QD3D_DepthCopy missing";
                Log.Error("ann_depth_shader_missing Resources/QD3D_DepthCopy.compute");
            }

            Subscribe();
        }

        public bool IsPending => m_pending;
        public string LastError { get; private set; } = "";
        public int Failures { get; private set; }

        /// <summary>Where the texture came from and what it is (logged once per recording).</summary>
        public string Describe()
        {
            var t = CurrentTexture(out var source);
            var matrices = Shader.GetGlobalMatrixArray(ReprojectionId);
            return t == null
                ? $"source=none event_subscribed={m_handler != null} manager={(m_manager != null)} compute={SystemInfo.supportsComputeShaders}"
                : $"source={source} size={t.width}x{t.height} dim={t.dimension} volume={t.volumeDepth} color={t.graphicsFormat} " +
                  $"depth={t.depthStencilFormat} created={t.IsCreated()} matrices={(matrices?.Length ?? 0)} compute={SystemInfo.supportsComputeShaders}";
        }

        /// <summary>Copies the current left-eye depth to a buffer and reads it back asynchronously; onReady gets metres + matrices.
        /// False (with LastError) when nothing could be started.</summary>
        public bool TryCapture(Action<DepthFrame> onReady)
        {
            if (m_pending)
            {
                if (Time.realtimeSinceStartup - m_pendingSince < ReadbackTimeout)
                {
                    return false;
                }

                Fail("depth readback did not complete within 2 s (reset)");
                m_pending = false;
            }

            if (m_shader == null)
            {
                return Fail("compute shader missing");
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                return Fail("compute shaders not supported");
            }

            var texture = CurrentTexture(out _);
            if (texture == null)
            {
                return Fail("no environment depth texture yet (event and global both empty)");
            }

            if (!texture.IsCreated())
            {
                return Fail("depth texture not created");
            }

            var matrices = Shader.GetGlobalMatrixArray(ReprojectionId);
            if (matrices == null || matrices.Length == 0)
            {
                return Fail("no _EnvironmentDepthReprojectionMatrices");
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
            var m = matrices[0];
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    frame.Reprojection[4 * r + c] = m[r, c];
                }
            }

            try
            {
                var isArray = texture.dimension == TextureDimension.Tex2DArray;
                var kernel = isArray ? m_kernelArray : m_kernel2D;
                var depthOnly = texture.graphicsFormat == GraphicsFormat.None;
                var element = depthOnly ? RenderTextureSubElement.Depth : RenderTextureSubElement.Default;
                m_shader.SetTexture(kernel, isArray ? DepthTextureId : DepthTexture2DId, texture, 0, element);
                m_shader.SetVector(ZBufferParamsId, frame.ZBufferParams);
                m_shader.SetInt(SizeId, size);
                m_shader.SetBuffer(kernel, OutId, m_buffer);
                var groups = (size + 7) / 8;
                m_shader.Dispatch(kernel, groups, groups, 1);
            }
            catch (Exception e)
            {
                return Fail("dispatch failed: " + e.Message);
            }

            m_pending = true;
            m_pendingSince = Time.realtimeSinceStartup;
            AsyncGPUReadback.Request(m_buffer, request =>
            {
                m_pending = false;
                if (request.hasError)
                {
                    Fail("depth readback error");
                    return;
                }

                frame.Metres = request.GetData<float>().ToArray();
                LastError = "";
                onReady(frame);
            });
            return true;
        }

        /// <summary>Newest depth texture: from the manager's update event, else the global shader texture.</summary>
        private RenderTexture CurrentTexture(out string source)
        {
            if (m_handler == null)
            {
                Subscribe(); // the manager may be created after the recorder (EnvironmentRaycastManager adds it)
            }

            if (m_eventTexture != null)
            {
                source = "manager_event";
                return m_eventTexture;
            }

            source = "global";
            return Shader.GetGlobalTexture(DepthTextureId) as RenderTexture;
        }

        /// <summary>Subscribes to EnvironmentDepthManager.onDepthTextureUpdate (internal) by reflection.</summary>
        private void Subscribe()
        {
            try
            {
                var type = Type.GetType("Meta.XR.EnvironmentDepth.EnvironmentDepthManager, Meta.XR.EnvironmentDepth");
                if (type == null)
                {
                    return;
                }

                m_manager = UnityEngine.Object.FindAnyObjectByType(type) as Component;
                if (m_manager == null)
                {
                    return;
                }

                m_event = type.GetEvent("onDepthTextureUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var add = m_event?.GetAddMethod(true);
                if (add == null)
                {
                    return;
                }

                Action<RenderTexture> handler = rt => m_eventTexture = rt;
                m_handler = handler;
                add.Invoke(m_manager, new object[] { handler });
            }
            catch (Exception e)
            {
                Log.Warn($"ann_depth_subscribe_failed {e.GetType().Name}: {e.Message}");
                m_handler = null;
            }
        }

        private bool Fail(string reason)
        {
            Failures++;
            if (LastError != reason)
            {
                Log.Warn($"ann_depth_fail reason=\"{reason}\" {Describe()}");
            }

            LastError = reason;
            return false;
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

        /// <summary>Releases the GPU buffer and the event subscription.</summary>
        public void Dispose()
        {
            try
            {
                if (m_handler != null && m_manager != null)
                {
                    m_event?.GetRemoveMethod(true)?.Invoke(m_manager, new object[] { m_handler });
                }
            }
            catch (Exception)
            {
                // manager already destroyed
            }

            m_handler = null;
            m_buffer?.Dispose();
            m_buffer = null;
        }
    }
}
