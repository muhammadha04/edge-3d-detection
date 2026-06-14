// 2D-only Objectron debug: passthrough camera feed + multi_box_rects overlay (no 3D placement or .obj).

using System.Collections;
using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;
using UnityEngine.UI;

namespace QuestObjectron
{
    public class Objectron2DDebugManager : MonoBehaviour
    {
        private const int InferenceEveryNFrames = 2;
        private const float StatusLogIntervalSec = 2f;

        [Header("Meta / MediaPipe")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughImageSource m_imageSource;
        [SerializeField] private ObjectronGraph m_objectronGraph;
        [SerializeField] private Bootstrap m_bootstrap;
        [SerializeField] private TextureFramePool m_textureFramePool;
        [SerializeField] private Objectron2DFeedOverlay m_feedOverlay;

        [Header("Camera feed")]
        [SerializeField] private Objectron2DCameraFeedView m_cameraFeedView;
        [SerializeField] private RawImage m_cameraFeedImage;
        [SerializeField] [Range(0.2f, 1f)] private float m_cameraFeedAlpha = 0.92f;

        [Header("Tuning")]
        [SerializeField] private RunningMode m_runningMode = RunningMode.Async;
        [SerializeField] private float m_minDetectionConfidence = 0.35f;
        [SerializeField] private float m_minTrackingConfidence = 0.55f;

        private readonly object m_overlayLock = new();
        private List<NormalizedRect> m_lastRects;
        private bool m_hasPendingOverlay;
        private Coroutine m_pipeline;
        private bool m_graphReady;
        private bool m_shutdownForSceneExit;
        private int m_frameId;
        private int m_detectionFrameCount;
        private float m_lastStatusLogTime = -999f;

        private void Awake()
        {
            m_shutdownForSceneExit = false;

            if (m_cameraAccess == null)
            {
                m_cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
            }

            if (m_imageSource == null)
            {
                m_imageSource = GetComponent<PassthroughImageSource>()
                    ?? gameObject.AddComponent<PassthroughImageSource>();
            }

            m_imageSource.Bind(m_cameraAccess);
            if (m_imageSource is PassthroughImageSource passthroughSrc)
            {
                passthroughSrc.ApplyQuest3Defaults();
            }

            if (m_textureFramePool == null)
            {
                m_textureFramePool = GetComponent<TextureFramePool>()
                    ?? gameObject.AddComponent<TextureFramePool>();
            }

            if (m_objectronGraph == null)
            {
                m_objectronGraph = GetComponent<ObjectronGraph>()
                    ?? gameObject.AddComponent<ObjectronGraph>();
            }

            if (m_bootstrap == null)
            {
                m_bootstrap = FindAnyObjectByType<Bootstrap>();
            }

            if (m_feedOverlay == null)
            {
                m_feedOverlay = GetComponent<Objectron2DFeedOverlay>()
                    ?? gameObject.AddComponent<Objectron2DFeedOverlay>();
            }

            EnsureCameraFeedView();
            m_cameraFeedView?.BindImageSource(m_imageSource);
            SyncFeedOverlayBinding();

            ApplyLaunchSettings();
            ApplyGraphTuning();
        }

        private IEnumerator Start()
        {
            m_shutdownForSceneExit = false;
            yield return WaitForPermissions();
            yield return WaitForBootstrap();
            if (m_shutdownForSceneExit)
            {
                yield break;
            }

            StartCoroutine(StartCameraFeed());
            m_pipeline = StartCoroutine(RunPipeline());
        }

        private void EnsureCameraFeedView()
        {
            if (m_cameraFeedView == null)
            {
                m_cameraFeedView = GetComponent<Objectron2DCameraFeedView>()
                    ?? gameObject.AddComponent<Objectron2DCameraFeedView>();
            }

            if (m_cameraFeedImage == null && m_cameraFeedView != null)
            {
                m_cameraFeedView.EnsureWorldCanvas();
                m_cameraFeedImage = m_cameraFeedView.Image;
            }
        }

        private void Update()
        {
            TryFlushOverlayOnMainThread();
            MaybeLogStatus();
        }

        public void ShutdownForSceneExit()
        {
            CleanupActiveSession();
        }

        private void OnDestroy()
        {
            CleanupActiveSession();
        }

        private void CleanupActiveSession()
        {
            m_shutdownForSceneExit = true;

            if (m_pipeline != null)
            {
                StopCoroutine(m_pipeline);
                m_pipeline = null;
            }

            if (m_objectronGraph != null && m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnMultiBoxRectsOutput -= OnMultiBoxRectsAsync;
            }

            lock (m_overlayLock)
            {
                m_hasPendingOverlay = false;
                m_lastRects = null;
            }

            m_graphReady = false;
            m_frameId = 0;
            m_detectionFrameCount = 0;
            m_objectronGraph?.Stop();
            m_imageSource?.Stop();
            m_feedOverlay?.Clear();

            if (m_imageSource != null && ImageSourceProvider.ImageSource == m_imageSource)
            {
                ImageSourceProvider.ImageSource = null;
            }

            QuestObjectronLogger.Boot("objectron_2d_debug_shutdown");
        }

        private void ApplyLaunchSettings()
        {
            m_minDetectionConfidence = ObjectronLaunchSettings.MinDetectionConfidence;
            m_minTrackingConfidence = ObjectronLaunchSettings.MinTrackingConfidence;
        }

        private void ApplyGraphTuning()
        {
            if (m_objectronGraph == null)
            {
                return;
            }

            m_objectronGraph.category = ObjectronGraph.Category.Chair;
            ObjectronLaunchSettings.ApplyToGraph(m_objectronGraph);
        }

        private void OnMultiBoxRectsAsync(object sender, OutputEventArgs<List<NormalizedRect>> e)
        {
            lock (m_overlayLock)
            {
                m_lastRects = e.value;
                m_hasPendingOverlay = true;
            }
        }

        private void TryFlushOverlayOnMainThread()
        {
            if (m_feedOverlay == null)
            {
                return;
            }

            List<NormalizedRect> rects;
            lock (m_overlayLock)
            {
                if (!m_hasPendingOverlay)
                {
                    return;
                }

                m_hasPendingOverlay = false;
                rects = m_lastRects;
            }

            if (rects == null || rects.Count == 0)
            {
                return;
            }

            m_detectionFrameCount++;
            m_feedOverlay.Enqueue(rects);
        }

        private void SyncFeedOverlayBinding()
        {
            if (m_feedOverlay == null || m_imageSource == null)
            {
                return;
            }

            if (m_cameraFeedImage == null && m_cameraFeedView != null)
            {
                m_cameraFeedImage = m_cameraFeedView.Image;
            }

            var feedRect = m_cameraFeedImage != null
                ? m_cameraFeedImage.rectTransform
                : m_cameraFeedView?.FeedRect;
            if (feedRect == null)
            {
                return;
            }

            var isMirrored = m_imageSource.isHorizontallyFlipped ^ m_imageSource.isFrontFacing;
            m_feedOverlay.Bind(feedRect, m_imageSource.rotation.Reverse(), isMirrored);
        }

        private void MaybeLogStatus()
        {
            if (!m_graphReady)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now - m_lastStatusLogTime < StatusLogIntervalSec)
            {
                return;
            }

            m_lastStatusLogTime = now;
            var boxCount = m_lastRects?.Count ?? 0;
            QuestObjectronLogger.Detect(
                $"2d_debug boxes={boxCount} frames={m_detectionFrameCount} overlay={m_feedOverlay != null && m_feedOverlay.HasDrawnThisSession}");
        }

