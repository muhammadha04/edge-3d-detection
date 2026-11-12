#if UNITY_EDITOR || QD3D_OFFLINE
// Tests of the QD3D Annotate maths: room frame, box from touches, box editor, pivot calibration, repeatability,
// ground-truth update, 16-bit PNG, keypoint projection (shared fixture with the Python exporter).
using System;
using System.Collections.Generic;
using System.IO;
using QuestDetect3D.Annotation;
using QuestDetect3D.Recording;
using UnityEngine;
using Report = QuestDetect3D.Tests.PipelineTests.Report;

namespace QuestDetect3D.Tests
{
    public static class AnnotationLogicTests
    {
        /// <summary>Shared with Tools/annotation/tests (relative to the project root).</summary>
        public const string FixturePath = "Tools/annotation/tests/fixtures/projection_fixture.json";

        public static void RunAll(Report r)
        {
            Run(r, "room-frame", TestRoomFrame);
            Run(r, "touch-box", TestTouchBox);
            Run(r, "box-edit", TestBoxEdit);
            Run(r, "pivot", TestPivot);
            Run(r, "repeatability", TestRepeatability);
            Run(r, "gt-update", TestGroundTruthUpdate);
            Run(r, "png16", TestPng16);
            Run(r, "keypoints", TestKeypointFixture);
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

        private static float Err(Vector3 a, Vector3 b) => (a - b).magnitude;

        private static void TestRoomFrame(Report r)
        {
            var centre = new Vector3(1.2f, 0.01f, -0.7f);
            var arm = centre + new Vector3(0.3f, 0.002f, 0.3f); // 45° heading
            var ok = RoomFrame.TryFromCrossTouches(centre, arm, out var frame, out _);
            var yawOk = Mathf.Abs(frame.Yaw * Mathf.Rad2Deg - 45f) < 1e-3f;
            var p = new Vector3(2f, 0.5f, 3f);
            var roundTrip = Err(frame.RoomToWorld(frame.WorldToRoom(p)), p);
            var armRoom = frame.WorldToRoom(arm);
            var armOnZ = Mathf.Abs(armRoom.x) < 1e-4f && armRoom.z > 0.4f;
            var tooClose = !RoomFrame.TryFromCrossTouches(centre, centre + new Vector3(0.05f, 0f, 0.05f), out _, out var err);
            r.Check("room-frame-cross", ok && yawOk && roundTrip < 1e-5f && armOnZ && tooClose,
                $"yaw={frame.Yaw * Mathf.Rad2Deg:F3}° roundtrip={roundTrip:E1} arm_room=({armRoom.x:F4},{armRoom.z:F3}) close_error='{err}'");

            // Box world → room → world, and a tilted anchor pose keeps the heading.
            var box = new UprightBox(new Vector3(2f, 0f, 1f), 0.3f, new Vector3(0.6f, 0.9f, 0.5f));
            var back = frame.BoxToWorld(frame.BoxToRoom(box));
            var boxErr = Err(back.BottomCenter, box.BottomCenter) + Mathf.Abs(RoomFrame.WrapPi(back.Yaw - box.Yaw));
            var tilted = Quaternion.Euler(4f, 45f, -3f);
            var fromPose = RoomFrame.FromPose(centre, tilted);
            var tiltYawErr = Mathf.Abs(fromPose.Yaw * Mathf.Rad2Deg - 45f);
            r.Check("room-frame-box", boxErr < 1e-5f && tiltYawErr < 0.5f, $"box_err={boxErr:E1} tilted_anchor_yaw_err={tiltYawErr:F2}°");
        }

        /// <summary>Synthetic chair: points on its surface, touched as the operator would.</summary>
        private static void TestTouchBox(Report r)
        {
            var truth = new UprightBox(new Vector3(0.8f, 0f, 1.5f), 25f * Mathf.Deg2Rad, new Vector3(0.62f, 0.95f, 0.58f));
            Func<float, float, float, Vector3> Local = (x, y, z) => truth.BottomCenter + truth.Right * x + Vector3.up * y + truth.Forward * z;
            var frontA = Local(-0.2f, 0.45f, 0.29f);
            var frontB = Local(0.22f, 0.46f, 0.29f);
            var head = Local(0.1f, 1.6f, 1.2f); // operator in front
            var okYaw = TouchBox.TryFrontYaw(frontA, frontB, head, out var yaw, out _);
            var back = Local(0.05f, 0.9f, -0.29f);
            yaw = TouchBox.ResolveFrontWithBack(yaw, frontA, frontB, back);
            var pts = new List<Vector3>
            {
                frontA, frontB,
                Local(0.0f, 0.2f, 0.29f),      // front-most (caster)
                back,                           // back-most
                Local(-0.31f, 0.65f, 0.0f),    // left-most (armrest)
                Local(0.31f, 0.62f, 0.1f),     // right-most
                Local(0.0f, 0.95f, -0.25f),    // top-most
            };
            var box = TouchBox.FromPoints(yaw, pts, 0f);
            var yawErr = Mathf.Abs(RoomFrame.WrapPi(box.Yaw - truth.Yaw)) * Mathf.Rad2Deg;
            var centreErr = Err(box.BottomCenter, truth.BottomCenter);
            var sizeErr = Err(box.Size, truth.Size);
            var outside = TouchBox.MaxOutside(box, pts);
            r.Check("touch-box", okYaw && yawErr < 0.01f && centreErr < 1e-4f && sizeErr < 1e-4f && outside < 1e-5f,
                $"yaw_err={yawErr:F4}° centre_err={centreErr * 1000f:F3}mm size_err={sizeErr * 1000f:F3}mm outside={outside:E1}");

            // Hint behind the chair but back point touched: the back point wins.
            TouchBox.TryFrontYaw(frontA, frontB, Local(0f, 1.6f, -1.5f), out var wrong, out _);
            var fixedYaw = TouchBox.ResolveFrontWithBack(wrong, frontA, frontB, back);
            var flipErr = Mathf.Abs(RoomFrame.WrapPi(fixedYaw - truth.Yaw)) * Mathf.Rad2Deg;
            var closeFail = !TouchBox.TryFrontYaw(frontA, frontA + new Vector3(0.01f, 0f, 0.01f), head, out _, out _);
            r.Check("touch-box-front-side", flipErr < 0.01f && closeFail, $"flip_err={flipErr:F4}° close_points_rejected={closeFail}");
        }

        private static void TestBoxEdit(Report r)
        {
            var b = new UprightBox(new Vector3(1f, 0f, 2f), 30f * Mathf.Deg2Rad, new Vector3(0.6f, 0.9f, 0.5f));
            var c = new Vector3[8];
            b.GetCorners(c);

            // Push +x by 4 cm: the -x face stays.
            var pushed = BoxEdit.PushFace(b, 0, 0.04f);
            var leftBefore = BoxEdit.FaceCenter(b, 1);
            var leftAfter = BoxEdit.FaceCenter(pushed, 1);
            var rightMoved = Vector3.Dot(BoxEdit.FaceCenter(pushed, 0) - BoxEdit.FaceCenter(b, 0), b.Right);
            var bottomPushed = BoxEdit.PushFace(b, 5, 0.02f);
            var okPush = Err(leftBefore, leftAfter) < 1e-5f && Mathf.Abs(rightMoved - 0.04f) < 1e-5f &&
                         Mathf.Abs(bottomPushed.BottomCenter.y + 0.02f) < 1e-6f && Mathf.Abs(bottomPushed.TopCenter.y - b.TopCenter.y) < 1e-5f;

            // Turning the front arrow does not change the box.
            var turned = BoxEdit.TurnFront(b, 1);
            var ct = new Vector3[8];
            turned.GetCorners(ct);
            var maxCornerMatch = 0f;
            foreach (var p in c)
            {
                var best = float.MaxValue;
                foreach (var q in ct)
                {
                    best = Mathf.Min(best, Err(p, q));
                }

                maxCornerMatch = Mathf.Max(maxCornerMatch, best);
            }

            // Ray from the front hits the front face.
            var origin = b.Center + b.Forward * 2f;
            var hit = BoxEdit.RayFace(b, origin, -b.Forward, out var face, out var dist);
            var snapped = BoxEdit.SnapToFloor(BoxEdit.Move(b, new Vector3(0f, 0.05f, 0f)), 0f);
            r.Check("box-edit", okPush && maxCornerMatch < 1e-5f && hit && face == 2 && Mathf.Abs(dist - (2f - 0.25f)) < 1e-4f &&
                                Mathf.Abs(snapped.BottomCenter.y) < 1e-6f && Mathf.Abs(snapped.Size.y - 0.95f) < 1e-5f,
                $"push_ok={okPush} turn_corner_err={maxCornerMatch:E1} ray_face={face} dist={dist:F4} snap_h={snapped.Size.y:F3}");
        }

        private static void TestPivot(Report r)
        {
            var rng = new System.Random(3);
            var tip = new Vector3(0.004f, -0.031f, 0.062f);
            var pivot = new Vector3(0.9f, 0.75f, 1.8f);
            var rots = new List<Quaternion>();
            var pos = new List<Vector3>();
            for (var i = 0; i < 120; i++)
            {
                var q = Quaternion.Euler((float)(rng.NextDouble() * 70 - 35), (float)(rng.NextDouble() * 120 - 60),
                    (float)(rng.NextDouble() * 70 - 35));
                var noise = new Vector3((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)) * 0.001f;
                rots.Add(q);
                pos.Add(pivot - q * tip + noise);
            }

            // Two slipped samples (outliers).
            pos[10] += new Vector3(0.02f, 0f, 0f);
            pos[50] += new Vector3(0f, 0.03f, 0f);
            var res = PivotCalibration.Solve(rots, pos);
            var tipErr = Err(res.TipOffset, tip) * 1000f;
            var pivErr = Err(res.Pivot, pivot) * 1000f;
            r.Check("pivot", res.Ok && tipErr < 0.6f && pivErr < 0.6f && res.RmsMm < 0.6f && res.Used == 118,
                $"{res} tip_err={tipErr:F2}mm pivot_err={pivErr:F2}mm");

            var flat = new List<Quaternion>();
            for (var i = 0; i < 60; i++)
            {
                flat.Add(Quaternion.Euler(0f, i * 0.2f, 0f));
            }

            var bad = PivotCalibration.Solve(flat, pos.GetRange(0, 60));
            r.Check("pivot-needs-rotation", !bad.Ok, bad.ToString());
        }

        private static void TestRepeatability(Report r)
        {
            var boxes = new List<UprightBox>
            {
                new UprightBox(new Vector3(1.000f, 0f, 2.000f), 10f * Mathf.Deg2Rad, new Vector3(0.60f, 0.90f, 0.50f)),
                new UprightBox(new Vector3(1.010f, 0f, 2.000f), 11f * Mathf.Deg2Rad, new Vector3(0.61f, 0.90f, 0.50f)),
                new UprightBox(new Vector3(0.990f, 0f, 2.000f), 9f * Mathf.Deg2Rad, new Vector3(0.59f, 0.90f, 0.50f)),
            };
            var s = Repeatability.Compute(boxes, 2f * Mathf.PI);
            // centre deviations 0, 1, 1 cm → sqrt(2/2) = 1 cm; yaw 0, 1, 1 deg → 1 deg; width 1/0.6 % over 3 dims.
            var expectedSize = (0.01f / 0.60f * 100f) / 3f;
            r.Check("repeatability", Mathf.Abs(s.CentreCm - 1f) < 1e-3f && Mathf.Abs(s.YawDeg - 1f) < 1e-3f &&
                                     Mathf.Abs(s.SizePct - expectedSize) < 1e-3f && Mathf.Abs(s.Box.BottomCenter.x - 1f) < 1e-6f && s.N == 3,
                $"centre={s.CentreCm:F4}cm yaw={s.YawDeg:F4}° size={s.SizePct:F4}% (expect {expectedSize:F4})");

            // 4-fold symmetric table annotated with fronts 90° apart: same physical box, zero spread.
            var table = new List<UprightBox>
            {
                new UprightBox(new Vector3(0f, 0f, 0f), 0f, new Vector3(1.2f, 0.75f, 0.8f)),
                new UprightBox(new Vector3(0f, 0f, 0f), 0.5f * Mathf.PI, new Vector3(0.8f, 0.75f, 1.2f)),
                new UprightBox(new Vector3(0f, 0f, 0f), Mathf.PI, new Vector3(1.2f, 0.75f, 0.8f)),
            };
            var t4 = Repeatability.Compute(table, Repeatability.PeriodFor("4"));
            var tNone = Repeatability.Compute(table, Repeatability.PeriodFor("none"));
            r.Check("repeatability-symmetry", t4.YawDeg < 1e-3f && t4.SizePct < 1e-3f && tNone.YawDeg > 80f,
                $"4-fold yaw={t4.YawDeg:F4}° size={t4.SizePct:F4}%  none yaw={tNone.YawDeg:F1}°");
        }

        private static void TestGroundTruthUpdate(Report r)
        {
            var obj = new ObjectAnnotations { ObjectId = "chair_01" };
            AddRep(obj, "M4", 5f, true);
            GroundTruthUpdate.Recompute(obj, "none", "t");
            var assistedOnly = obj.Accepted == null && obj.AcceptedByMethod.ContainsKey("M4");
            AddRep(obj, "M2", 2f, true);
            AddRep(obj, "M1", 1f, true);
            AddRep(obj, "M1", 1.02f, true);
            AddRep(obj, "M1", 9f, false); // discarded repetition
            GroundTruthUpdate.Recompute(obj, "none", "t");
            var primary = obj.Accepted;
            r.Check("gt-update", assistedOnly && primary != null && primary.Method == "M1" && primary.N == 2 &&
                                 Mathf.Abs(primary.Box.BottomCenter[0] - 1.01f) < 1e-5f && obj.AcceptedByMethod["M2"].N == 1 &&
                                 GroundTruthUpdate.NextIndex(obj, "M1") == 4,
                $"assisted_only_no_gt={assistedOnly} primary={primary?.Method} n={primary?.N} x={primary?.Box.BottomCenter[0]}");
        }

        private static void AddRep(ObjectAnnotations obj, string method, float x, bool accepted)
        {
            obj.Repetitions.Add(new Repetition
            {
                Method = method,
                Index = GroundTruthUpdate.NextIndex(obj, method),
                Assisted = method == "M4",
                Box = BoxDto.From(new UprightBox(new Vector3(x, 0f, 0f), 0f, new Vector3(0.5f, 0.9f, 0.5f))),
                Accepted = accepted,
            });
        }

        private static void TestPng16(Report r)
        {
            const int w = 37, h = 23;
            var px = new ushort[w * h];
            for (var i = 0; i < px.Length; i++)
            {
                px[i] = (ushort)((i * 2654435761u) >> 16);
            }

            px[0] = 0;
            px[1] = 65535;
            var png = Png16.Encode(px, w, h);
            var back = Png16.Decode(png, out var w2, out var h2);
            var same = w2 == w && h2 == h;
            for (var i = 0; same && i < px.Length; i++)
            {
                same = px[i] == back[i];
            }

            r.Check("png16", same, $"{png.Length} bytes, {w2}x{h2}, identical={same}");
        }

        /// <summary>A fixed camera + box → 9 keypoints in JPEG pixels. The same JSON fixture is checked by the
        /// Python exporter's tests, so the C# app and the PC tools project identically.</summary>
        public static (CameraModel cam, UprightBox box) FixtureScene()
        {
            var cam = CameraModel.FromIntrinsics(new Vector3(0.1f, 1.5f, -0.2f), Quaternion.Euler(18f, 12f, 2f),
                new Vector2(1168.2f, 1168.2f), new Vector2(870.2f, 863.7f), new Vector2Int(1728, 1728), new Vector2Int(1280, 960));
            var box = new UprightBox(new Vector3(0.4f, 0f, 1.9f), 30f * Mathf.Deg2Rad, new Vector3(0.62f, 0.95f, 0.58f));
            return (cam, box);
        }

        public static string FixtureJson()
        {
            var (cam, box) = FixtureScene();
            var kps = KeypointProjection.Keypoints3D(box);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"camera\": {\"position\": [0.1, 1.5, -0.2], \"rotation_xyzw\": [")
                .Append(string.Join(", ", new[]
                {
                    cam.Rotation.x.ToString("R", inv), cam.Rotation.y.ToString("R", inv), cam.Rotation.z.ToString("R", inv),
                    cam.Rotation.w.ToString("R", inv),
                }))
                .Append("],\n    \"focal\": [1168.2, 1168.2], \"principal\": [870.2, 863.7], \"sensor_resolution\": [1728, 1728], \"resolution\": [1280, 960]},\n")
                .Append("  \"box\": {\"bottom_center\": [0.4, 0.0, 1.9], \"yaw_deg\": 30.0, \"size\": [0.62, 0.95, 0.58]},\n  \"keypoints_px\": [\n");
            for (var i = 0; i < kps.Length; i++)
            {
                KeypointProjection.ToPixel(cam, kps[i], out var px);
                sb.Append("    [").Append(px.x.ToString("F3", inv)).Append(", ").Append(px.y.ToString("F3", inv)).Append(i < kps.Length - 1 ? "],\n" : "]\n");
            }

            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>Numeric comparison (Unity's and the offline shim's float maths differ in the last digits): the
        /// camera rotation must match within 1e-5 and the 9 keypoints, projected with the FIXTURE's rotation, within
        /// 0.01 px.</summary>
        private static void TestKeypointFixture(Report r)
        {
            var path = FindFixture();
            if (path == null)
            {
                r.Check("keypoints-fixture", false, $"fixture not found ({FixturePath}); generate it with run_tests.sh --write-fixture");
                return;
            }

            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            var q = json["camera"]["rotation_xyzw"];
            var fixtureRot = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
            var (cam0, box) = FixtureScene();
            var rotErr = Mathf.Max(Mathf.Max(Mathf.Abs(fixtureRot.x - cam0.Rotation.x), Mathf.Abs(fixtureRot.y - cam0.Rotation.y)),
                Mathf.Max(Mathf.Abs(fixtureRot.z - cam0.Rotation.z), Mathf.Abs(fixtureRot.w - cam0.Rotation.w)));
            var cam = new CameraModel(cam0.Position, fixtureRot, cam0.Focal, cam0.Principal, cam0.SensorCrop, cam0.Resolution);
            var kps = KeypointProjection.Keypoints3D(box);
            var expected = json["keypoints_px"];
            var maxErr = 0f;
            for (var i = 0; i < kps.Length; i++)
            {
                KeypointProjection.ToPixel(cam, kps[i], out var px);
                maxErr = Mathf.Max(maxErr, Mathf.Max(Mathf.Abs(px.x - (float)expected[i][0]), Mathf.Abs(px.y - (float)expected[i][1])));
            }

            r.Check("keypoints-fixture", rotErr < 1e-5f && maxErr < 0.01f,
                $"rotation_err={rotErr:E1} keypoint_err={maxErr:F4}px (shared with Tools/annotation/tests)");
        }

        public static string FindFixture()
        {
            foreach (var root in new[] { ".", "..", "../..", Environment.GetEnvironmentVariable("QD3D_ROOT") ?? "." })
            {
                var p = Path.Combine(root, FixturePath);
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return null;
        }
    }
}
#endif
