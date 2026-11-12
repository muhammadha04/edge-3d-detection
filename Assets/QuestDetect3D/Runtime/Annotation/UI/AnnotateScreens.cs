// Annotate: pick object → method → guided steps → box editor → confirm → self-rating → repeat counter k/3.
//   M1 Touch   : touch 2 points on the front reference (sets yaw), then the extremes front/back/left/right/top
//                with the controller TIP. Trigger = replace that side, grip + trigger = add a point (extend).
//   M2 Point   : the same steps with a controller ray hitting depth (or the floor) instead of touching.
//   M4 Assisted: start from a live YOLO + depth box; stored with assisted = true, never the primary ground truth.
// Editor (right controller): thumbstick = move / rotate / size per mode (hold grip for fine 2 mm / 0.2° steps),
// left thumbstick up/down = height, point at a face + hold grip = push / pull that face. Live checks: translucent
// walls, red depth points outside the box, plumb lines while touching, box vs tape Δ.
// M1/M2 never show detector output (independence). Blind repeats hide the earlier boxes of the object.
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class AnnotateSetupScreen : IAnnScreen
    {
        private const int PageSize = 4;
        private static readonly string[] Methods = { "M1", "M2", "M4" };
        private readonly AnnotationMode m_mode;
        private ObjectEntry m_obj;
        private string m_method = "M1";
        private bool m_blind = true;
        private int m_page;

        public AnnotateSetupScreen(AnnotationMode mode, ObjectEntry obj = null, string method = "M1", bool blind = true)
        {
            m_mode = mode;
            m_obj = obj;
            m_method = method;
            m_blind = blind;
        }

        public string Title => "Annotate";

        /// <summary>Display name of a method id (M1 / M2 / M4).</summary>
        public static string MethodName(string m) => m == "M1" ? "M1 Touch" : m == "M2" ? "M2 Point" : "M4 Assisted";

        /// <summary>Object list with counts per method, method buttons, blind toggle, Start.</summary>
        public void Build(AnnPanel p)
        {
            var objects = m_mode.Catalog.Objects;
            var pages = Mathf.Max(1, (objects.Count + PageSize - 1) / PageSize);
            m_page = Mathf.Clamp(m_page, 0, pages - 1);
            p.Label("1. Object:", 24, AnnPanel.DimText);
            for (var i = m_page * PageSize; i < Mathf.Min(objects.Count, (m_page + 1) * PageSize); i++)
            {
                var o = objects[i];
                var ann = m_mode.Annotations?.Find(o.Id);
                var counts = ann == null ? "0" : $"M1 {Count(ann, "M1")} · M2 {Count(ann, "M2")} · M4 {Count(ann, "M4")}";
                var selected = m_obj != null && m_obj.Id == o.Id;
                p.Button($"{o.Name}   ({counts})", () =>
                {
                    m_obj = o;
                    m_mode.Refresh();
                }, true, selected ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 54, 26);
            }

            Pager.Build(p, m_page, pages, d =>
            {
                m_page += d;
                m_mode.Refresh();
            });
            p.Label("2. Method:", 24, AnnPanel.DimText);
            p.BeginRow();
            foreach (var m in Methods)
            {
                var mm = m;
                p.Button(MethodName(m), () =>
                {
                    m_method = mm;
                    m_mode.Refresh();
                }, true, m_method == m ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56);
            }

            p.EndRow();
            p.BeginRow();
            p.Button(m_blind ? "Blind: ON (earlier boxes hidden)" : "Blind: OFF (earlier boxes shown)", () =>
            {
                m_blind = !m_blind;
                m_mode.Refresh();
            }, true, null, 56, 26);
            p.EndRow();
            if (m_method == "M4")
            {
                p.Label("M4 starts from the live detector box (shown). Stored as assisted: never the primary ground truth.", 22, AnnPanel.Warning);
            }

            p.BeginRow();
            p.Button("Start", () => m_mode.Show(new AnnotateTaskScreen(m_mode, m_obj, m_method, m_blind)), m_obj != null && m_mode.Room.HasFrame,
                AnnPanel.PrimaryButton);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
        }

        /// <summary>Accepted repetitions of one method for an object.</summary>
        public static int Count(ObjectAnnotations ann, string method)
        {
            var n = 0;
            if (ann == null)
            {
                return 0;
            }

            foreach (var r in ann.Repetitions)
            {
                if (r.Method == method && r.Accepted)
                {
                    n++;
                }
            }

            return n;
        }
    }

    public sealed class AnnotateTaskScreen : IAnnTickScreen, IAnnLeaveScreen
    {
        private enum Step
        {
            AssistPick,
            FrontA,
            FrontB,
            Extremes,
            Edit,
            Rate,
            Summary,
        }

        private enum EditMode
        {
            Move,
            Rotate,
            Size,
            Height,
        }

        private static readonly string[] SideNames = TouchBox.ExtremeLabels;
        private const float StickThreshold = 0.6f;
        private const float RepeatDelay = 0.35f;
        private const float RepeatRate = 0.1f;

        private readonly AnnotationMode m_mode;
        private readonly ObjectEntry m_obj;
        private readonly string m_method;
        private readonly bool m_blind;
        private readonly float m_start;
        private readonly LiveChecks m_checks = new LiveChecks();
        private readonly List<TouchInput> m_touches = new List<TouchInput>();
        private readonly List<EditOp> m_edits = new List<EditOp>();
        private readonly List<UprightBox> m_undo = new List<UprightBox>();
        private readonly List<int> m_undoQuarter = new List<int>();
        private int m_lastTrackCount = -1;

        private Step m_step;
        private int m_side;
        private Vector3? m_frontA;
        private Vector3? m_frontB;
        private Vector3 m_hint;
        private float m_yaw;
        private bool m_hasYaw;
        private bool m_hasBox;
        private UprightBox m_methodBox;  // room frame, straight from the touches / detector
        private UprightBox m_box;        // room frame, edited
        private int m_frontQuarter;
        private float m_floorY;
        private string m_floorSource;
        private string m_lastController = "right";
        private AssistSource m_assist;
        private EditMode m_editMode = EditMode.Move;
        private int m_hoverFace = -1;
        private int m_dragFace = -1;
        private UprightBox m_dragStartBox;
        private Vector3 m_dragStartPos;
        private Vector3 m_dragNormal;
        private Vector2Int m_heldDir;
        private float m_nextRepeat;
        private int m_heldHeight;
        private float m_nextHeightRepeat;
        private string m_message;
        private int m_savedIndex;
        private UnityEngine.UI.Text m_hud;

        public AnnotateTaskScreen(AnnotationMode mode, ObjectEntry obj, string method, bool blind)
        {
            m_mode = mode;
            m_obj = obj;
            m_method = method;
            m_blind = blind;
            m_start = Time.realtimeSinceStartup;
            m_step = method == "M4" ? Step.AssistPick : Step.FrontA;
            m_floorY = mode.FloorRoomY(out m_floorSource);
            if (method == "M4")
            {
                mode.Context.ResetDetector?.Invoke();
                mode.AssistedWantsDetector = true;
            }

            var ann = mode.Annotations?.Find(obj.Id);
            mode.Audit("annotation_start", new JObject
            {
                ["object_id"] = obj.Id,
                ["method"] = method,
                ["blind"] = blind,
                ["repetition"] = ann != null ? GroundTruthUpdate.NextIndex(ann, method) : 1,
                ["floor_y_room"] = m_floorY,
                ["floor_source"] = m_floorSource,
                ["anchor_uuid"] = mode.Room.AnchorUuid,
            });
            Log.Info($"ann_task_start object={obj.Id} method={method} blind={blind} floor_y={m_floorY:F3}({m_floorSource})");
        }

        private bool IsPoint => m_method == "M2";

        public string Title => $"{m_obj.Name} · {AnnotateSetupScreen.MethodName(m_method)} · rep {NextIndex()}{(m_blind ? " · blind" : "")}";

        /// <summary>Repetition number this task will get.</summary>
        private int NextIndex()
        {
            var ann = m_mode.Annotations?.Find(m_obj.Id);
            return ann != null ? GroundTruthUpdate.NextIndex(ann, m_method) : 1;
        }

        // ------------------------------------------------------------------ panel

        /// <summary>Panel for the current step (touch, assist pick, editor, rating, summary).</summary>
        public void Build(AnnPanel p)
        {
            if (!m_mode.Room.HasFrame && m_step != Step.Summary)
            {
                p.Label("ROOM FRAME LOST: " + m_mode.Room.Status + "  -  boxes cannot be saved until it is localised again.", 26,
                    new Color(1f, 0.45f, 0.45f));
            }

            switch (m_step)
            {
                case Step.AssistPick:
                    BuildAssist(p);
                    break;
                case Step.FrontA:
                case Step.FrontB:
                case Step.Extremes:
                    BuildTouch(p);
                    break;
                case Step.Edit:
                    BuildEdit(p);
                    break;
                case Step.Rate:
                    BuildRate(p);
                    break;
                default:
                    BuildSummary(p);
                    break;
            }
        }

        private string Verb => IsPoint ? "Point the ray at" : "Touch with the tip";

        /// <summary>Touch / point prompts, side buttons (✓ = has a point), HUD, Edit / Cancel.</summary>
        private void BuildTouch(AnnPanel p)
        {
            string prompt;
            if (m_step == Step.FrontA)
            {
                prompt = $"{Verb} the FRONT reference, point 1 (e.g. left end of the seat front edge), trigger.";
            }
            else if (m_step == Step.FrontB)
            {
                prompt = $"{Verb} the FRONT reference, point 2 (≥ 5 cm away along the same edge). Stand in front of the object.";
            }
            else
            {
                prompt = $"{Verb} the {SideNames[m_side].ToUpperInvariant()}-MOST point of the object (any height), trigger.  Grip + trigger adds a point.";
            }

            p.Label(prompt, 28);
            if (!string.IsNullOrEmpty(m_message))
            {
                p.Label(m_message, 24, AnnPanel.Warning);
            }

            if (m_step == Step.Extremes || m_hasYaw)
            {
                p.BeginRow();
                for (var i = 0; i < SideNames.Length; i++)
                {
                    var index = i;
                    var n = CountSide(SideNames[i]);
                    p.Button($"{SideNames[i]}{(n > 0 ? " ✓" : "")}", () =>
                    {
                        m_side = index;
                        m_step = Step.Extremes;
                        m_mode.Refresh();
                    }, m_hasYaw, i == m_side && m_step == Step.Extremes ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56, 26);
                }

                p.EndRow();
            }

            m_hud = p.Label(HudText(), 24, AnnPanel.Accent);
            p.BeginRow();
            p.Button("Restart front", () =>
            {
                m_frontA = m_frontB = null;
                m_hasYaw = false;
                m_step = Step.FrontA;
                Supersede("front_a");
                Supersede("front_b");
                Recompute();
                m_mode.Refresh();
            }, m_frontA.HasValue, null, 56, 26);
            p.Button(AllSidesSet() ? "Edit box >" : "Edit box (sides missing)", () => EnterEdit(), m_hasBox, AllSidesSet() ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56, 26);
            p.Button("Cancel", Cancel, true, AnnPanel.DangerButton, 56, 26);
            p.EndRow();
        }

        /// <summary>M4: instructions and the 'most confident' shortcut.</summary>
        private void BuildAssist(AnnPanel p)
        {
            var n = m_mode.LatestTracks.Count;
            p.Label("Look at the object until its detector box (magenta) is stable, point the RIGHT ray at it and press the trigger " +
                    "(or use the most confident one).", 26);
            p.Label(n == 0 ? "No detector boxes yet… (" + (m_mode.Context.DetectorStatus?.Invoke() ?? "") + ")" : $"{n} detector box(es).", 24,
                n == 0 ? AnnPanel.Warning : AnnPanel.DimText);
            p.BeginRow();
            p.Button("Use most confident", () => PickTrack(BestTrack()), n > 0, AnnPanel.PrimaryButton);
            p.Button("Cancel", Cancel, true, AnnPanel.DangerButton);
            p.EndRow();
        }

        /// <summary>Editor modes, floor snap, front +90°, undo, reset, HUD, confirm / discard.</summary>
        private void BuildEdit(AnnPanel p)
        {
            p.Label("Walk around. RED dots = object outside the box → point at that face, hold GRIP and pull it out. " +
                    "Stick: " + EditHelp() + " (grip = fine).  Left stick ↕ = height.", 23);
            p.BeginRow();
            foreach (EditMode m in System.Enum.GetValues(typeof(EditMode)))
            {
                var mm = m;
                p.Button(m.ToString(), () =>
                {
                    m_editMode = mm;
                    m_mode.Refresh();
                }, true, m_editMode == m ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 54, 26);
            }

            p.EndRow();
            p.BeginRow();
            p.Button("Floor snap", () => Apply(BoxEdit.SnapToFloor(m_box, m_floorY), "floor_snap", new[] { m_floorY }), true, null, 54, 26);
            p.Button("Front +90°", () =>
            {
                Apply(BoxEdit.TurnFront(m_box, 1), "front_quarter", new float[] { 1 });
                m_frontQuarter = (m_frontQuarter + 1) % 4;
            }, true, null, 54, 26);
            p.Button("Undo", Undo, m_undo.Count > 0, null, 54, 26);
            p.Button("Reset", () => Apply(m_methodBox, "reset_to_method_box", null), true, null, 54, 26);
            p.EndRow();
            m_hud = p.Label(HudText(), 24, AnnPanel.Accent);
            p.BeginRow();
            p.Button(m_method == "M4" ? "< Pick again" : "< Touches", () =>
            {
                m_step = m_method == "M4" ? Step.AssistPick : Step.Extremes;
                if (m_method == "M4")
                {
                    m_mode.AssistedWantsDetector = true;
                }

                m_mode.Refresh();
            }, true, null, 56, 26);
            p.Button("Confirm box", () =>
            {
                m_step = Step.Rate;
                m_mode.Refresh();
            }, m_mode.Room.HasFrame, AnnPanel.PrimaryButton, 56, 26);
            p.Button("Discard", Cancel, true, AnnPanel.DangerButton, 56, 26);
            p.EndRow();
        }

        /// <summary>Self-rating 1–5 (stored with the repetition).</summary>
        private void BuildRate(AnnPanel p)
        {
            p.Label("How sure are you that this box is right?  1 = unsure … 5 = sure", 28);
            p.BeginRow();
            for (var i = 1; i <= 5; i++)
            {
                var rating = i;
                p.Button(i.ToString(), () => Save(rating), m_mode.Room.HasFrame, AnnPanel.PrimaryButton);
            }

            p.EndRow();
            p.Button("< Back to editor", () =>
            {
                m_step = Step.Edit;
                m_mode.Refresh();
            });
        }

        /// <summary>k/3 counter, spread of this method, mean box vs tape, Repeat / Done.</summary>
        private void BuildSummary(AnnPanel p)
        {
            var ann = m_mode.Annotations.Find(m_obj.Id);
            var k = AnnotateSetupScreen.Count(ann, m_method);
            p.Label($"Saved repetition {m_savedIndex} of {m_obj.Name} ({AnnotateSetupScreen.MethodName(m_method)}).   {k}/3", 30, AnnPanel.Accent);
            if (ann != null && ann.AcceptedByMethod.TryGetValue(m_method, out var acc))
            {
                var b = acc.Box.ToUpright();
                p.Label(k >= 2
                    ? $"Spread over {acc.N}: centre {acc.GtSigma.CentreCm:F1} cm · yaw {acc.GtSigma.YawDeg:F1}° · size {acc.GtSigma.SizePct:F1} %"
                    : "Spread appears from the 2nd repetition.", 26);
                p.Label($"Mean box W×D×H: {b.Size.x * 100f:F1} × {b.Size.z * 100f:F1} × {b.Size.y * 100f:F1} cm   {TapeDelta(b)}", 24);
            }

            if (ann?.Accepted != null)
            {
                p.Label($"Accepted ground truth: {ann.Accepted.Method}, n = {ann.Accepted.N}", 22, AnnPanel.DimText);
            }

            p.BeginRow();
            p.Button(k < 3 ? "Repeat (take a few steps away first)" : "Another repeat", () =>
                m_mode.Show(new AnnotateTaskScreen(m_mode, m_obj, m_method, m_blind)), true, AnnPanel.PrimaryButton);
            p.EndRow();
            p.BeginRow();
            p.Button("Other object / method", () => m_mode.Show(new AnnotateSetupScreen(m_mode, m_obj, m_method, m_blind)));
            p.Button("Done", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>Per frame: input for the current step, drawing (previous boxes unless blind, touches, box, walls, front arrow), live checks, HUD.</summary>
        public void Tick()
        {
            var frameOk = m_mode.Room.HasFrame;
            var frame = m_mode.Room.Frame;
            var floorWorld = frameOk ? frame.Origin.y + m_floorY : m_mode.FloorWorldY();

            if (m_step == Step.FrontA || m_step == Step.FrontB || m_step == Step.Extremes)
            {
                if (IsPoint)
                {
                    DrawPointerHits(floorWorld);
                }
                else
                {
                    TouchAids.DrawTips(m_mode, true);
                }

                if (frameOk)
                {
                    HandleTouchInput(frame, floorWorld);
                }
            }
            else if (m_step == Step.AssistPick)
            {
                TickAssist();
            }
            else if (m_step == Step.Edit && frameOk)
            {
                HandleEditInput(frame);
            }

            if (!frameOk)
            {
                return;
            }

            DrawPrevious(frame);
            DrawTouches(frame);
            if (m_hasBox && m_step != Step.Summary)
            {
                var world = frame.BoxToWorld(m_box);
                m_mode.Draw.Box(world, new Color(0.3f, 0.9f, 1f, 1f), 0.002f, new Color(0.3f, 0.85f, 1f, 0.12f), true,
                    m_step == Step.Edit ? (m_dragFace >= 0 ? m_dragFace : m_hoverFace) : -1);
                m_mode.Draw.FrontArrow(world, new Color(1f, 0.9f, 0.2f, 1f));
                m_checks.Update(m_mode, world, floorWorld);
            }

            if (m_hud != null)
            {
                m_hud.text = HudText();
            }
        }

        /// <summary>M2: ray + reticle where each controller's ray hits depth or the floor.</summary>
        private void DrawPointerHits(float floorWorld)
        {
            foreach (var c in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch })
            {
                if (!m_mode.Rig.IsTracked(c) || m_mode.RayOnPanel(c))
                {
                    continue;
                }

                if (PointerHit(c, floorWorld, out var hit, out _))
                {
                    var ray = m_mode.Rig.PointerRay(c);
                    m_mode.Draw.Line(ray.origin, hit, new Color(1f, 1f, 1f, 0.5f), 0.0008f);
                    m_mode.Draw.Point(hit, new Color(1f, 0.85f, 0.2f, 1f), 0.02f);
                }
            }
        }

        /// <summary>M2: first depth surface along the controller ray, else the floor plane.</summary>
        private bool PointerHit(OVRInput.Controller c, float floorWorld, out Vector3 hit, out string source)
        {
            var ray = m_mode.Rig.PointerRay(c);
            var depth = m_mode.Context.Depth;
            if (depth != null && depth.IsAvailable && depth.Raycast(ray, 6f, out hit, out _))
            {
                source = "ray_depth";
                return true;
            }

            source = "ray_floor";
            if (ray.direction.y < -1e-3f)
            {
                var t = (floorWorld - ray.origin.y) / ray.direction.y;
                if (t > 0f && t < 8f)
                {
                    hit = ray.origin + ray.direction * t;
                    return true;
                }
            }

            hit = default;
            return false;
        }

        /// <summary>World trigger → touch (M1 tip / M2 ray hit) for the current step; front points set the yaw; extremes extend the box.</summary>
        private void HandleTouchInput(RoomFrame frame, float floorWorld)
        {
            if (!m_mode.WorldTriggerDown(out var c))
            {
                return;
            }

            Vector3 world;
            string source;
            if (IsPoint)
            {
                if (!PointerHit(c, floorWorld, out world, out source))
                {
                    m_message = "The ray hits nothing: point at the object or the floor.";
                    m_mode.Refresh();
                    return;
                }
            }
            else
            {
                world = m_mode.Rig.TipWorld(c);
                source = "tip";
            }

            m_lastController = AnnRig.Name(c);
            var p = frame.WorldToRoom(world);
            var extend = AnnRig.GripHeld(c);
            string label;
            if (m_step == Step.FrontA)
            {
                label = "front_a";
                Supersede(label);
                m_frontA = p;
                m_step = Step.FrontB;
            }
            else if (m_step == Step.FrontB)
            {
                label = "front_b";
                var head = m_mode.Context.Head != null ? frame.WorldToRoom(m_mode.Context.Head.position) : p + Vector3.forward;
                if (!TouchBox.TryFrontYaw(m_frontA.Value, p, head, out var yaw, out var error))
                {
                    m_message = error;
                    m_mode.Audit("touch_rejected", Touched(label, p, source, error));
                    m_mode.Refresh();
                    return;
                }

                Supersede(label);
                m_frontB = p;
                m_hint = head;
                m_yaw = yaw;
                m_hasYaw = true;
                m_step = Step.Extremes;
                m_side = NextUnsetSide(0);
            }
            else
            {
                label = SideNames[m_side];
                if (!extend)
                {
                    Supersede(label);
                }

                // Auto-advance to the next side that has no point yet.
                m_side = NextUnsetSide(m_side, label);
            }

            m_touches.Add(new TouchInput
            {
                Label = label,
                Point = Vec.ToArray(p),
                T = Time.realtimeSinceStartup - m_start,
                Source = source,
                Controller = m_lastController,
            });
            m_message = null;
            var audit = Touched(label, p, source, null);
            audit["extend"] = extend;
            m_mode.Audit("touch", audit);
            Recompute();
            m_mode.Refresh();
        }

        private JObject Touched(string label, Vector3 p, string source, string error) => new JObject
        {
            ["object_id"] = m_obj.Id,
            ["method"] = m_method,
            ["label"] = label,
            ["point_room"] = new JArray(p.x, p.y, p.z),
            ["source"] = source,
            ["controller"] = m_lastController,
            ["error"] = error,
        };

        /// <summary>Next extreme side without a point (auto-advance).</summary>
        private int NextUnsetSide(int from, string justSet = null)
        {
            for (var k = 1; k <= SideNames.Length; k++)
            {
                var i = (from + k) % SideNames.Length;
                if (SideNames[i] != justSet && CountSide(SideNames[i]) == 0)
                {
                    return i;
                }
            }

            return justSet != null ? from : 0;
        }

        /// <summary>Marks earlier touches of a side as replaced (kept for the audit trail).</summary>
        private void Supersede(string label)
        {
            foreach (var t in m_touches)
            {
                if (t.Label == label)
                {
                    t.Superseded = true;
                }
            }
        }

        /// <summary>Active touches of one side.</summary>
        private int CountSide(string label)
        {
            var n = 0;
            foreach (var t in m_touches)
            {
                if (t.Label == label && !t.Superseded)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>True when front, back, left, right and top all have a point.</summary>
        private bool AllSidesSet()
        {
            foreach (var s in SideNames)
            {
                if (CountSide(s) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Box from all active touches (TouchBox.FromPoints), front side resolved with the back point.</summary>
        private void Recompute()
        {
            if (!m_hasYaw)
            {
                m_hasBox = false;
                return;
            }

            var points = new List<Vector3>();
            Vector3? back = null;
            foreach (var t in m_touches)
            {
                if (t.Superseded)
                {
                    continue;
                }

                var p = Vec.FromArray(t.Point);
                points.Add(p);
                if (t.Label == "back")
                {
                    back = p;
                }
            }

            var yaw = back.HasValue ? TouchBox.ResolveFrontWithBack(m_yaw, m_frontA.Value, m_frontB.Value, back.Value) : m_yaw;
            m_methodBox = TouchBox.FromPoints(yaw, points, m_floorY);
            m_box = m_methodBox;
            m_hasBox = true;
        }

        // ------------------------------------------------------------------ M4 assisted

        /// <summary>Highest-confidence detector track (M4 shortcut).</summary>
        private TrackedObject BestTrack()
        {
            TrackedObject best = null;
            foreach (var t in m_mode.LatestTracks)
            {
                if (best == null || t.Confidence > best.Confidence)
                {
                    best = t;
                }
            }

            return best;
        }

        /// <summary>M4: draws the detector boxes, highlights the one the right ray points at, trigger picks it.</summary>
        private void TickAssist()
        {
            var fresh = Time.realtimeSinceStartup - m_mode.LatestTracksTime < 2f;
            var ray = m_mode.Rig.PointerRay(OVRInput.Controller.RTouch);
            TrackedObject pointed = null;
            var best = float.MaxValue;
            foreach (var t in m_mode.LatestTracks)
            {
                var color = new Color(1f, 0.3f, 0.9f, fresh ? 1f : 0.4f);
                m_mode.Draw.Box(t.Estimate, color, 0.003f, new Color(1f, 0.3f, 0.9f, 0.08f), true);
                m_mode.Draw.Label(t.Estimate.TopCenter + Vector3.up * 0.06f, $"{t.Category} {t.Confidence:P0}", color, 0.022f);
                if (BoxEdit.RayFace(t.Estimate, ray.origin, ray.direction, out _, out var d) && d < best)
                {
                    best = d;
                    pointed = t;
                }
            }

            if (pointed != null)
            {
                m_mode.Draw.Box(pointed.Estimate, Color.white, 0.005f, default, false);
            }

            if (pointed != null && AnnRig.TriggerDown(OVRInput.Controller.RTouch) && !m_mode.RayOnPanel(OVRInput.Controller.RTouch))
            {
                PickTrack(pointed);
            }

            if (m_mode.LatestTracks.Count != m_lastTrackCount)
            {
                m_lastTrackCount = m_mode.LatestTracks.Count;
                m_mode.Refresh(); // keep the box count fresh
            }
        }

        /// <summary>M4: takes a detector box (world → room frame) as the starting box and opens the editor.</summary>
        private void PickTrack(TrackedObject track)
        {
            if (track == null || !m_mode.Room.HasFrame)
            {
                return;
            }

            m_assist = new AssistSource
            {
                TrackId = track.Id,
                Category = track.Category.ToString(),
                Confidence = track.Confidence,
                State = track.State.ToString(),
                BoxWorld = BoxDto.From(track.Estimate),
                DetectorModel = m_mode.Context.DetectorModel,
            };
            m_methodBox = m_mode.Room.Frame.BoxToRoom(track.Estimate);
            m_box = m_methodBox;
            m_hasBox = true;
            m_mode.AssistedWantsDetector = false;
            m_mode.Audit("assist_pick", new JObject
            {
                ["object_id"] = m_obj.Id,
                ["track_id"] = track.Id,
                ["category"] = track.Category.ToString(),
                ["confidence"] = track.Confidence,
                ["box_room"] = JObject.FromObject(BoxDto.From(m_methodBox)),
            });
            EnterEdit();
        }

        // ------------------------------------------------------------------ editor

        /// <summary>Switches to the editor step and resets the live check points.</summary>
        private void EnterEdit()
        {
            m_step = Step.Edit;
            m_checks.Clear();
            m_mode.Audit("editor_enter", new JObject { ["object_id"] = m_obj.Id, ["box_room"] = JObject.FromObject(BoxDto.From(m_box)) });
            m_mode.Refresh();
        }

        /// <summary>What the right thumbstick does in the current edit mode.</summary>
        private string EditHelp()
        {
            switch (m_editMode)
            {
                case EditMode.Move: return "move (1 cm)";
                case EditMode.Rotate: return "← → rotate (1°)";
                case EditMode.Size: return "← → width, ↕ depth (1 cm)";
                default: return "↕ height (1 cm)";
            }
        }

        /// <summary>Editor input: grip on a face = push/pull, right stick = move/rotate/size (grip = fine), left stick = height.</summary>
        private void HandleEditInput(RoomFrame frame)
        {
            const OVRInput.Controller right = OVRInput.Controller.RTouch;
            var world = frame.BoxToWorld(m_box);
            var ray = m_mode.Rig.PointerRay(right);
            var onPanel = m_mode.RayOnPanel(right);
            m_hoverFace = !onPanel && BoxEdit.RayFace(world, ray.origin, ray.direction, out var face, out _) ? face : -1;

            // Face push / pull with the grip.
            if (m_dragFace < 0 && AnnRig.GripDown(right) && m_hoverFace >= 0 && AnnRig.Stick(right).magnitude < 0.3f)
            {
                m_dragFace = m_hoverFace;
                m_dragStartBox = m_box;
                m_dragStartPos = m_mode.Rig.Anchor(right).position;
                m_dragNormal = BoxEdit.FaceNormal(world, m_dragFace);
            }

            if (m_dragFace >= 0)
            {
                var delta = Vector3.Dot(m_mode.Rig.Anchor(right).position - m_dragStartPos, m_dragNormal);
                m_box = BoxEdit.PushFace(m_dragStartBox, m_dragFace, delta);
                if (AnnRig.GripUp(right) || !AnnRig.GripHeld(right))
                {
                    m_undo.Add(m_dragStartBox);
                    m_undoQuarter.Add(m_frontQuarter);
                    Record($"face_push:{BoxEdit.FaceNames[m_dragFace]}", new[] { delta });
                    m_dragFace = -1;
                }

                return;
            }

            var fine = AnnRig.GripHeld(right);
            var step = fine ? 0.002f : 0.01f;
            var angle = (fine ? 0.2f : 1f) * Mathf.Deg2Rad;
            var dir = Quantize(AnnRig.Stick(right));
            if (dir != Vector2Int.zero && (dir != m_heldDir || Time.realtimeSinceStartup >= m_nextRepeat))
            {
                m_nextRepeat = Time.realtimeSinceStartup + (dir != m_heldDir ? RepeatDelay : RepeatRate);
                switch (m_editMode)
                {
                    case EditMode.Move:
                    {
                        var head = m_mode.Context.Head;
                        var fwd = head != null ? Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized : Vector3.forward;
                        var rightDir = new Vector3(fwd.z, 0f, -fwd.x);
                        var deltaWorld = (rightDir * dir.x + fwd * dir.y) * step;
                        var deltaRoom = frame.DirectionToRoom(deltaWorld);
                        Apply(BoxEdit.Move(m_box, deltaRoom), "move", new[] { deltaRoom.x, deltaRoom.y, deltaRoom.z });
                        break;
                    }
                    case EditMode.Rotate:
                        if (dir.x != 0)
                        {
                            Apply(BoxEdit.Rotate(m_box, dir.x * angle), "rotate_deg", new[] { dir.x * angle * Mathf.Rad2Deg });
                        }

                        break;
                    case EditMode.Size:
                        Apply(BoxEdit.Resize(m_box, dir.x * step, 0f, dir.y * step), "resize", new[] { dir.x * step, 0f, dir.y * step });
                        break;
                    default:
                        if (dir.y != 0)
                        {
                            Apply(BoxEdit.Resize(m_box, 0f, dir.y * step, 0f), "resize", new[] { 0f, dir.y * step, 0f });
                        }

                        break;
                }
            }

            m_heldDir = dir;

            // Left stick up/down: height, in any mode.
            var hdir = Quantize(AnnRig.Stick(OVRInput.Controller.LTouch)).y;
            if (hdir != 0 && (hdir != m_heldHeight || Time.realtimeSinceStartup >= m_nextHeightRepeat))
            {
                m_nextHeightRepeat = Time.realtimeSinceStartup + (hdir != m_heldHeight ? RepeatDelay : RepeatRate);
                var hstep = AnnRig.GripHeld(OVRInput.Controller.LTouch) || fine ? 0.002f : 0.01f;
                Apply(BoxEdit.Resize(m_box, 0f, hdir * hstep, 0f), "resize", new[] { 0f, hdir * hstep, 0f });
            }

            m_heldHeight = hdir;
        }

        /// <summary>Thumbstick → one of the 4 directions (or none below 0.6).</summary>
        private static Vector2Int Quantize(Vector2 v)
        {
            if (v.magnitude < StickThreshold)
            {
                return Vector2Int.zero;
            }

            return Mathf.Abs(v.x) > Mathf.Abs(v.y) ? new Vector2Int(v.x > 0f ? 1 : -1, 0) : new Vector2Int(0, v.y > 0f ? 1 : -1);
        }

        /// <summary>Replaces the box (undo-able) and records the edit.</summary>
        private void Apply(UprightBox next, string op, float[] value)
        {
            m_undo.Add(m_box);
            m_undoQuarter.Add(m_frontQuarter);
            if (m_undo.Count > 200)
            {
                m_undo.RemoveAt(0);
                m_undoQuarter.RemoveAt(0);
            }

            m_box = next;
            Record(op, value);
        }

        /// <summary>Adds an edit to raw_inputs and to the audit trail.</summary>
        private void Record(string op, float[] value)
        {
            m_edits.Add(new EditOp { Op = op, Value = value, T = Time.realtimeSinceStartup - m_start });
            m_mode.Audit("edit", new JObject
            {
                ["object_id"] = m_obj.Id,
                ["op"] = op,
                ["value"] = value != null ? new JArray(value) : null,
                ["box_room"] = JObject.FromObject(BoxDto.From(m_box)),
            });
        }

        /// <summary>Restores the previous box (and front quarter).</summary>
        private void Undo()
        {
            if (m_undo.Count == 0)
            {
                return;
            }

            m_box = m_undo[m_undo.Count - 1];
            m_frontQuarter = m_undoQuarter[m_undoQuarter.Count - 1];
            m_undo.RemoveAt(m_undo.Count - 1);
            m_undoQuarter.RemoveAt(m_undoQuarter.Count - 1);
            Record("undo", null);
            m_mode.Refresh();
        }

        // ------------------------------------------------------------------ save

        /// <summary>Stores the confirmed repetition with all raw inputs, recomputes the accepted box, saves and audits.</summary>
        private void Save(int rating)
        {
            if (!m_mode.Room.HasFrame)
            {
                return;
            }

            var ann = m_mode.Annotations.GetOrAdd(m_obj.Id);
            var controller = AnnRig.FromName(m_lastController);
            var rep = new Repetition
            {
                Method = m_method,
                Assisted = m_method == "M4",
                Index = GroundTruthUpdate.NextIndex(ann, m_method),
                Blind = m_blind,
                Box = BoxDto.From(m_box, m_frontQuarter),
                RawInputs = new RawInputs
                {
                    Touches = m_touches,
                    Edits = m_edits,
                    BoxBeforeEdit = BoxDto.From(m_methodBox),
                    FloorY = m_floorY,
                    FloorSource = m_floorSource,
                    TipOffset = Vec.ToArray(m_mode.Rig.TipOffset(controller)),
                    TipIsDefault = m_mode.Rig.TipIsDefault(controller),
                    Controller = m_lastController,
                    AssistSource = m_assist,
                    FrontHint = m_hasYaw ? Vec.ToArray(m_hint) : null,
                },
                Operator = m_mode.Operator,
                Timestamp = AnnotationStore.Timestamp(),
                DurationS = Time.realtimeSinceStartup - m_start,
                SelfRating = rating,
                Accepted = true,
                AnchorUuid = m_mode.Room.AnchorUuid,
                SessionId = m_mode.Recorder.IsRecording ? m_mode.Recorder.SessionId : null,
            };
            ann.Repetitions.Add(rep);
            if (ann.Verify == "ok")
            {
                ann.Verify = null; // the accepted box changes: verify again
            }

            GroundTruthUpdate.Recompute(ann, m_obj.Symmetry, rep.Timestamp);
            m_mode.SaveAnnotations();
            m_savedIndex = rep.Index;
            var b = m_box;
            m_mode.Audit("annotation_confirmed", new JObject
            {
                ["object_id"] = m_obj.Id,
                ["method"] = m_method,
                ["repetition"] = rep.Index,
                ["blind"] = m_blind,
                ["self_rating"] = rating,
                ["duration_s"] = rep.DurationS,
                ["box_room"] = JObject.FromObject(rep.Box),
                ["edits"] = m_edits.Count,
                ["outside_points"] = m_checks.OutsideCount,
            });
            Log.Info($"ann_confirmed object={m_obj.Id} method={m_method} rep={rep.Index} rating={rating} box={b} " +
                     $"duration={rep.DurationS:F0}s edits={m_edits.Count}");
            m_step = Step.Summary;
            m_mode.Refresh();
        }

        /// <summary>Discards this repetition (audited) and returns to the setup screen.</summary>
        private void Cancel()
        {
            m_mode.Audit("annotation_discarded", new JObject
            {
                ["object_id"] = m_obj.Id,
                ["method"] = m_method,
                ["step"] = m_step.ToString(),
                ["touches"] = m_touches.Count,
                ["edits"] = m_edits.Count,
            });
            m_mode.Show(new AnnotateSetupScreen(m_mode, m_obj, m_method, m_blind));
        }

        // ------------------------------------------------------------------ drawing + HUD

        /// <summary>Non-blind: earlier repetitions of this object in grey.</summary>
        private void DrawPrevious(RoomFrame frame)
        {
            if (m_blind || m_step == Step.Summary)
            {
                return;
            }

            var ann = m_mode.Annotations?.Find(m_obj.Id);
            if (ann == null)
            {
                return;
            }

            foreach (var r in ann.Repetitions)
            {
                if (r.Accepted && r.Box != null)
                {
                    m_mode.Draw.Box(frame.BoxToWorld(r.Box.ToUpright()), new Color(0.7f, 0.7f, 0.7f, 0.7f), 0.001f, default, false);
                }
            }
        }

        /// <summary>Touched points (yellow = front reference, cyan = extremes) and the front edge.</summary>
        private void DrawTouches(RoomFrame frame)
        {
            foreach (var t in m_touches)
            {
                if (t.Superseded)
                {
                    continue;
                }

                var p = frame.RoomToWorld(Vec.FromArray(t.Point));
                var color = t.Label.StartsWith("front_") ? new Color(1f, 0.9f, 0.2f, 1f) : new Color(0.3f, 0.9f, 1f, 1f);
                m_mode.Draw.Point(p, color, 0.015f);
            }

            if (m_frontA.HasValue && m_frontB.HasValue)
            {
                m_mode.Draw.Line(frame.RoomToWorld(m_frontA.Value), frame.RoomToWorld(m_frontB.Value), new Color(1f, 0.9f, 0.2f, 1f), 0.002f);
            }
        }

        /// <summary>Live box size, Δ vs tape, depth points outside/inside, editor state.</summary>
        private string HudText()
        {
            if (!m_hasBox)
            {
                return m_hasYaw ? "Front set. Now the extremes." : "";
            }

            var b = m_box;
            var s = $"Box W×D×H {b.Size.x * 100f:F1} × {b.Size.z * 100f:F1} × {b.Size.y * 100f:F1} cm  {TapeDelta(b)}\n" +
                    $"Depth points outside: <b>{m_checks.OutsideCount}</b>" +
                    (m_checks.OutsideCount > 0 ? $" (max {m_checks.MaxOutsideCm:F1} cm)" : "") + $"   inside: {m_checks.InsideCount}";
            if (m_step == Step.Edit)
            {
                s += $"   mode: {m_editMode}" + (m_dragFace >= 0 ? $"   dragging {BoxEdit.FaceNames[m_dragFace]}" : m_hoverFace >= 0 ? $"   face {BoxEdit.FaceNames[m_hoverFace]}" : "");
            }

            return s;
        }

        /// <summary>Box size minus tape dims (W/D/H, cm).</summary>
        private string TapeDelta(UprightBox b)
        {
            var t = m_obj.TapeDimsCm;
            if (t == null || !t.IsSet)
            {
                return "(no tape dims)";
            }

            return $"| tape {t.W:0.#}×{t.D:0.#}×{t.H:0.#} | Δ {b.Size.x * 100f - t.W:+0.0;-0.0} / {b.Size.z * 100f - t.D:+0.0;-0.0} / {b.Size.y * 100f - t.H:+0.0;-0.0} cm";
        }

        /// <summary>Stops the detector request of M4.</summary>
        public void OnLeave()
        {
            m_mode.AssistedWantsDetector = false;
        }
    }
}