        private IEnumerator StartCameraFeed()
        {
            EnsureCameraFeedView();

            while (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                if (m_shutdownForSceneExit)
                {
                    yield break;
                }

                yield return null;
            }

            if (m_cameraFeedImage == null && m_cameraFeedView != null)
            {
                m_cameraFeedView.EnsureWorldCanvas();
                m_cameraFeedImage = m_cameraFeedView.Image;
            }

            if (m_cameraFeedImage != null)
            {
                m_cameraFeedImage.texture = m_cameraAccess.GetTexture();
                m_cameraFeedView?.BindImageSource(m_imageSource);
                SyncFeedOverlayBinding();
                var color = m_cameraFeedImage.color;
                color.a = m_cameraFeedAlpha;
                m_cameraFeedImage.color = color;
                QuestObjectronLogger.Viz("2d_debug camera feed bound");
            }
            else
            {
                QuestObjectronLogger.Err("2d_debug camera feed missing — no RawImage canvas");
            }
        }

        private IEnumerator RunPipeline()
        {
            while (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                if (m_shutdownForSceneExit)
                {
                    yield break;
                }

                yield return null;
            }

            yield return m_imageSource.Play();
            ImageSourceProvider.ImageSource = m_imageSource;

            if (!m_imageSource.isPrepared)
            {
                QuestObjectronLogger.Err("2d_debug image source failed");
                yield break;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!GpuManager.IsInitialized)
            {
                QuestObjectronLogger.Err("2d_debug GpuManager not initialized");
                yield break;
            }
#endif

            var camTex = m_imageSource.GetCurrentTexture();
            var texW = camTex != null ? camTex.width : m_imageSource.textureWidth;
            var texH = camTex != null ? camTex.height : m_imageSource.textureHeight;
            m_textureFramePool.ResizeTexture(texW, texH, TextureFormat.RGBA32);

            yield return new WaitForSeconds(0.5f);

            var initRequest = m_objectronGraph.WaitForInit(m_runningMode);
            yield return initRequest;
            if (initRequest.isError)
            {
                QuestObjectronLogger.Err($"2d_debug graph init failed: {initRequest.error}");
                yield break;
            }

            if (m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnMultiBoxRectsOutput += OnMultiBoxRectsAsync;
            }

            m_objectronGraph.StartRun(m_imageSource);
            m_graphReady = true;
            m_cameraFeedView?.BindImageSource(m_imageSource);
            SyncFeedOverlayBinding();
            QuestObjectronLogger.Detect("2d_debug graph_started — 2D boxes on camera feed");

            var waitEndOfFrame = new WaitForEndOfFrame();

            while (!m_shutdownForSceneExit)
            {
                if (!m_cameraAccess.IsPlaying)
                {
                    yield return null;
                    continue;
                }

                m_frameId++;
                if (m_frameId % InferenceEveryNFrames != 0)
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                if (!m_textureFramePool.TryGetTextureFrame(out var textureFrame))
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                CopyCameraToFrame(textureFrame);
                m_objectronGraph.AddTextureFrameToInputStream(textureFrame);
                yield return waitEndOfFrame;

                if (m_runningMode == RunningMode.NonBlockingSync
                    && m_objectronGraph.TryGetNext(out _, out var rects, out _, false))
                {
                    lock (m_overlayLock)
                    {
                        if (rects != null)
                        {
                            m_lastRects = rects;
                        }

                        m_hasPendingOverlay = true;
                    }
                }
                else if (m_runningMode == RunningMode.Sync
                    && m_objectronGraph.TryGetNext(out _, out var rectsSync, out _, true))
                {
                    lock (m_overlayLock)
                    {
                        if (rectsSync != null)
                        {
                            m_lastRects = rectsSync;
                        }

                        m_hasPendingOverlay = true;
                    }
                }
            }
        }

