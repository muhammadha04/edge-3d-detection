// YOLOv9-t (Meta's MultiObjectDetection model, MIT) on Unity Inference Engine, fed directly from the
// Passthrough Camera GPU texture. Runs fully on the headset:
//   PCA texture ──TextureConverter (GPU, 640×640, top-left origin)──► Worker (GPUCompute or CPU)
//   ──ScheduleIterable: N layers per rendered frame (no main-thread stall)──► async readback ──► YoloDecode
// One frame in flight; each result carries the camera model of the image it was computed from.
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class YoloFrame
    {
        public int FrameId;
        public CameraModel Camera;
        public float CaptureRealtime;
        public float InferenceMs;
        public int LayerFrames;
        public readonly List<Detection2D> Detections = new List<Detection2D>();
    }

    public sealed class YoloDetector : IDisposable
    {
        private readonly Model m_model;
        private readonly string[] m_labels;
        private Worker m_worker;
        private BackendType m_backend;
        private float m_hzEma;
        private float m_lastDone = -1f;

        public YoloDetector(ModelAsset modelAsset, TextAsset labels)
        {
            m_model = ModelLoader.Load(modelAsset);
            var shape = m_model.inputs[0].shape;
            InputSize = new Vector2Int(shape.Get(3), shape.Get(2)); // NCHW → (W, H)
            m_labels = labels != null
                ? labels.text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            Status = "loaded";
            Log.Info($"yolo_model input={InputSize.x}x{InputSize.y} outputs={m_model.outputs.Count} labels={m_labels.Length}");
        }

        public Vector2Int InputSize { get; }
        public IReadOnlyList<string> Labels => m_labels;
        public string ModelName => "YOLO11n@320 (Inference Engine)";
        public bool IsReady { get; private set; }
        public bool IsBusy { get; private set; }
        public string Status { get; private set; }
        public BackendType Backend => m_backend;
        public float LastInferenceMs { get; private set; }
        public float InferenceHz => m_hzEma;
        public int LayersPerFrame { get; set; } = 40;
        public float ScoreThreshold { get; set; } = YoloDecode.DefaultScoreThreshold;
        public float IouThreshold { get; set; } = YoloDecode.DefaultIouThreshold;

        /// <summary>(Re)create the worker. The first inference compiles kernels and can block for seconds,
        /// so this runs one dummy inference while the start menu is up.</summary>
        public IEnumerator Initialize(BackendType backend)
        {
            IsReady = false;
            while (IsBusy)
            {
                yield return null;
            }

            m_worker?.Dispose();
            m_backend = backend;
            Status = $"warming up ({backend})";
            var t0 = Time.realtimeSinceStartup;
            m_worker = new Worker(m_model, backend);
            using (var input = new Tensor<float>(new TensorShape(1, 3, InputSize.y, InputSize.x)))
            {
                m_worker.Schedule(input);
                var awaiter = (m_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                while (!awaiter.IsCompleted)
                {
                    yield return null;
                }

                awaiter.GetResult().Dispose();
            }

            IsReady = true;
            Status = "ready";
            Log.Info($"yolo_ready backend={backend} warmup_ms={(Time.realtimeSinceStartup - t0) * 1000f:F0}");
        }

        public IEnumerator Run(Texture texture, CameraModel camera, int frameId, Action<YoloFrame> onDone)
        {
            if (!IsReady || IsBusy || texture == null)
            {
                yield break;
            }

            IsBusy = true;
            var t0 = Time.realtimeSinceStartup;
            var frame = new YoloFrame { FrameId = frameId, Camera = camera, CaptureRealtime = t0 };
            var input = new Tensor<float>(new TensorShape(1, 3, InputSize.y, InputSize.x));
            try
            {
                TextureConverter.ToTensor(texture, input, new TextureTransform());

                if (m_backend == BackendType.CPU)
                {
                    // CPU: spread layers over frames so the render loop never stalls.
                    var schedule = m_worker.ScheduleIterable(input);
                    var layers = 0;
                    while (schedule.MoveNext())
                    {
                        if (++layers % Mathf.Max(1, LayersPerFrame) == 0)
                        {
                            frame.LayerFrames++;
                            yield return null;
                        }
                    }
                }
                else
                {
                    // GPU: dispatch the whole network at once; it runs asynchronously on the GPU.
                    m_worker.Schedule(input);
                }

                // Request all three readbacks together and wait once (was three sequential waits).
                var boxesAwaiter = (m_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                var classesAwaiter = (m_worker.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
                var scoresAwaiter = (m_worker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
                while (!boxesAwaiter.IsCompleted || !classesAwaiter.IsCompleted || !scoresAwaiter.IsCompleted)
                {
                    frame.LayerFrames++;
                    yield return null;
                }

                using (var boxes = boxesAwaiter.GetResult())
                using (var classes = classesAwaiter.GetResult())
                using (var scores = scoresAwaiter.GetResult())
                {
                    var count = Mathf.Min(scores.shape[0], Mathf.Min(classes.shape[0], boxes.shape[0]));
                    YoloDecode.Decode(boxes.DownloadToArray(), classes.DownloadToArray(), scores.DownloadToArray(),
                        count, InputSize, m_labels, ScoreThreshold, IouThreshold, frame.Detections);
                }

                var now = Time.realtimeSinceStartup;
                LastInferenceMs = frame.InferenceMs = (now - t0) * 1000f;
                if (m_lastDone > 0f)
                {
                    var hz = 1f / Mathf.Max(1e-3f, now - m_lastDone);
                    m_hzEma = m_hzEma <= 0f ? hz : Mathf.Lerp(m_hzEma, hz, 0.1f);
                }

                m_lastDone = now;
                Status = "running";
            }
            finally
            {
                input.Dispose();
                IsBusy = false;
            }

            onDone?.Invoke(frame);
        }

        public void Dispose()
        {
            m_worker?.Dispose();
            m_worker = null;
            IsReady = false;
        }
    }
}
