// QD3D Annotate mode controller. Detect3DApp calls Enter() when "Annotate" is started from the start menu and
// Exit() when the user leaves; nothing here runs in Detect mode (the EventSystem child stays inactive).
// Owns the data (AnnotationStore: catalog, rooms, annotations), the controller rays, and one panel whose content
// is the current screen.
//   Left Menu button: bring the panel back in front of you.
using System;
using System.Collections.Generic;
using Meta.XR;
using Newtonsoft.Json.Linq;
using Oculus.Interaction;
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
    }

    public interface IAnnScreen
    {
        string Title { get; }
        void Build(AnnPanel panel);
    }

    public sealed class AnnotationMode : MonoBehaviour
    {
        private const string KeyCurrentRoom = "qd3d.ann.room";

        [SerializeField] private GameObject m_eventSystem;

        private AnnPanel m_panel;
        private AnnRay m_rightRay;
        private AnnRay m_leftRay;
        private IAnnScreen m_screen;
        private bool m_refreshQueued;

        public event Action ExitRequested;

        public bool IsActive { get; private set; }
        public AnnotationContext Context { get; private set; }
        public AnnotationStore Store { get; private set; }
        public CatalogFile Catalog { get; private set; }
        public RoomFile CurrentRoom { get; private set; }
        public AnnotationFile Annotations { get; private set; }

        public static string DataRoot => System.IO.Path.Combine(Application.persistentDataPath, "QD3D");

        // ------------------------------------------------------------------ enter / exit

        public void Enter(AnnotationContext context)
        {
            Context = context;
            IsActive = true;
            EnsureEventSystem();
            LoadData();
            EnsureUi();
            m_panel.Visible = true;
            m_panel.PlaceInFront(context.Head);
            m_rightRay?.SetActive(true);
            m_leftRay?.SetActive(true);
            Show(new HomeScreen(this));
            Log.Info($"ann_enter root={Store.Root} rooms={Store.ListRooms().Count} objects={Catalog.Objects.Count} " +
                     $"room={(CurrentRoom != null ? CurrentRoom.Id : "none")}");
            Audit("mode_enter", new JObject { ["app_version"] = Application.version });
        }

        public void Exit()
        {
            if (!IsActive)
            {
                return;
            }

            IsActive = false;
            if (m_panel != null)
            {
                m_panel.Visible = false;
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

        public void RequestExit() => ExitRequested?.Invoke();

        private void LoadData()
        {
            Store = new AnnotationStore(DataRoot);
            Catalog = Store.LoadCatalog();
            CurrentRoom = null;
            Annotations = null;
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

        private void EnsureUi()
        {
            if (m_panel != null)
            {
                return;
            }

            m_panel = new AnnPanel("AnnPanel", 0.78f, 0.74f, Context.Font);
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig == null)
            {
                Log.Error("ann_no_camera_rig: controller rays unavailable");
                return;
            }

            m_rightRay = AnnRay.Create(rig.rightControllerAnchor, OVRInput.Controller.RTouch, Context.Material);
            m_leftRay = AnnRay.Create(rig.leftControllerAnchor, OVRInput.Controller.LTouch, Context.Material);
        }

        // ------------------------------------------------------------------ screens

        public void Show(IAnnScreen screen)
        {
            m_screen = screen;
            Log.Info($"ann_screen {screen.GetType().Name}");
            Rebuild();
        }

        /// <summary>Rebuild the current screen after a state change. Deferred to the end of the frame so a button
        /// never destroys itself while its own click is still being dispatched.</summary>
        public void Refresh() => m_refreshQueued = true;

        private void Rebuild()
        {
            m_refreshQueued = false;
            m_panel.Clear();
            m_panel.Title = m_screen.Title;
            m_screen.Build(m_panel);
        }

        private void LateUpdate()
        {
            if (!IsActive)
            {
                return;
            }

            if (m_refreshQueued && m_screen != null)
            {
                Rebuild();
            }

            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
            {
                m_panel.PlaceInFront(Context.Head);
            }
        }

        /// <summary>True while a controller ray is on a panel (later milestones: ignore world clicks then).</summary>
        public bool PointerOnPanel => (m_rightRay != null && m_rightRay.IsOnPanel) || (m_leftRay != null && m_leftRay.IsOnPanel);

        // ------------------------------------------------------------------ data actions (logged + audited)

        public void SetRoom(RoomFile room, bool audit = true)
        {
            CurrentRoom = room;
            Annotations = Store.LoadAnnotations(room.Id);
            PlayerPrefs.SetString(KeyCurrentRoom, room.Id);
            PlayerPrefs.Save();
            if (audit)
            {
                Log.Info($"ann_room_selected id={room.Id}");
                Audit("room_selected", new JObject { ["room_id"] = room.Id });
            }
        }

        public RoomFile NewRoom()
        {
            var names = new List<string>();
            foreach (var r in Store.ListRooms())
            {
                names.Add(r.Name);
            }

            var room = Store.CreateRoom(AnnotationStore.NextName("Room", names));
            Log.Info($"ann_room_created id={room.Id} name={room.Name}");
            Audit("room_created", new JObject { ["room_id"] = room.Id, ["name"] = room.Name });
            SetRoom(room);
            return room;
        }

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

        public int RepetitionCount(string objectId)
        {
            var o = Annotations?.Find(objectId);
            return o != null ? o.Repetitions.Count : 0;
        }

        public void Audit(string type, JObject data = null)
        {
            try
            {
                Store?.AppendEvent(type, data);
            }
            catch (Exception e)
            {
                Log.Error($"ann_audit_failed type={type} error={e.Message}");
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && IsActive)
            {
                Audit("app_pause");
            }
        }
    }
}
