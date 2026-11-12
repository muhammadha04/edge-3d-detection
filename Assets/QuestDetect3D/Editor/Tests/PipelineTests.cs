#if UNITY_EDITOR || QD3D_OFFLINE
// End-to-end tests of the YOLO + depth 3D pipeline against a synthetic room (no headset needed).
// Run offline with mono (Tools/offline/run_tests.sh) or inside Unity (QuestDetect3D → Run Self-Test, and
// automatically before every batch build → Builds/selftest.txt).
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace QuestDetect3D.Tests
{
    public static class PipelineTests
    {
        public sealed class Report
        {
            public readonly StringBuilder Text = new StringBuilder();
            public int Passed;
            public int Failed;

            public void Line(string s) => Text.AppendLine(s);

            public void Check(string name, bool ok, string detail)
            {
                if (ok)
                {
                    Passed++;
                }
                else
                {
                    Failed++;
                }

                Text.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
            }

            public override string ToString() => Text + $"== {Passed} passed, {Failed} failed ==\n";
        }

        public static Report RunAll()
        {
            var r = new Report();
            Run(r, "decode", TestDecode);
            Run(r, "aligned-size", TestAlignedSize);
            Run(r, "single-view", rr => TestSingleView(rr, "single-view", true, false));
            Run(r, "single-view-nofloor", rr => TestSingleView(rr, "no-floor", false, false));
            Run(r, "single-view-wall", rr => TestSingleView(rr, "near-wall", true, true));
            Run(r, "walk-around", TestWalkAround);
            Run(r, "backrest", TestBackrest);
            return r;
        }

        private static void Run(Report r, string name, Action<Report> test)
        {
            try
            {
                test(r);
            }
            catch (Exception e)
            {
                r.Check(name, false, "exception " + e);
            }
        }

        // ------------------------------------------------------------------ YOLO decoding + NMS

        private static void TestDecode(Report r)
        {
            var labels = new List<string>();
            for (var i = 0; i < 80; i++)
            {
                labels.Add("c" + i);
            }

            labels[56] = "chair";
            labels[60] = "tvmonitor";
            labels[0] = "person";
            labels[59] = "bed"; // mapped category, but not in YoloDecode.Allowed → must be dropped
            // 4 anchors: two overlapping chairs (NMS → 1), a table overlapping the chair (different group → kept),
            // a person (ignored class).
            var boxes = new float[]
            {
                100, 200, 300, 500,
                110, 210, 305, 505,
                80, 300, 600, 520,
                400, 50, 500, 600,
            };
            var classes = new[] { 56, 56, 60, 59 };
            var scores = new[] { 0.9f, 0.8f, 0.7f, 0.95f };
            var dets = new List<Detection2D>();
            YoloDecode.Decode(boxes, classes, scores, 4, new Vector2Int(640, 640), labels, 0.3f, 0.55f, dets);
            r.Check("decode.count", dets.Count == 2, $"{dets.Count} detections ({string.Join("; ", dets)})");
            if (dets.Count >= 1)
            {
                var vp = dets[0].Viewport;
                // x1=100/640, y1=200/640 (top) → viewport yMax = 1 - 200/640.
                var ok = Mathf.Abs(vp.xMin - 100f / 640f) < 1e-4f && Mathf.Abs(vp.yMax - (1f - 200f / 640f)) < 1e-4f &&
                         Mathf.Abs(vp.yMin - (1f - 500f / 640f)) < 1e-4f;
                r.Check("decode.viewport", ok && dets[0].Category == ObjectCategory.Chair, dets[0].ToString());
            }
        }

        // ------------------------------------------------------------------ 90° symmetric fusion

        private static void TestAlignedSize(Report r)
        {
            var a = new UprightBox(Vector3.zero, 44f * Mathf.Deg2Rad, new Vector3(0.5f, 0.9f, 0.7f));
            var b = new UprightBox(Vector3.zero, -46f * Mathf.Deg2Rad, new Vector3(0.7f, 0.9f, 0.5f)); // same box, quarter turn
            var sa = TrackedObject.AlignedSize(a, 44f * Mathf.Deg2Rad, Mathf.PI / 2f);
            var sb = TrackedObject.AlignedSize(b, 44f * Mathf.Deg2Rad, Mathf.PI / 2f);
            r.Check("aligned-size.swap", (sa - sb).magnitude < 1e-4f, $"a={sa} b={sb}");
        }

        // ------------------------------------------------------------------ single-view fit accuracy

        private static void TestSingleView(Report r, string name, bool floorKnown, bool nearWall)
        {
            var room = new SyntheticRoom(3);
            // Near-wall case: chair backrest 5 cm in front of the room's back wall (z = -HalfSize), facing +Z.
            var origin = nearWall ? new Vector3(0f, 0f, -room.HalfSize + 0.35f) : Vector3.zero;
            var chair = room.AddChair("chair", origin, nearWall ? 0f : 25f);
            var fitter = new DepthBoxFitter(5);
            var info = Categories.Get(ObjectCategory.Chair);
            var centreErr = new List<float>();
            var heightErr = new List<float>();
            var yawErr = new List<float>();
            var fails = 0;
            var tries = 0;
            for (var az = 0; az < 360; az += 15)
            {
                foreach (var dist in new[] { 1.3f, 2.0f, 2.8f })
                {
                    if (nearWall && az > 60 && az < 300)
                    {
                        continue; // only views from the open side of the wall
                    }

                    var a = az * Mathf.Deg2Rad;
                    var eye = origin + new Vector3(Mathf.Sin(a) * dist, 1.55f + room.FloorY, Mathf.Cos(a) * dist);
                    tries++;
                    var cam = SyntheticRoom.QuestCamera(eye, Quaternion.LookRotation(chair.Truth.Center - eye, Vector3.up));
                    if (!room.ProjectRect(chair, cam, out var rect))
                    {
                        fails++;
                        continue;
                    }

                    var fit = fitter.Fit(room.Jitter(rect, 0.03f), cam, info, room, floorKnown ? room.FloorY : float.NaN);
                    if (!fit.Valid)
                    {
                        fails++;
                        continue;
                    }

                    centreErr.Add(HorizontalDistance(fit.Box.BottomCenter, chair.Truth.BottomCenter));
                    heightErr.Add(Mathf.Abs(fit.Box.Size.y - chair.Truth.Size.y));
                    yawErr.Add(YawErrorMod90(fit.Box.Yaw, chair.Truth.Yaw));
                }
            }

            var medC = Median(centreErr);
            var p90C = Percentile(centreErr, 0.9f);
            var medH = Median(heightErr);
            var medY = Median(yawErr);
            r.Line($"{name}: {tries - fails}/{tries} fits, centre err median {medC * 100f:F1} cm (p90 {p90C * 100f:F1}), " +
                   $"height err median {medH * 100f:F1} cm, yaw err median {medY:F1}°");
            r.Check($"{name}.success", fails <= tries / 10, $"{fails} failures of {tries}");
            r.Check($"{name}.centre", medC < 0.12f, $"median {medC * 100f:F1} cm (< 12 cm: one view sees only the front)");
            r.Check($"{name}.height", medH < 0.08f, $"median {medH * 100f:F1} cm");
            r.Check($"{name}.yaw", medY < 12f, $"median {medY:F1} deg");
        }

        // ------------------------------------------------------------------ multi-view tracking walk-around

        private static void TestWalkAround(Report r)
        {
            var room = new SyntheticRoom(11);
            room.AddChair("chair A", new Vector3(0.6f, 0f, 0.4f), 20f);
            room.AddChair("chair B", new Vector3(-0.7f, 0f, 0.9f), -60f, 0.52f, 0.55f, 1.02f, 0.47f);
            room.AddTable("table", new Vector3(0.2f, 0f, -1.2f), 5f);
            room.AddSofa("sofa", new Vector3(-1.9f, 0f, -0.9f), 90f);
            var fitter = new DepthBoxFitter(9);
            var tracker = new ObjectTracker();
            var observations = new List<BoxObservation>();
            var now = 0f;
            var frames = 0;
            var detections = 0;
            var fitFails = 0;
            var failReasons = new Dictionary<string, int>();

            // Walk a loop around the furniture at 10 Hz for 20 s, looking at a slowly changing target.
            for (var i = 0; i < 200; i++)
            {
                now += 0.1f;
                var a = i / 200f * 2f * Mathf.PI;
                var eye = new Vector3(2.4f * Mathf.Sin(a), 1.55f + room.FloorY, 2.2f * Mathf.Cos(a));
                var target = room.Objects[(i / 25) % room.Objects.Count].Truth.Center;
                var look = Quaternion.LookRotation(target - eye, Vector3.up) *
                           Quaternion.Euler((float)(room.NextDouble() - 0.5) * 6f, (float)(room.NextDouble() - 0.5) * 10f, 0f);
                var cam = SyntheticRoom.QuestCamera(eye, look);
                frames++;
                observations.Clear();
                foreach (var o in room.Objects)
                {
                    if (!room.ProjectRect(o, cam, out var rect) || !room.CentreVisible(o, eye) || room.NextDouble() < 0.15)
                    {
                        continue; // out of view, occluded, or a YOLO miss
                    }

                    detections++;
                    var category = o.Category;
                    var p = room.NextDouble();
                    if (category == ObjectCategory.Chair && p < 0.2)
                    {
                        category = ObjectCategory.Bench; // YOLO confusion seen on real photos
                    }
                    else if (category == ObjectCategory.Sofa && p < 0.15)
                    {
                        category = ObjectCategory.Chair;
                    }

                    var info = Categories.Get(category);
                    var fit = fitter.Fit(room.Jitter(rect, 0.04f), cam, info, room, room.FloorY);
                    if (!fit.Valid)
                    {
                        fitFails++;
                        var key = o.Name + ": " + fit.FailReason;
                        failReasons.TryGetValue(key, out var c);
                        failReasons[key] = c + 1;
                        continue;
                    }

                    var score = 0.45f + 0.5f * (float)room.NextDouble();
                    var q = ConfidenceModel.DepthObservationQuality(fit, info, score);
                    var view = fit.Box.Center - cam.Position;
                    view.y = 0f;
                    observations.Add(new BoxObservation
                    {
                        Box = fit.Box,
                        Quality = q.Total,
                        Breakdown = q,
                        Time = now,
                        ViewDirection = view.normalized,
                        SupportSigma = 0.01f,
                        FrameId = i,
                        Category = category,
                        YawPeriodDeg = 90f,
                        Score = score,
                        SigmaPos = fit.SigmaPos,
                    });
                }

                tracker.ProcessMulti(observations, t => IsVisible(room, cam, t), now);
            }

            r.Line($"walk-around: {frames} frames, {detections} detections, {fitFails} fit failures, {tracker.Tracks.Count} tracks");
            foreach (var kv in failReasons)
            {
                r.Line($"  fit failure x{kv.Value}: {kv.Key}");
            }
            var matched = 0;
            foreach (var o in room.Objects)
            {
                TrackedObject best = null;
                var bestD = float.MaxValue;
                foreach (var t in tracker.Tracks)
                {
                    var d = HorizontalDistance(t.Estimate.BottomCenter, o.Truth.BottomCenter);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = t;
                    }
                }

                if (best == null)
                {
                    r.Check($"walk.{o.Name}", false, "no track");
                    continue;
                }

                var sizeErr = SizeErrorMod90(best.Estimate, o.Truth);
                var yawErr = YawErrorMod90(best.Estimate.Yaw, o.Truth.Yaw);
                var ok = bestD < 0.15f && best.State == TrackState.Confirmed && best.Category == o.Category && sizeErr < 0.20f;
                if (ok)
                {
                    matched++;
                }

                r.Check($"walk.{o.Name}", ok,
                    $"track {best.Id} {best.Category} {best.State} C={best.Confidence:F2} centre err {bestD * 100f:F1} cm, " +
                    $"size err {sizeErr * 100f:F0}%, yaw err {yawErr:F0}°, ±{best.UncertaintyCm:F1} cm, hits {best.Hits} " +
                    $"[{best.ConfidenceDetail}]");
            }

            // Every confirmed track must sit on a real object, and no object may own two confirmed tracks.
            var extra = 0;
            var duplicates = 0;
            var owners = new Dictionary<SyntheticObject, int>();
            foreach (var t in tracker.Tracks)
            {
                if (t.State != TrackState.Confirmed)
                {
                    continue;
                }

                SyntheticObject nearest = null;
                var nd = float.MaxValue;
                foreach (var o in room.Objects)
                {
                    var d = HorizontalDistance(t.Estimate.BottomCenter, o.Truth.BottomCenter);
                    if (d < nd)
                    {
                        nd = d;
                        nearest = o;
                    }
                }

                if (nearest == null || nd > 0.4f)
                {
                    extra++;
                    r.Line($"  false track {t.Id} {t.Category} {t.Estimate} C={t.Confidence:F2}");
                    continue;
                }

                owners.TryGetValue(nearest, out var n);
                owners[nearest] = n + 1;
                if (n >= 1)
                {
                    duplicates++;
                    r.Line($"  duplicate track {t.Id} on {nearest.Name} C={t.Confidence:F2}");
                }
            }

            r.Check("walk.no-false-or-duplicate", extra == 0 && duplicates == 0,
                $"{CountConfirmed(tracker)} confirmed, {matched} fully correct, {extra} false, {duplicates} duplicates");
        }

        private static int CountConfirmed(ObjectTracker tracker)
        {
            var n = 0;
            foreach (var t in tracker.Tracks)
            {
                if (t.State == TrackState.Confirmed)
                {
                    n++;
                }
            }

            return n;
        }

        private static bool IsVisible(SyntheticRoom room, CameraModel cam, TrackedObject t)
        {
            var c = t.Estimate.Center;
            if (!cam.WorldToViewport(c, out var vp) || vp.x < 0.12f || vp.x > 0.88f || vp.y < 0.12f || vp.y > 0.88f)
            {
                return false;
            }

            var dist = Vector3.Distance(cam.Position, c);
            if (dist < 0.5f || dist > 3.5f)
            {
                return false;
            }

            var dir = (c - cam.Position) / dist;
            if (room.TraceExact(cam.Position, dir, out var hit, out _))
            {
                var footprint = Mathf.Max(t.Estimate.Size.x, t.Estimate.Size.z);
                if (hit < dist - 0.5f * footprint - 0.1f)
                {
                    return false;
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ backrest side

        private static void TestBackrest(Report r)
        {
            var correct = 0;
            var total = 0;
            var details = new StringBuilder();
            foreach (var chairYaw in new[] { 0f, 35f, 90f, 160f, -120f })
            {
                var room = new SyntheticRoom(21);
                var chair = room.AddChair("chair", new Vector3(0f, 0f, 0f), chairYaw);
                // Tracker box: same box as truth but expressed with a random quarter-turn offset (yaw mod 90°).
                var offsetQ = ((int)(chairYaw / 37f) % 4 + 4) % 4;
                var box = new UprightBox(chair.Truth.BottomCenter, chair.Truth.Yaw + offsetQ * Mathf.PI / 2f,
                    (offsetQ & 1) != 0 ? new Vector3(chair.Truth.Size.z, chair.Truth.Size.y, chair.Truth.Size.x) : chair.Truth.Size);
                var est = new BackrestEstimator();
                for (var step = 0; step < 60 && !est.Decided; step++)
                {
                    var a = (step * 12f + 30f) * Mathf.Deg2Rad;
                    var eye = new Vector3(1.8f * Mathf.Sin(a), 1.55f + room.FloorY, 1.8f * Mathf.Cos(a));
                    est.Probe(box, eye, room, 12);
                }

                // Truth: backrest is at chair local -Z; in the tracker box frame that is quarter (−offsetQ) mod 4.
                var expected = ((0 - offsetQ) % 4 + 4) % 4;
                total++;
                if (est.Decided && est.QuarterTurns == expected)
                {
                    correct++;
                }

                details.Append($"yaw {chairYaw:F0}°/q{offsetQ}: got {(est.Decided ? est.QuarterTurns.ToString() : "-")} want {expected}; ");
            }

            r.Check("backrest.side", correct == total, $"{correct}/{total} correct. {details}");
        }

        // ------------------------------------------------------------------ helpers

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float YawErrorMod90(float a, float b)
        {
            var d = Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, b * Mathf.Rad2Deg)) % 90f;
            return Mathf.Min(d, 90f - d);
        }

        private static float SizeErrorMod90(UprightBox est, UprightBox truth)
        {
            var s = TrackedObject.AlignedSize(est, truth.Yaw, Mathf.PI / 2f);
            var e = new Vector3(Mathf.Abs(s.x - truth.Size.x) / truth.Size.x, Mathf.Abs(s.y - truth.Size.y) / truth.Size.y,
                Mathf.Abs(s.z - truth.Size.z) / truth.Size.z);
            return Mathf.Max(e.x, Mathf.Max(e.y, e.z));
        }

        private static float Median(List<float> v) => Percentile(v, 0.5f);

        private static float Percentile(List<float> v, float q)
        {
            if (v.Count == 0)
            {
                return float.NaN;
            }

            var s = new List<float>(v);
            s.Sort();
            return s[Mathf.Clamp(Mathf.RoundToInt(q * (s.Count - 1)), 0, s.Count - 1)];
        }
    }
}
#endif
