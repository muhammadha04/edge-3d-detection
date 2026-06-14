// Main pipeline: PCA camera -> MediaPipe Objectron (Chair) -> world-space bounding boxes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        private const int InferenceEveryNFrames = 2;
        /// <summary>Min interval between main-thread detection process + ok logs (stops objectId 1–21 spam).</summary>
        private const float DetectionProcessMinInterval = 0.2f;
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
        [SerializeField] private ObjectronScanMeshVisuals m_scanMeshVisuals;
        [SerializeField] private ObjectronLiveMeshTuner m_liveMeshTuner;
        [SerializeField] private ObjectronHeadsetHud m_headsetHud;
        [SerializeField] private ObjectronPassthroughOverlay m_passthroughOverlay;
        [SerializeField] private ObjectronEnvironmentDepthProvider m_environmentDepthProvider;

        [Header("Tuning")]
        [SerializeField] private ObjectronPlacementOptions m_placementOptions = new();
        [SerializeField] private RunningMode m_runningMode = RunningMode.Async;
        [SerializeField] private float m_minDetectionConfidence = 0.35f;
        [SerializeField] private float m_minTrackingConfidence = 0.55f;

        private readonly object m_pendingLock = new();
        private readonly ObjectronFramePoseQueue m_framePoses = new();
        private FrameAnnotation m_pendingFrame;
        private bool m_hasPendingFrame;

        private ObjectronWorldPlacement m_worldPlacement;

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

            if (m_scanMeshVisuals == null)
            {
                m_scanMeshVisuals = GetComponent<ObjectronScanMeshVisuals>()
                    ?? gameObject.AddComponent<ObjectronScanMeshVisuals>();
            }

            if (m_liveMeshTuner == null)
            {
                m_liveMeshTuner = GetComponent<ObjectronLiveMeshTuner>()
                    ?? gameObject.AddComponent<ObjectronLiveMeshTuner>();
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
            if (ObjectronLaunchSettings.ShowScanMeshOverlay)
            {
                m_scanMeshVisuals.Prewarm();
            }
            m_bboxDrawer?.Prewarm();
            m_detectionDebug?.Prewarm();
        }

        private void Update()
        {
            TryControllerInput();
            m_liveMeshTuner?.UpdateFrame();
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
                    if (ObjectronLaunchSettings.EnableUprightPresetMode
                        && m_liveMeshTuner != null
                        && m_liveMeshTuner.HasSelection)
                    {
                        m_liveMeshTuner.TryResetSelectionToUprightBase();
                    }
                    else
                    {
                        m_liveMeshTuner?.ClearSelection();
                        ResetDetection();
                    }
                }
            }

            if (ObjectronLaunchSettings.ShowScanMeshOverlay
                && (ObjectronLaunchSettings.EnableLiveMeshTune || ObjectronLaunchSettings.EnableUprightPresetMode)
                && ObjectronQuestControllerButtons.LeftXPressed()
                && m_cameraAccess != null)
            {
                m_liveMeshTuner?.TrySaveSelection(m_cameraAccess.GetCameraPose());
            }
