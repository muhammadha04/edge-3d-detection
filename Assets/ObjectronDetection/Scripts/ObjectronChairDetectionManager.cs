// Two-stage pipeline: PCA -> ObjectronGpuSubgraph (SSD stage-1 + BoxLandmark/EPnP stage-2) -> world boxes.
// Placement follows MediaPipe objectron.md coordinate systems (twostage branch).

using System;
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
    public class ObjectronChairDetectionManager : MonoBehaviour
    {
        private const int BaseInferenceEveryNFrames = 2;
        private const int MidInferenceEveryNFrames = 3;
        private const int FullScanInferenceEveryNFrames = 6;
        private const int MidLocalizedThreshold = 2;
        private const float RefineCooldownSec = 0.75f;
        private const float RefineMotionBypassM = 0.15f;
        private const float NewLocalizeCooldownSec = 2f;
        private const int RequiredStableLatchFrames = 5;
        private const float LatchStabilityRadiusM = 0.15f;
        /// <summary>Min interval between main-thread detection process (box-debug style latch).</summary>
        private const float DetectionProcessMinInterval = 0.1f;
        private int MaxLocalizedChairs => ObjectronLaunchSettings.ClampMaxObjects(ObjectronLaunchSettings.MaxObjects);

        [Header("Meta / MediaPipe")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private PassthroughImageSource m_imageSource;
        [SerializeField] private ObjectronGraph m_objectronGraph;
        [SerializeField] private Bootstrap m_bootstrap;
        [SerializeField] private TextureFramePool m_textureFramePool;
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private OrientedBBoxDrawer m_bboxDrawer;
        [SerializeField] private ObjectronDetectionDebug m_detectionDebug;
        [SerializeField] private ObjectronQuestVisuals m_questVisuals;
        [SerializeField] private ObjectronHeadsetHud m_headsetHud;
        [SerializeField] private ObjectronPassthroughOverlay m_passthroughOverlay;
        [SerializeField] private ObjectronEnvironmentDepthProvider m_environmentDepthProvider;

        [Header("Tuning")]
        [SerializeField] private ObjectronPlacementOptions m_placementOptions = new();
        [SerializeField] private RunningMode m_runningMode = RunningMode.Async;
        [SerializeField] private float m_minDetectionConfidence = 0.5f;
        [SerializeField] private float m_minTrackingConfidence = 0.55f;

        private readonly object m_pendingLock = new();
        private readonly ObjectronFramePoseQueue m_framePoses = new();
        private FrameAnnotation m_pendingFrame;
        private List<NormalizedRect> m_pendingStageOneRects;
        private List<NormalizedRect> m_lastStageOneRects;
        private bool m_hasPendingFrame;

        /// <summary>Primary world placement (same path as box debug — depth-refined stage-2 output).</summary>
        private ObjectronWorldPlacement m_worldPlacement;
        /// <summary>Raw MediaPipe pose only — used for pipeline diagnostics vs world placement.</summary>
        private ObjectronTwoStagePlacement m_rawStage2Placement;

        public ObjectronPlacementOptions PlacementOptions => m_placementOptions;
        private Coroutine m_pipeline;
        private int m_frameId;
        private bool m_graphReady;
        private int m_emptyLogCount;
        private float m_lastDetectionProcessTime = -999f;
        private int m_lastLoggedOkCount = -1;
        private float m_lastRotationHintTime;
        private int m_lastHudObjectId = -1;
        private string m_lastPlacementMethod = "—";
        private List<Vector3[]> m_lastWorldBoxes;
        private ObjectronPinSnapshot m_lastPinSnapshot;
        [NonSerialized] private List<ObjectronLocalizedChairState> m_localizedChairs = new();
        private int m_lastLoggedLocalizedCount = -1;
        private bool m_shutdownForSceneExit;
        private float m_lastNewLocalizeTime = -999f;
        private Vector3? m_latchCenter;
        private int m_latchStableFrames;
        private int m_liveLatchCount;
#if UNITY_ANDROID && !UNITY_EDITOR
        private float m_lastResetButtonTime = -999f;
        private const float ResetButtonCooldownSec = 1f;
#endif

        public int LocalizedChairCount => m_localizedChairs.Count;
        public bool Scanning => m_localizedChairs.Count < MaxLocalizedChairs;

        public void WireReferences(
            PassthroughCameraAccess cameraAccess,
            PassthroughImageSource imageSource,
            ObjectronGraph objectronGraph,
            Bootstrap bootstrap,
            TextureFramePool textureFramePool,
            EnvironmentRayCastSampleManager environmentRaycast,
            OrientedBBoxDrawer bboxDrawer)
        {
            if (cameraAccess != null) m_cameraAccess = cameraAccess;
            if (imageSource != null) m_imageSource = imageSource;
            if (objectronGraph != null) m_objectronGraph = objectronGraph;
            if (bootstrap != null) m_bootstrap = bootstrap;
            if (textureFramePool != null) m_textureFramePool = textureFramePool;
            if (environmentRaycast != null) m_environmentRaycast = environmentRaycast;
            if (bboxDrawer != null) m_bboxDrawer = bboxDrawer;
        }

        private void EnsurePlacementFixMenu()
        {
            if (ObjectronPlacementFixMenu.Instance != null)
            {
                return;
            }

            var existing = GetComponent<ObjectronPlacementFixMenu>()
                ?? FindAnyObjectByType<ObjectronPlacementFixMenu>();
            if (existing == null)
            {
                gameObject.AddComponent<ObjectronPlacementFixMenu>();
            }
        }

        private void EnsureDebug()
        {
            if (m_detectionDebug == null)
            {
                m_detectionDebug = GetComponent<ObjectronDetectionDebug>()
                    ?? FindAnyObjectByType<ObjectronDetectionDebug>();
                if (m_detectionDebug == null)
                {
                    m_detectionDebug = gameObject.AddComponent<ObjectronDetectionDebug>();
                }
            }

            if (m_questVisuals == null)
            {
                m_questVisuals = GetComponent<ObjectronQuestVisuals>()
                    ?? FindAnyObjectByType<ObjectronQuestVisuals>();
                if (m_questVisuals == null)
                {
                    m_questVisuals = gameObject.AddComponent<ObjectronQuestVisuals>();
                }
            }

            if (m_headsetHud == null)
            {
                m_headsetHud = GetComponent<ObjectronHeadsetHud>()
                    ?? FindAnyObjectByType<ObjectronHeadsetHud>();
                if (m_headsetHud == null)
                {
                    m_headsetHud = gameObject.AddComponent<ObjectronHeadsetHud>();
                }
            }

            if (m_environmentDepthProvider == null)
            {
                m_environmentDepthProvider = GetComponent<ObjectronEnvironmentDepthProvider>()
                    ?? gameObject.AddComponent<ObjectronEnvironmentDepthProvider>();
            }

            if (m_passthroughOverlay != null)
            {
                m_passthroughOverlay.enabled = false;
            }

            EnsurePlacementFixMenu();
            m_questVisuals.Prewarm();
            m_bboxDrawer?.Prewarm();
            m_detectionDebug?.Prewarm();
        }

        private void Update()
        {
            TryControllerInput();
            TryProcessPendingOnMainThread();
            MaybeLogRotationHint();
        }

        private void TryControllerInput()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            {
                var now = Time.realtimeSinceStartup;
                if (now - m_lastResetButtonTime >= ResetButtonCooldownSec)
                {
                    m_lastResetButtonTime = now;
                    ResetDetection();
                }
            }
#endif
        }

        /// <summary>Clear localized chairs without user-facing reset logs (scene exit / shutdown).</summary>
        private void ClearLocalizedStateSilent()
        {
            m_localizedChairs.Clear();
            m_lastWorldBoxes = null;
            m_lastLoggedLocalizedCount = -1;
            m_latchCenter = null;
            m_latchStableFrames = 0;
            m_lastNewLocalizeTime = -999f;
            m_questVisuals?.ClearLocalization(silent: true);
            m_detectionDebug?.Clear();
            m_bboxDrawer?.SetDetections(null);
        }

        /// <summary>Clear all localized chairs and restart the one-shot scan (Y on left controller).</summary>
        public void ResetDetection()
        {
            ClearLocalizedStateSilent();
            ApplyAdaptiveGraphCap();
            QuestObjectronLogger.Detect($"chair_scan_reset — point at chairs to localize (max {MaxLocalizedChairs})");
        }

        private void MaybeLogRotationHint()
        {
            if (m_imageSource == null || m_emptyLogCount == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now - m_lastRotationHintTime < 15f)
            {
                return;
            }

            m_lastRotationHintTime = now;
            QuestObjectronLogger.Boot(
                $"controls: Y (left)=reset chair scan (rotation={m_imageSource.rotation} flip={m_imageSource.isHorizontallyFlipped})");
        }

        private void Awake()
        {
            m_shutdownForSceneExit = false;
            m_localizedChairs ??= new List<ObjectronLocalizedChairState>();
            QuestObjectronLogger.Boot($"version={Application.version} unity={Application.unityVersion} platform={Application.platform}");
            if (m_placementOptions == null)
            {
                m_placementOptions = new ObjectronPlacementOptions();
            }

            ApplyTwoStagePlacementDefaultsToOptions();
            SyncPlacementOptionsFromImageSource();
            ObjectronPlacementFixSettings.Active = m_placementOptions;
            m_worldPlacement = new ObjectronWorldPlacement(
                m_cameraAccess,
                m_environmentRaycast,
                m_placementOptions);
            var rawOptions = ClonePlacementOptions(m_placementOptions);
            rawOptions.EnableFloorSnap = false;
            rawOptions.ConstrainUprightOnTable = false;
            m_rawStage2Placement = new ObjectronTwoStagePlacement(
                m_cameraAccess,
                m_environmentRaycast,
                rawOptions,
                smoothing: 0f);

            if (m_imageSource == null)
            {
                m_imageSource = gameObject.AddComponent<PassthroughImageSource>();
            }

            m_imageSource.Bind(m_cameraAccess);
            if (m_imageSource is PassthroughImageSource passthroughSrc)
            {
                passthroughSrc.ApplyQuest3Defaults();
            }

            QuestObjectronLogger.Boot(
                $"camera_rotation={m_imageSource.rotation} flip={m_imageSource.isHorizontallyFlipped} (PassthroughImageSource — change if [DETECT] empty)");

            ApplyLaunchSettings();
            ApplyGraphTuning();
            EnsureDebug();
            QuestObjectronLogger.Boot(
                $"twostage pipeline=ObjectronGpuSubgraph (SSD+BoxLandmark+EPnP) max_objects={MaxLocalizedChairs} " +
                $"detect_conf={m_minDetectionConfidence:F2} track_conf={m_minTrackingConfidence:F2}");
            QuestObjectronLogger.Boot($"placement_options: {m_placementOptions.Summary}");
        }

        private void ApplyTwoStagePlacementDefaultsToOptions()
        {
            // Match box-debug: depth-refined stage-2 latch; floor snap only on pin (see TryLocalizeNewChair).
            m_placementOptions.UseUnityCameraFrame = true;
            m_placementOptions.Mirror3DLocalXWhenFlipped = false;
            m_placementOptions.UseMaskWhenBadOrientation = true;
            m_placementOptions.AutoPickLegacyRotationFrame = false;
            m_placementOptions.EnableTableSnap = false;
            m_placementOptions.DisableMaskAlignedFallback = false;
            m_placementOptions.CompensateHeadRoll = true;
            m_placementOptions.ConstrainUprightOnTable = false;
            m_placementOptions.EnableFloorSnap = false;
        }

        private static ObjectronPlacementOptions ClonePlacementOptions(ObjectronPlacementOptions source)
        {
            return new ObjectronPlacementOptions
            {
                MirrorInferenceHorizontal = source.MirrorInferenceHorizontal,
                UseUnityCameraFrame = source.UseUnityCameraFrame,
                Mirror3DLocalXWhenFlipped = source.Mirror3DLocalXWhenFlipped,
                UseMaskWhenBadOrientation = source.UseMaskWhenBadOrientation,
                AutoPickLegacyRotationFrame = source.AutoPickLegacyRotationFrame,
                EnableTableSnap = source.EnableTableSnap,
                CompensateHeadRoll = source.CompensateHeadRoll,
                ConstrainUprightOnTable = source.ConstrainUprightOnTable,
                EnableFloorSnap = source.EnableFloorSnap,
                DisableMaskAlignedFallback = source.DisableMaskAlignedFallback,
            };
        }

        private void SyncPlacementOptionsFromImageSource()
        {
            if (m_imageSource == null)
            {
                return;
            }

            m_placementOptions.MirrorInferenceHorizontal = m_imageSource.isHorizontallyFlipped;
            m_worldPlacement?.SetMirrorHorizontal(m_placementOptions.MirrorInferenceHorizontal);
            m_rawStage2Placement?.SetMirrorHorizontal(m_placementOptions.MirrorInferenceHorizontal);
        }

        private void ApplyLaunchSettings()
        {
            m_minDetectionConfidence = ObjectronLaunchSettings.MinDetectionConfidence;
            m_minTrackingConfidence = ObjectronLaunchSettings.MinTrackingConfidence;
            ObjectronLaunchSettings.ApplyToGraph(m_objectronGraph);
        }

        private void ApplyGraphTuning()
        {
            if (m_objectronGraph == null)
            {
                return;
            }

            m_objectronGraph.category = ObjectronGraph.Category.Chair;
            ApplyAdaptiveGraphCap();
            m_objectronGraph.minDetectionConfidence = m_minDetectionConfidence;
            m_objectronGraph.minTrackingConfidence = m_minTrackingConfidence;
        }

        private void ApplyAdaptiveGraphCap()
        {
            if (m_objectronGraph == null)
            {
                return;
            }

            var unlocalized = Mathf.Max(0, MaxLocalizedChairs - m_localizedChairs.Count);
            var cap = Scanning ? Mathf.Max(1, unlocalized + 1) : 1;
            m_objectronGraph.maxNumObjects = Mathf.Min(cap, MaxLocalizedChairs);
        }

        private int GetInferenceEveryNFrames()
        {
            if (!Scanning)
            {
                return FullScanInferenceEveryNFrames;
            }

            if (m_localizedChairs.Count >= MidLocalizedThreshold)
            {
                return MidInferenceEveryNFrames;
            }

            return BaseInferenceEveryNFrames;
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

        public void ShutdownForSceneExit()
        {
            CleanupActiveSession();
        }

        private void CleanupActiveSession()
        {
            m_shutdownForSceneExit = true;
            ClearLocalizedStateSilent();
            m_frameId = 0;
            m_lastLoggedOkCount = -1;
            m_lastLoggedLocalizedCount = -1;
            m_emptyLogCount = 0;

            if (m_pipeline != null)
            {
                StopCoroutine(m_pipeline);
                m_pipeline = null;
            }

            if (m_objectronGraph != null && m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnLiftedObjectsOutput -= OnLiftedObjectsAsync;
                m_objectronGraph.OnMultiBoxRectsOutput -= OnMultiBoxRectsAsync;
            }

            lock (m_pendingLock)
            {
                m_hasPendingFrame = false;
                m_pendingFrame = null;
                m_pendingStageOneRects = null;
            }

            m_framePoses.Clear();
            m_graphReady = false;
            m_objectronGraph?.Stop();
            m_imageSource?.Stop();

            if (m_imageSource != null && ImageSourceProvider.ImageSource == m_imageSource)
            {
                ImageSourceProvider.ImageSource = null;
            }

            QuestObjectronLogger.Boot("chair_detection_shutdown");
        }

        private void OnDestroy()
        {
            if (Application.isPlaying)
            {
                CleanupActiveSession();
            }
        }

        private IEnumerator WaitForPermissions()
        {
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                yield return null;
            }

            var sceneOk = m_environmentRaycast == null || m_environmentRaycast.HasScenePermission();
            QuestObjectronLogger.Perm($"camera=granted spatial={(sceneOk ? "granted" : "pending")}");
        }

        private IEnumerator WaitForBootstrap()
        {
            if (m_bootstrap == null)
            {
                m_bootstrap = FindAnyObjectByType<Bootstrap>();
            }

            if (m_bootstrap == null)
            {
                QuestObjectronLogger.Err("Bootstrap not found in scene — add MediaPipe Bootstrap prefab");
                yield break;
            }

            var bootstrapTimeout = Time.realtimeSinceStartup + 30f;
            while (!m_bootstrap.isFinished)
            {
                if (Time.realtimeSinceStartup > bootstrapTimeout)
                {
                    QuestObjectronLogger.Err("mediapipe_bootstrap=timeout (check earlier Unity errors from Bootstrap.Init)");
                    yield break;
                }

                yield return null;
            }

            QuestObjectronLogger.Boot(
                $"mediapipe_bootstrap=ready inference={m_bootstrap.inferenceMode} gpu_mgr={GpuManager.IsInitialized}");
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
                QuestObjectronLogger.Err("Passthrough image source failed to start");
                yield break;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!GpuManager.IsInitialized)
            {
                QuestObjectronLogger.Err(
                    "GpuManager not initialized — Bootstrap must use GPU inference (ObjectronCpuSubgraph is absent from Android AAR)");
                yield break;
            }
#endif

            QuestObjectronLogger.Boot(
                $"graphics={SystemInfo.graphicsDeviceType} objectron_config={m_objectronGraph.configType} inference={m_objectronGraph.inferenceMode} running_mode={m_runningMode}");

            var camTex = m_imageSource.GetCurrentTexture();
            var texW = camTex != null ? camTex.width : m_imageSource.textureWidth;
            var texH = camTex != null ? camTex.height : m_imageSource.textureHeight;
            const TextureFormat texFormat = TextureFormat.RGBA32;
            m_textureFramePool.ResizeTexture(texW, texH, texFormat);
            var camFormat = camTex != null ? camTex.graphicsFormat.ToString() : "n/a";
            QuestObjectronLogger.Boot(
                $"texture_pool={texW}x{texH} pool_format={texFormat} camera_format={camFormat} camera_rotation={m_imageSource.rotation} graph_resize=internal");

            yield return new WaitForSeconds(0.5f);

            ApplyGraphTuning();
            QuestObjectronLogger.Boot(
                $"detect_thresh={m_minDetectionConfidence:F2} track_thresh={m_minTrackingConfidence:F2} flip={m_imageSource.isHorizontallyFlipped}");

            var initRequest = m_objectronGraph.WaitForInit(m_runningMode);
            yield return initRequest;

            if (initRequest.isError)
            {
                QuestObjectronLogger.Err($"Objectron graph init failed: {initRequest.error}");
                yield break;
            }

            if (m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnLiftedObjectsOutput += OnLiftedObjectsAsync;
                m_objectronGraph.OnMultiBoxRectsOutput += OnMultiBoxRectsAsync;
            }

            m_objectronGraph.StartRun(m_imageSource);
            m_graphReady = true;
            QuestObjectronLogger.Detect(
                "twostage_graph_started model=Chair — tiered: cheap track (candidates) + heavy localize/refine only");

            var waitEndOfFrame = new WaitForEndOfFrame();

            while (true)
            {
                if (!m_cameraAccess.IsPlaying)
                {
                    yield return null;
                    continue;
                }

                m_frameId++;
                if (m_frameId % GetInferenceEveryNFrames() != 0)
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                var t0 = Time.realtimeSinceStartup;
                var cameraPose = m_cameraAccess.GetCameraPose();

                if (!m_textureFramePool.TryGetTextureFrame(out var textureFrame))
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                m_framePoses.Enqueue(cameraPose);
                CopyCameraToFrame(textureFrame);
                m_objectronGraph.AddTextureFrameToInputStream(textureFrame);
                yield return waitEndOfFrame;

                switch (m_runningMode)
                {
                    case RunningMode.Async:
                        break;
                    case RunningMode.NonBlockingSync:
                    {
                        FrameAnnotation lifted = null;
                        List<NormalizedRect> rects = null;
                        yield return new WaitUntil(() =>
                            m_objectronGraph.TryGetNext(out lifted, out rects, out _, false));
                        ProcessDetections(lifted, cameraPose, rects, t0);
                        break;
                    }
                    case RunningMode.Sync:
                        if (m_objectronGraph.TryGetNext(out var liftedSync, out var rectsSync, out _, true))
                        {
                            ProcessDetections(liftedSync, cameraPose, rectsSync, t0);
                        }
                        else
                        {
                            QuestObjectronLogger.Detect("empty");
                        }

                        break;
                }

                if (m_frameId % 30 == 0)
                {
                    var res = m_cameraAccess.CurrentResolution;
                    QuestObjectronLogger.Frame($"id={m_frameId} res={res.x}x{res.y}");
                }
            }
        }

        /// <summary>MediaPipe callback thread — enqueue only, no Unity API.</summary>
        private void OnLiftedObjectsAsync(object sender, OutputEventArgs<FrameAnnotation> e)
        {
            if (!m_graphReady)
            {
                return;
            }

            lock (m_pendingLock)
            {
                m_pendingFrame = e.value;
                m_pendingStageOneRects = m_lastStageOneRects;
                m_hasPendingFrame = true;
            }
        }

        /// <summary>Stage-1 SSD crop rects (multi_box_rects stream).</summary>
        private void OnMultiBoxRectsAsync(object sender, OutputEventArgs<List<NormalizedRect>> e)
        {
            if (!m_graphReady)
            {
                return;
            }

            lock (m_pendingLock)
            {
                m_lastStageOneRects = e.value;
            }
        }

        private void TryProcessPendingOnMainThread()
        {
            if (!m_graphReady || m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                return;
            }

            FrameAnnotation frame;
            List<NormalizedRect> stageOneRects;
            lock (m_pendingLock)
            {
                if (!m_hasPendingFrame)
                {
                    return;
                }

                var now = Time.realtimeSinceStartup;
                if (now - m_lastDetectionProcessTime < DetectionProcessMinInterval)
                {
                    return;
                }

                frame = m_pendingFrame;
                stageOneRects = m_pendingStageOneRects;
                m_hasPendingFrame = false;
                m_lastDetectionProcessTime = now;
            }

            var cameraPose = m_framePoses.DequeueOrCurrent(() => m_cameraAccess.GetCameraPose());
            SyncPlacementOptionsFromImageSource();
            ProcessDetections(frame, cameraPose, stageOneRects, m_lastDetectionProcessTime);
        }

        private void ProcessDetections(
            FrameAnnotation lifted,
            Pose cameraPose,
            List<NormalizedRect> stageOneRects,
            float startTime)
        {
            if (lifted?.Annotations == null || lifted.Annotations.Count == 0)
            {
                ResetLatchStability();
                ProcessEmptyDetections();
                return;
            }

            m_emptyLogCount = 0;
            m_liveLatchCount++;

            var count = lifted.Annotations.Count;
            var stageOneCount = stageOneRects?.Count ?? 0;
            var ms = (Time.realtimeSinceStartup - startTime) * 1000f;
            ObjectronPipelineDiagnostics.LogStageSummary(lifted, stageOneCount, ms);

            if (count != m_lastLoggedOkCount)
            {
                m_lastLoggedOkCount = count;
            }

            // Box-debug style: one world-placement pass per frame (not per-annotation localize spam).
            var placementOutputs = m_worldPlacement.PlaceDetailed(lifted, cameraPose, null);
            var changed = false;
            var liveCandidates = new List<Vector3[]>();
            PlacementOutput bestNewCandidate = default;
            ObjectAnnotation bestNewAnnotation = null;
            var bestNewQuality = float.MaxValue;

            foreach (var output in placementOutputs)
            {
                if (output.Corners == null || output.Method == PlacementMethod.None)
                {
                    continue;
                }

                if (!ObjectronBoxValidation.TryGetExtentMeters(output.Corners, out _))
                {
                    continue;
                }

                var annotation = FindAnnotation(lifted, output.ObjectId);
                var localizedIndex = ObjectronChairDedup.FindDuplicateIndex(
                    m_localizedChairs, output.ObjectId, output.Corners);

                if (localizedIndex >= 0)
                {
                    if (TryRefineLocalizedChair(localizedIndex, annotation, output))
                    {
                        changed = true;
                    }

                    continue;
                }

                if (!Scanning)
                {
                    continue;
                }

                liveCandidates.Add(output.Corners);

                if (!ObjectronDetectionQuality.TryEvaluate(output, 0f, 0f, out var quality))
                {
                    continue;
                }

                if (quality.Score < bestNewQuality)
                {
                    bestNewQuality = quality.Score;
                    bestNewCandidate = output;
                    bestNewAnnotation = annotation;
                }
            }

            if (ObjectronPipelineDiagnostics.Enabled && bestNewAnnotation != null)
            {
                var rawTrack = m_rawStage2Placement.PlaceOneTrack(
                    bestNewAnnotation, cameraPose,
                    stageOneRects != null && lifted.Annotations.Count > 0 ? stageOneRects[0] : null);
                ObjectronPipelineDiagnostics.LogPlacementCompare(bestNewAnnotation, rawTrack, bestNewCandidate);
            }

            UpdateLatchStability(bestNewCandidate);
            ShowLivePreviewBoxes(liveCandidates);

            if (Scanning && CanAutoLocalize() && TryLocalizeNewChair(bestNewCandidate, bestNewAnnotation))
            {
                changed = true;
                ResetLatchStability();
            }

            if (changed)
            {
                if (m_localizedChairs.Count != m_lastLoggedLocalizedCount)
                {
                    m_lastLoggedLocalizedCount = m_localizedChairs.Count;
                    QuestObjectronLogger.Detect($"chair_localized_total={m_localizedChairs.Count}");
                }

                RefreshLocalizedVisuals();
            }

            if (m_liveLatchCount == 1 || m_liveLatchCount % 30 == 0)
            {
                QuestObjectronLogger.Detect(
                    $"live_latch #{m_liveLatchCount} stable={m_latchStableFrames}/{RequiredStableLatchFrames} " +
                    $"candidates={liveCandidates.Count} localized={m_localizedChairs.Count}");
            }

            ReportDetectionHud(lifted, cameraPose, count);
        }

        private void UpdateLatchStability(PlacementOutput candidate)
        {
            if (candidate.Corners == null || candidate.Method == PlacementMethod.None)
            {
                ResetLatchStability();
                return;
            }

            var center = candidate.Corners[0];
            if (m_latchCenter.HasValue
                && Vector3.Distance(m_latchCenter.Value, center) <= LatchStabilityRadiusM)
            {
                m_latchStableFrames++;
            }
            else
            {
                m_latchStableFrames = 1;
            }

            m_latchCenter = center;
        }

        private void ResetLatchStability()
        {
            m_latchCenter = null;
            m_latchStableFrames = 0;
        }

        private bool CanAutoLocalize()
        {
            if (!Scanning)
            {
                return false;
            }

            if (m_latchStableFrames < RequiredStableLatchFrames)
            {
                return false;
            }

            return Time.realtimeSinceStartup - m_lastNewLocalizeTime >= NewLocalizeCooldownSec;
        }

        private bool TryLocalizeNewChair(PlacementOutput output, ObjectAnnotation annotation)
        {
            if (output.Corners == null
                || output.Method == PlacementMethod.None
                || annotation == null)
            {
                return false;
            }

            if (ObjectronChairDedup.FindDuplicateIndex(m_localizedChairs, output.ObjectId, output.Corners) >= 0)
            {
                return false;
            }

            if (!ObjectronDetectionQuality.TryEvaluate(output, 0f, 0f, out var quality)
                || !ObjectronChairSizeFit.TryScore(output.Corners, out _, out var detectedSortedM))
            {
                return false;
            }

            var corners = (Vector3[])output.Corners.Clone();
            if (TrySnapLocalizedCorners(annotation, corners, out var snapped))
            {
                corners = snapped;
            }

            m_localizedChairs.Add(new ObjectronLocalizedChairState
            {
                ObjectId = output.ObjectId,
                Method = output.Method,
                Corners = corners,
                Annotation = annotation,
                DebugReport = output.DebugReport,
                SizeFitScore = quality.SizeFitScore,
                QualityScore = quality.Score,
                DetectedExtentsSortedM = detectedSortedM,
                LastRefineTime = Time.realtimeSinceStartup,
                LastQuality = quality,
            });

            m_lastNewLocalizeTime = Time.realtimeSinceStartup;
            ApplyAdaptiveGraphCap();
            QuestObjectronLogger.Detect(
                $"chair_localized id={output.ObjectId} method={output.Method} " +
                $"center={corners[0]:F2} quality={quality.Score:F3} stable={m_latchStableFrames} " +
                $"total={m_localizedChairs.Count} — point at next chair");
            return true;
        }

        private bool TrySnapLocalizedCorners(
            ObjectAnnotation annotation,
            Vector3[] corners,
            out Vector3[] snapped)
        {
            snapped = null;
            if (m_environmentRaycast == null || !m_environmentRaycast.HasScenePermission())
            {
                return false;
            }

            var modelHalf = ObjectronMediaPipeCoordinates.GetHalfExtents(annotation);
            if (!ObjectronFloorPlaneSnap.TrySnapBoxToFloor(
                    m_environmentRaycast,
                    annotation.ObjectId,
                    corners,
                    modelHalf,
                    out var floored,
                    out _,
                    out _))
            {
                return false;
            }

            snapped = floored;
            return true;
        }

        private bool ShouldAttemptRefine(ObjectronLocalizedChairState chair, float centerJumpM)
        {
            var now = Time.realtimeSinceStartup;
            if (now - chair.LastRefineTime < RefineCooldownSec && centerJumpM < RefineMotionBypassM)
            {
                return false;
            }

            return true;
        }

        private void ShowLivePreviewBoxes(IReadOnlyList<Vector3[]> candidateCorners)
        {
            if (m_bboxDrawer == null)
            {
                return;
            }

            if (!Scanning || candidateCorners == null || candidateCorners.Count == 0)
            {
                m_bboxDrawer.SetDetections(null);
                return;
            }

            m_bboxDrawer.SetDetections(candidateCorners);
        }

        private void ProcessEmptyDetections()
        {
            if (m_localizedChairs.Count > 0)
            {
                m_bboxDrawer?.SetDetections(null);
                m_emptyLogCount++;
                var primary = m_localizedChairs[0];
                PushHud(
                    $"localized ({m_localizedChairs.Count}/{MaxLocalizedChairs})",
                    primary.Corners?[0],
                    GetExtent(primary.Corners),
                    GetDistance(primary.Corners?[0]),
                    primary.ObjectId,
                    primary.Method);
                return;
            }

            m_bboxDrawer?.SetDetections(null);
            m_detectionDebug?.Clear();
            m_emptyLogCount++;
            PushHud("scanning", null, 0f, -1f, -1, PlacementMethod.None);
            if (m_emptyLogCount == 1 || m_emptyLogCount % 45 == 0)
            {
                QuestObjectronLogger.Detect("scanning — point at chair ~1-3m; Y (left)=reset scan");
            }
        }

        private bool TryRefineLocalizedChair(
            int index,
            ObjectAnnotation annotation,
            PlacementOutput heavy)
        {
            var chair = m_localizedChairs[index];
            var centerJumpM = heavy.Corners != null && chair.Corners != null
                ? Vector3.Distance(heavy.Corners[0], chair.Corners[0])
                : 0f;

            if (!ShouldAttemptRefine(chair, centerJumpM))
            {
                return false;
            }

            if (heavy.Corners == null || heavy.Method == PlacementMethod.None)
            {
                return false;
            }

            if (!ObjectronDetectionQuality.TryEvaluate(heavy, 0f, centerJumpM, out var quality))
            {
                return false;
            }

            if (!ObjectronDetectionQuality.IsBetterThan(quality, chair.LastQuality))
            {
                return false;
            }

            if (!ObjectronChairSizeFit.TryScore(heavy.Corners, out var sizeScore, out var detectedSortedM))
            {
                return false;
            }

            var corners = (Vector3[])heavy.Corners.Clone();
            if (TrySnapLocalizedCorners(annotation, corners, out var snapped))
            {
                corners = snapped;
            }

            var previousScore = chair.QualityScore;
            var previousExtents = chair.DetectedExtentsSortedM;
            chair.ObjectId = heavy.ObjectId;
            chair.Method = heavy.Method;
            chair.Corners = corners;
            chair.Annotation = annotation;
            chair.DebugReport = heavy.DebugReport;
            chair.SizeFitScore = sizeScore;
            chair.QualityScore = quality.Score;
            chair.DetectedExtentsSortedM = detectedSortedM;
            chair.LastRefineTime = Time.realtimeSinceStartup;
            chair.LastQuality = quality;
            QuestObjectronLogger.Detect(
                $"chair_refined idx={index} id={heavy.ObjectId} " +
                $"quality {previousScore:F3}->{quality.Score:F3} " +
                $"edges {ObjectronChairSizeFit.FormatExtentsCm(previousExtents)}->" +
                $"{ObjectronChairSizeFit.FormatExtentsCm(detectedSortedM)}");
            return true;
        }

        private void RefreshLocalizedVisuals()
        {
            m_lastWorldBoxes = BuildLocalizedWorldBoxes();
            m_bboxDrawer?.SetDetections(null);
            m_questVisuals?.Localize(m_lastWorldBoxes);

            if (m_lastWorldBoxes.Count > 0 && m_localizedChairs.Count > 0)
            {
                var chair = m_localizedChairs[m_localizedChairs.Count - 1];
                m_lastPinSnapshot = new ObjectronPinSnapshot(
                    chair.ObjectId,
                    chair.Method,
                    m_cameraAccess.GetCameraPose(),
                    m_lastWorldBoxes[m_lastWorldBoxes.Count - 1],
                    chair.Annotation,
                    chair.DebugReport);
            }
        }

        private List<Vector3[]> BuildLocalizedWorldBoxes()
        {
            var boxes = new List<Vector3[]>(m_localizedChairs.Count);
            foreach (var chair in m_localizedChairs)
            {
                if (chair.Corners != null)
                {
                    boxes.Add(chair.Corners);
                }
            }

            return boxes;
        }

        private void ReportDetectionHud(FrameAnnotation lifted, Pose cameraPose, int frameCount)
        {
            var primary = m_localizedChairs.Count > 0
                ? m_localizedChairs[m_localizedChairs.Count - 1]
                : null;
            var ann = primary?.Annotation ?? lifted.Annotations[0];
            Vector3? camT = null;
            Vector3? worldCenter = null;
            if (ann.Translation != null && ann.Translation.Count >= 3)
            {
                var t = new Vector3(ann.Translation[0], ann.Translation[1], ann.Translation[2]);
                camT = t;
                worldCenter = primary?.Corners?[0] ?? cameraPose.position + cameraPose.rotation * t;
            }

            var worldBoxes = m_lastWorldBoxes ?? BuildLocalizedWorldBoxes();
            var placementMethod = primary?.Method ?? PlacementMethod.None;
            var sizeFit = primary != null ? primary.SizeFitScore : float.NaN;
            var detail =
                $"localized={m_localizedChairs.Count}/{MaxLocalizedChairs} scanning={Scanning} " +
                $"size_fit={(float.IsNaN(sizeFit) ? "—" : sizeFit.ToString("F3"))} " +
                $"kp2d={CountKeypointsWith2D(ann)} kp3d={CountKeypointsWith3D(ann)}";
            m_detectionDebug?.Report(new DetectionDebugInfo(
                camT.HasValue
                    ? $"chair cam=({camT.Value.x:F2},{camT.Value.y:F2},{camT.Value.z:F2})m"
                    : "chair detected",
                detail,
                worldCenter,
                worldBoxes.Count > 0 ? worldBoxes[0] : null));

            m_lastHudObjectId = primary?.ObjectId ?? ann.ObjectId;
            m_lastPlacementMethod = placementMethod.ToString();
            var state = Scanning
                ? $"scanning ({m_localizedChairs.Count}/{MaxLocalizedChairs})"
                : $"done ({m_localizedChairs.Count})";
            PushHud(state, worldCenter, GetExtent(worldBoxes.Count > 0 ? worldBoxes[0] : null),
                GetDistance(worldCenter), m_lastHudObjectId, placementMethod);
        }

        private static float GetExtent(Vector3[] corners)
        {
            if (corners == null)
            {
                return 0f;
            }

            ObjectronBoxValidation.TryGetExtentMeters(corners, out var extent);
            return extent;
        }

        private static float GetDistance(Vector3? worldCenter)
        {
            if (!worldCenter.HasValue)
            {
                return -1f;
            }

            var hmd = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            return Vector3.Distance(hmd, worldCenter.Value);
        }

        private static ObjectAnnotation FindAnnotation(FrameAnnotation lifted, int objectId)
        {
            if (lifted?.Annotations == null)
            {
                return null;
            }

            foreach (var annotation in lifted.Annotations)
            {
                if (annotation.ObjectId == objectId)
                {
                    return annotation;
                }
            }

            return null;
        }

        private void PushHud(
            string detectState,
            Vector3? worldCenter,
            float extent,
            float distHmd,
            int objectId,
            PlacementMethod placementMethod)
        {
            if (m_headsetHud == null)
            {
                return;
            }

            var rotLabel = m_imageSource != null
                ? $"{m_imageSource.rotation} flip={m_imageSource.isHorizontallyFlipped}"
                : "n/a";
            var pcaRes = "n/a";
            if (m_cameraAccess != null && m_cameraAccess.IsPlaying)
            {
                var res = m_cameraAccess.CurrentResolution;
                pcaRes = $"{Mathf.RoundToInt(res.x)}x{Mathf.RoundToInt(res.y)}";
            }

            var hint = BuildHudHint(m_localizedChairs.Count, MaxLocalizedChairs);
            m_headsetHud.Apply(new ObjectronHudSnapshot(
                detectState,
                rotLabel,
                worldCenter,
                extent,
                distHmd,
                m_questVisuals != null ? m_questVisuals.ActiveCount : 0,
                objectId,
                placementMethod == PlacementMethod.None ? m_lastPlacementMethod : placementMethod.ToString(),
                m_frameId,
                pcaRes,
                hint,
                false,
                false,
                false));
        }

        private static string BuildHudHint(int localizedCount, int maxChairs)
        {
            if (localizedCount >= maxChairs)
            {
                return $"all {maxChairs} chairs localized — frozen until better view; Y=reset";
            }

            if (localizedCount > 0)
            {
                return $"localized {localizedCount}/{maxChairs} — thick=scanning thin=pinned frozen; Y=reset";
            }

            return "scanning — point at each chair; hold steady ~0.5s to pin; then next chair";
        }

        private static int CountKeypointsWith2D(ObjectAnnotation ann)
        {
            if (ann.Keypoints == null)
            {
                return 0;
            }

            var n = 0;
            foreach (var kp in ann.Keypoints)
            {
                if (kp.Point2D != null)
                {
                    n++;
                }
            }

            return n;
        }

        private static int CountKeypointsWith3D(ObjectAnnotation ann)
        {
            if (ann.Keypoints == null)
            {
                return 0;
            }

            var n = 0;
            foreach (var kp in ann.Keypoints)
            {
                if (kp.Point3D != null)
                {
                    n++;
                }
            }

            return n;
        }

        private void CopyCameraToFrame(TextureFrame textureFrame)
        {
            var tex = ImageSourceProvider.ImageSource?.GetCurrentTexture();
            if (tex == null)
            {
                return;
            }

            var source = ImageSourceProvider.ImageSource;
            var sourceTexture = source?.GetCurrentTexture();
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
    }
}
