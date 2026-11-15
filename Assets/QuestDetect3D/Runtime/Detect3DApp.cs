// QuestDetect3D — orchestrates two interchangeable on-device pipelines (chosen in the start menu):
//   YOLO + depth (default): PCA GPU texture → YOLOv9-t (Unity Inference Engine) 2D boxes → Quest depth rays
//       inside each box → DepthBoxFitter metric upright box + per-term uncertainty → multi-class tracker.
//   Objectron (v2 baseline): PCA frame → MediaPipe Objectron 9 keypoints → BoxSolver (LM on support plane).
//   Hybrid: YOLO + depth as above (recall, metric box); Objectron runs only on a crop around one YOLO chair box
//       at a time → keypoint box gives the front (heading) and a second size/centre estimate; HybridFuser checks
//       the result against depth (support, free-space violations, agreement) and scales the observation quality.
// Both feed the same multi-view tracker with confidence → anchored, uncertainty-scaled 3D boxes + HUD.
//
// A start menu (SessionMenu) picks the display mode (box, or lab-chair scan + cyan keypoints) and the minimum
// confidence a box needs before it is drawn. Every session starts empty; nothing is saved between launches
// except those two menu settings.
//
// Controls (Quest Touch):
//   A (right) / Menu (left)  open the start menu (Start there begins a fresh session)
//   B (right)  toggle debug overlay (network keypoints vs fitted box)
//   Y (left)   reset all objects
//   X (left)   show / hide HUD
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR;
using QuestDetect3D.Annotation;
using QuestDetect3D.Recording;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class Detect3DApp : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_camera;
        [SerializeField] private EnvironmentRaycastManager m_raycast;
        [SerializeField] private ObjectronDetector m_detector;
        [SerializeField] private ModelAsset m_yoloModel;
        [SerializeField] private TextAsset m_yoloLabels;
        [SerializeField] private Shader m_overlayShader;
        [SerializeField] private bool m_useSpatialAnchors = true;
        [SerializeField] private AnnotationMode m_annotate;

        private readonly ObjectTracker m_tracker = new ObjectTracker();
        private readonly FloorEstimator m_floor = new FloorEstimator();
        private readonly Dictionary<int, BoxVisual> m_visuals = new Dictionary<int, BoxVisual>();
        private readonly Dictionary<int, Transform> m_anchors = new Dictionary<int, Transform>();
        private readonly List<BoxObservation> m_observations = new List<BoxObservation>();
        private readonly StringBuilder m_hudText = new StringBuilder(512);
        private readonly Dictionary<int, BackrestEstimator> m_backrest = new Dictionary<int, BackrestEstimator>();
        private readonly List<TrackedObject> m_probeList = new List<TrackedObject>();
        private readonly List<DetectorFit> m_frameFits = new List<DetectorFit>();

        private const float ShowHysteresis = 0.03f;
        private const int BackrestRaysPerTrack = 12;
        private const int BackrestTracksPerFrame = 2;
        private int m_probeCursor;

        private AppSettings m_settings;
        private SessionMenu m_menu;

        // YOLO + depth pipeline
        private YoloDetector m_yolo;
        private Coroutine m_yoloInit;
        private EnvironmentDepthRaycaster m_depth;
        private readonly DepthBoxFitter m_fitter = new DepthBoxFitter();
        private readonly List<Vector3> m_fitPoints = new List<Vector3>(256);
        private int m_yoloFrameId;

        // Hybrid pipeline (YOLO + Objectron-in-box + depth checks)
        private static readonly Vector2Int HybridCropResolution = new Vector2Int(640, 480);
        private readonly HybridFuser m_fuser = new HybridFuser();
        private readonly List<KeypointEvidence> m_kpEvidence = new List<KeypointEvidence>();
        private readonly List<Rect> m_hybridTargets = new List<Rect>();
        private float m_hybridTargetsTime = -10f;
        private int m_hybridCursor;
        private int m_hybridDwell;
        private NativeArray<byte> m_cropBuffer;
        private Vector2Int m_objectronResolution;
        private int m_hybridCrops;
        private int m_hybridKeypoints;
        private int m_hybridFronts;
        private float m_lastVerification;
        private Quaternion m_lastHeadRotation = Quaternion.identity;
        private float m_headSpeedDegPerSec;
        private float m_nextMenuRefresh;
        private const float MaxHeadSpeedForCapture = 120f; // deg/s: skip motion-blurred frames

        private PcaFrameSource m_frames;
        private Transform m_head;
        private Material m_material;
        private Font m_font;
        private HudPanel m_hud;
        private DebugOverlay m_debug;
        private Transform m_root;
        private Coroutine m_detectorRoutine;
        private ObjectCategory m_category;
        private bool m_hudVisible = true;

        // Diagnostics
        private string m_lastFitSummary = "—";
        private int m_framesWithDetections;
        private int m_fitsOk;
        private int m_fitsFailed;
        private string m_lastFailReason = "";
        private float m_lastLogTime;

        private void Awake()
        {
            // Keep logcat readable: no managed stack traces on info logs.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            OVRPermissionsRequester.Request(new[]
            {
                OVRPermissionsRequester.Permission.Scene,
                OVRPermissionsRequester.Permission.PassthroughCameraAccess,
            });
        }

        private IEnumerator Start()
        {
            m_head = Camera.main != null ? Camera.main.transform : null;
            m_material = new Material(m_overlayShader != null ? m_overlayShader : Shader.Find("QuestDetect3D/Unlit Transparent"))
            {
                name = "QD3D_Overlay",
            };
            m_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_root = new GameObject("DetectedObjects").transform;
            m_hud = HudPanel.Create(m_material, m_font, m_head);
            m_debug = DebugOverlay.Create(m_material);
            m_tracker.TrackConfirmed += OnTrackConfirmed;
            m_tracker.TrackRemoved += OnTrackRemoved;
            // Chairs only for now (the other Objectron models stay in the build for later experiments).
            m_category = ObjectCategory.Chair;
            m_settings = AppSettings.Load();
            m_menu = SessionMenu.Create(m_material, m_font, m_head, m_settings);
            m_menu.Started += OnSessionStart;
            if (m_annotate != null)
            {
                m_annotate.ExitRequested += OnAnnotateExit;
            }

            m_depth = new EnvironmentDepthRaycaster(m_raycast);
            if (m_yoloModel != null)
            {
                m_yolo = new YoloDetector(m_yoloModel, m_yoloLabels);
                m_yoloInit = StartCoroutine(m_yolo.Initialize(ToBackend(m_settings.Backend)));
            }
            else
            {
                Log.Error("yolo_model missing: assign m_yoloModel (SceneBuilder does this)");
            }

            OpenMenu();

            Log.Info($"app_start version={Application.version} unity={Application.unityVersion} " +
                     $"pca_supported={PassthroughCameraAccess.IsSupported} depth_supported={EnvironmentRaycastManager.IsSupported}");

            m_frames = new PcaFrameSource(m_camera);
            while (m_camera == null || !m_camera.IsPlaying)
            {
                yield return null;
            }

            if (m_detector != null)
            {
                m_detector.Bind(m_camera);
                m_detector.FrameProcessed += OnDetections;
            }

            var intrinsics = m_camera.Intrinsics;
            Log.Info($"pca_ready res={m_camera.CurrentResolution.x}x{m_camera.CurrentResolution.y} " +
                     $"f=({intrinsics.FocalLength.x:F1},{intrinsics.FocalLength.y:F1}) " +
                     $"c=({intrinsics.PrincipalPoint.x:F1},{intrinsics.PrincipalPoint.y:F1}) " +
                     $"sensor={intrinsics.SensorResolution.x}x{intrinsics.SensorResolution.y}");
        }

        private static BackendType ToBackend(InferenceBackend b) => b == InferenceBackend.Cpu ? BackendType.CPU : BackendType.GPUCompute;

        private bool UseYolo => m_settings != null && (m_settings.Detector == DetectorKind.YoloDepth || m_settings.Detector == DetectorKind.Hybrid);

        private bool UseHybrid => m_settings != null && m_settings.Detector == DetectorKind.Hybrid;

        private Vector2Int ObjectronResolution => UseHybrid ? HybridCropResolution : m_camera.CurrentResolution;

        private void StartDetector(ObjectCategory category)
        {
            if (m_detectorRoutine != null)
            {
                StopCoroutine(m_detectorRoutine);
            }

            m_category = category;
            m_debug.Clear();
            m_objectronResolution = ObjectronResolution;
            m_kpEvidence.Clear();
            m_detectorRoutine = StartCoroutine(m_detector.Run(category, m_objectronResolution));
        }

        /// <summary>Run only the detector the session uses (Objectron costs GPU time even when idle).</summary>
        private void EnsureDetectorState()
        {
            var cameraReady = m_camera != null && m_camera.IsPlaying;
            var wantObjectron = !MenuOpen && !AnnotateActive && (!UseYolo || UseHybrid) && cameraReady && m_detector != null;
            if (wantObjectron && (m_detectorRoutine == null || m_objectronResolution != ObjectronResolution))
            {
                if (m_detectorRoutine != null)
                {
                    StopCoroutine(m_detectorRoutine);
                    m_detector.StopGraph();
                }

                StartDetector(ObjectCategory.Chair);
            }
            else if (!wantObjectron && m_detectorRoutine != null && ((UseYolo && !UseHybrid) || AnnotateActive))
            {
                StopCoroutine(m_detectorRoutine);
                m_detector.StopGraph();
                m_detectorRoutine = null;
                Log.Info("objectron_stopped");
            }
        }

        private bool MenuOpen => m_menu != null && m_menu.IsOpen;

        /// <summary>Annotate mode is running: detection, its visuals and its controller shortcuts are off.</summary>
        private bool AnnotateActive => m_annotate != null && m_annotate.IsActive;

        private void OpenMenu()
        {
            m_menu.Open();
            m_hud.SetVisible(false);
            m_debug.Clear();
            Log.Info($"menu_open {m_settings}");
        }

        private void OnSessionStart()
        {
            if (m_settings.AppMode == AppMode.Annotate && m_annotate != null)
            {
                EnterAnnotate();
                return;
            }

            if (m_yolo != null && UseYolo && m_yolo.Backend != ToBackend(m_settings.Backend))
            {
                if (m_yoloInit != null)
                {
                    StopCoroutine(m_yoloInit);
                }

                m_yoloInit = StartCoroutine(m_yolo.Initialize(ToBackend(m_settings.Backend)));
            }

            m_menu.Close();
            m_tracker.Reset();
            m_kpEvidence.Clear();
            m_hybridCrops = m_hybridKeypoints = m_hybridFronts = 0;
            m_backrest.Clear();
            m_debug.Enabled = m_settings.ShowScan; // scan mode shows the cyan fitted keypoints
            m_debug.Clear();
            m_hud.SetVisible(m_hudVisible);
            Log.Info($"session_start {m_settings}");
        }

        private void EnterAnnotate()
        {
            m_menu.Close();
            m_tracker.Reset();
            m_kpEvidence.Clear();
            m_hybridCrops = m_hybridKeypoints = m_hybridFronts = 0;
            m_backrest.Clear();
            m_debug.Enabled = false;
            m_debug.Clear();
            m_hud.SetVisible(false);
            Log.Info($"annotate_start {m_settings}");
            m_annotate.Enter(new AnnotationContext
            {
                Head = m_head,
                Material = m_material,
                Font = m_font,
                Floor = m_floor,
                Raycast = m_raycast,
                Depth = m_depth,
                Camera = m_camera,
                ResetDetector = () =>
                {
                    m_tracker.Reset();
            m_kpEvidence.Clear();
            m_hybridCrops = m_hybridKeypoints = m_hybridFronts = 0;
                    m_backrest.Clear();
                },
                DetectorStatus = () => m_yolo == null ? "no model" : $"{m_yolo.Status} {m_yolo.InferenceHz:F1} Hz",
                DetectorModel = m_yolo != null ? m_yolo.ModelName : "none",
            });
        }

        private void OnAnnotateExit()
        {
            m_annotate.Exit();
            OpenMenu();
        }

        private void Update()
        {
            HandleInput();
            m_floor.Update(m_raycast, m_head);
            EnsureDetectorState();
            TrackHeadSpeed();

            if (!AnnotateActive && !UseYolo && !MenuOpen && m_frames != null && m_detector != null && m_detector.IsReady && !m_detector.IsBusy &&
                !m_frames.IsPending && m_camera.IsUpdatedThisFrame)
            {
                m_frames.TryCapture(OnFrameCaptured);
            }

            if (UseHybrid && !AnnotateActive && !MenuOpen && m_frames != null && m_detector != null && m_detector.IsReady &&
                !m_detector.IsBusy && !m_frames.IsPending && m_camera.IsUpdatedThisFrame &&
                m_headSpeedDegPerSec < MaxHeadSpeedForCapture && TryNextHybridTarget(out var target))
            {
                m_frames.TryCapture((rgba, context) => OnHybridFrameCaptured(rgba, context, target));
            }

            if ((AnnotateActive ? m_annotate.WantsDetector : UseYolo) && !MenuOpen && m_yolo != null && m_yolo.IsReady && !m_yolo.IsBusy && m_frames != null &&
                m_camera.IsUpdatedThisFrame && m_headSpeedDegPerSec < MaxHeadSpeedForCapture &&
                m_frames.TryCurrentCameraModel(out var cam))
            {
                StartCoroutine(m_yolo.Run(m_camera.GetTexture(), cam, ++m_yoloFrameId, OnYoloFrame));
            }

            if (MenuOpen && Time.time >= m_nextMenuRefresh)
            {
                m_nextMenuRefresh = Time.time + 0.5f;
                m_menu.StatusLine = MenuStatus();
                m_menu.RefreshText();
            }

            UpdateVisuals();
            ProbeBackrests();
            UpdateHud();
            Heartbeat();
        }

        private void OnApplicationPause(bool paused)
        {
            Log.Info($"app_pause={paused}");
            Log.Flush();
        }

        private void OnApplicationQuit() => Log.Flush();

        private void OnDestroy()
        {
            m_yolo?.Dispose();
            if (m_cropBuffer.IsCreated)
            {
                m_cropBuffer.Dispose();
            }
        }

        private void TrackHeadSpeed()
        {
            if (m_head == null || Time.deltaTime <= 0f)
            {
                return;
            }

            var angle = Quaternion.Angle(m_lastHeadRotation, m_head.rotation);
            m_lastHeadRotation = m_head.rotation;
            m_headSpeedDegPerSec = Mathf.Lerp(m_headSpeedDegPerSec, angle / Time.deltaTime, 0.5f);
        }

        private string MenuStatus()
        {
            var cam = m_camera != null && m_camera.IsPlaying ? "camera ok" : "waiting for camera permission";
            var depth = m_depth != null && m_depth.IsAvailable ? "depth ok" : "depth unavailable";
            var model = UseYolo
                ? (m_yolo == null ? "YOLO model missing" : $"YOLO {m_yolo.Status}")
                : (m_detector == null ? "Objectron missing" : "Objectron starts with the session");
            return $"{model} · {cam} · {depth}";
        }

        private float m_lastHeartbeat;
        private int m_framesProcessed;
        private int m_rawDetections;

        private void Heartbeat()
        {
            var now = Time.realtimeSinceStartup;
            if (now - m_lastHeartbeat < 3f)
            {
                return;
            }

            m_lastHeartbeat = now;
            var detStatus = UseYolo
                ? $"yolo={(m_yolo != null ? m_yolo.Status : "null")}/{m_yolo?.Backend} hz={m_yolo?.InferenceHz:F1} ms={m_yolo?.LastInferenceMs:F0}"
                : $"objectron={(m_detector != null ? m_detector.Status : "null")} hz={m_detector?.InferenceHz:F1} ms={m_detector?.LastInferenceMs:F0}";
            Log.Info($"status menu={MenuOpen} annotate={AnnotateActive} cam={(m_camera != null && m_camera.IsPlaying)} {detStatus} " +
                     $"head={m_headSpeedDegPerSec:F0}deg/s frames={m_framesProcessed} raw_dets={m_rawDetections} " +
                     $"frames_with_dets={m_framesWithDetections} fits_ok={m_fitsOk} fits_failed={m_fitsFailed} " +
                     $"last_fail={m_lastFailReason} floor={(m_floor.HasFloor ? m_floor.FloorY.ToString("F2") : "none")}" +
                     $"({m_floor.SampleCount}) depth={EnvironmentRaycastManager.IsSupported} tracks={m_tracker.Tracks.Count} " +
                     $"confirmed={m_tracker.ConfirmedCount}");
        }

        private void OnFrameCaptured(NativeArray<byte> rgba, FrameContext context)
        {
            if (m_detector != null && m_detector.IsReady)
            {
                m_detector.TrySubmit(rgba, context);
            }
        }

        // ------------------------------------------------------------------ detection → geometry → tracking

        private void OnDetections(DetectionFrame frame)
        {
            if (MenuOpen)
            {
                return;
            }

            if (frame.Context.IsCrop)
            {
                OnHybridKeypoints(frame);
                return;
            }

            var now = Time.realtimeSinceStartup;
            var cam = frame.Context.Camera;
            var info = Categories.Get(m_category);
            m_observations.Clear();
            m_debug.Begin();
            m_framesProcessed++;
            m_rawDetections += frame.Detections.Count;
            if (frame.Detections.Count > 0)
            {
                m_framesWithDetections++;
            }

            foreach (var det in frame.Detections)
            {
                var support = SupportPlaneEstimator.Estimate(det, cam, m_raycast, m_floor, info);
                var fit = BoxSolver.Fit(det, cam, support, info);
                if (!fit.Valid)
                {
                    m_fitsFailed++;
                    m_lastFailReason = fit.FailReason;
                    continue;
                }

                m_fitsOk++;
                var distance = Vector3.Distance(cam.Position, fit.Box.Center);
                var quality = ConfidenceModel.ObservationQuality(fit, support, det, info, distance);
                var view = fit.Box.Center - cam.Position;
                view.y = 0f;
                m_observations.Add(new BoxObservation
                {
                    Box = fit.Box,
                    Quality = quality.Total,
                    Breakdown = quality,
                    Time = now,
                    ViewDirection = view.sqrMagnitude > 1e-6f ? view.normalized : Vector3.forward,
                    RmsPx = fit.RmsPx,
                    SupportSigma = support.Sigma,
                    FrameId = frame.Context.FrameId,
                });
                m_debug.AddFit(det, cam, fit);
                m_lastFitSummary = $"err {fit.NormalizedRms * 100f:F1}%  q {quality.Total:F2}  {fit.Box.Size.x * 100f:F0}×" +
                                   $"{fit.Box.Size.z * 100f:F0}×{fit.Box.Size.y * 100f:F0}cm";

                if (now - m_lastLogTime > 1f)
                {
                    m_lastLogTime = now;
                    Log.Info($"fit frame={frame.Context.FrameId} {fit.Box} rms={fit.RmsPx:F1}px ({fit.NormalizedRms * 100f:F1}% of {fit.DiagonalPx:F0}px) " +
                             $"init={fit.InitialRmsPx:F1}px inliers={fit.InlierFraction:F2} it={fit.Iterations} " +
                             $"chi={fit.SideHandedness} layout={det.Layout} vert={det.ModelVerticality:F2} " +
                             $"support[{support}] {quality} infer={frame.InferenceMs:F0}ms");
                }
            }

            m_debug.End();
            m_tracker.Process(m_category, m_observations, t => IsExpectedVisible(t, cam), now);
        }

        private void OnYoloFrame(YoloFrame frame)
        {
            if (MenuOpen || (!UseYolo && !AnnotateActive))
            {
                return;
            }

            m_frameFits.Clear();

            var now = Time.realtimeSinceStartup;
            var cam = frame.Camera;
            if (UseHybrid)
            {
                UpdateHybridTargets(frame.Detections, now);
            }

            var floorY = m_floor.HasFloor ? m_floor.FloorY : float.NaN;
            m_observations.Clear();
            m_debug.Begin();
            m_framesProcessed++;
            m_rawDetections += frame.Detections.Count;
            if (frame.Detections.Count > 0)
            {
                m_framesWithDetections++;
            }

            foreach (var det in frame.Detections)
            {
                var info = Categories.Get(det.Category);
                var fit = m_fitter.Fit(det.Viewport, cam, info, m_depth, floorY, m_debug.Enabled ? m_fitPoints : null);
                if (AnnotateActive)
                {
                    m_frameFits.Add(new DetectorFit { Detection = det, Fit = fit });
                }

                if (!fit.Valid)
                {
                    m_fitsFailed++;
                    m_lastFailReason = $"{det.Label}: {fit.FailReason}";
                    continue;
                }

                m_fitsOk++;
                var quality = ConfidenceModel.DepthObservationQuality(fit, info, det.Score);
                var hybrid = default(HybridResult);
                var box = fit.Box;
                var obsQuality = quality.Total;
                var fused = UseHybrid && det.Category == ObjectCategory.Chair;
                if (fused)
                {
                    hybrid = m_fuser.Fuse(fit, FindKeypointEvidence(fit.Box, now), cam, m_depth, floorY);
                    box = hybrid.Box;
                    obsQuality *= 0.4f + 0.6f * hybrid.Verification;
                    m_lastVerification = hybrid.Verification;
                    if (hybrid.HasFront)
                    {
                        m_hybridFronts++;
                    }
                }

                var view = box.Center - cam.Position;
                view.y = 0f;
                m_observations.Add(new BoxObservation
                {
                    Box = box,
                    HasFront = hybrid.HasFront,
                    FrontYaw = hybrid.FrontYaw,
                    Verification = fused ? hybrid.Verification : 1f,
                    Quality = obsQuality,
                    Breakdown = quality,
                    Time = now,
                    ViewDirection = view.sqrMagnitude > 1e-6f ? view.normalized : Vector3.forward,
                    RmsPx = 0f,
                    SupportSigma = fit.UsedFloor ? m_floor.Sigma : 0.03f,
                    FrameId = frame.FrameId,
                    Category = det.Category,
                    YawPeriodDeg = 90f,
                    Score = det.Score,
                    SigmaPos = fit.SigmaPos,
                });
                if (m_debug.Enabled)
                {
                    m_debug.AddDepthFit(cam, det.Viewport, fit, m_fitPoints);
                }

                m_lastFitSummary = $"{info.DisplayName} {det.Score:F2} · {fit.ObjectPoints} pts · " +
                                   $"{fit.Box.Size.x * 100f:F0}×{fit.Box.Size.z * 100f:F0}×{fit.Box.Size.y * 100f:F0}cm · " +
                                   $"σ {fit.SigmaPos * 100f:F0}cm · q {quality.Total:F2}";
                if (now - m_lastLogTime > 1f)
                {
                    m_lastLogTime = now;
                    Log.Info($"yolo_fit frame={frame.FrameId} {det} {fit} {quality} infer={frame.InferenceMs:F0}ms " +
                             $"layer_frames={frame.LayerFrames}" + (fused ? $" {hybrid}" : ""));
                }
            }

            m_debug.End();
            m_tracker.ProcessMulti(m_observations, t => IsExpectedVisible(t, cam), now);
            if (AnnotateActive)
            {
                m_annotate.OnDetectorFrame(frame, m_frameFits, m_tracker.Tracks);
            }
        }

        // ------------------------------------------------------------------ hybrid: Objectron inside YOLO boxes

        /// <summary>Chair boxes of the latest YOLO frame, left to right (stable order for the round-robin).</summary>
        private void UpdateHybridTargets(List<Detection2D> detections, float now)
        {
            m_hybridTargets.Clear();
            foreach (var det in detections)
            {
                if (det.Category == ObjectCategory.Chair)
                {
                    m_hybridTargets.Add(det.Viewport);
                }
            }

            m_hybridTargets.Sort((a, b) => a.center.x.CompareTo(b.center.x));
            m_hybridTargetsTime = now;
        }

        /// <summary>Next YOLO box to run Objectron on: dwell 3 crops per box (Objectron tracks between frames), then move on.</summary>
        private bool TryNextHybridTarget(out Rect target)
        {
            target = default;
            if (m_hybridTargets.Count == 0 || Time.realtimeSinceStartup - m_hybridTargetsTime > 0.4f)
            {
                return false;
            }

            if (++m_hybridDwell >= 3)
            {
                m_hybridDwell = 0;
                m_hybridCursor++;
            }

            target = m_hybridTargets[m_hybridCursor % m_hybridTargets.Count];
            return true;
        }

        /// <summary>Crop the camera frame around the target (frame aspect, padded) into the Objectron input size.</summary>
        private void OnHybridFrameCaptured(NativeArray<byte> rgba, FrameContext context, Rect target)
        {
            if (m_detector == null || !m_detector.IsReady || m_detector.IsBusy)
            {
                return;
            }

            var res = context.Camera.Resolution;
            var cw = HybridCropResolution.x;
            var ch = HybridCropResolution.y;
            if (rgba.Length != res.x * res.y * 4)
            {
                return;
            }

            if (!m_cropBuffer.IsCreated)
            {
                m_cropBuffer = new NativeArray<byte>(cw * ch * 4, Allocator.Persistent);
            }

            var crop = HybridFuser.CropForBox(target, res, cw / (float)ch);
            // Readback rows are bottom-up, like viewport y: row = y · H.
            var src = rgba.Reinterpret<uint>(1);
            var dst = m_cropBuffer.Reinterpret<uint>(1);
            var x0 = crop.x * res.x;
            var y0 = crop.y * res.y;
            var sx = crop.width * res.x / cw;
            var sy = crop.height * res.y / ch;
            for (var y = 0; y < ch; y++)
            {
                var ry = Mathf.Min(res.y - 1, (int)(y0 + (y + 0.5f) * sy));
                var srcRow = ry * res.x;
                var dstRow = y * cw;
                for (var x = 0; x < cw; x++)
                {
                    var rx = Mathf.Min(res.x - 1, (int)(x0 + (x + 0.5f) * sx));
                    dst[dstRow + x] = src[srcRow + rx];
                }
            }

            context.Crop = crop;
            context.Target = target;
            context.IsCrop = true;
            if (m_detector.TrySubmit(m_cropBuffer, context))
            {
                m_hybridCrops++;
            }
        }

        /// <summary>Objectron result on a crop: map keypoints to the full frame, keep the ones inside the YOLO box, solve.</summary>
        private void OnHybridKeypoints(DetectionFrame frame)
        {
            var now = Time.realtimeSinceStartup;
            var cam = frame.Context.Camera;
            var info = Categories.Get(ObjectCategory.Chair);
            m_kpEvidence.RemoveAll(e => now - e.Time > 1f);
            foreach (var det in frame.Detections)
            {
                for (var i = 0; i < KeypointDetection.KeypointCount; i++)
                {
                    det.Viewport[i] = HybridFuser.FromCrop(frame.Context.Crop, det.Viewport[i]);
                }

                if (!HybridFuser.BelongsTo(HybridFuser.KeypointBounds(det), frame.Context.Target))
                {
                    continue;
                }

                var support = SupportPlaneEstimator.Estimate(det, cam, m_raycast, m_floor, info);
                var fit = BoxSolver.Fit(det, cam, support, info);
                if (!fit.Valid)
                {
                    continue;
                }

                var quality = ConfidenceModel.ObservationQuality(fit, support, det, info, Vector3.Distance(cam.Position, fit.Box.Center));
                m_kpEvidence.Add(new KeypointEvidence { Box = fit.Box, Quality = quality.Total, Time = now });
                m_hybridKeypoints++;
                if (m_debug.Enabled)
                {
                    m_debug.Begin();
                    m_debug.AddFit(det, cam, fit);
                    m_debug.End();
                }
            }
        }

        /// <summary>Most recent keypoint box (≤ 0.6 s old) describing the same object as this depth box.</summary>
        private KeypointEvidence? FindKeypointEvidence(in UprightBox depthBox, float now)
        {
            KeypointEvidence? best = null;
            foreach (var e in m_kpEvidence)
            {
                if (now - e.Time <= 0.6f && m_fuser.SameObject(depthBox, e.Box) && (!best.HasValue || e.Time > best.Value.Time))
                {
                    best = e;
                }
            }

            return best;
        }

        private bool IsExpectedVisible(TrackedObject track, CameraModel cam)
        {
            var center = track.Estimate.Center;
            if (!cam.WorldToViewport(center, out var vp) || vp.x < 0.12f || vp.x > 0.88f || vp.y < 0.12f || vp.y > 0.88f)
            {
                return false;
            }

            var distance = Vector3.Distance(cam.Position, center);
            if (distance < 0.5f || distance > 3.5f)
            {
                return false;
            }

            // Occluded by something closer → not a miss.
            if (m_raycast != null && m_raycast.Raycast(new Ray(cam.Position, center - cam.Position), out var hit, 6f))
            {
                var footprint = Mathf.Max(track.Estimate.Size.x, track.Estimate.Size.z);
                if (Vector3.Distance(cam.Position, hit.point) < distance - 0.5f * footprint - 0.1f)
                {
                    return false;
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ visuals + anchors

        private void UpdateVisuals()
        {
            ConfidenceModel.UseCalibration = UseHybrid;
            foreach (var track in m_tracker.Tracks)
            {
                if (!m_visuals.TryGetValue(track.Id, out var visual))
                {
                    var parent = m_anchors.TryGetValue(track.Id, out var anchor) ? anchor : m_root;
                    visual = BoxVisual.Create(parent, track.Id, m_material, m_font, m_head);
                    m_visuals[track.Id] = visual;
                }

                if (track.Relocated && m_backrest.TryGetValue(track.Id, out var moved))
                {
                    moved.Reset(); // the chair moved: re-measure where its backrest is
                }

                visual.Apply(track, Categories.Get(track.Category));

                // Minimum-confidence threshold from the start menu (small hysteresis against flicker).
                var threshold = m_settings.MinConfidence - (visual.Shown ? ShowHysteresis : 0f);
                visual.SetShown(!MenuOpen && !AnnotateActive && ConfidenceModel.Displayed(track.Confidence) >= threshold);

                var side = m_backrest.TryGetValue(track.Id, out var est) && est.Decided ? est.QuarterTurns : -1;
                visual.SetScan(m_settings.ShowScan && track.Category == ObjectCategory.Chair, side);
            }
        }

        /// <summary>Scan mode: a few depth rays per frame decide which side of each shown chair is the backrest.</summary>
        private void ProbeBackrests()
        {
            if (MenuOpen || AnnotateActive || !m_settings.ShowScan || m_head == null || m_depth == null || !m_depth.IsAvailable)
            {
                return;
            }

            m_probeList.Clear();
            var eye = m_head.position;
            foreach (var track in m_tracker.Tracks)
            {
                if (track.Category != ObjectCategory.Chair || !m_visuals.TryGetValue(track.Id, out var visual) || !visual.Shown)
                {
                    continue;
                }

                var toObj = track.Estimate.Center - eye;
                var dist = toObj.magnitude;
                if (dist < 0.4f || dist > 4.5f || Vector3.Dot(toObj / dist, m_head.forward) < 0.5f)
                {
                    continue;
                }

                m_probeList.Add(track);
            }

            if (m_probeList.Count == 0)
            {
                return;
            }

            var n = Mathf.Min(BackrestTracksPerFrame, m_probeList.Count);
            for (var k = 0; k < n; k++)
            {
                var track = m_probeList[(m_probeCursor + k) % m_probeList.Count];
                if (!m_backrest.TryGetValue(track.Id, out var est))
                {
                    est = new BackrestEstimator();
                    m_backrest[track.Id] = est;
                }

                if (est.Probe(track.Estimate, eye, m_depth, BackrestRaysPerTrack))
                {
                    Log.Info($"backrest id={track.Id} {est} box={track.Estimate}");
                }
            }

            m_probeCursor = (m_probeCursor + n) % Mathf.Max(1, m_probeList.Count);
        }

        private void OnTrackConfirmed(TrackedObject track)
        {
            if (!m_useSpatialAnchors || Application.isEditor)
            {
                return;
            }

            var anchorGo = new GameObject($"Anchor_{track.Id}");
            anchorGo.transform.SetPositionAndRotation(track.Estimate.BottomCenter, track.Estimate.Rotation);
            anchorGo.AddComponent<OVRSpatialAnchor>();
            m_anchors[track.Id] = anchorGo.transform;
            if (m_visuals.TryGetValue(track.Id, out var visual))
            {
                visual.transform.SetParent(anchorGo.transform, true);
            }
        }

        private void OnTrackRemoved(TrackedObject track)
        {
            m_backrest.Remove(track.Id);

            if (m_visuals.TryGetValue(track.Id, out var visual))
            {
                if (visual != null)
                {
                    Destroy(visual.gameObject);
                }

                m_visuals.Remove(track.Id);
            }

            if (m_anchors.TryGetValue(track.Id, out var anchor))
            {
                if (anchor != null)
                {
                    Destroy(anchor.gameObject);
                }

                m_anchors.Remove(track.Id);
            }
        }

        // ------------------------------------------------------------------ input + HUD

        private void HandleInput()
        {
            if (m_menu == null || MenuOpen || AnnotateActive)
            {
                return; // the menu / the annotation mode read the controllers themselves
            }

            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
            {
                OpenMenu();
                return;
            }

            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
            {
                m_debug.Enabled = !m_debug.Enabled;
                if (!m_debug.Enabled)
                {
                    m_debug.Clear();
                }
            }

            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            {
                Log.Info("reset");
                m_tracker.Reset();
            m_kpEvidence.Clear();
            m_hybridCrops = m_hybridKeypoints = m_hybridFronts = 0;
                m_backrest.Clear();
                m_debug.Clear();
            }

            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
            {
                m_hudVisible = !m_hudVisible;
                m_hud.SetVisible(m_hudVisible);
            }
        }

        private void UpdateHud()
        {
            if (m_hud == null || !m_hudVisible || MenuOpen || AnnotateActive)
            {
                return;
            }

            m_hudText.Clear();
            m_hudText.Append("<b>QuestDetect3D</b>   ")
                .Append(UseHybrid ? "Hybrid · YOLO + keypoints + depth" : UseYolo ? "YOLO + depth · furniture" : "Objectron · chairs").Append('\n');
            if (m_camera == null || !m_camera.IsPlaying)
            {
                m_hudText.Append("Waiting for camera permission…\n");
            }
            else if (UseYolo)
            {
                m_hudText.Append(m_yolo == null
                    ? "YOLO model missing\n"
                    : m_yolo.IsReady
                        ? $"YOLO11n {m_yolo.InferenceHz:F1} Hz · {m_yolo.LastInferenceMs:F0} ms · {(m_yolo.Backend == BackendType.CPU ? "CPU" : "GPU")}\n"
                        : $"YOLO: {m_yolo.Status}\n");
                if (UseHybrid)
                {
                    m_hudText.Append(m_detector != null && m_detector.IsReady
                        ? $"Objectron in boxes {m_detector.InferenceHz:F1} Hz · kp {m_hybridKeypoints}/{m_hybridCrops} · front {m_hybridFronts} · V {m_lastVerification:F2}\n"
                        : $"Objectron: {(m_detector != null ? m_detector.Status : "missing")}\n");
                }
            }
            else if (m_detector == null || !m_detector.IsReady)
            {
                m_hudText.Append("Model: ").Append(m_detector != null ? m_detector.Status : "missing").Append('\n');
            }
            else
            {
                m_hudText.Append($"Objectron {m_detector.InferenceHz:F1} Hz · {m_detector.LastInferenceMs:F0} ms\n");
            }

            m_hudText.Append(m_floor.HasFloor
                ? $"Floor: y={m_floor.FloorY:F2} m (±{m_floor.Sigma * 100f:F0} cm)\n"
                : $"Floor: measuring… ({m_floor.SampleCount} samples, look at the floor)\n");

            var confirmed = 0;
            var tentative = 0;
            var shown = 0;
            foreach (var t in m_tracker.Tracks)
            {
                if (t.State == TrackState.Confirmed)
                {
                    confirmed++;
                }
                else
                {
                    tentative++;
                }

                if (m_visuals.TryGetValue(t.Id, out var v) && v.Shown)
                {
                    shown++;
                }
            }

            m_hudText.Append($"Objects: {confirmed} confirmed, {tentative} candidates · showing {shown} (≥{m_settings.MinConfidence * 100f:F0}%)\n");
            m_hudText.Append(m_settings.ShowScan ? "Display: 3D scan + keypoints\n" : "Display: box\n");
            m_hudText.Append($"Last fit: {m_lastFitSummary}\n");
            if (m_fitsFailed > 0 && !string.IsNullOrEmpty(m_lastFailReason))
            {
                m_hudText.Append($"Rejected fits: {m_fitsFailed} ({m_lastFailReason})\n");
            }

            m_hudText.Append("<size=48>A: menu   B: keypoints   Y: reset   X: hide</size>");
            m_hud.SetText(m_hudText.ToString());
        }
    }
}
