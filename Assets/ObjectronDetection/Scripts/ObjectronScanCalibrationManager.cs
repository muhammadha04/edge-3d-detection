// Align lab-chair scan with a live Objectron chair box; save relative pose for future mesh placement.

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
    public enum ScanCalibrationState
    {
        Booting,
        Ready,
        BoxShown,
        ScanSpawned,
        ScanFrozen,
        Saved,
    }

    public class ObjectronScanCalibrationManager : MonoBehaviour
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
        [SerializeField] private ObjectronScanManipulator m_scanManipulator;
        [SerializeField] private GameObject m_scanModelPrefab;

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
        private int m_lastObjectId = -1;
        private int m_liveDetectionCount;
        private bool m_graphReady;
        private ScanCalibrationState m_state = ScanCalibrationState.Booting;
        private Coroutine m_pipeline;
        private int m_frameId;
        private bool m_shutdownForSceneExit;

        public ScanCalibrationState State => m_state;
        public bool IsReady => m_graphReady;

        private void Awake()
        {
            m_shutdownForSceneExit = false;
            if (m_placementOptions == null)
            {
                m_placementOptions = new ObjectronPlacementOptions();
            }

            m_placementOptions.CompensateHeadRoll = true;
            m_placementOptions.ConstrainUprightOnTable = true;
            m_placementOptions.EnableFloorSnap = true;
            m_placementOptions.DisableMaskAlignedFallback = true;
            WireReferences();
            ApplyLaunchSettings();
            ApplyGraphTuning();
            m_visuals.Prewarm();
            m_scanManipulator.Configure(m_scanModelPrefab);
            if (!m_scanManipulator.PreloadResources())
            {
                QuestObjectronLogger.Err(
                    "scan_calibration lab-chair not found in Resources — rebuild after Unity imports ScanCalibration/LabChair.obj");
            }
        }

        private void WireReferences()
        {
            m_cameraAccess ??= FindAnyObjectByType<PassthroughCameraAccess>();
            m_environmentRaycast ??= FindAnyObjectByType<EnvironmentRayCastSampleManager>();
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

            m_visuals ??= GetComponent<ObjectronLabeledBoxVisuals>()
                ?? gameObject.AddComponent<ObjectronLabeledBoxVisuals>();
            m_textureFramePool ??= GetComponent<TextureFramePool>() ?? gameObject.AddComponent<TextureFramePool>();
            m_objectronGraph ??= GetComponent<ObjectronGraph>() ?? gameObject.AddComponent<ObjectronGraph>();
            m_bootstrap ??= FindAnyObjectByType<Bootstrap>();
            m_scanManipulator ??= GetComponent<ObjectronScanManipulator>()
                ?? gameObject.AddComponent<ObjectronScanManipulator>();

            if (GetComponent<ObjectronScanCalibrationUi>() == null)
            {
                gameObject.AddComponent<ObjectronScanCalibrationUi>();
            }
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
            m_scanManipulator?.UpdateManipulation();
            TryControllerInput();
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
            m_visuals?.Clear();
            m_scanManipulator?.Clear();
            m_lastCorners = null;
            m_lastScale = default;
            m_lastTranslation = default;
            m_lastRotationEuler = default;
            m_lastObjectId = -1;
            m_liveDetectionCount = 0;
            m_frameId = 0;
            m_state = ScanCalibrationState.Booting;

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

            QuestObjectronLogger.Boot("scan_calibration_shutdown");
        }

        public void CaptureAndDetect()
        {
            if (!m_graphReady)
            {
                QuestObjectronLogger.Err("scan_calibration capture skipped — graph not ready");
                return;
            }

            if (m_lastCorners == null)
            {
                m_visuals?.Clear();
                m_state = ScanCalibrationState.Ready;
                QuestObjectronLogger.Detect(
                    $"scan_calibration no chair latched yet (live_count={m_liveDetectionCount}) — point at chair, wait, press B");
                return;
            }

            m_visuals.Show(m_lastCorners, m_lastScale, m_lastTranslation, m_lastRotationEuler, m_lastCameraPose);
            m_state = ScanCalibrationState.BoxShown;
            QuestObjectronLogger.Detect(
                $"scan_calibration box shown id={m_lastObjectId} scale=({m_lastScale.x:F2},{m_lastScale.y:F2},{m_lastScale.z:F2})");
        }

        public void ClearDetectionBox()
        {
            m_visuals?.Clear();
            m_state = m_graphReady ? ScanCalibrationState.Ready : ScanCalibrationState.Booting;
            QuestObjectronLogger.Viz("scan_calibration detection box cleared (A)");
        }

        public int LiveDetectionCount => m_liveDetectionCount;
        public bool HasLatchedCorners => m_lastCorners != null;

        public void TrySpawnScan()
        {
            if (m_state != ScanCalibrationState.BoxShown || m_lastCorners == null)
            {
                QuestObjectronLogger.Viz("scan_calibration spawn skipped — capture detection box with B first");
                return;
            }

            if (m_scanManipulator.TrySpawnAtControllerAim())
            {
                m_state = ScanCalibrationState.ScanSpawned;
            }
        }

        public void TryFreezeScan()
        {
            if (!m_scanManipulator.HasSpawned)
            {
                QuestObjectronLogger.Viz("scan_calibration freeze skipped — spawn scan first (right trigger tap)");
                return;
            }

            if (m_scanManipulator.IsFrozen)
            {
                QuestObjectronLogger.Viz("scan_calibration already frozen — press Left X to save");
                return;
            }

            if (m_scanManipulator.TryFreeze())
            {
                m_state = ScanCalibrationState.ScanFrozen;
                QuestObjectronLogger.Detect("scan_calibration frozen — press Left X to save");
            }
        }

        public void TrySaveCalibration()
        {
            if (!m_scanManipulator.HasSpawned || !m_scanManipulator.IsFrozen || m_lastCorners == null)
            {
                QuestObjectronLogger.Viz(
                    "scan_calibration save skipped — need B box, spawned scan, Y freeze, then X");
                return;
            }

            var record = ObjectronScanCalibrationRecord.Create(
                m_lastObjectId,
                m_lastCorners,
                m_lastTranslation,
                m_lastScale,
                m_lastRotationEuler,
                m_lastCameraPose,
                m_scanManipulator.ScanRoot,
                m_scanManipulator.MeshBoundsLocal);

            if (record == null)
            {
                QuestObjectronLogger.Err("scan_calibration save failed — record build failed");
                return;
            }

            if (ObjectronScanCalibrationStore.Save(record))
            {
                m_state = ScanCalibrationState.Saved;
                QuestObjectronLogger.Detect("scan_calibration saved — see SCAN_CALIBRATION_JSON in logcat");
            }
        }

        private void TryControllerInput()
        {
            if (ObjectronQuestControllerButtons.RightBPressed())
            {
                CaptureAndDetect();
            }

            if (ObjectronQuestControllerButtons.RightAPressed())
            {
                ClearDetectionBox();
            }

            if (ObjectronQuestControllerButtons.RightTriggerPressed() && !m_scanManipulator.HasSpawned)
            {
                TrySpawnScan();
            }

            if (ObjectronQuestControllerButtons.LeftYPressed())
            {
                TryFreezeScan();
            }

            if (ObjectronQuestControllerButtons.LeftXPressed())
            {
                TrySaveCalibration();
            }
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
            m_lastObjectId = ann.ObjectId;
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
                QuestObjectronLogger.Detect(
                    $"scan_calibration live latch #{m_liveDetectionCount} id={m_lastObjectId} " +
                    $"scale=({m_lastScale.x:F2},{m_lastScale.y:F2},{m_lastScale.z:F2})");
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
                QuestObjectronLogger.Err("scan_calibration image source failed");
                yield break;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!GpuManager.IsInitialized)
            {
                QuestObjectronLogger.Err("scan_calibration GpuManager not initialized");
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
                QuestObjectronLogger.Err($"scan_calibration graph init failed: {initRequest.error}");
                yield break;
            }

            if (m_runningMode == RunningMode.Async)
            {
                m_objectronGraph.OnLiftedObjectsOutput += OnLiftedObjectsAsync;
            }

            m_objectronGraph.StartRun(m_imageSource);
            m_graphReady = true;
            m_state = ScanCalibrationState.Ready;
            QuestObjectronLogger.Detect(
                "scan_calibration graph_started — B=box A=clear trigger=spawn grip=move trigger-hold=rotate both-grip=scale left-Y=freeze left-X=save");

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
                    if (m_objectronGraph.TryGetNext(out var lifted, out _, out _, false))
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
                QuestObjectronLogger.Boot("scan_calibration bootstrap reused (GpuManager ready)");
                yield break;
            }
#endif

            m_bootstrap ??= FindAnyObjectByType<Bootstrap>();
            if (m_bootstrap == null)
            {
                QuestObjectronLogger.Err("scan_calibration Bootstrap not found");
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
                    QuestObjectronLogger.Err("scan_calibration bootstrap timeout");
                    yield break;
                }

                yield return null;
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
    }
}
