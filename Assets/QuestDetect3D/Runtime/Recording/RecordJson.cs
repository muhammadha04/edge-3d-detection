// JSON shapes shared by the device recorder, the PC model runner and the offline replay (detections.jsonl).
// Boxes: {"bottom_center": [x,y,z], "yaw_deg": d, "size": [w,h,d]} in the frame named by the key ("box" = world,
// "box_room" = room frame). Poses: {"position": [x,y,z], "rotation": [qx,qy,qz,qw]} (Unity world, left-handed).
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using QuestDetect3D.Annotation;
using UnityEngine;

namespace QuestDetect3D.Recording
{
    public struct DetectorFit
    {
        public Detection2D Detection;
        public DepthFitResult Fit;
    }

    public static class RecordJson
    {
        public static JArray V3(Vector3 v) => new JArray(R(v.x), R(v.y), R(v.z));
        public static JArray Q(Quaternion q) => new JArray(R6(q.x), R6(q.y), R6(q.z), R6(q.w));
        private static double R(float f) => System.Math.Round(f, 5);
        private static double R6(float f) => System.Math.Round(f, 7);

        public static JObject Pose(Vector3 p, Quaternion q) => new JObject { ["position"] = V3(p), ["rotation"] = Q(q) };

        public static JObject Box(UprightBox b) => new JObject
        {
            ["bottom_center"] = V3(b.BottomCenter),
            ["yaw_deg"] = System.Math.Round(b.Yaw * Mathf.Rad2Deg, 4),
            ["size"] = V3(b.Size),
        };

        public static JObject Frame(RoomFrame f, string anchorUuid) => new JObject
        {
            ["origin"] = V3(f.Origin),
            ["yaw_deg"] = System.Math.Round(f.Yaw * Mathf.Rad2Deg, 5),
            ["anchor_uuid"] = anchorUuid,
        };

        public static JObject Camera(in CameraModel cam) => new JObject
        {
            ["position"] = V3(cam.Position),
            ["rotation"] = Q(cam.Rotation),
            ["focal"] = new JArray(R(cam.Focal.x), R(cam.Focal.y)),
            ["principal"] = new JArray(R(cam.Principal.x), R(cam.Principal.y)),
            ["sensor_crop"] = new JArray(R(cam.SensorCrop.x), R(cam.SensorCrop.y), R(cam.SensorCrop.width), R(cam.SensorCrop.height)),
            ["resolution"] = new JArray(cam.Resolution.x, cam.Resolution.y),
        };

        /// <summary>One processed detector frame: raw 2D detections, depth fits and tracker states.</summary>
        public static JObject DetectorFrame(int frameId, float realtime, long unixMs, in CameraModel cam, string source,
            IReadOnlyList<DetectorFit> fits, IReadOnlyList<TrackedObject> tracks, RoomFrame? room)
        {
            var dets = new JArray();
            foreach (var df in fits)
            {
                var d = df.Detection;
                var o = new JObject
                {
                    ["label"] = d.Label,
                    ["category"] = d.Category.ToString(),
                    ["class_id"] = d.ClassId,
                    ["score"] = System.Math.Round(d.Score, 4),
                    ["viewport"] = new JArray(R(d.Viewport.xMin), R(d.Viewport.yMin), R(d.Viewport.xMax), R(d.Viewport.yMax)),
                };
                var fit = new JObject { ["valid"] = df.Fit.Valid };
                if (df.Fit.Valid)
                {
                    fit["box"] = Box(df.Fit.Box);
                    if (room.HasValue)
                    {
                        fit["box_room"] = Box(room.Value.BoxToRoom(df.Fit.Box));
                    }

                    fit["sigma_pos"] = R(df.Fit.SigmaPos);
                    fit["sigma_yaw_deg"] = R(df.Fit.SigmaYawDeg);
                    fit["sigma_size_rel"] = R(df.Fit.SigmaSizeRel);
                    fit["object_points"] = df.Fit.ObjectPoints;
                    fit["distance"] = R(df.Fit.Distance);
                    fit["used_floor"] = df.Fit.UsedFloor;
                    fit["yaw_source"] = df.Fit.YawSource;
                }
                else
                {
                    fit["fail_reason"] = df.Fit.FailReason;
                }

                o["fit"] = fit;
                dets.Add(o);
            }

            var trk = new JArray();
            if (tracks != null)
            {
                foreach (var t in tracks)
                {
                    var o = new JObject
                    {
                        ["id"] = t.Id,
                        ["category"] = t.Category.ToString(),
                        ["state"] = t.State.ToString(),
                        ["confidence"] = System.Math.Round(t.Confidence, 4),
                        ["box"] = Box(t.Estimate),
                        ["position_sigma"] = R(t.PositionSigma),
                        ["yaw_sigma_deg"] = R(t.YawSigmaDeg),
                        ["size_sigma_rel"] = R(t.SizeSigmaRel),
                        ["uncertainty_cm"] = R(t.UncertaintyCm),
                        ["hits"] = t.Hits,
                    };
                    if (room.HasValue)
                    {
                        o["box_room"] = Box(room.Value.BoxToRoom(t.Estimate));
                    }

                    trk.Add(o);
                }
            }

            var frame = new JObject
            {
                ["frame_id"] = frameId,
                ["source"] = source,
                ["t_realtime"] = System.Math.Round(realtime, 4),
                ["unix_ms"] = unixMs,
                ["camera"] = Camera(cam),
                ["detections"] = dets,
                ["tracks"] = trk,
            };
            if (room.HasValue)
            {
                frame["room_frame"] = new JObject { ["origin"] = V3(room.Value.Origin), ["yaw_deg"] = System.Math.Round(room.Value.Yaw * Mathf.Rad2Deg, 5) };
            }

            return frame;
        }
    }
}
