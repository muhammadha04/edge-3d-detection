// QuestDetect3D v2 — orchestrates: PCA frame → detector (2D box keypoints) → metric box fit on the support
// plane → multi-view tracker with confidence → anchored 3D highlights + HUD.
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
using Unity.Collections;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class Detect3DApp : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_camera;
        [SerializeField] private EnvironmentRaycastManager m_raycast;
        [SerializeField] private ObjectronDetector m_detector;
        [SerializeField] private Shader m_overlayShader;
        [SerializeField] private ObjectCategory m_startCategory = ObjectCategory.Chair;
        [SerializeField] private bool m_useSpatialAnchors = true;

        private readonly ObjectTracker m_tracker = new ObjectTracker();
        private readonly FloorEstimator m_floor = new FloorEstimator();
        private readonly Dictionary<int, BoxVisual> m_visuals = new Dictionary<int, BoxVisual>();
        private readonly Dictionary<int, Transform> m_anchors = new Dictionary<int, Transform>();
        private readonly List<BoxObservation> m_observations = new List<BoxObservation>();
        private readonly StringBuilder m_hudText = new StringBuilder(512);
        private readonly Dictionary<int, BackrestEstimator> m_backrest = new Dictionary<int, BackrestEstimator>();
        private readonly List<TrackedObject> m_probeList = new List<TrackedObject>();

        private const float ShowHysteresis = 0.03f;
        private const int BackrestRaysPerTrack = 12;
        private const int BackrestTracksPerFrame = 2;
        private int m_probeCursor;

        private AppSettings m_settings;
        private SessionMenu m_menu;

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
            OpenMenu();

            Log.Info($"app_start version={Application.version} unity={Application.unityVersion} " +
                     $"pca_supported={PassthroughCameraAccess.IsSupported} depth_supported={EnvironmentRaycastManager.IsSupported}");

            m_frames = new PcaFrameSource(m_camera);
            while (m_camera == null || !m_camera.IsPlaying)
            {
                yield return null;
            }

            m_detector.Bind(m_camera);
            m_detector.FrameProcessed += OnDetections;
            var intrinsics = m_camera.Intrinsics;
            Log.Info($"pca_ready res={m_camera.CurrentResolution.x}x{m_camera.CurrentResolution.y} " +
                     $"f=({intrinsics.FocalLength.x:F1},{intrinsics.FocalLength.y:F1}) " +
                     $"c=({intrinsics.PrincipalPoint.x:F1},{intrinsics.PrincipalPoint.y:F1}) " +
                     $"sensor={intrinsics.SensorResolution.x}x{intrinsics.SensorResolution.y}");
            StartDetector(m_category);
        }

        private void StartDetector(ObjectCategory category)
        {
            if (m_detectorRoutine != null)
            {
                StopCoroutine(m_detectorRoutine);
            }

            m_category = category;
            m_debug.Clear();
            m_detectorRoutine = StartCoroutine(m_detector.Run(category, m_camera.CurrentResolution));
        }

        private bool MenuOpen => m_menu != null && m_menu.IsOpen;

        private void OpenMenu()
        {
            m_menu.Open();
            m_hud.SetVisible(false);
            m_debug.Clear();
            Log.Info($"menu_open {m_settings}");
        }

        private void OnSessionStart()
        {
            m_menu.Close();
            m_tracker.Reset();
            m_backrest.Clear();
            m_debug.Enabled = m_settings.ShowScan; // scan mode shows the cyan fitted keypoints
            m_debug.Clear();
            m_hud.SetVisible(m_hudVisible);
            Log.Info($"session_start {m_settings}");
        }

        private void Update()
        {
            HandleInput();
            m_floor.Update(m_raycast, m_head);

            if (!MenuOpen && m_frames != null && m_detector != null && m_detector.IsReady && !m_detector.IsBusy &&
                !m_frames.IsPending && m_camera.IsUpdatedThisFrame)
            {
                m_frames.TryCapture(OnFrameCaptured);
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
            Log.Info($"status cam={(m_camera != null && m_camera.IsPlaying)} " +
                     $"detector={(m_detector != null ? m_detector.Status : "null")} hz={m_detector?.InferenceHz:F1} " +
                     $"ms={m_detector?.LastInferenceMs:F0} frames={m_framesProcessed} raw_dets={m_rawDetections} " +
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
                visual.SetShown(!MenuOpen && track.Confidence >= threshold);

                var side = m_backrest.TryGetValue(track.Id, out var est) && est.Decided ? est.QuarterTurns : -1;
                visual.SetScan(m_settings.ShowScan, side);
            }
        }

        /// <summary>Scan mode: a few depth rays per frame decide which side of each shown chair is the backrest.</summary>
        private void ProbeBackrests()
        {
            if (MenuOpen || !m_settings.ShowScan || m_head == null || m_raycast == null)
            {
                return;
            }

            m_probeList.Clear();
            var eye = m_head.position;
            foreach (var track in m_tracker.Tracks)
            {
                if (!m_visuals.TryGetValue(track.Id, out var visual) || !visual.Shown)
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

                if (est.Probe(track.Estimate, eye, m_raycast, BackrestRaysPerTrack))
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
            if (m_menu == null || MenuOpen)
            {
                return; // the menu reads the controllers itself
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
            if (m_hud == null || !m_hudVisible || MenuOpen)
            {
                return;
            }

            var info = Categories.Get(m_category);
            m_hudText.Clear();
            m_hudText.Append("<b>QuestDetect3D v2</b>   looking for: <b>").Append(info.DisplayName).Append("</b>\n");
            if (m_camera == null || !m_camera.IsPlaying)
            {
                m_hudText.Append("Waiting for camera permission…\n");
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

            m_hudText.Append($"Chairs: {confirmed} confirmed, {tentative} candidates · showing {shown} (≥{m_settings.MinConfidence * 100f:F0}%)\n");
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