#endif
        }

        /// <summary>Clear localized chairs without user-facing reset logs (scene exit / shutdown).</summary>
        private void ClearLocalizedStateSilent()
        {
            m_localizedChairs.Clear();
            m_lastWorldBoxes = null;
            m_lastLoggedLocalizedCount = -1;
            m_questVisuals?.ClearLocalization(silent: true);
            m_scanMeshVisuals?.Localize(null);
            m_liveMeshTuner?.ClearSelection();
            m_detectionDebug?.Clear();
            m_bboxDrawer?.SetDetections(null);
        }

        /// <summary>Clear all localized chairs and restart the one-shot scan (Y on left controller).</summary>
        public void ResetDetection()
        {
            ClearLocalizedStateSilent();
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

            m_placementOptions.CompensateHeadRoll = true;
            m_placementOptions.ConstrainUprightOnTable = true;
            m_placementOptions.EnableFloorSnap = true;
            m_placementOptions.DisableMaskAlignedFallback = true;
            SyncPlacementOptionsFromImageSource();
            ObjectronPlacementFixSettings.Active = m_placementOptions;
            m_worldPlacement = new ObjectronWorldPlacement(
                m_cameraAccess,
                m_environmentRaycast,
                m_placementOptions);

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
                $"launch_settings max_objects={MaxLocalizedChairs} " +
                $"detect_conf={m_minDetectionConfidence:F2} track_conf={m_minTrackingConfidence:F2} " +
                $"scan_mesh={ObjectronLaunchSettings.ShowScanMeshOverlay} " +
                $"live_tune={ObjectronLaunchSettings.EnableLiveMeshTune} " +
                $"upright_preset={ObjectronLaunchSettings.EnableUprightPresetMode}");
            QuestObjectronLogger.Boot($"placement_options: {m_placementOptions.Summary}");
        }

        private void SyncPlacementOptionsFromImageSource()
        {
            if (m_imageSource == null)
            {
                return;
            }

            m_placementOptions.MirrorInferenceHorizontal = m_imageSource.isHorizontallyFlipped;
            if (m_worldPlacement != null)
            {
                m_worldPlacement.SetMirrorHorizontal(m_placementOptions.MirrorInferenceHorizontal);
            }
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
            }

            m_objectronGraph.StartRun(m_imageSource);
            m_graphReady = true;
            QuestObjectronLogger.Detect("graph_started model=Chair");

            var waitEndOfFrame = new WaitForEndOfFrame();

            while (true)
            {
                if (!m_cameraAccess.IsPlaying)
                {
                    yield return null;
                    continue;
                }

                if (!IsHeadPoseReliable())
                {
                    yield return waitEndOfFrame;
                    continue;
                }

                m_frameId++;
                if (m_frameId % InferenceEveryNFrames != 0)
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
                        yield return new WaitUntil(() =>
                            m_objectronGraph.TryGetNext(out lifted, out _, out _, false));
                        ProcessDetections(lifted, cameraPose, t0);
                        break;
                    }
                    case RunningMode.Sync:
                        if (m_objectronGraph.TryGetNext(out var liftedSync, out _, out _, true))
                        {
                            ProcessDetections(liftedSync, cameraPose, t0);
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

                var now = Time.realtimeSinceStartup;
                if (now - m_lastDetectionProcessTime < DetectionProcessMinInterval)
                {
                    return;
                }

                frame = m_pendingFrame;
                m_hasPendingFrame = false;
                m_lastDetectionProcessTime = now;
            }

            var cameraPose = m_framePoses.DequeueOrCurrent(() => m_cameraAccess.GetCameraPose());
            if (m_worldPlacement != null && m_imageSource != null)
            {
                m_worldPlacement.SetMirrorHorizontal(m_imageSource.isHorizontallyFlipped);
            }

            ProcessDetections(frame, cameraPose, m_lastDetectionProcessTime);
        }

        private void ProcessDetections(FrameAnnotation lifted, Pose cameraPose, float startTime)
        {
            if (lifted == null || lifted.Annotations == null || lifted.Annotations.Count == 0)
            {
                ProcessEmptyDetections();
                return;
            }

            m_emptyLogCount = 0;

            var count = lifted.Annotations.Count;
            var ms = (Time.realtimeSinceStartup - startTime) * 1000f;
            if (count != m_lastLoggedOkCount)
            {
                m_lastLoggedOkCount = count;
                QuestObjectronLogger.Detect($"ok count={count} ms={ms:F0}");
            }

            var placementOutputs = m_worldPlacement.PlaceDetailed(lifted, cameraPose, null);
            ProcessChairPlacements(lifted, placementOutputs);

            ReportDetectionHud(lifted, cameraPose, count);
        }

        private void ProcessEmptyDetections()
        {
            if (m_localizedChairs.Count > 0)
            {
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

        private void ProcessChairPlacements(FrameAnnotation lifted, IReadOnlyList<PlacementOutput> placementOutputs)
        {
            var changed = false;
            var acceptedThisFrame = new List<PlacementOutput>();
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

                if (IsDuplicatePlacement(output, acceptedThisFrame))
                {
                    QuestObjectronLogger.Detect(
                        $"chair_duplicate_skipped id={output.ObjectId} center={output.Corners[0]:F2}");
                    continue;
                }

                if (!ObjectronChairSizeFit.TryScore(output.Corners, out var sizeScore, out var detectedSortedM))
                {
                    continue;
                }

                var center = output.Corners[0];
                var existingIndex = ObjectronChairDedup.FindDuplicateIndex(
                    m_localizedChairs, output.ObjectId, output.Corners);
                if (existingIndex >= 0)
                {
                    if (TryRefineLocalizedChair(existingIndex, output, lifted, sizeScore, detectedSortedM))
                    {
                        changed = true;
                    }

                    acceptedThisFrame.Add(output);
                    continue;
                }

                if (!Scanning)
                {
                    continue;
                }

                var annotation = FindAnnotation(lifted, output.ObjectId);
                m_localizedChairs.Add(new ObjectronLocalizedChairState
                {
                    ObjectId = output.ObjectId,
                    Method = output.Method,
                    Corners = (Vector3[])output.Corners.Clone(),
                    Annotation = annotation,
                    DebugReport = output.DebugReport,
                    SizeFitScore = sizeScore,
                    DetectedExtentsSortedM = detectedSortedM,
                });
                changed = true;
                QuestObjectronLogger.Detect(
                    $"chair_localized id={output.ObjectId} method={output.Method} " +
                    $"center={center:F2} edges={ObjectronChairSizeFit.FormatExtentsCm(detectedSortedM)} " +
                    $"size_fit={sizeScore:F3} total={m_localizedChairs.Count}");
                acceptedThisFrame.Add(output);
            }

            if (!changed)
            {
                return;
            }

            if (m_localizedChairs.Count != m_lastLoggedLocalizedCount)
            {
                m_lastLoggedLocalizedCount = m_localizedChairs.Count;
                QuestObjectronLogger.Detect($"chair_localized_total={m_localizedChairs.Count}");
            }

            RefreshLocalizedVisuals();
        }

        private bool TryRefineLocalizedChair(
            int index,
            PlacementOutput output,
            FrameAnnotation lifted,
            float candidateScore,
            Vector3 candidateSortedM)
        {
            var chair = m_localizedChairs[index];
            if (!ObjectronChairSizeFit.IsBetterFit(candidateScore, chair.SizeFitScore))
            {
                return false;
            }

            var previousScore = chair.SizeFitScore;
            var previousExtents = chair.DetectedExtentsSortedM;
            chair.ObjectId = output.ObjectId;
            chair.Method = output.Method;
            chair.Corners = (Vector3[])output.Corners.Clone();
            chair.Annotation = FindAnnotation(lifted, output.ObjectId);
            chair.DebugReport = output.DebugReport;
            chair.SizeFitScore = candidateScore;
            chair.DetectedExtentsSortedM = candidateSortedM;
            QuestObjectronLogger.Detect(
                $"chair_size_refined idx={index} id={output.ObjectId} " +
                $"fit {previousScore:F3}->{candidateScore:F3} " +
                $"edges {ObjectronChairSizeFit.FormatExtentsCm(previousExtents)}->" +
                $"{ObjectronChairSizeFit.FormatExtentsCm(candidateSortedM)} " +
                $"ref={ObjectronChairSizeFit.FormatExtentsCm(ObjectronChairSizeFit.ReferenceExtentsM)}");
            return true;
        }

        private static bool IsDuplicatePlacement(
            PlacementOutput candidate,
            IReadOnlyList<PlacementOutput> acceptedThisFrame)
        {
            foreach (var existing in acceptedThisFrame)
            {
                if (ObjectronChairDedup.AreSameChair(
                        candidate.ObjectId,
                        candidate.Corners,
                        existing.ObjectId,
                        existing.Corners))
                {
                    return true;
                }
            }

            return false;
        }

        private void RefreshLocalizedVisuals()
        {
            m_lastWorldBoxes = BuildLocalizedWorldBoxes();
            m_bboxDrawer?.SetDetections(null);
            m_questVisuals?.Localize(m_lastWorldBoxes);
            RefreshScanMeshVisuals();

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

        private void RefreshScanMeshVisuals()
        {
            if (!ObjectronLaunchSettings.ShowScanMeshOverlay)
            {
                m_scanMeshVisuals?.Localize(null);
                return;
            }

            m_scanMeshVisuals?.Localize(m_lastWorldBoxes, BuildLocalizedModelScales());
        }

        public void RefreshScanMeshVisualsAfterCalibrationSave()
        {
            ObjectronScanCalibrationDefaults.ClearCache();
            RefreshScanMeshVisuals();
        }

        internal bool TryGetLocalizedChairForPoolIndex(int poolIndex, out ObjectronLocalizedChairState chair)
        {
            chair = null;
            if (poolIndex < 0 || m_localizedChairs == null || poolIndex >= m_localizedChairs.Count)
            {
                return false;
            }

            chair = m_localizedChairs[poolIndex];
            return chair?.Corners != null && chair.Corners.Length >= 9;
        }

        private List<Vector3> BuildLocalizedModelScales()
        {
            var scales = new List<Vector3>(m_localizedChairs.Count);
            foreach (var chair in m_localizedChairs)
            {
                scales.Add(ReadAnnotationModelScale(chair.Annotation));
            }

            return scales;
        }

        private static Vector3 ReadAnnotationModelScale(ObjectAnnotation ann)
        {
            if (ann?.Scale != null && ann.Scale.Count >= 3)
            {
                return new Vector3(ann.Scale[0], ann.Scale[1], ann.Scale[2]);
            }

            return Vector3.zero;
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
                return $"all {maxChairs} chairs localized — refining size; Y=reset";
            }

            if (localizedCount > 0)
            {
                return $"localized {localizedCount}/{maxChairs} — refining size; Y=reset";
            }

            return "scanning — point at each chair (~45×45×90 cm); Y=reset";
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

        private static bool IsHeadPoseReliable()
        {
            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

            return ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess();
        }
    }
}
