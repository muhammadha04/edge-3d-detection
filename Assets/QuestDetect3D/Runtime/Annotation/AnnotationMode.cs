// =====================================================================================================================
// QD3D ANNOTATE – OPERATOR GUIDE (the full guide with pictures of the set-up is Docs/annotation-quickstart.md)
// ---------------------------------------------------------------------------------------------------------------------
// Purpose: ground-truth 3D boxes for the thesis (accuracy + calibration claims rest on them), plus recordings that
// any model can later be replayed on, on the PC (Tools/annotation, AnalyseSessions.bat).
//
// Before: tape two crosses on the floor with masking tape (arms ~30 cm): the ROOM cross near the objects and a DRIFT
// cross ≥ 2 m away. Mark a pen dot on a table for the tip calibration. Tape-measure every object (W, D, H to the
// extremes, cm). Nothing is printed.
//
// In the app (start menu → Mode: Annotate → Start annotating):
//   1. Home → Operator name (once).                                       2. Calibrate tip: each controller you use,
//   3. Room → New room → Place room cross (tip on centre, then on an arm)      residual < 3 mm (repeat otherwise).
//      → Drift check (first touch sets the reference) → Place test box.
//   4. Catalog → Add object → name, category, tape W/D/H, symmetry, front definition, photo.
//   5. Annotate → object → M1 Touch, Blind ON → front point 1, front point 2, then front/back/left/right/top-most
//      → Edit box (walk around: red dots = object outside → pull that face) → Confirm → rate 1–5 → Repeat.
//   6. Verify → walk around, OK / Redo per object.
//   7. Record → 5 Hz, depth ON, detector ON → Drift check (session_start) → Start → walk 2 min at 0.7–3 m → Stop →
//      Drift check (session_end).                                         8. Export (optional) → PC: PullSession.bat
//
// How much (pilot → full dataset):
//   pilot      1 room, 1 chair: M1 ×3 and M2 ×3 blind repetitions, 2 recordings (normal + dim light).
//   dataset    15–25 objects over 2–3 rooms (hold 1 room out for testing); per object ≥ 3 blind M1 repetitions
//              (take a few steps away between repeats); per room ≥ 3 recordings of 2 min (≥ 1 per lighting) so the
//              PC split by session has train/val/test; ≤ 6 objects in one recording keeps every object in view often.
//   quality    go ahead when centre σ ≤ 1.5 cm and the box is within 1 cm of the tape (repeatability.py says so);
//              drift-cross change < 1–2 cm per session; tip residual < 3 mm.
// Never annotate with the detector shown (M1/M2 hide it). M4 Assisted is stored but is never the primary truth.
// =====================================================================================================================
//
// QD3D Annotate mode controller. Detect3DApp calls Enter() when "Annotate" is started from the start menu and
// Exit() when the user leaves; nothing here runs in Detect mode (the EventSystem child stays inactive).
// Owns the data (AnnotationStore: catalog, rooms, annotations, calibration), the room frame (RoomService), the
// controllers (AnnRig + Interaction SDK rays), the recorder, and one panel whose content is the current screen.
// Controls that work on every screen:
//   left Menu button  bring the panel in front of you
//   X (left)          hide / show the panel
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Meta.XR;
using Newtonsoft.Json.Linq;
using Oculus.Interaction;
using QuestDetect3D.Recording;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestDetect3D.Annotation
{
    /// <summary>What the annotation mode borrows from the app.</summary>
    public sealed class AnnotationContext
    {
        public Transform Head;
        public Material Material;
        public Font Font;
        public FloorEstimator Floor;
        public EnvironmentRaycastManager Raycast;
        public IDepthRaycaster Depth;
        public PassthroughCameraAccess Camera;
        /// <summary>Clears the detector's tracks (start of a recording / assisted annotation).</summary>
        public Action ResetDetector;
        public Func<string> DetectorStatus;
        public string DetectorModel;
    }

    public interface IAnnScreen
    {
        string Title { get; }
        void Build(AnnPanel panel);
    }

    /// <summary>Screens that need a per-frame update (world input, live readouts, drawing).</summary>
    public interface IAnnTickScreen : IAnnScreen
    {
        void Tick();
    }

    /// <summary>Screens that must clean up when another screen is shown.</summary>
    public interface IAnnLeaveScreen
    {
        void OnLeave();
    }

    public sealed class AnnotationMode : MonoBehaviour
    {
        private const string KeyCurrentRoom = "qd3d.ann.room";

        [SerializeField] private GameObject m_eventSystem;

        private readonly ConcurrentQueue<Action> m_mainThread = new ConcurrentQueue<Action>();
        private AnnPanel m_panel;
        private AnnRay m_rightRay;
        private AnnRay m_leftRay;
        private IAnnScreen m_screen;
        private bool m_refreshQueued;
        private readonly List<DetectorFit> m_lastFits = new List<DetectorFit>();

        public event Action ExitRequested;

        public bool IsActive { get; private set; }
        public AnnotationContext Context { get; private set; }
        public AnnotationStore Store { get; private set; }
        public CatalogFile Catalog { get; private set; }
        public CalibrationFile Calibration { get; private set; }
        public RoomFile CurrentRoom { get; private set; }
        public AnnotationFile Annotations { get; private set; }
        public AnnRig Rig { get; private set; }
        public AnnDraw Draw { get; private set; }
        public RoomService Room { get; } = new RoomService();
        public SessionRecorder Recorder { get; private set; }
        public AnnKeyboard Keyboard { get; private set; }
        public IAnnScreen CurrentScreen => m_screen;
        public string Operator => Calibration != null && !string.IsNullOrEmpty(Calibration.Operator) ? Calibration.Operator : "unknown";

        /// <summary>Set by the Assisted method while it needs live detector boxes.</summary>
        public bool AssistedWantsDetector { get; set; }

        /// <summary>Detect3DApp runs the YOLO + depth pipeline in Annotate mode only while this is true.</summary>
        public bool WantsDetector => IsActive && ((Recorder != null && Recorder.IsRecording && Recorder.Settings.Detector) || AssistedWantsDetector);

        /// <summary>Latest detector tracks (for M4 Assisted).</summary>
        public List<TrackedObject> LatestTracks { get; } = new List<TrackedObject>();
        public float LatestTracksTime { get; private set; } = -1f;

        public static string DataRoot => System.IO.Path.Combine(Application.persistentDataPath, "QD3D");

        // ------------------------------------------------------------------ enter / exit

        /// <summary>Starts Annotate mode (called by Detect3DApp when the start menu says Annotate): loads all data, shows the panel and the controller rays, opens Home.</summary>
        public void Enter(AnnotationContext context)
        {
            Context = context;
            IsActive = true;
            EnsureEventSystem();
            LoadData();
            EnsureUi();
            Draw.Visible = true;
            m_panel.Visible = true;
            m_panel.PlaceInFront(context.Head);
            m_rightRay?.SetActive(true);
            m_leftRay?.SetActive(true);
            Show(new HomeScreen(this));
            Log.Info($"ann_enter root={Store.Root} rooms={Store.ListRooms().Count} objects={Catalog.Objects.Count} " +
                     $"room={(CurrentRoom != null ? CurrentRoom.Id : "none")} operator={Operator}");
            Audit("mode_enter", new JObject { ["app_version"] = Application.version });
        }

        /// <summary>Leaves Annotate mode: stops a running recording, hides panel/rays/drawings, disables the EventSystem. Detect mode is untouched.</summary>
        public void Exit()
        {
            if (!IsActive)
            {
                return;
            }

            if (Recorder != null && Recorder.IsRecording)
            {
                Recorder.Stop();
            }

            (m_screen as IAnnLeaveScreen)?.OnLeave();
            m_screen = null;
            AssistedWantsDetector = false;
            IsActive = false;
            if (m_panel != null)
            {
                m_panel.Visible = false;
            }

            if (Draw != null)
            {
                Draw.Begin();
                Draw.End();
                Draw.Visible = false;
            }

            m_rightRay?.SetActive(false);
            m_leftRay?.SetActive(false);
            if (m_eventSystem != null)
            {
                m_eventSystem.SetActive(false);
            }

            Audit("mode_exit");
            Log.Info("ann_exit");
        }

        /// <summary>Asks Detect3DApp to leave Annotate mode and reopen the start menu (Home → Exit button).</summary>
        public void RequestExit() => ExitRequested?.Invoke();

        /// <summary>Reads catalog, calibration and the last used room from persistentDataPath/QD3D and starts localising that room's anchor.</summary>
        private void LoadData()
        {
            Store = new AnnotationStore(DataRoot);
            Catalog = Store.LoadCatalog();
            Calibration = Store.LoadCalibration();
            Recorder ??= new SessionRecorder(this);
            Keyboard ??= new AnnKeyboard(this);
            CurrentRoom = null;
            Annotations = null;
            Room.Clear();
            var roomId = PlayerPrefs.GetString(KeyCurrentRoom, "");
            if (!string.IsNullOrEmpty(roomId))
            {
                var room = Store.LoadRoom(roomId);
                if (room != null)
                {
                    SetRoom(room, false);
                }
                else
                {
                    Log.Warn($"ann_room_missing id={roomId} (the last used room file is gone or unreadable)");
                }
            }
        }

        /// <summary>Activates the EventSystem + Interaction SDK canvas module (built by SceneBuilder; created here only as a fallback).</summary>
        private void EnsureEventSystem()
        {
            if (m_eventSystem == null)
            {
                // SceneBuilder normally adds it; this keeps the mode working in a hand-made test scene.
                m_eventSystem = new GameObject("EventSystem");
                m_eventSystem.transform.SetParent(transform, false);
                m_eventSystem.AddComponent<EventSystem>();
                m_eventSystem.AddComponent<PointableCanvasModule>();
                Log.Warn("ann_event_system_created_at_runtime");
            }

            m_eventSystem.SetActive(true);
        }

        /// <summary>Creates the rig helper, the drawing mesh, the panel and one ray per controller (once).</summary>
        private void EnsureUi()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (Rig == null)
            {
                Rig = new AnnRig(rig, Context.Head);
            }

            Rig.ApplyCalibration(Calibration);
            if (Draw == null)
            {
                Draw = new AnnDraw(Context.Material, Context.Font, Context.Head);
            }

            if (m_panel != null)
            {
                return;
            }

            m_panel = new AnnPanel("AnnPanel", 0.80f, 0.76f, Context.Font);
            if (rig == null)
            {
                Log.Error("ann_no_camera_rig: controller rays unavailable");
                return;
            }

            m_rightRay = AnnRay.Create(rig.rightControllerAnchor, OVRInput.Controller.RTouch, Context.Material);
            m_leftRay = AnnRay.Create(rig.leftControllerAnchor, OVRInput.Controller.LTouch, Context.Material);
        }

        // ------------------------------------------------------------------ screens

        /// <summary>Switches the panel to another screen (calls OnLeave on the old one) and rebuilds it.</summary>
        public void Show(IAnnScreen screen)
        {
            if (m_screen != null && !ReferenceEquals(m_screen, screen))
            {
                (m_screen as IAnnLeaveScreen)?.OnLeave();
            }

            m_screen = screen;
            Log.Info($"ann_screen {screen.GetType().Name}");
            if (m_panel != null && !m_panel.Visible)
            {
                m_panel.Visible = true;
            }

            Rebuild();
        }

        /// <summary>Rebuild the current screen after a state change. Deferred to the end of the frame so a button
        /// never destroys itself while its own click is still being dispatched.</summary>
        public void Refresh() => m_refreshQueued = true;

        /// <summary>Queues an action from a worker thread to run on the main thread in the next Update.</summary>
        public void RunOnMain(Action action) => m_mainThread.Enqueue(action);

        /// <summary>Moves the panel in front of the user at reading distance (0.8 m).</summary>
        public void PlacePanel() => m_panel?.PlaceInFront(Context.Head);

        /// <summary>Clears the panel and lets the current screen describe its content again.</summary>
        private void Rebuild()
        {
            m_refreshQueued = false;
            if (m_screen == null || m_panel == null)
            {
                return;
            }

            m_panel.Clear();
            m_panel.Title = m_screen.Title;
            m_screen.Build(m_panel);
        }

        /// <summary>Per frame: main-thread queue, room anchor, keyboard, recorder, panel buttons (Menu / X), then the current screen's Tick.</summary>
        private void Update()
        {
            if (!IsActive)
            {
                return;
            }

            while (m_mainThread.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Error($"ann_main_thread_action {e}");
                }
            }

            Room.Update();
            Keyboard.Update();
            Recorder.Tick();

            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
            {
                m_panel.Visible = true;
                m_panel.PlaceInFront(Context.Head);
            }

            if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch))
            {
                m_panel.Visible = !m_panel.Visible;
            }

            Draw.Begin();
            DrawRoom();
            if (m_screen is IAnnTickScreen tick)
            {
                try
                {
                    tick.Tick();
                }
                catch (Exception e)
                {
                    Log.Error($"ann_screen_tick {m_screen.GetType().Name} {e}");
                }
            }
        }

        /// <summary>Uploads this frame's drawings and applies a pending screen refresh (after button callbacks have finished).</summary>
        private void LateUpdate()
        {
            if (!IsActive)
            {
                return;
            }

            Draw.End();
            if (m_refreshQueued && m_screen != null)
            {
                Rebuild();
            }
        }

        /// <summary>Room axes at the origin and the test box, whenever the frame is known.</summary>
        private void DrawRoom()
        {
            if (!Room.HasFrame)
            {
                return;
            }

            var f = Room.Frame;
            Draw.Axes(f.Origin + Vector3.up * 0.002f, f.Yaw, 0.25f);
            if (CurrentRoom?.TestBox != null)
            {
                var box = f.BoxToWorld(CurrentRoom.TestBox.ToUpright());
                Draw.Box(box, new Color(1f, 0.55f, 0.1f, 1f), 0.003f, default, false);
                Draw.Label(box.TopCenter + Vector3.up * 0.05f, "test box", new Color(1f, 0.55f, 0.1f, 1f), 0.02f);
            }
        }

        /// <summary>The ray of this controller is on a panel: its trigger belongs to the UI, not to the world.</summary>
        public bool RayOnPanel(OVRInput.Controller c)
        {
            var ray = c == OVRInput.Controller.LTouch ? m_leftRay : m_rightRay;
            return ray != null && ray.IsOnPanel;
        }

        /// <summary>Turns one controller's UI ray on or off (the tip calibration needs the trigger for itself).</summary>
        public void SetRayEnabled(OVRInput.Controller c, bool enabled)
        {
            var ray = c == OVRInput.Controller.LTouch ? m_leftRay : m_rightRay;
            ray?.SetActive(enabled);
        }

        /// <summary>Trigger pressed this frame on a controller that is not pointing at a panel.</summary>
        public bool WorldTriggerDown(out OVRInput.Controller controller)
        {
            foreach (var c in new[] { OVRInput.Controller.RTouch, OVRInput.Controller.LTouch })
            {
                if (AnnRig.TriggerDown(c) && !RayOnPanel(c))
                {
                    controller = c;
                    return true;
                }
            }

            controller = OVRInput.Controller.None;
            return false;
        }

        // ------------------------------------------------------------------ detector hook (Detect3DApp)

        /// <summary>Called by Detect3DApp for every YOLO + depth frame while WantsDetector is true: keeps the latest tracks (M4) and records them.</summary>
        public void OnDetectorFrame(YoloFrame frame, IReadOnlyList<DetectorFit> fits, IReadOnlyList<TrackedObject> tracks)
        {
            if (!IsActive)
            {
                return;
            }

            m_lastFits.Clear();
            m_lastFits.AddRange(fits);
            LatestTracks.Clear();
            LatestTracks.AddRange(tracks);
            LatestTracksTime = Time.realtimeSinceStartup;
            Recorder.OnDetectorFrame(frame, fits, tracks);
        }

        // ------------------------------------------------------------------ data actions (logged + audited)

        /// <summary>Makes a room current: loads its annotations, remembers it for the next launch and starts localising its anchor.</summary>
        public void SetRoom(RoomFile room, bool audit = true)
        {
            CurrentRoom = room;
            Annotations = Store.LoadAnnotations(room.Id);
            PlayerPrefs.SetString(KeyCurrentRoom, room.Id);
            PlayerPrefs.Save();
            Room.Load(room);
            if (audit)
            {
                Log.Info($"ann_room_selected id={room.Id}");
                Audit("room_selected", new JObject { ["room_id"] = room.Id, ["anchor_uuid"] = room.AnchorUuid });
            }
        }

        /// <summary>Writes the current room file (atomic).</summary>
        public void SaveRoom()
        {
            if (CurrentRoom != null)
            {
                Store.SaveRoom(CurrentRoom);
            }
        }

        /// <summary>Writes catalog.json (atomic).</summary>
        public void SaveCatalog() => Store.SaveCatalog(Catalog);
        /// <summary>Writes annotations/&lt;room&gt;.json (atomic).</summary>
        public void SaveAnnotations() => Store.SaveAnnotations(Annotations);

        /// <summary>Writes calibration.json and applies the tip offsets to the controllers.</summary>
        public void SaveCalibration()
        {
            Store.SaveCalibration(Calibration);
            Rig.ApplyCalibration(Calibration);
        }

        /// <summary>Creates a room (name from the keyboard, or Room_NN) and makes it current.</summary>
        public RoomFile NewRoom(string name)
        {
            var names = new List<string>();
            foreach (var r in Store.ListRooms())
            {
                names.Add(r.Name);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = AnnotationStore.NextName("Room", names);
            }

            var room = Store.CreateRoom(name);
            Log.Info($"ann_room_created id={room.Id} name={room.Name}");
            Audit("room_created", new JObject { ["room_id"] = room.Id, ["name"] = room.Name });
            SetRoom(room);
            return room;
        }

        /// <summary>Adds a catalog object with the next free name (Chair_NN); edit it afterwards in the Catalog.</summary>
        public ObjectEntry NewObject()
        {
            var names = new List<string>();
            foreach (var o in Catalog.Objects)
            {
                names.Add(o.Name);
            }

            var entry = Store.CreateObject(Catalog, AnnotationStore.NextName("Chair", names));
            Log.Info($"ann_object_created id={entry.Id} name={entry.Name} category={entry.Category}");
            Audit("object_created", new JObject { ["object_id"] = entry.Id, ["name"] = entry.Name, ["category"] = entry.Category });
            return entry;
        }

        /// <summary>Number of repetitions (all methods) stored for an object in the current room.</summary>
        public int RepetitionCount(string objectId)
        {
            var o = Annotations?.Find(objectId);
            return o != null ? o.Repetitions.Count : 0;
        }

        /// <summary>Floor height in the room frame: the FloorEstimator if it has a floor, else the room cross (y = 0).</summary>
        public float FloorRoomY(out string source)
        {
            if (Context.Floor != null && Context.Floor.HasFloor && Room.HasFrame)
            {
                source = "floor_estimator";
                return Context.Floor.FloorY - Room.Frame.Origin.y;
            }

            source = "room_cross";
            return 0f;
        }

        /// <summary>World floor height for drawing: FloorEstimator if available, else the room cross height.</summary>
        public float FloorWorldY()
        {
            if (Context.Floor != null && Context.Floor.HasFloor)
            {
                return Context.Floor.FloorY;
            }

            return Room.HasFrame ? Room.Frame.Origin.y : (Context.Head != null ? Context.Head.position.y - 1.6f : 0f);
        }

        /// <summary>Audit trail: QD3D/events.jsonl always, and the session's events.jsonl while recording.</summary>
        public void Audit(string type, JObject data = null)
        {
            try
            {
                var line = new JObject
                {
                    ["t"] = AnnotationStore.Timestamp(),
                    ["type"] = type,
                    ["room_id"] = CurrentRoom?.Id,
                    ["operator"] = Operator,
                };
                if (data != null)
                {
                    foreach (var p in data.Properties())
                    {
                        line[p.Name] = p.Value;
                    }
                }

                Store?.AppendEvent(type, line);
                Recorder?.LogEvent(line);
            }
            catch (Exception e)
            {
                Log.Error($"ann_audit_failed type={type} error={e.Message}");
            }
        }

        /// <summary>Writes an audit line when the headset is taken off / the app is paused.</summary>
        private void OnApplicationPause(bool paused)
        {
            if (paused && IsActive)
            {
                Audit("app_pause");
            }
        }

        /// <summary>Finishes a running recording when the app closes.</summary>
        private void OnApplicationQuit()
        {
            if (Recorder != null && Recorder.IsRecording)
            {
                Recorder.Stop();
            }
        }
    }
}
