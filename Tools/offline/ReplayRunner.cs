// replay_fitter: feeds RECORDED depth and recorded (device) or PC (run_models.py) 2D detections through the REAL
// DepthBoxFitter + ConfidenceModel + ObjectTracker of the app, and writes the fitted and tracked boxes per frame in
// the detections.jsonl schema (source "replay:<input>").
//
//   Tools/offline/run_tests.sh replay <session_dir> [--detections detections.jsonl] [--out replay_detections.jsonl]
//                                     [--floor room|none]
//
// Floor: "room" (default) = the room cross height (the cross is touched on the floor), "none" = let the fitter find
// the support plane. The device used its live FloorEstimator instead; both are logged in the output.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuestDetect3D;
using QuestDetect3D.Annotation;
using QuestDetect3D.Recording;
using UnityEngine;

public static class ReplayRunner
{
    /// <summary>replay &lt;session&gt; [--detections f] [--out f] [--floor room|none]: fits + tracks for every detections line.</summary>
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: replay <session_dir> [--detections file] [--out file] [--floor room|none]");
            return 2;
        }

        var session = args[0];
        var detName = Arg(args, "--detections", "detections.jsonl");
        var detPath = File.Exists(detName) ? detName : Path.Combine(session, detName);
        var outPath = Arg(args, "--out", Path.Combine(session, "replay_" + Path.GetFileNameWithoutExtension(detPath) + ".jsonl"));
        var mode = Arg(args, "--mode", "auto"); // auto: keypoints → Objectron path, boxes → YOLO+depth; hybrid: YOLO+depth fused with keypoints found inside the box
        var hybrid = mode == "hybrid" || (mode == "auto" && Path.GetFileName(detPath).Contains("hybrid"));
        var fuser = new HybridFuser
        {
            YawWeight = float.Parse(Arg(args, "--kp-yaw-weight", "0"), System.Globalization.CultureInfo.InvariantCulture),
            KeypointWeight = float.Parse(Arg(args, "--kp-weight", "0.35"), System.Globalization.CultureInfo.InvariantCulture),
            ShapeAgreeDeg = float.Parse(Arg(args, "--shape-agree", "25"), System.Globalization.CultureInfo.InvariantCulture),
        };
        int nHybridKp = 0, nHybridFront = 0;
        double sumV = 0;
        var floorMode = Arg(args, "--floor", "depth"); // depth: FloorEstimator on recorded depth (as on device); room: anchor origin y; none
        if (!File.Exists(detPath))
        {
            Console.WriteLine("detections not found: " + detPath);
            return 2;
        }

        var sessionJson = JObject.Parse(File.ReadAllText(Path.Combine(session, "session.json")));
        var frames = ReadJsonl(Path.Combine(session, "frames.jsonl"));
        var depthById = new Dictionary<int, JObject>();
        foreach (var d in ReadJsonl(Path.Combine(session, "depth.jsonl")))
        {
            depthById[(int)d["frame_id"]] = d;
        }

        var frameById = new Dictionary<int, JObject>();
        foreach (var f in frames)
        {
            frameById[(int)f["frame_id"]] = f;
        }

        var startFrame = sessionJson["room"]?["frame_at_start"] as JObject;
        var raycaster = new RecordedDepthRaycaster();
        var fitter = new DepthBoxFitter();
        var tracker = new ObjectTracker();
        // Objectron keypoint path (pc_objectron_*.jsonl): same BoxSolver + SupportPlaneEstimator as the headset.
        var envStub = new Meta.XR.EnvironmentRaycastManager { Source = raycaster };
        var floorEst = new FloorEstimator();
        var head = new Transform();
        var layoutVoter = new LayoutVoterReplay();
        int nKpFits = 0, nKpFail = 0;
        TrackedObject.ResetIds();
        var source = Path.GetFileNameWithoutExtension(detPath);
        var loadedDepth = -1;
        int nFrames = 0, nFits = 0, nFail = 0, noDepth = 0;
        var t0 = DateTime.UtcNow;
        using (var writer = new StreamWriter(outPath, false))
        {
            foreach (var line in ReadJsonl(detPath))
            {
                var t = (float)(line["t_realtime"] ?? 0f);
                var fid = (int)(line["frame_id"] ?? -1);
                // PC detections carry the recorded frame id; device detections are matched by time.
                JObject frameRec = null;
                if (line["source"]?.ToString().StartsWith("pc:") == true && frameById.TryGetValue(fid, out var fr))
                {
                    frameRec = fr;
                }
                else
                {
                    frameRec = Nearest(frames, t);
                }

                var depthId = frameRec?["depth_frame"]?.Type == JTokenType.Integer ? (int)frameRec["depth_frame"] : -1;
                if (depthId < 0 || !depthById.ContainsKey(depthId))
                {
                    raycaster.Clear();
                    noDepth++;
                }
                else if (depthId != loadedDepth)
                {
                    var drec = depthById[depthId];
                    var png = File.ReadAllBytes(Path.Combine(session, drec["file"].ToString()));
                    var mm = Png16.Decode(png, out var w, out var h);
                    var m = new double[16];
                    for (var i = 0; i < 16; i++)
                    {
                        m[i] = (double)drec["reprojection_matrix"][i];
                    }

                    raycaster.SetFrame(mm, w, h, m, (double)drec["zbuffer_params"][0], (double)drec["zbuffer_params"][1]);
                    loadedDepth = depthId;
                }

                var cam = Camera(line["camera"]);
                var roomJson = line["room_frame"] as JObject ?? frameRec?["room_frame"] as JObject ?? startFrame;
                RoomFrame? room = null;
                if (roomJson != null)
                {
                    room = new RoomFrame(V3(roomJson["origin"]), (float)roomJson["yaw_deg"] * Mathf.Deg2Rad);
                }

                var floorY = floorMode == "room" && room.HasValue ? room.Value.Origin.y : float.NaN;
                if (raycaster.IsAvailable)
                {
                    // Floor model fed the same way as on the headset (downward depth rays from the head pose).
                    head.position = cam.Position;
                    head.rotation = cam.Rotation;
                    for (var k = 0; k < 6; k++)
                    {
                        floorEst.Update(envStub, head);
                    }
                }

                if (floorMode == "depth" && floorEst.HasFloor)
                {
                    floorY = floorEst.FloorY;
                }
                var fits = new List<DetectorFit>();
                var observations = new List<BoxObservation>();
                foreach (JObject d in line["detections"])
                {
                    if (!Enum.TryParse<ObjectCategory>(d["category"]?.ToString(), out var category))
                    {
                        continue;
                    }

                    KeypointEvidence? kev = null;

                    if (d["keypoints"] is JArray kps && kps.Count == KeypointDetection.KeypointCount)
                    {
                        var kinfo = Categories.Get(category);
                        var kd = new KeypointDetection { Category = category, ModelScore = (float)(d["score"] ?? 1f) };
                        var lifted = new Vector3[KeypointDetection.KeypointCount];
                        var hasLifted = new bool[KeypointDetection.KeypointCount];
                        for (var i = 0; i < KeypointDetection.KeypointCount; i++)
                        {
                            kd.Viewport[i] = new Vector2((float)kps[i][0], (float)kps[i][1]);
                            kd.Has[i] = true;
                            kd.Weight[i] = 1f;
                            if (d["lifted"] is JArray la && la.Count == KeypointDetection.KeypointCount)
                            {
                                lifted[i] = V3(la[i]);
                                hasLifted[i] = true;
                            }
                        }

                        layoutVoter.Apply(kd, lifted, hasLifted, cam);
                        var support = SupportPlaneEstimator.Estimate(kd, cam, envStub, floorEst, kinfo);
                        var kfit = BoxSolver.Fit(kd, cam, support, kinfo);
                        var vpk = d["viewport"];
                        var det2 = new Detection2D
                        {
                            Label = d["label"]?.ToString(), Category = category, ClassId = -1, Score = kd.ModelScore,
                            Viewport = Rect.MinMaxRect((float)vpk[0], (float)vpk[1], (float)vpk[2], (float)vpk[3]),
                        };
                        if (hybrid)
                        {
                            if (kfit.Valid)
                            {
                                var kq0 = ConfidenceModel.ObservationQuality(kfit, support, kd, kinfo, Vector3.Distance(cam.Position, kfit.Box.Center));
                                kev = new KeypointEvidence { Box = kfit.Box, Quality = kq0.Total, Time = t };
                            }
                        }
                        else if (!kfit.Valid)
                        {
                            nKpFail++;
                            fits.Add(new DetectorFit { Detection = det2, Fit = new DepthFitResult { FailReason = kfit.FailReason } });
                            continue;
                        }

                        if (hybrid)
                        {
                            goto depthPath;
                        }

                        nKpFits++;
                        var kdist = Vector3.Distance(cam.Position, kfit.Box.Center);
                        var kq = ConfidenceModel.ObservationQuality(kfit, support, kd, kinfo, kdist);
                        fits.Add(new DetectorFit
                        {
                            Detection = det2,
                            Fit = new DepthFitResult { Valid = true, Box = kfit.Box, Distance = kdist, SigmaPos = 0f, YawSource = "objectron", UsedFloor = floorEst.HasFloor },
                        });
                        var kview = kfit.Box.Center - cam.Position;
                        kview.y = 0f;
                        observations.Add(new BoxObservation
                        {
                            Box = kfit.Box, Quality = kq.Total, Breakdown = kq, Time = t,
                            ViewDirection = kview.sqrMagnitude > 1e-6f ? kview.normalized : Vector3.forward,
                            RmsPx = kfit.RmsPx, SupportSigma = support.Sigma, FrameId = fid, Category = category,
                            YawPeriodDeg = 360f, Score = kd.ModelScore, SigmaPos = 0f,
                        });
                        continue;
                    }

                    depthPath:
                    var vp = d["viewport"];
                    var det = new Detection2D
                    {
                        Label = d["label"]?.ToString(),
                        Category = category,
                        ClassId = (int)(d["class_id"] ?? -1),
                        Score = (float)d["score"],
                        Viewport = Rect.MinMaxRect((float)vp[0], (float)vp[1], (float)vp[2], (float)vp[3]),
                    };
                    var info = Categories.Get(category);
                    var fit = raycaster.IsAvailable ? fitter.Fit(det.Viewport, cam, info, raycaster, floorY) : new DepthFitResult { FailReason = "no depth" };
                    HybridResult hr = default;
                    if (hybrid && fit.Valid)
                    {
                        hr = fuser.Fuse(fit, kev, cam, raycaster, floorY);
                        if (kev.HasValue) nHybridKp++;
                        if (hr.HasFront) nHybridFront++;
                        sumV += hr.Verification;
                        fit.Box = hr.Box;
                        fit.YawSource = hr.HasFront ? "hybrid_front" : (kev.HasValue ? "hybrid_kp_disagree" : fit.YawSource);
                    }

                    fits.Add(new DetectorFit { Detection = det, Fit = fit });
                    if (!fit.Valid)
                    {
                        nFail++;
                        continue;
                    }

                    nFits++;
                    var quality = ConfidenceModel.DepthObservationQuality(fit, info, det.Score);
                    var q = quality.Total;
                    if (hybrid)
                    {
                        q *= 0.4f + 0.6f * hr.Verification;
                    }

                    var view = fit.Box.Center - cam.Position;
                    view.y = 0f;
                    observations.Add(new BoxObservation
                    {
                        Box = fit.Box,
                        Quality = q,
                        Breakdown = quality,
                        Time = t,
                        ViewDirection = view.sqrMagnitude > 1e-6f ? view.normalized : Vector3.forward,
                        RmsPx = 0f,
                        HasFront = hr.HasFront,
                        FrontYaw = hr.FrontYaw,
                        Verification = hybrid ? hr.Verification : 1f,
                        SupportSigma = fit.UsedFloor ? 0.02f : 0.03f,
                        FrameId = fid,
                        Category = det.Category,
                        YawPeriodDeg = 90f,
                        Score = det.Score,
                        SigmaPos = fit.SigmaPos,
                    });
                }

                tracker.ProcessMulti(observations, track => ExpectedVisible(track, cam, raycaster), t);
                var json = RecordJson.DetectorFrame(fid, t, (long)(line["unix_ms"] ?? 0L), cam, "replay:" + source, fits, tracker.Tracks, room);
                json["replay"] = new JObject { ["depth_frame"] = depthId, ["floor_mode"] = floorMode, ["floor_y"] = float.IsNaN(floorY) ? null : (JToken)floorY };
                writer.WriteLine(json.ToString(Formatting.None));
                nFrames++;
            }
        }

        if (hybrid)
        {
            Console.WriteLine($"hybrid: {nHybridKp}/{nFits} depth fits had keypoints in the box, {nHybridFront} gave the front, mean verification {(nFits > 0 ? sumV / nFits : 0):F2}");
        }

        Console.WriteLine($"replay {source}: {nFrames} frames, {nFits + nKpFits} fits ({nKpFits} keypoint), {nFail + nKpFail} failed fits, {noDepth} frames without depth, " +
                          $"{tracker.Tracks.Count} tracks, {raycaster.Rays} rays, {(DateTime.UtcNow - t0).TotalSeconds:F1} s -> {outPath}");
        return 0;
    }

    /// <summary>Same rule as Detect3DApp.IsExpectedVisible (missing a visible track counts against it).</summary>
    private static bool ExpectedVisible(TrackedObject track, CameraModel cam, IDepthRaycaster depth)
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

        if (depth.IsAvailable && depth.Raycast(new Ray(cam.Position, center - cam.Position), 6f, out var hit, out _))
        {
            var footprint = Mathf.Max(track.Estimate.Size.x, track.Estimate.Size.z);
            if (Vector3.Distance(cam.Position, hit) < distance - 0.5f * footprint - 0.1f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>CameraModel from a detections.jsonl camera object (pose, focal, principal, sensor crop, resolution).</summary>
    private static CameraModel Camera(JToken c)
    {
        var q = c["rotation"];
        var crop = c["sensor_crop"];
        var res = c["resolution"];
        return new CameraModel(V3(c["position"]), new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]),
            new Vector2((float)c["focal"][0], (float)c["focal"][1]), new Vector2((float)c["principal"][0], (float)c["principal"][1]),
            new Rect((float)crop[0], (float)crop[1], (float)crop[2], (float)crop[3]), new Vector2Int((int)res[0], (int)res[1]));
    }

    private static Vector3 V3(JToken a) => new Vector3((float)a[0], (float)a[1], (float)a[2]);

    /// <summary>Recorded frame closest in time (device detections carry their own time, not a recorded frame id).</summary>
    private static JObject Nearest(List<JObject> frames, float t)
    {
        JObject best = null;
        var bestDt = float.MaxValue;
        foreach (var f in frames)
        {
            var dt = Math.Abs((float)f["t_realtime"] - t);
            if (dt < bestDt)
            {
                bestDt = dt;
                best = f;
            }
        }

        return best;
    }

    /// <summary>All parsable lines of a JSONL file.</summary>
    private static List<JObject> ReadJsonl(string path)
    {
        var list = new List<JObject>();
        if (!File.Exists(path))
        {
            return list;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                list.Add(JObject.Parse(line));
            }
            catch (JsonException)
            {
                // a line cut by a crash
            }
        }

        return list;
    }

    /// <summary>Value after a command line flag, or the fallback.</summary>
    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}

