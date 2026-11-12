// Room screens: create / select rooms, place the room cross (anchor + frame), drift checks, test box.
// The room frame is defined by a physical tape cross on the floor. Placing the cross = touching its centre and a
// point along one arm with the calibrated controller tip; a persisted OVRSpatialAnchor re-finds it later.
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class RoomScreen : IAnnScreen
    {
        private const int PageSize = 3;
        private readonly AnnotationMode m_mode;
        private int m_page;

        public RoomScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Rooms";

        /// <summary>Paged room list, New room, Current room.</summary>
        public void Build(AnnPanel p)
        {
            var rooms = m_mode.Store.ListRooms();
            var current = m_mode.CurrentRoom;
            p.Label(current != null ? $"Current room: <b>{current.Name}</b>  (id {current.Id})" : "No room selected.",
                AnnPanel.TextSize, current != null ? AnnPanel.TextColor : AnnPanel.Warning);
            p.Label(rooms.Count == 0 ? "No rooms yet. Press New room." : $"{rooms.Count} room(s). Select one:", 24, AnnPanel.DimText);

            var pages = Mathf.Max(1, (rooms.Count + PageSize - 1) / PageSize);
            m_page = Mathf.Clamp(m_page, 0, pages - 1);
            for (var i = m_page * PageSize; i < Mathf.Min(rooms.Count, (m_page + 1) * PageSize); i++)
            {
                var room = rooms[i];
                var isCurrent = current != null && current.Id == room.Id;
                var anchor = string.IsNullOrEmpty(room.AnchorUuid) ? "no cross" : "cross " + RoomService.Short(room.AnchorUuid);
                p.Button($"{room.Name}   ({anchor}){(isCurrent ? "   [current]" : "")}", () =>
                {
                    if (!isCurrent)
                    {
                        m_mode.SetRoom(room);
                    }

                    m_mode.Show(new RoomDetailScreen(m_mode));
                }, true, isCurrent ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 60);
            }

            Pager.Build(p, m_page, pages, delta =>
            {
                m_page += delta;
                m_mode.Refresh();
            });

            p.BeginRow();
            p.Button("New room", () => m_mode.Keyboard.Open("Room name", "", false, name =>
            {
                m_mode.NewRoom(name);
                m_mode.Show(new RoomDetailScreen(m_mode));
            }), true, AnnPanel.PrimaryButton);
            p.Button("Current room", () => m_mode.Show(new RoomDetailScreen(m_mode)), current != null);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
        }
    }

    public sealed class RoomDetailScreen : IAnnTickScreen
    {
        private readonly AnnotationMode m_mode;
        private UnityEngine.UI.Text m_status;
        private RoomFrameState m_lastState;

        public RoomDetailScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Room: " + (m_mode.CurrentRoom != null ? m_mode.CurrentRoom.Name : "none");

        /// <summary>Frame status, anchor, drift summary, Place/Re-place cross, Retry, Drift check, Re-touch, Test box, Rename.</summary>
        public void Build(AnnPanel p)
        {
            var room = m_mode.CurrentRoom;
            if (room == null)
            {
                p.Label("No room selected.", AnnPanel.TextSize, AnnPanel.Warning);
                p.Button("Back", () => m_mode.Show(new RoomScreen(m_mode)));
                return;
            }

            m_lastState = m_mode.Room.State;
            m_status = p.Label(m_mode.Room.Status, 26, StatusColor());
            var placed = !string.IsNullOrEmpty(room.AnchorUuid);
            var hasFrame = m_mode.Room.HasFrame;
            p.Label($"Anchor: {(placed ? room.AnchorUuid : "none")}", 20, AnnPanel.DimText);
            p.Label(DriftSummary(room), 22);

            p.BeginRow();
            p.Button(placed ? "Re-place room cross" : "Place room cross", () => m_mode.Show(new PlaceCrossScreen(m_mode)), true,
                placed ? AnnPanel.ButtonColor : AnnPanel.PrimaryButton);
            p.Button("Retry localise", () => m_mode.Room.Load(room), placed && !hasFrame);
            p.EndRow();
            p.BeginRow();
            p.Button("Drift check", () => m_mode.Show(new TouchCheckScreen(m_mode, "drift_cross")), hasFrame, AnnPanel.PrimaryButton);
            p.Button("Re-touch room cross", () => m_mode.Show(new TouchCheckScreen(m_mode, "room_cross")), hasFrame);
            p.EndRow();
            p.BeginRow();
            p.Button(room.TestBox == null ? "Place test box" : "Move test box", () => m_mode.Show(new TestBoxScreen(m_mode)), hasFrame);
            p.Button("Rename", () => m_mode.Keyboard.Open("Room name", room.Name, false, name =>
            {
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                m_mode.Audit("room_renamed", new JObject { ["old"] = room.Name, ["new"] = name });
                room.Name = name;
                m_mode.SaveRoom();
            }));
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
            if (m_mode.Room.State == RoomFrameState.Failed)
            {
                p.Label("The room frame is NOT available: boxes cannot be shown or saved. Re-placing the cross creates a NEW anchor; " +
                        "existing boxes stay valid only if you touch exactly the same tape cross.", 22, new Color(1f, 0.5f, 0.5f));
            }
        }

        /// <summary>Live frame status; rebuilds the panel when the anchor state changes.</summary>
        public void Tick()
        {
            if (m_status != null)
            {
                m_status.text = m_mode.Room.Status;
                m_status.color = StatusColor();
            }

            if (m_mode.Room.State != m_lastState)
            {
                m_mode.Refresh();
            }

            TouchAids.DrawTips(m_mode, false);
        }

        private Color StatusColor() => m_mode.Room.State == RoomFrameState.Localized ? new Color(0.45f, 0.95f, 0.55f) :
            m_mode.Room.State == RoomFrameState.Failed ? new Color(1f, 0.4f, 0.4f) : AnnPanel.Warning;

        /// <summary>Last 4 drift / room-cross checks for display.</summary>
        public static string DriftSummary(RoomFile room)
        {
            if (room.DriftChecks.Count == 0)
            {
                return room.DriftReference == null ? "Drift: no drift cross reference yet" : "Drift: reference set, no checks yet";
            }

            var sb = new System.Text.StringBuilder("Last checks: ");
            var start = Mathf.Max(0, room.DriftChecks.Count - 4);
            for (var i = start; i < room.DriftChecks.Count; i++)
            {
                var d = room.DriftChecks[i];
                sb.Append(d.Kind == "room_cross" ? "room " : "drift ").Append(d.DeltaMm.ToString("F1")).Append(" mm (").Append(d.When).Append(")");
                if (i < room.DriftChecks.Count - 1)
                {
                    sb.Append(",  ");
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>Touch the room cross centre, then a point along one tape arm → preview frame → create the anchor.</summary>
    public sealed class PlaceCrossScreen : IAnnTickScreen
    {
        private readonly AnnotationMode m_mode;
        private Vector3? m_centre;
        private Vector3? m_arm;
        private RoomFrame m_preview;
        private string m_error;
        private bool m_saving;
        private string m_controller;

        public PlaceCrossScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Place room cross";

        /// <summary>Step prompt (centre → arm → save), error, Save / Restart / Cancel.</summary>
        public void Build(AnnPanel p)
        {
            if (m_saving)
            {
                p.Label("Creating and saving the spatial anchor…", AnnPanel.TextSize, AnnPanel.Warning);
                return;
            }

            p.Label(m_centre == null
                ? "1. Put the controller TIP on the CENTRE of the room tape cross and press the trigger."
                : m_arm == null
                    ? "2. Put the tip on one tape ARM, ≥ 30 cm from the centre (this sets +Z), and press the trigger."
                    : "3. Check the axes on the floor (blue = +Z along the arm), then Save.", 28);
            p.Label("Touch with the tip resting ON the floor tape. The white plumb line shows where the tip is above the floor.", 22, AnnPanel.DimText);
            if (!string.IsNullOrEmpty(m_error))
            {
                p.Label(m_error, 24, new Color(1f, 0.45f, 0.45f));
            }

            p.BeginRow();
            p.Button("Save room frame", Save, m_preview.IsValid, AnnPanel.PrimaryButton);
            p.Button("Restart", () =>
            {
                m_centre = m_arm = null;
                m_preview = default;
                m_error = null;
                m_mode.Refresh();
            });
            p.Button("Cancel", () => m_mode.Show(new RoomDetailScreen(m_mode)));
            p.EndRow();
        }

        /// <summary>Tip + plumb line; trigger records the centre, then the arm point (frame preview).</summary>
        public void Tick()
        {
            TouchAids.DrawTips(m_mode, true);
            if (m_centre.HasValue)
            {
                m_mode.Draw.Point(m_centre.Value, new Color(0.3f, 0.9f, 1f, 1f), 0.02f);
            }

            if (m_preview.IsValid)
            {
                m_mode.Draw.Axes(m_preview.Origin + Vector3.up * 0.003f, m_preview.Yaw, 0.4f);
            }

            if (m_saving || m_preview.IsValid || !m_mode.WorldTriggerDown(out var c))
            {
                return;
            }

            var tip = m_mode.Rig.TipWorld(c);
            m_controller = AnnRig.Name(c);
            if (m_centre == null)
            {
                m_centre = tip;
                m_mode.Audit("room_cross_touch", new JObject { ["label"] = "centre", ["world"] = RecordVec(tip), ["controller"] = m_controller });
            }
            else
            {
                m_arm = tip;
                m_mode.Audit("room_cross_touch", new JObject { ["label"] = "arm", ["world"] = RecordVec(tip), ["controller"] = m_controller });
                if (!RoomFrame.TryFromCrossTouches(m_centre.Value, tip, out m_preview, out m_error))
                {
                    m_arm = null;
                }
                else
                {
                    m_error = null;
                }
            }

            m_mode.Refresh();
        }

        /// <summary>Creates and saves the anchor, stores it in the room file with the anchor history.</summary>
        private void Save()
        {
            var room = m_mode.CurrentRoom;
            var oldUuid = room.AnchorUuid;
            var oldFrame = m_mode.Room.Frame;
            var oldValid = m_mode.Room.HasFrame;
            var frame = m_preview;
            m_saving = true;
            m_mode.Refresh();
            m_mode.Room.CreateAnchor(frame, (uuid, error) =>
            {
                m_saving = false;
                if (uuid == null)
                {
                    m_error = "Anchor failed: " + error;
                    m_mode.Audit("room_cross_failed", new JObject { ["error"] = error });
                    m_mode.Refresh();
                    return;
                }

                var record = new AnchorRecord
                {
                    Uuid = uuid,
                    Created = AnnotationStore.Timestamp(),
                    CentreWorld = Vec.ToArray(m_centre.Value),
                    ArmWorld = Vec.ToArray(m_arm.Value),
                    Reason = oldUuid == null ? "first placement" : "re-placed",
                };
                if (oldValid)
                {
                    // Where the new cross touch lands in the previous frame: anchor + touch repeatability.
                    var off = oldFrame.WorldToRoom(frame.Origin) * 1000f;
                    record.PreviousFrameOffsetMm = new[] { off.x, off.y, off.z };
                    record.PreviousFrameYawDeg = RoomFrame.WrapPi(frame.Yaw - oldFrame.Yaw) * Mathf.Rad2Deg;
                }

                room.AnchorHistory.Add(record);
                room.AnchorUuid = uuid;
                room.AnchorSaved = record.Created;
                m_mode.SaveRoom();
                m_mode.Audit("room_cross_placed", new JObject
                {
                    ["anchor_uuid"] = uuid,
                    ["previous_uuid"] = oldUuid,
                    ["frame"] = frame.ToString(),
                    ["previous_frame_offset_mm"] = record.PreviousFrameOffsetMm != null ? new JArray(record.PreviousFrameOffsetMm) : null,
                    ["previous_frame_yaw_deg"] = oldValid ? (JToken)record.PreviousFrameYawDeg : null,
                });
                Log.Info($"ann_room_cross_placed room={room.Id} uuid={uuid} {frame}");
                m_mode.Show(new RoomDetailScreen(m_mode));
            });
        }

        public static JArray RecordVec(Vector3 v) => new JArray(v.x, v.y, v.z);
    }

    /// <summary>Drift check (second tape cross ≥ 2 m away) or re-touch of the room cross: touch its centre,
    /// log its position in the room frame; drift = change vs the reference.</summary>
    public sealed class TouchCheckScreen : IAnnTickScreen
    {
        private static readonly string[] Whens = { "session_start", "on_demand", "session_end" };
        private readonly AnnotationMode m_mode;
        private readonly string m_kind;
        private int m_when = 1;
        private string m_result;

        public TouchCheckScreen(AnnotationMode mode, string kind)
        {
            m_mode = mode;
            m_kind = kind;
            if (kind == "drift_cross" && !mode.Recorder.IsRecording)
            {
                m_when = 1;
            }
        }

        public string Title => m_kind == "room_cross" ? "Re-touch room cross" : "Drift check";

        /// <summary>Instructions, when-selector, result, Done (and reset of the drift reference).</summary>
        public void Build(AnnPanel p)
        {
            var room = m_mode.CurrentRoom;
            var setRef = m_kind == "drift_cross" && room.DriftReference == null;
            p.Label(m_kind == "room_cross"
                ? "Touch the CENTRE of the ROOM cross with the tip and press the trigger. Expected: 0 mm."
                : setRef
                    ? "No drift reference yet: touch the CENTRE of the DRIFT cross (≥ 2 m away) to set it."
                    : "Touch the CENTRE of the DRIFT cross with the tip and press the trigger.", 28);
            p.BeginRow();
            p.Button("< When", () =>
            {
                m_when = (m_when + Whens.Length - 1) % Whens.Length;
                m_mode.Refresh();
            }, true, null, 56);
            p.Button(Whens[m_when], null, false, null, 56);
            p.Button("When >", () =>
            {
                m_when = (m_when + 1) % Whens.Length;
                m_mode.Refresh();
            }, true, null, 56);
            p.EndRow();
            if (!string.IsNullOrEmpty(m_result))
            {
                p.Label(m_result, 30, AnnPanel.Accent);
            }

            p.Label(RoomDetailScreen.DriftSummary(room), 22, AnnPanel.DimText);
            p.BeginRow();
            if (m_kind == "drift_cross" && room.DriftReference != null)
            {
                p.Button("Reset drift reference", () =>
                {
                    m_mode.Audit("drift_reference_reset", new JObject { ["old"] = new JArray(room.DriftReference) });
                    room.DriftReference = null;
                    m_mode.SaveRoom();
                    m_mode.Refresh();
                }, true, AnnPanel.DangerButton);
            }

            p.Button("Done", () => m_mode.Show(new RoomDetailScreen(m_mode)));
            p.EndRow();
        }

        /// <summary>Trigger records the tip in the room frame and the distance to the reference.</summary>
        public void Tick()
        {
            TouchAids.DrawTips(m_mode, true);
            var room = m_mode.CurrentRoom;
            if (m_kind == "drift_cross" && room.DriftReference != null && m_mode.Room.HasFrame)
            {
                var refWorld = m_mode.Room.Frame.RoomToWorld(Vec.FromArray(room.DriftReference));
                m_mode.Draw.FloorRing(refWorld + Vector3.up * 0.002f, 0.03f, new Color(0.3f, 0.9f, 1f, 1f), 0.0015f);
            }

            if (!m_mode.Room.HasFrame || !m_mode.WorldTriggerDown(out var c))
            {
                return;
            }

            var p = m_mode.Room.Frame.WorldToRoom(m_mode.Rig.TipWorld(c));
            float delta;
            if (m_kind == "room_cross")
            {
                delta = p.magnitude * 1000f;
            }
            else if (room.DriftReference == null)
            {
                room.DriftReference = Vec.ToArray(p);
                delta = 0f;
            }
            else
            {
                delta = (p - Vec.FromArray(room.DriftReference)).magnitude * 1000f;
            }

            var check = new DriftCheck
            {
                Timestamp = AnnotationStore.Timestamp(),
                Kind = m_kind,
                When = Whens[m_when],
                SessionId = m_mode.Recorder.IsRecording ? m_mode.Recorder.SessionId : null,
                Position = Vec.ToArray(p),
                DeltaMm = delta,
            };
            room.DriftChecks.Add(check);
            m_mode.SaveRoom();
            m_mode.Audit("drift_check", new JObject
            {
                ["kind"] = m_kind,
                ["when"] = check.When,
                ["position_room"] = new JArray(check.Position),
                ["delta_mm"] = delta,
                ["controller"] = AnnRig.Name(c),
                ["anchor_uuid"] = m_mode.Room.AnchorUuid,
            });
            Log.Info($"ann_drift kind={m_kind} when={check.When} delta_mm={delta:F1} pos=({p.x:F4},{p.y:F4},{p.z:F4})");
            m_result = $"{(m_kind == "room_cross" ? "Room cross" : "Drift cross")}: {delta:F1} mm  (x {p.x * 1000f:F0}, y {p.y * 1000f:F0}, z {p.z * 1000f:F0} mm)";
            m_mode.Refresh();
        }
    }

    /// <summary>Places a 20 cm test box on the floor under the tip (room frame), to check the frame after a restart.</summary>
    public sealed class TestBoxScreen : IAnnTickScreen
    {
        private readonly AnnotationMode m_mode;

        public TestBoxScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Test box";

        /// <summary>Instructions and Done.</summary>
        public void Build(AnnPanel p)
        {
            p.Label("Hold the tip above a spot on the floor (e.g. a tape mark) and press the trigger: a 20 cm box is stored there in the room frame. " +
                    "After a restart it must reappear on the same spot.", 26);
            p.Button("Done", () => m_mode.Show(new RoomDetailScreen(m_mode)));
        }

        /// <summary>Trigger stores a 20 cm box on the floor under the tip.</summary>
        public void Tick()
        {
            TouchAids.DrawTips(m_mode, true);
            if (!m_mode.Room.HasFrame || !m_mode.WorldTriggerDown(out var c))
            {
                return;
            }

            var tip = m_mode.Room.Frame.WorldToRoom(m_mode.Rig.TipWorld(c));
            var floor = m_mode.FloorRoomY(out _);
            var box = new UprightBox(new Vector3(tip.x, floor, tip.z), 0f, new Vector3(0.2f, 0.2f, 0.2f));
            m_mode.CurrentRoom.TestBox = BoxDto.From(box);
            m_mode.SaveRoom();
            m_mode.Audit("test_box_placed", new JObject { ["box_room"] = JObject.FromObject(m_mode.CurrentRoom.TestBox) });
            Log.Info($"ann_test_box room={m_mode.CurrentRoom.Id} {box}");
            m_mode.Show(new RoomDetailScreen(m_mode));
        }
    }
}
