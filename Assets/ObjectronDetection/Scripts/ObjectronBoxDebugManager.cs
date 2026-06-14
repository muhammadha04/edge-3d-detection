// Box debug: continuous inference (same as ObjectronChairDetectionManager), show labeled box on Capture.

using System.Collections;
using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Objectron;
using Meta.XR;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace QuestObjectron
{
    public enum BoxDebugState
    {
        Booting,
        Ready,
        Detected,
        NoDetection,
        Localized,
    }

    public class ObjectronBoxDebugManager : MonoBehaviour
    {
        private const int InferenceEveryNFrames = 2;

        [Header("Meta / MediaPipe")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughImageSource m_imageSource;
        [SerializeField] private ObjectronGraph m_objectronGraph;
        [SerializeField] private Bootstrap m_bootstrap;
        [SerializeField] private TextureFramePool m_textureFramePool;
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private ObjectronLabeledBoxVisuals m_visuals;

        [Header("Tuning")]
        [SerializeField] private ObjectronPlacementOptions m_placementOptions = new();
        [SerializeField] private RunningMode m_runningMode = RunningMode.Async;
        [SerializeField] private float m_minDetectionConfidence = 0.5f;
        [SerializeField] private float m_minTrackingConfidence = 0.5f;

        private readonly object m_pendingLock = new();
        private readonly ObjectronFramePoseQueue m_framePoses = new();
        private FrameAnnotation m_pendingFrame;
        private bool m_hasPendingFrame;
        private ObjectronWorldPlacement m_worldPlacement;
        private Vector3[] m_lastCorners;
        private Vector3 m_lastScale;
        private Vector3 m_lastTranslation;
        private Vector3 m_lastRotationEuler;
        private Pose m_lastCameraPose;
        private int m_liveDetectionCount;
        private bool m_graphReady;
        private BoxDebugState m_state = BoxDebugState.Booting;
        private Coroutine m_pipeline;
        private int m_frameId;
        private bool m_shutdownForSceneExit;

        public BoxDebugState State => m_visuals != null && m_visuals.IsLocalized
            ? BoxDebugState.Localized
            : m_state;

        public bool CanLocalize => m_lastCorners != null && m_visuals != null && !m_visuals.IsLocalized;
        public bool CanClear => m_visuals != null && (m_visuals.IsLocalized || m_visuals.HasActiveBox);
        public bool IsReady => m_graphReady;
        public int LiveDetectionCount => m_liveDetectionCount;

        private void Awake()
        {
            m_shutdownForSceneExit = false;
            if (m_placementOptions == null)
            {
                m_placementOptions = new ObjectronPlacementOptions();
            }

            m_placementOptions.CompensateHeadRoll = true;

            if (m_cameraAccess == null)
            {
                m_cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
            }

            if (m_environmentRaycast == null)
            {
                m_environmentRaycast = FindAnyObjectByType<EnvironmentRayCastSampleManager>();
            }

            if (m_imageSource == null)
            {
                m_imageSource = gameObject.AddComponent<PassthroughImageSource>();
            }

            m_imageSource.Bind(m_cameraAccess);
            if (m_imageSource is PassthroughImageSource passthroughSrc)
            {
                passthroughSrc.ApplyQuest3Defaults();
            }

            SyncPlacementOptionsFromImageSource();
            m_worldPlacement = new ObjectronWorldPlacement(
                m_cameraAccess,
                m_environmentRaycast,
                m_placementOptions);

            if (m_visuals == null)
            {
                m_visuals = GetComponent<ObjectronLabeledBoxVisuals>()
                    ?? gameObject.AddComponent<ObjectronLabeledBoxVisuals>();
            }

            if (m_textureFramePool == null)
            {
                m_textureFramePool = GetComponent<TextureFramePool>() ?? gameObject.AddComponent<TextureFramePool>();
            }

            if (m_objectronGraph == null)
            {
                m_objectronGraph = GetComponent<ObjectronGraph>() ?? gameObject.AddComponent<ObjectronGraph>();
            }

            if (m_bootstrap == null)
            {
                m_bootstrap = FindAnyObjectByType<Bootstrap>();
            }

            ApplyLaunchSettings();
            ApplyGraphTuning();
            m_visuals.Prewarm();
        }

        private void ApplyLaunchSettings()
        {
            m_minDetectionConfidence = ObjectronLaunchSettings.MinDetectionConfidence;
            m_minTrackingConfidence = ObjectronLaunchSettings.MinTrackingConfidence;
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

            m_pipeline = StartCoroutine(RunPipeline());
        }

        private void Update()
        {
            TryProcessPendingOnMainThread();
            TryControllerInput();
        }

        public void ShutdownForSceneExit()
        {
            CleanupActiveSession();
        }

        private void CleanupActiveSession()
        {
            m_shutdownForSceneExit = true;
            m_visuals?.Clear();
            m_lastCorners = null;
            m_lastScale = default;
            m_lastTranslation = default;
            m_lastRotationEuler = default;
            m_liveDetectionCount = 0;
            m_frameId = 0;
            m_state = BoxDebugState.Booting;

            if (m_pipeline != null)
            {
                StopCoroutine(m_pipeline);
                m_pipeline = null;
            }

            if (m_objectronGraph != null && m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnLiftedObjectsOutput -= OnLiftedObjectsAsync;
            }

            lock (m_pendingLock)
            {
                m_hasPendingFrame = false;
                m_pendingFrame = null;
            }

            m_framePoses.Clear();
            m_graphReady = false;
            m_objectronGraph?.Stop();
            m_imageSource?.Stop();

            if (m_imageSource != null && ImageSourceProvider.ImageSource == m_imageSource)
            {
                ImageSourceProvider.ImageSource = null;
            }

            QuestObjectronLogger.Boot("box_debug_shutdown");
        }

        private void OnDestroy()
        {
            CleanupActiveSession();
        }

        public void CaptureAndDetect()
        {
            if (!m_graphReady)
            {
                QuestObjectronLogger.Err("box_debug capture skipped — graph not ready");
                return;
            }

            if (m_lastCorners == null)
            {
                m_visuals?.Clear();
                m_state = BoxDebugState.NoDetection;
                QuestObjectronLogger.Detect(
                    $"box_debug no chair latched yet (live_count={m_liveDetectionCount}) — point at chair, wait a moment, retry");
                return;
            }

            m_visuals.Show(m_lastCorners, m_lastScale, m_lastTranslation, m_lastRotationEuler, m_lastCameraPose);
            m_state = BoxDebugState.Detected;
            QuestObjectronLogger.Detect(
                $"box_debug capture shown scale=({m_lastScale.x:F2},{m_lastScale.y:F2},{m_lastScale.z:F2}) live_count={m_liveDetectionCount}");
        }

        public void Localize()
        {
            if (!CanLocalize)
            {
                QuestObjectronLogger.Viz("box_debug localize skipped — capture a box first");
                return;
            }

            m_visuals.Localize(m_lastCorners, m_lastScale, m_lastTranslation, m_lastRotationEuler, m_lastCameraPose);
            m_state = BoxDebugState.Localized;
            QuestObjectronLogger.Viz("box_debug localized");
        }

        public void ClearLocalization()
        {
            m_visuals?.ClearLocalization();
            m_state = m_graphReady ? BoxDebugState.Ready : BoxDebugState.Booting;
            QuestObjectronLogger.Viz("box_debug cleared");
        }

        private void TryControllerInput()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
            {
                CaptureAndDetect();
            }

            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            {
                ClearLocalization();
            }
#endif
        }

        private void OnLiftedObjectsAsync(object sender, OutputEventArgs<FrameAnnotation> e)
        {
            if (!m_graphReady)
            {
                return;
            }

            lock (m_pendingLock)
            {
                m_pendingFrame = e.value;
                m_hasPendingFrame = true;
            }
        }

        private void TryProcessPendingOnMainThread()
        {
            if (!m_graphReady || m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            FrameAnnotation frame;
            lock (m_pendingLock)
            {
                if (!m_hasPendingFrame)
                {
                    return;
                }

                frame = m_pendingFrame;
                m_hasPendingFrame = false;
            }

            m_lastCameraPose = m_framePoses.DequeueOrCurrent(() => m_cameraAccess.GetCameraPose());
            SyncPlacementOptionsFromImageSource();
            LatchDetection(frame, m_lastCameraPose);
        }

        private void LatchDetection(FrameAnnotation lifted, Pose cameraPose)
        {
            if (lifted?.Annotations == null || lifted.Annotations.Count == 0)
            {
                return;
            }

            m_liveDetectionCount++;
            var ann = lifted.Annotations[0];
            var placementOutputs = m_worldPlacement.PlaceDetailed(lifted, cameraPose, null);
            var placement = placementOutputs.Count > 0
                ? new PlacementResult(placementOutputs[0].Method, placementOutputs[0].Corners)
                : m_worldPlacement.TryPlaceAnnotation(ann, cameraPose);

            if (placement.Corners == null || placement.Method == PlacementMethod.None)
            {
                return;
            }

            ReadModelVectors(ann, placement.Corners, out m_lastTranslation, out m_lastScale, out m_lastRotationEuler);
            m_lastCorners = placement.Corners;

            if (m_liveDetectionCount == 1 || m_liveDetectionCount % 30 == 0)
            {
                var headEuler = Camera.main != null ? Camera.main.transform.rotation.eulerAngles : Vector3.zero;
                var camEuler = cameraPose.rotation.eulerAngles;
                var rollDeg = ObjectronWorldOrientation.GetHeadRollDegrees(cameraPose);
                QuestObjectronLogger.Detect(
                    $"box_debug live latch #{m_liveDetectionCount} placement={placement.Method} " +
                    $"scale=({m_lastScale.x:F2},{m_lastScale.y:F2},{m_lastScale.z:F2}) " +
                    $"cam_euler=({camEuler.x:F0},{camEuler.y:F0},{camEuler.z:F0}) roll={rollDeg:F0} " +
                    $"level_roll={m_placementOptions.CompensateHeadRoll} pose_q={m_framePoses.Count}");
            }
        }

        private static void ReadModelVectors(
            ObjectAnnotation ann,
            Vector3[] corners,
            out Vector3 translation,
            out Vector3 scale,
            out Vector3 rotationEuler)
        {
            translation = Vector3.zero;
            scale = Vector3.zero;
            rotationEuler = Vector3.zero;

            if (ann.Translation != null && ann.Translation.Count >= 3)
            {
                translation = new Vector3(ann.Translation[0], ann.Translation[1], ann.Translation[2]);
            }

            if (ann.Scale != null && ann.Scale.Count >= 3)
            {
                scale = new Vector3(ann.Scale[0], ann.Scale[1], ann.Scale[2]);
            }
            else if (ObjectronBoxMetrics.TryGetAxisEdgeLengthsMeters(corners, out var edgeMeters))
            {
                scale = edgeMeters;
            }

            if (ann.Rotation != null && ann.Rotation.Count >= 9)
            {
                var r = ann.Rotation;
                var rot = new Matrix4x4(
                    new Vector4(r[0], r[1], r[2], 0f),
                    new Vector4(r[3], r[4], r[5], 0f),
                    new Vector4(r[6], r[7], r[8], 0f),
                    new Vector4(0f, 0f, 0f, 1f));
                rotationEuler = rot.rotation.eulerAngles;
            }
        }

        private IEnumerator RunPipeline()
        {
            while (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                yield return null;
            }

            yield return m_imageSource.Play();
            ImageSourceProvider.ImageSource = m_imageSource;

            if (!m_imageSource.isPrepared)
            {
                QuestObjectronLogger.Err("box_debug image source failed");
                yield break;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!GpuManager.IsInitialized)
            {
                QuestObjectronLogger.Err("box_debug GpuManager not initialized");
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
                QuestObjectronLogger.Err($"box_debug graph init failed: {initRequest.error}");
                yield break;
            }

            if (m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnLiftedObjectsOutput += OnLiftedObjectsAsync;
            }

            m_objectronGraph.StartRun(m_imageSource);
            m_graphReady = true;
            m_state = BoxDebugState.Ready;
            QuestObjectronLogger.Detect(
                "box_debug graph_started — same ObjectronGpuSubgraph as chair (stage1=SSD rects, stage2=lifted_objects); " +
                "placement=ObjectronWorldPlacement depth-refined (NOT stage-1 only). B or Capture to show box");

            var waitEndOfFrame = new WaitForEndOfFrame();

            while (true)
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

                var capturePose = m_cameraAccess.GetCameraPose();
                m_framePoses.Enqueue(capturePose);
                CopyCameraToFrame(textureFrame);
                m_objectronGraph.AddTextureFrameToInputStream(textureFrame);
                yield return waitEndOfFrame;

                if (m_runningMode == RunningMode.NonBlockingSync)
                {
                    FrameAnnotation lifted = null;
                    if (m_objectronGraph.TryGetNext(out lifted, out _, out _, false))
                    {
                        m_lastCameraPose = m_framePoses.DequeueOrCurrent(() => capturePose);
                        SyncPlacementOptionsFromImageSource();
                        LatchDetection(lifted, m_lastCameraPose);
                    }
                }
                else if (m_runningMode == RunningMode.Sync)
                {
                    if (m_objectronGraph.TryGetNext(out var liftedSync, out _, out _, true))
                    {
                        m_lastCameraPose = m_framePoses.DequeueOrCurrent(() => capturePose);
                        SyncPlacementOptionsFromImageSource();
                        LatchDetection(liftedSync, m_lastCameraPose);
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

        private void SyncPlacementOptionsFromImageSource()
        {
            if (m_imageSource == null)
            {
                return;
            }

            m_placementOptions.MirrorInferenceHorizontal = m_imageSource.isHorizontallyFlipped;
            m_worldPlacement?.SetMirrorHorizontal(m_placementOptions.MirrorInferenceHorizontal);
        }

        private IEnumerator WaitForPermissions()
        {
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                yield return null;
            }
        }

        private IEnumerator WaitForBootstrap()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (GpuManager.IsInitialized)
            {
                QuestObjectronLogger.Boot("box_debug bootstrap reused (GpuManager ready)");
                yield break;
            }
#endif

            if (m_bootstrap == null)
            {
                m_bootstrap = FindAnyObjectByType<Bootstrap>();
            }

            if (m_bootstrap == null)
            {
                QuestObjectronLogger.Err("box_debug Bootstrap not found");
                yield break;
            }

            if (m_bootstrap.isFinished)
            {
                yield break;
            }

            var timeout = Time.realtimeSinceStartup + 30f;
            while (!m_bootstrap.isFinished)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    QuestObjectronLogger.Err("box_debug bootstrap timeout");
                    yield break;
                }

                yield return null;
            }
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
    }
}