        private void CopyCameraToFrame(TextureFrame textureFrame)
        {
            var sourceTexture = ImageSourceProvider.ImageSource?.GetCurrentTexture();
            if (sourceTexture == null)
            {
                return;
            }

            if (sourceTexture is Texture2D t2d)
            {
                textureFrame.ReadTextureFromOnCPU(t2d);
            }
            else
            {
                textureFrame.ReadTextureFromOnCPU(sourceTexture);
            }
        }

        private IEnumerator WaitForPermissions()
        {
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                if (m_shutdownForSceneExit)
                {
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator WaitForBootstrap()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (GpuManager.IsInitialized)
            {
                QuestObjectronLogger.Boot("2d_debug bootstrap reused (GpuManager ready)");
                yield break;
            }
#endif

            if (m_bootstrap == null)
            {
                m_bootstrap = FindAnyObjectByType<Bootstrap>();
            }

            if (m_bootstrap == null)
            {
                QuestObjectronLogger.Err("2d_debug Bootstrap not found");
                yield break;
            }

            if (m_bootstrap.isFinished)
            {
                yield break;
            }

            var timeout = Time.realtimeSinceStartup + 30f;
            while (!m_bootstrap.isFinished)
            {
                if (m_shutdownForSceneExit)
                {
                    yield break;
                }

                if (Time.realtimeSinceStartup > timeout)
                {
                    QuestObjectronLogger.Err("2d_debug bootstrap timeout");
                    yield break;
                }

                yield return null;
            }
        }
    }
}
