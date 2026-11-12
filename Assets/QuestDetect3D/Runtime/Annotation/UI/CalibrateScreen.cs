// Controller-tip pivot calibration (no markers): keep the tip on one fixed point (a pen mark on a table), hold
// the trigger and rotate the controller around the tip for ~5 s. The tip offset is solved by least squares
// (PivotCalibration) and stored per controller in calibration.json with its residual (target < 3 mm).
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class CalibrateScreen : IAnnTickScreen, IAnnLeaveScreen
    {
        private const float MaxSeconds = 8f;
        private const int MaxPoses = 400;

        private readonly AnnotationMode m_mode;
        private readonly List<Quaternion> m_rot = new List<Quaternion>();
        private readonly List<Vector3> m_pos = new List<Vector3>();
        private OVRInput.Controller m_controller = OVRInput.Controller.RTouch;
        private bool m_armed;
        private bool m_collecting;
        private float m_startTime;
        private PivotResult? m_result;
        private UnityEngine.UI.Text m_live;

        public CalibrateScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Calibrate controller tip";

        public void Build(AnnPanel p)
        {
            var name = AnnRig.Name(m_controller);
            var current = m_mode.Rig.TipOffset(m_controller) * 1000f;
            p.BeginRow();
            p.Button("Left controller", () => Select(OVRInput.Controller.LTouch), !m_armed,
                m_controller == OVRInput.Controller.LTouch ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56);
            p.Button("Right controller", () => Select(OVRInput.Controller.RTouch), !m_armed,
                m_controller == OVRInput.Controller.RTouch ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56);
            p.EndRow();
            p.Label($"{name}: {(m_mode.Rig.TipIsDefault(m_controller) ? "DEFAULT offset" : "calibrated")} " +
                    $"({current.x:F1}, {current.y:F1}, {current.z:F1}) mm", 24, AnnPanel.DimText);
            p.Label("1. Mark a small dot on a table. 2. Press Start. 3. Put the tip (the yellow/green cross) on the dot, " +
                    "HOLD the trigger and slowly tilt the controller in all directions for ~5 s without the tip sliding. 4. Release.", 24);
            m_live = p.Label(LiveText(), 28, AnnPanel.Accent);
            if (m_result.HasValue)
            {
                var r = m_result.Value;
                var good = r.Ok && r.RmsMm < PivotCalibration.TargetRmsMm;
                p.Label(r.Ok
                        ? $"Residual {r.RmsMm:F2} mm RMS (max {r.MaxMm:F1}), {r.Used}/{r.Total} poses, rotation spread {r.SpreadDeg:F0}° → {(good ? "GOOD" : "above 3 mm: repeat")}"
                        : "Failed: " + r.Error,
                    26, good ? new Color(0.45f, 0.95f, 0.55f) : new Color(1f, 0.5f, 0.45f));
            }

            p.BeginRow();
            p.Button(m_armed ? "Waiting for trigger…" : "Start", Arm, !m_armed, AnnPanel.PrimaryButton);
            p.Button("Save", Save, m_result.HasValue && m_result.Value.Ok && !m_armed, AnnPanel.PrimaryButton);
            p.Button("Reset to default", ResetDefault, !m_armed);
            p.EndRow();
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)), !m_collecting);
        }

        private void Select(OVRInput.Controller c)
        {
            m_controller = c;
            m_result = null;
            m_mode.Refresh();
        }

        private void Arm()
        {
            m_armed = true;
            m_result = null;
            m_rot.Clear();
            m_pos.Clear();
            // The trigger now belongs to the calibration, not to the panel.
            m_mode.SetRayEnabled(m_controller, false);
            m_mode.Refresh();
        }

        public void Tick()
        {
            TouchAids.DrawTips(m_mode, false);
            if (m_live != null)
            {
                m_live.text = LiveText();
            }

            if (!m_armed)
            {
                return;
            }

            var anchor = m_mode.Rig.Anchor(m_controller);
            if (anchor == null)
            {
                return;
            }

            if (!m_collecting && AnnRig.TriggerDown(m_controller))
            {
                m_collecting = true;
                m_startTime = Time.realtimeSinceStartup;
            }

            if (m_collecting)
            {
                if (m_mode.Rig.IsTracked(m_controller) && m_rot.Count < MaxPoses)
                {
                    m_rot.Add(anchor.rotation);
                    m_pos.Add(anchor.position);
                }

                if (!AnnRig.TriggerHeld(m_controller) || Time.realtimeSinceStartup - m_startTime > MaxSeconds)
                {
                    Finish();
                }
            }
        }

        private void Finish()
        {
            m_collecting = false;
            m_armed = false;
            m_mode.SetRayEnabled(m_controller, true);
            // Subsample to ~100 poses spread over the whole collection.
            var step = Mathf.Max(1, m_rot.Count / 120);
            var rots = new List<Quaternion>();
            var pos = new List<Vector3>();
            for (var i = 0; i < m_rot.Count; i += step)
            {
                rots.Add(m_rot[i]);
                pos.Add(m_pos[i]);
            }

            m_result = PivotCalibration.Solve(rots, pos);
            Log.Info($"ann_tip_calibration controller={AnnRig.Name(m_controller)} raw_poses={m_rot.Count} {m_result.Value}");
            m_mode.Audit("tip_calibration_run", new JObject
            {
                ["controller"] = AnnRig.Name(m_controller),
                ["ok"] = m_result.Value.Ok,
                ["error"] = m_result.Value.Error,
                ["offset_mm"] = m_result.Value.Ok ? new JArray(m_result.Value.TipOffset.x * 1000f, m_result.Value.TipOffset.y * 1000f, m_result.Value.TipOffset.z * 1000f) : null,
                ["rms_mm"] = m_result.Value.RmsMm,
                ["max_mm"] = m_result.Value.MaxMm,
                ["spread_deg"] = m_result.Value.SpreadDeg,
                ["poses_used"] = m_result.Value.Used,
                ["poses_total"] = m_result.Value.Total,
            });
            m_mode.Refresh();
        }

        private void Save()
        {
            var r = m_result.Value;
            Store(new TipCalibration
            {
                Controller = AnnRig.Name(m_controller),
                Offset = Vec.ToArray(r.TipOffset),
                ResidualMm = r.RmsMm,
                NPoses = r.Used,
                IsDefault = false,
                Timestamp = AnnotationStore.Timestamp(),
            });
        }

        private void ResetDefault()
        {
            Store(new TipCalibration
            {
                Controller = AnnRig.Name(m_controller),
                Offset = Vec.ToArray(AnnRig.DefaultTipOffset),
                ResidualMm = -1f,
                NPoses = 0,
                IsDefault = true,
                Timestamp = AnnotationStore.Timestamp(),
            });
        }

        private void Store(TipCalibration tip)
        {
            var tips = m_mode.Calibration.Tips;
            tips.RemoveAll(t => t.Controller == tip.Controller);
            tips.Add(tip);
            m_mode.SaveCalibration();
            m_mode.Audit("tip_calibration_saved", new JObject
            {
                ["controller"] = tip.Controller,
                ["offset"] = new JArray(tip.Offset),
                ["residual_mm"] = tip.ResidualMm,
                ["is_default"] = tip.IsDefault,
            });
            Log.Info($"ann_tip_saved controller={tip.Controller} default={tip.IsDefault} residual_mm={tip.ResidualMm:F2}");
            m_result = null;
            m_mode.Refresh();
        }

        private string LiveText()
        {
            if (m_collecting)
            {
                return $"Collecting… {m_rot.Count} poses, {Time.realtimeSinceStartup - m_startTime:F1} s  (keep the tip on the dot)";
            }

            return m_armed ? "Tip on the dot, then HOLD the trigger and rotate." : "";
        }

        public void OnLeave()
        {
            m_mode.SetRayEnabled(OVRInput.Controller.LTouch, true);
            m_mode.SetRayEnabled(OVRInput.Controller.RTouch, true);
        }
    }
}
