// MediaPipe Objectron (via homuler MediaPipeUnityPlugin) as an IBoxKeypointDetector.
//
// - Exactly one frame in flight: every result is paired with the exact pose/intrinsics of its image.
// - The image is fed unmirrored; 2D keypoints map to viewport as (x, 1 - y).
// - Objectron's lifted 3D output (metric scale unknown, phone intrinsics) is used ONLY to decide which
//   keypoint bit is "up" / "front" / "side" and to score how upright the model thinks the object is.
using System;
using System.Collections;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using Unity.Collections;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class ObjectronDetector : MonoBehaviour, IBoxKeypointDetector
    {
        [SerializeField] private ObjectronGraph m_graph;
        [SerializeField] private TextureFramePool m_pool;
        [SerializeField] private PcaImageSource m_imageSource;
        [SerializeField] private Bootstrap m_bootstrap;
        [SerializeField, Range(1, 8)] private int m_maxObjects = 4;
        [SerializeField, Range(0.05f, 0.95f)] private float m_minDetectionConfidence = 0.4f;
        [SerializeField, Range(0.05f, 0.99f)] private float m_minTrackingConfidence = 0.6f;
        [SerializeField] private float m_resultTimeoutSec = 1.5f;

        private readonly object m_lock = new object();
        private FrameAnnotation m_result;
        private bool m_hasResult;
        private bool m_subscribed;
        private FrameContext m_inFlight;
        private float m_submitTime;
        private float m_lastResultTime;
        private readonly LayoutVoter m_layoutVoter = new LayoutVoter();
        private bool m_loggedKeypointIds;

        public bool IsReady { get; private set; }
        public bool IsBusy => m_inFlight != null;
        public string ModelName => "MediaPipe Objectron";
        public ObjectCategory Category { get; private set; } = ObjectCategory.Chair;
        public string Status { get; private set; } = "starting";
        public float InferenceHz { get; private set; }
        public float LastInferenceMs { get; private set; }
        public int MaxObjects => m_maxObjects;

        public event Action<DetectionFrame> FrameProcessed;

        public void Bind(PassthroughCameraAccess cameraAccess) => m_imageSource.Bind(cameraAccess);

        public IEnumerator Run(ObjectCategory category, Vector2Int resolution)
        {
            IsReady = false;
            Status = $"loading {Categories.Get(category).DisplayName} model";
            StopGraph();
            m_layoutVoter.Reset();

            if (m_bootstrap == null)
            {
                Status = "error: MediaPipe Bootstrap missing";
                Log.Error(Status);
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + 30f;
            while (!m_bootstrap.isFinished)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Status = "error: MediaPipe bootstrap timeout";
                    Log.Error(Status);
                    yield break;
                }

                yield return null;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!GpuManager.IsInitialized)
            {
                Status = "error: MediaPipe GPU not initialised";
                Log.Error(Status);
                yield break;
            }
#endif

            m_graph.category = ToMediaPipe(category);
            m_graph.maxNumObjects = m_maxObjects;
            m_graph.minDetectionConfidence = m_minDetectionConfidence;
            m_graph.minTrackingConfidence = m_minTrackingConfidence;
            m_pool.ResizeTexture(resolution.x, resolution.y, TextureFormat.RGBA32);

            var init = m_graph.WaitForInit(RunningMode.Async);
            yield return init;
            if (init.isError)
            {
                Status = $"error: graph init {init.error}";
                Log.Error(Status);
                yield break;
            }

            m_graph.OnLiftedObjectsOutput += OnLiftedObjects;
            m_subscribed = true;
            m_graph.StartRun(m_imageSource);

            Category = category;
            IsReady = true;
            Status = "running";
            m_lastResultTime = Time.realtimeSinceStartup;
            Log.Info($"detector_ready model=Objectron/{category} config={m_graph.configType} " +
                     $"input={resolution.x}x{resolution.y} max_objects={m_maxObjects} " +
                     $"det={m_minDetectionConfidence:F2} track={m_minTrackingConfidence:F2}");
        }

        public bool TrySubmit(NativeArray<byte> rgba, FrameContext context)
        {
            if (!IsReady || m_inFlight != null)
            {
                return false;
            }

            if (!m_pool.TryGetTextureFrame(out var frame))
            {
                return false;
            }

            var dst = frame.GetRawTextureData<byte>();
            if (dst.Length != rgba.Length)
            {
                frame.Release();
                Log.Warn($"frame_size_mismatch pool={dst.Length} pca={rgba.Length}");
                return false;
            }

            dst.CopyFrom(rgba);
            m_inFlight = context;
            m_submitTime = Time.realtimeSinceStartup;
            m_graph.AddTextureFrameToInputStream(frame);
            return true;
        }

        public void StopGraph()
        {
            IsReady = false;
            if (m_graph != null)
            {
                if (m_subscribed)
                {
                    try
                    {
                        m_graph.OnLiftedObjectsOutput -= OnLiftedObjects;
                    }
                    catch (Exception)
                    {
                        // Stream already disposed.
                    }

                    m_subscribed = false;
                }

                m_graph.Stop();
            }

            lock (m_lock)
            {
                m_hasResult = false;
                m_result = null;
            }

            m_inFlight = null;
        }

        private void OnDestroy() => StopGraph();

        // MediaPipe worker thread: no Unity API here.
        private void OnLiftedObjects(object sender, OutputEventArgs<FrameAnnotation> e)
        {
            lock (m_lock)
            {
                m_result = e.value;
                m_hasResult = true;
            }
        }

        private void Update()
        {
            if (m_inFlight == null)
            {
                return;
            }

            FrameAnnotation annotation = null;
            var has = false;
            lock (m_lock)
            {
                if (m_hasResult)
                {
                    annotation = m_result;
                    m_result = null;
                    m_hasResult = false;
                    has = true;
                }
            }

            var now = Time.realtimeSinceStartup;
            if (!has)
            {
                if (now - m_submitTime > m_resultTimeoutSec)
                {
                    Log.Warn("inference_timeout — resubmitting");
                    m_inFlight = null;
                }

                return;
            }

            var context = m_inFlight;
            m_inFlight = null;
            LastInferenceMs = (now - m_submitTime) * 1000f;
            var dt = Mathf.Max(1e-3f, now - m_lastResultTime);
            m_lastResultTime = now;
            InferenceHz = Mathf.Lerp(InferenceHz, 1f / dt, 0.15f);

            var frame = new DetectionFrame { Context = context, InferenceMs = LastInferenceMs };
            if (annotation?.Annotations != null)
            {
                foreach (var objectAnnotation in annotation.Annotations)
                {
                    var det = Convert(objectAnnotation, context.Camera);
                    if (det != null)
                    {
                        frame.Detections.Add(det);
                    }
                }
            }

            FrameProcessed?.Invoke(frame);
        }

        private KeypointDetection Convert(ObjectAnnotation annotation, in CameraModel camera)
        {
            if (annotation?.Keypoints == null || annotation.Keypoints.Count < 6)
            {
                return null;
            }

            var det = new KeypointDetection
            {
                Category = Category,
                ModelObjectId = annotation.ObjectId,
                ModelScore = annotation.Visibility > 0f ? Mathf.Clamp01(annotation.Visibility) : 1f,
            };

            var lifted = new Vector3[KeypointDetection.KeypointCount];
            var hasLifted = new bool[KeypointDetection.KeypointCount];

            // Objectron emits the 9 keypoints in order (centre, corners 1..8) but does not always fill `id`
            // (all zeros). Use the id only when the ids are distinct; otherwise use the list position.
            var keypoints = annotation.Keypoints;
            var idMask = 0;
            var idsDistinct = true;
            foreach (var kp in keypoints)
            {
                if (kp.Id < 0 || kp.Id >= KeypointDetection.KeypointCount || (idMask & (1 << kp.Id)) != 0)
                {
                    idsDistinct = false;
                    break;
                }

                idMask |= 1 << kp.Id;
            }

            if (!m_loggedKeypointIds)
            {
                m_loggedKeypointIds = true;
                var ids = new System.Text.StringBuilder();
                foreach (var kp in keypoints)
                {
                    ids.Append(kp.Id).Append(kp.Point2D != null ? "" : "(no2d)").Append(' ');
                }

                Log.Info($"keypoint_ids count={keypoints.Count} ids=[{ids.ToString().Trim()}] " +
                         $"using={(idsDistinct ? "id" : "list order")}");
            }

            for (var k = 0; k < keypoints.Count; k++)
            {
                var kp = keypoints[k];
                var id = idsDistinct ? kp.Id : k;
                if (id < 0 || id >= KeypointDetection.KeypointCount)
                {
                    continue;
                }

                if (kp.Point2D != null)
                {
                    // MediaPipe: normalized, origin top-left, y down → viewport: origin bottom-left, y up.
                    det.Viewport[id] = new Vector2(kp.Point2D.X, 1f - kp.Point2D.Y);
                    det.Has[id] = true;
                    det.Weight[id] = kp.Hidden ? 0.5f : 1f;
                }

                if (kp.Point3D != null)
                {
                    // MediaPipe camera: +x right, +y up, -z into the scene → Unity camera: +z forward.
                    lifted[id] = new Vector3(kp.Point3D.X, kp.Point3D.Y, -kp.Point3D.Z);
                    hasLifted[id] = true;
                }
            }

            ApplyLayout(det, lifted, hasLifted, camera);
            return det;
        }

        private void ApplyLayout(KeypointDetection det, Vector3[] lifted, bool[] hasLifted, in CameraModel camera)
        {
            var layout = BoxLayout.MediaPipeDefault;
            if (hasLifted[1] && hasLifted[5] && hasLifted[3] && hasLifted[2])
            {
                var bits = new[] { 4, 2, 1 };
                var dirs = new Vector3[3];
                var best = -1;
                var bestDot = -1f;
                for (var b = 0; b < 3; b++)
                {
                    dirs[b] = camera.Rotation * (lifted[1 + bits[b]] - lifted[1]);
                    var len = dirs[b].magnitude;
                    if (len < 1e-5f)
                    {
                        dirs[b] = Vector3.zero;
                        continue;
                    }

                    dirs[b] /= len;
                    var dot = Mathf.Abs(dirs[b].y);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = b;
                    }
                }

                if (best >= 0)
                {
                    det.ModelVerticality = bestDot;
                    var upBit = bits[best];
                    var upSign = dirs[best].y >= 0f ? 1 : -1;
                    m_layoutVoter.Vote(upBit, upSign, bestDot);
                }
            }

            if (m_layoutVoter.TryGet(out var votedUp, out var votedSign))
            {
                layout.UpBit = votedUp;
                layout.UpSign = votedSign;
            }

            layout.FrontBit = layout.UpBit == 1 ? 4 : 1;
            layout.SideBit = 7 - layout.UpBit - layout.FrontBit;
            det.Layout = layout;

            if (hasLifted[1] && hasLifted[1 + layout.FrontBit] && hasLifted[1 + layout.SideBit])
            {
                var front = camera.Rotation * (lifted[1 + layout.FrontBit] - lifted[1]);
                var side = camera.Rotation * (lifted[1 + layout.SideBit] - lifted[1]);
                front.y = 0f;
                side.y = 0f;
                det.ModelFrontWorld = front.sqrMagnitude > 1e-8f ? front.normalized : Vector3.zero;
                det.ModelSideWorld = side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.zero;
            }
        }

        private static ObjectronGraph.Category ToMediaPipe(ObjectCategory category)
        {
            switch (category)
            {
                case ObjectCategory.Cup: return ObjectronGraph.Category.Cup;
                case ObjectCategory.Sneaker: return ObjectronGraph.Category.Sneaker;
                case ObjectCategory.Camera: return ObjectronGraph.Category.Camera;
                default: return ObjectronGraph.Category.Chair;
            }
        }

        /// <summary>
        /// Learns which keypoint bit is "up" from confident frames, so a single bad EPnP lift cannot tip a box over.
        /// </summary>
        private sealed class LayoutVoter
        {
            private readonly float[,] m_votes = new float[3, 2];
            private float m_total;

            public void Reset()
            {
                Array.Clear(m_votes, 0, m_votes.Length);
                m_total = 0f;
            }

            public void Vote(int upBit, int upSign, float verticality)
            {
                if (verticality < 0.8f)
                {
                    return;
                }

                m_votes[BitIndex(upBit), upSign > 0 ? 1 : 0] += verticality;
                m_total += verticality;
            }

            public bool TryGet(out int upBit, out int upSign)
            {
                upBit = 2;
                upSign = 1;
                if (m_total < 3f)
                {
                    return false;
                }

                var best = 0f;
                for (var b = 0; b < 3; b++)
                {
                    for (var s = 0; s < 2; s++)
                    {
                        if (m_votes[b, s] > best)
                        {
                            best = m_votes[b, s];
                            upBit = 4 >> b;
                            upSign = s == 1 ? 1 : -1;
                        }
                    }
                }

                return true;
            }

            private static int BitIndex(int bit) => bit == 4 ? 0 : bit == 2 ? 1 : 2;
        }
    }
}