/// <summary>Port of ObjectronDetector.ApplyLayout + LayoutVoter for replay (which keypoint bit is up / front / side).</summary>
public sealed class LayoutVoterReplay
{
    private readonly float[,] m_votes = new float[3, 2];
    private float m_total;

    public void Apply(KeypointDetection det, Vector3[] lifted, bool[] hasLifted, in CameraModel camera)
    {
        var layout = BoxLayout.MediaPipeDefault;
        if (hasLifted[1] && hasLifted[5] && hasLifted[3] && hasLifted[2])
        {
            var bits = new[] { 4, 2, 1 };
            var best = -1;
            var bestDot = -1f;
            var dirs = new Vector3[3];
            for (var b = 0; b < 3; b++)
            {
                dirs[b] = camera.Rotation * (lifted[1 + bits[b]] - lifted[1]);
                var len = dirs[b].magnitude;
                if (len < 1e-5f) { dirs[b] = Vector3.zero; continue; }
                dirs[b] /= len;
                var dot = Mathf.Abs(dirs[b].y);
                if (dot > bestDot) { bestDot = dot; best = b; }
            }

            if (best >= 0)
            {
                det.ModelVerticality = bestDot;
                if (bestDot >= 0.8f)
                {
                    var bi = best;
                    m_votes[bi, dirs[best].y >= 0f ? 1 : 0] += bestDot;
                    m_total += bestDot;
                }
            }
        }

        if (m_total >= 3f)
        {
            var bestV = 0f;
            for (var b = 0; b < 3; b++)
            for (var sgn = 0; sgn < 2; sgn++)
            {
                if (m_votes[b, sgn] > bestV)
                {
                    bestV = m_votes[b, sgn];
                    layout.UpBit = 4 >> b;
                    layout.UpSign = sgn == 1 ? 1 : -1;
                }
            }
        }

        layout.FrontBit = layout.UpBit == 1 ? 4 : 1;
        layout.SideBit = 7 - layout.UpBit - layout.FrontBit;
        det.Layout = layout;
        if (hasLifted[1] && hasLifted[1 + layout.FrontBit] && hasLifted[1 + layout.SideBit])
        {
            var front = camera.Rotation * (lifted[1 + layout.FrontBit] - lifted[1]);
            var side = camera.Rotation * (lifted[1 + layout.SideBit] - lifted[1]);
            front.y = 0f;
            side.y = 0f;
            det.ModelFrontWorld = front.sqrMagnitude > 1e-8f ? front.normalized : Vector3.zero;
            det.ModelSideWorld = side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.zero;
        }
    }
}

