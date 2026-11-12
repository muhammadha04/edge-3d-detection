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
        var floorMode = Arg(args, "--floor", "room");
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
                var fits = new List<DetectorFit>();
                var observations = new List<BoxObservation>();
                foreach (JObject d in line["detections"])
                {
                    if (!Enum.TryParse<ObjectCategory>(d["category"]?.ToString(), out var category))
                    {
                        continue;
                    }

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
                    fits.Add(new DetectorFit { Detection = det, Fit = fit });
                    if (!fit.Valid)
                    {
                        nFail++;
                        continue;
                    }

                    nFits++;
                    var quality = ConfidenceModel.DepthObservationQuality(fit, info, det.Score);
                    var view = fit.Box.Center - cam.Position;
                    view.y = 0f;
                    observations.Add(new BoxObservation
                    {
                        Box = fit.Box,
                        Quality = quality.Total,
                        Breakdown = quality,
                        Time = t,
                        ViewDirection = view.sqrMagnitude > 1e-6f ? view.normalized : Vector3.forward,
                        RmsPx = 0f,
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

        Console.WriteLine($"replay {source}: {nFrames} frames, {nFits} fits, {nFail} failed fits, {noDepth} frames without depth, " +
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

    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
