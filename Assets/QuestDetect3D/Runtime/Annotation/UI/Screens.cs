// Home, ground-truth rule and shared screen helpers of QD3D Annotate. Each screen only describes its content;
// AnnotationMode owns the data and rebuilds the screen after every change (Refresh()).
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class HomeScreen : IAnnTickScreen
    {
        private readonly AnnotationMode m_mode;
        private UnityEngine.UI.Text m_status;
        private RoomFrameState m_lastState;

        public HomeScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => $"QD3D Annotate  v{Application.version}";

        /// <summary>Room / frame status, operator, tip calibration state, all main buttons.</summary>
        public void Build(AnnPanel p)
        {
            var room = m_mode.CurrentRoom;
            m_lastState = m_mode.Room.State;
            p.Label(room != null ? $"Room: <b>{room.Name}</b>" : "Room: <b>none</b>  -  create or select one first",
                AnnPanel.TextSize, room != null ? AnnPanel.TextColor : AnnPanel.Warning);
            m_status = p.Label(RoomStatus(), 24, RoomColor());
            p.Label($"Operator: <b>{m_mode.Operator}</b>     Objects: <b>{m_mode.Catalog.Objects.Count}</b>     Tip: {TipSummary()}", 24);
            var rec = m_mode.Recorder;
            if (rec.IsRecording)
            {
                p.Label($"<color=#FF6B6B>● RECORDING</color> {rec.SessionId}", 24);
            }

            p.BeginRow();
            p.Button("Room", () => m_mode.Show(new RoomScreen(m_mode)), true, AnnPanel.PrimaryButton);
            p.Button("Catalog", () => m_mode.Show(new CatalogScreen(m_mode)), true, AnnPanel.PrimaryButton);
            p.Button("Calibrate tip", () => m_mode.Show(new CalibrateScreen(m_mode)), true, AnnPanel.PrimaryButton);
            p.EndRow();
            var canAnnotate = m_mode.Room.HasFrame && m_mode.Catalog.Objects.Count > 0;
            p.BeginRow();
            p.Button("Annotate", () => m_mode.Show(new AnnotateSetupScreen(m_mode)), canAnnotate, AnnPanel.PrimaryButton);
            p.Button("Verify", () => m_mode.Show(new VerifyScreen(m_mode)), m_mode.Room.HasFrame, AnnPanel.PrimaryButton);
            p.EndRow();
            p.BeginRow();
            p.Button("Record", () => m_mode.Show(new RecordScreen(m_mode)), m_mode.CurrentRoom != null, AnnPanel.PrimaryButton);
            p.Button("Export", () => m_mode.Show(new ExportScreen(m_mode)));
            p.EndRow();
            p.BeginRow();
            p.Button("Operator name", () => m_mode.Keyboard.Open("Operator name", m_mode.Calibration.Operator, false, name =>
            {
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                m_mode.Calibration.Operator = name;
                m_mode.SaveCalibration();
                m_mode.Audit("operator_set", new JObject { ["operator"] = name });
            }));
            p.Button("Ground-truth rule", () => m_mode.Show(new RuleScreen(m_mode)));
            p.Button("Exit", m_mode.RequestExit, true, AnnPanel.DangerButton);
            p.EndRow();
            if (!canAnnotate)
            {
                p.Label(m_mode.Room.HasFrame ? "Add an object in Catalog to annotate." : "Annotate needs a localised room frame (Room).",
                    22, AnnPanel.Warning);
            }

            p.Label("Trigger = click.  Left Menu = panel in front of you.  X = hide/show panel.", 22, AnnPanel.DimText);
        }

        /// <summary>Live frame status; rebuild when the anchor state changes.</summary>
        public void Tick()
        {
            if (m_status != null)
            {
                m_status.text = RoomStatus();
                m_status.color = RoomColor();
            }

            if (m_mode.Room.State != m_lastState)
            {
                m_mode.Refresh();
            }
        }

        /// <summary>Frame status line.</summary>
        private string RoomStatus() => "Frame: " + m_mode.Room.Status;

        private Color RoomColor() => m_mode.Room.State == RoomFrameState.Localized ? new Color(0.45f, 0.95f, 0.55f) :
            m_mode.Room.State == RoomFrameState.Failed ? new Color(1f, 0.4f, 0.4f) : AnnPanel.Warning;

        /// <summary>Calibrated / default per controller.</summary>
        private string TipSummary()
        {
            var l = m_mode.Rig.TipIsDefault(OVRInput.Controller.LTouch) ? "L default" : "L calibrated";
            var r = m_mode.Rig.TipIsDefault(OVRInput.Controller.RTouch) ? "R default" : "R calibrated";
            return l + ", " + r;
        }
    }

    public sealed class RuleScreen : IAnnScreen
    {
        private readonly AnnotationMode m_mode;

        public RuleScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Ground-truth rule";

        /// <summary>The ground-truth definition and box convention.</summary>
        public void Build(AnnPanel p)
        {
            p.Label(GroundTruth.Definition, 30);
            p.Space();
            p.Label("Stored with every annotation file and recording (definition v" + GroundTruth.DefinitionVersion + ").", 22, AnnPanel.DimText);
            p.Label(GroundTruth.BoxConvention, 22, AnnPanel.DimText);
            p.Space();
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
        }
    }

    public static class Pager
    {
        /// <summary>Prev / page / Next row (only when there is more than one page).</summary>
        public static void Build(AnnPanel p, int page, int pages, Action<int> step)
        {
            if (pages <= 1)
            {
                return;
            }

            p.BeginRow();
            p.Button("< Prev", () => step(-1), page > 0, null, 56);
            p.Button($"Page {page + 1} / {pages}", null, false, null, 56);
            p.Button("Next >", () => step(1), page < pages - 1, null, 56);
            p.EndRow();
        }
    }

    /// <summary>Shared drawing for touch steps: tip spheres and plumb lines from both controller tips to the floor.</summary>
    public static class TouchAids
    {
        /// <summary>Draws both controller tips (amber = default offset, green = calibrated) and optional plumb lines to the floor.</summary>
        public static void DrawTips(AnnotationMode mode, bool plumb)
        {
            var floorY = mode.FloorWorldY();
            foreach (var c in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch })
            {
                if (!mode.Rig.IsTracked(c))
                {
                    continue;
                }

                var tip = mode.Rig.TipWorld(c);
                var color = mode.Rig.TipIsDefault(c) ? new Color(1f, 0.75f, 0.2f, 1f) : new Color(0.35f, 1f, 0.5f, 1f);
                mode.Draw.Point(tip, color, 0.012f);
                if (plumb && tip.y > floorY + 0.01f)
                {
                    var foot = new Vector3(tip.x, floorY + 0.002f, tip.z);
                    mode.Draw.Line(tip, foot, new Color(1f, 1f, 1f, 0.7f), 0.0008f);
                    mode.Draw.FloorRing(foot, 0.015f, new Color(1f, 1f, 1f, 0.8f), 0.001f);
                }
            }
        }
    }
}
