// Records a session for offline replay of any model (folder persistentDataPath/QD3D/sessions/<id>/):
//   session.json          versions, device, room + anchor, operator, lighting, settings, ground-truth definition
//   frames/NNNNNN.jpg     left PCA RGB 1280×960, JPEG q90, at 2/5/10 Hz
//   frames.jsonl          per frame: id, PCA timestamp, camera pose (world + room), intrinsics, head pose, depth id
//   depth/NNNNNN.png      16-bit mm environment depth (left eye) captured with the frame + depth.jsonl (matrices)
//   poses.csv             head + controller poses every rendered frame (72 Hz)
//   detections.jsonl      YOLO detections, depth fits and tracker states (detector on)
//   events.jsonl          annotation actions, edits, confirms, drift checks during the session (audit trail)
// Encoding and disk IO run on RecorderWorker; the main thread only issues GPU readbacks and copies bytes.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuestDetect3D.Annotation;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace QuestDetect3D.Recording
{
    public sealed class RecordSettings
    {
        public static readonly int[] Rates = { 2, 5, 10 };
        public static readonly string[] Lightings = { "normal", "dim", "bright", "mixed" };
        public int RateHz = 5;
        public bool Depth = true;
        public bool Detector;
        public string Lighting = "normal";
    }

    public sealed class SessionRecorder
    {
        private const int MaxPendingJobs = 24;
        private const double WarnFreeGb = 2.0;

        private readonly AnnotationMode m_mode;
        private readonly ConcurrentBag<byte[]> m_pool = new ConcurrentBag<byte[]>();
        private readonly StringBuilder m_poseRows = new StringBuilder(1 << 16);
        private RecorderWorker m_worker;
        private PcaFrameSource m_frames;
        private DepthCapture m_depth;
        private RecordSettings m_settings;
        private float m_startRealtime;
        private DateTime m_startUtc;
        private float m_nextFrameTime;
        private float m_nextPoseFlush;
        private float m_nextDiskCheck;
        private int m_frameIndex;
        private int m_framesWritten;
        private int m_depthWritten;
        private int m_dropped;
        private int m_detectorFrames;
        private int m_poseRowsTotal;
        private bool m_encodeOnMain;
        private JObject m_sessionJson;

        public SessionRecorder(AnnotationMode mode) => m_mode = mode;

        public bool IsRecording { get; private set; }
        public bool IsFinishing { get; private set; }
        public string SessionId { get; private set; }
        public string Dir { get; private set; }
        public RecordSettings Settings => m_settings;
        public int FramesWritten => Volatile.Read(ref m_framesWritten);
        public int DepthWritten => Volatile.Read(ref m_depthWritten);
        public int Dropped => Volatile.Read(ref m_dropped);
        public int DetectorFrames => m_detectorFrames;
        public long Bytes => m_worker != null ? m_worker.BytesWritten : 0;
        public int WorkerErrors => m_worker != null ? m_worker.Errors : 0;
        public string WorkerLastError => m_worker?.LastError;
        public float Elapsed => IsRecording ? Time.realtimeSinceStartup - m_startRealtime : 0f;
        public double FreeGb { get; private set; } = -1;
        public bool LowDisk => FreeGb >= 0 && FreeGb < WarnFreeGb;
        public string LastSessionDir { get; private set; }
        public string LastSummary { get; private set; } = "";

        /// <summary>Why depth is not being recorded (empty when it is, or when depth is off).</summary>
        public string DepthError => m_depth != null ? m_depth.LastError : m_lastDepthError;
        private string m_lastDepthError = "";

        /// <summary>Creates sessions/&lt;id&gt;/, writes session.json and starts capturing at the chosen rate. Record 2-min walk-arounds at 0.7–3 m.</summary>
        public bool Start(RecordSettings settings, out string error)
        {
            error = null;
            if (IsRecording || IsFinishing)
            {
                error = "already recording";
                return false;
            }

            var ctx = m_mode.Context;
            if (ctx.Camera == null || !ctx.Camera.IsPlaying)
            {
                error = "passthrough camera not running (camera permission?)";
                return false;
            }

            m_settings = settings;
            m_startUtc = DateTime.UtcNow;
            var room = m_mode.CurrentRoom;
            SessionId = m_startUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + (room != null ? room.Id : "noroom");
            Dir = Path.Combine(m_mode.Store.SessionsDir, SessionId);
            Directory.CreateDirectory(Path.Combine(Dir, "frames"));
            if (settings.Depth)
            {
                Directory.CreateDirectory(Path.Combine(Dir, "depth"));
            }

            m_worker = new RecorderWorker(MaxPendingJobs);
            m_frames = new PcaFrameSource(ctx.Camera);
            m_depth = settings.Depth ? new DepthCapture() : null;
            m_lastDepthError = "";
            m_frameIndex = 0;
            m_framesWritten = m_depthWritten = m_dropped = m_detectorFrames = m_poseRowsTotal = 0;
            m_encodeOnMain = false;
            m_startRealtime = Time.realtimeSinceStartup;
            m_nextFrameTime = m_startRealtime;
            m_nextPoseFlush = m_startRealtime + 0.5f;
            m_nextDiskCheck = 0f;
            m_poseRows.Clear();
            m_poseRows.Append("t_realtime,unix_ms,unity_frame," +
                              "head_px,head_py,head_pz,head_qx,head_qy,head_qz,head_qw," +
                              "left_tracked,left_px,left_py,left_pz,left_qx,left_qy,left_qz,left_qw," +
                              "right_tracked,right_px,right_py,right_pz,right_qx,right_qy,right_qz,right_qw\n");
            m_sessionJson = BuildSessionJson();
            AnnotationStore.Save(Path.Combine(Dir, "session.json"), m_sessionJson);
            IsRecording = true;
            CheckDisk();
            Log.Info($"ann_rec_start id={SessionId} rate={settings.RateHz} depth={settings.Depth} detector={settings.Detector} " +
                     $"lighting={settings.Lighting} free_gb={FreeGb:F1}");
            if (m_depth != null)
            {
                Log.Info($"ann_rec_depth_source {m_depth.Describe()}");
            }
            return true;
        }

        /// <summary>Stops capturing; the worker finishes the queued frames in the background, then session.json gets counts and duration.</summary>
        public void Stop()
        {
            if (!IsRecording)
            {
                return;
            }

            IsRecording = false;
            IsFinishing = true;
            FlushPoses();
            var worker = m_worker;
            var dir = Dir;
            var json = m_sessionJson;
            var elapsed = Time.realtimeSinceStartup - m_startRealtime;
            var depth = m_depth;
            m_lastDepthError = depth != null ? depth.LastError : "";
            var depthFailures = depth != null ? depth.Failures : 0;
            m_depth = null;
            Log.Info($"ann_rec_stopping id={SessionId} pending={worker.Pending}");
            Task.Run(() =>
            {
                var finished = worker.Stop(60000);
                m_mode.RunOnMain(() =>
                {
                    depth?.Dispose();
                    json["end_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    json["duration_s"] = Math.Round(elapsed, 2);
                    json["counts"] = new JObject
                    {
                        ["frames_written"] = FramesWritten,
                        ["depth_written"] = DepthWritten,
                        ["frames_dropped"] = Dropped,
                        ["detector_frames"] = m_detectorFrames,
                        ["pose_rows"] = m_poseRowsTotal,
                        ["bytes"] = worker.BytesWritten,
                        ["worker_errors"] = worker.Errors,
                        ["worker_last_error"] = worker.LastError,
                        ["worker_finished"] = finished,
                        ["depth_failures"] = depthFailures,
                        ["depth_last_error"] = m_lastDepthError,
                    };
                    AnnotationStore.Save(Path.Combine(dir, "session.json"), json);
                    worker.Dispose();
                    LastSessionDir = dir;
                    LastSummary = $"{SessionId}: {FramesWritten} frames, {DepthWritten} depth, {Dropped} dropped, " +
                                  $"{worker.BytesWritten / 1e6:F0} MB, {elapsed:F0} s";
                    Log.Info($"ann_rec_stop {LastSummary} errors={worker.Errors} last_error={worker.LastError}");
                    IsFinishing = false;
                    m_mode.Refresh();
                });
            });
        }

        /// <summary>Called every rendered frame by AnnotationMode.</summary>
        public void Tick()
        {
            if (!IsRecording)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            AppendPoseRow(now);
            if (now >= m_nextPoseFlush)
            {
                m_nextPoseFlush = now + 0.5f;
                FlushPoses();
            }

            if (now >= m_nextDiskCheck)
            {
                m_nextDiskCheck = now + 5f;
                CheckDisk();
            }

            var ctx = m_mode.Context;
            if (now >= m_nextFrameTime && ctx.Camera != null && ctx.Camera.IsUpdatedThisFrame)
            {
                if (m_frames.IsPending || (m_depth != null && m_depth.IsPending) || m_worker.Pending > MaxPendingJobs - 4)
                {
                    // The previous frame is still in flight: this slot is lost.
                    Interlocked.Increment(ref m_dropped);
                    m_nextFrameTime = now + 1f / m_settings.RateHz;
                    return;
                }

                m_nextFrameTime += 1f / m_settings.RateHz;
                if (m_nextFrameTime < now)
                {
                    m_nextFrameTime = now + 1f / m_settings.RateHz;
                }

                CaptureFrame();
            }
        }

        /// <summary>Requests one PCA frame (+ depth) and queues JPEG encoding and the frames.jsonl line.</summary>
        private void CaptureFrame()
        {
            var ctx = m_mode.Context;
            var id = ++m_frameIndex;
            var pcaUnixMs = new DateTimeOffset(DateTime.SpecifyKind(ctx.Camera.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var head = m_mode.Rig.Head;
            var headPos = head != null ? head.position : Vector3.zero;
            var headRot = head != null ? head.rotation : Quaternion.identity;
            var hasRoom = m_mode.Room.HasFrame;
            var room = m_mode.Room.Frame;
            var intrinsics = ctx.Camera.Intrinsics;

            var depthId = -1;
            if (m_depth != null)
            {
                if (m_depth.TryCapture(OnDepth(id)))
                {
                    depthId = id;
                }
            }

            var started = m_frames.TryCapture((rgba, fc) =>
            {
                var cam = fc.Camera;
                var record = new JObject
                {
                    ["frame_id"] = id,
                    ["file"] = $"frames/{id:000000}.jpg",
                    ["t_realtime"] = Math.Round(fc.CaptureRealtime, 4),
                    ["pca_timestamp_unix_ms"] = pcaUnixMs,
                    ["camera"] = RecordJson.Pose(cam.Position, cam.Rotation),
                    ["intrinsics"] = new JObject
                    {
                        ["focal"] = new JArray(intrinsics.FocalLength.x, intrinsics.FocalLength.y),
                        ["principal"] = new JArray(intrinsics.PrincipalPoint.x, intrinsics.PrincipalPoint.y),
                        ["sensor_resolution"] = new JArray(intrinsics.SensorResolution.x, intrinsics.SensorResolution.y),
                        ["sensor_crop"] = new JArray(cam.SensorCrop.x, cam.SensorCrop.y, cam.SensorCrop.width, cam.SensorCrop.height),
                        ["resolution"] = new JArray(cam.Resolution.x, cam.Resolution.y),
                        ["lens_offset"] = RecordJson.Pose(intrinsics.LensOffset.position, intrinsics.LensOffset.rotation),
                    },
                    ["exposure"] = null, // not exposed by the Passthrough Camera API
                    ["head"] = RecordJson.Pose(headPos, headRot),
                    ["depth_frame"] = depthId >= 0 ? (JToken)depthId : null,
                };
                if (hasRoom)
                {
                    record["room_frame"] = RecordJson.Frame(room, m_mode.Room.AnchorUuid);
                    record["camera_room"] = RecordJson.Pose(room.WorldToRoom(cam.Position), room.RotationToRoom(cam.Rotation));
                    record["head_room"] = RecordJson.Pose(room.WorldToRoom(headPos), room.RotationToRoom(headRot));
                }

                var w = cam.Resolution.x;
                var h = cam.Resolution.y;
                var bytes = Rent(rgba.Length);
                NativeArray<byte>.Copy(rgba, bytes, rgba.Length);
                var line = record.ToString(Formatting.None);
                var jpgPath = Path.Combine(Dir, "frames", $"{id:000000}.jpg");
                var framesPath = Path.Combine(Dir, "frames.jsonl");
                if (m_encodeOnMain)
                {
                    var jpg = ImageConversion.EncodeArrayToJPG(bytes, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, 90);
                    Return(bytes);
                    var worker = m_worker;
                    if (!worker.TryEnqueue(() =>
                        {
                            worker.WriteFile(jpgPath, jpg);
                            worker.AppendLineInJob(framesPath, line);
                            Interlocked.Increment(ref m_framesWritten);
                        }))
                    {
                        Interlocked.Increment(ref m_dropped);
                    }

                    return;
                }

                var wk = m_worker;
                if (!wk.TryEnqueue(() =>
                    {
                        try
                        {
                            var jpg = ImageConversion.EncodeArrayToJPG(bytes, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, 90);
                            wk.WriteFile(jpgPath, jpg);
                            wk.AppendLineInJob(framesPath, line);
                            Interlocked.Increment(ref m_framesWritten);
                        }
                        catch (Exception e) when (e.Message.Contains("main thread"))
                        {
                            m_encodeOnMain = true; // this Unity version encodes only on the main thread
                            Interlocked.Increment(ref m_dropped);
                        }
                        finally
                        {
                            Return(bytes);
                        }
                    }))
                {
                    Return(bytes);
                    Interlocked.Increment(ref m_dropped);
                }
            });
            if (!started)
            {
                Interlocked.Increment(ref m_dropped);
            }
        }

        /// <summary>Callback for a depth readback: queues PNG encoding and the depth.jsonl line.</summary>
        private Action<DepthFrame> OnDepth(int id)
        {
            return frame =>
            {
                if (!IsRecording && !IsFinishing)
                {
                    return;
                }

                var wk = m_worker;
                var dir = Dir;
                var record = new JObject
                {
                    ["frame_id"] = id,
                    ["file"] = $"depth/{id:000000}.png",
                    ["t_realtime"] = Math.Round(frame.CaptureRealtime, 4),
                    ["unix_ms"] = frame.CaptureUnixMs,
                    ["width"] = frame.Size,
                    ["height"] = frame.Size,
                    ["eye"] = "left",
                    ["encoding"] = "uint16 millimetres of linear depth (view-space z of the depth camera); 0 = no data",
                    ["reprojection_matrix"] = new JArray(frame.Reprojection),
                    ["matrix_layout"] = "row-major 4x4, world (Unity, left-handed) -> depth clip space",
                    ["zbuffer_params"] = new JArray(frame.ZBufferParams.x, frame.ZBufferParams.y, frame.ZBufferParams.z, frame.ZBufferParams.w),
                    ["pixel_to_ndc"] = "ndc_x = 2*(col+0.5)/W - 1; ndc_y = 2*(row+0.5)/H - 1; ndc_z = zb[0]/depth_m - zb[1]; world = inv(M)*[ndc,1] / w",
                };
                if (m_mode.Room.HasFrame)
                {
                    record["room_frame"] = RecordJson.Frame(m_mode.Room.Frame, m_mode.Room.AnchorUuid);
                }

                var line = record.ToString(Formatting.None);
                if (!wk.TryEnqueue(() =>
                    {
                        var png = Png16.Encode(DepthCapture.ToMillimetres(frame.Metres), frame.Size, frame.Size);
                        wk.WriteFile(Path.Combine(dir, "depth", $"{id:000000}.png"), png);
                        wk.AppendLineInJob(Path.Combine(dir, "depth.jsonl"), line);
                        Interlocked.Increment(ref m_depthWritten);
                    }))
                {
                    Log.Warn("ann_rec_depth_dropped");
                }
            };
        }

        // ------------------------------------------------------------------ detector, events

        /// <summary>Appends one detector frame (2D detections, fits, tracks) to detections.jsonl.</summary>
        public void OnDetectorFrame(YoloFrame frame, IReadOnlyList<DetectorFit> fits, IReadOnlyList<TrackedObject> tracks)
        {
            if (!IsRecording || m_settings == null || !m_settings.Detector)
            {
                return;
            }

            m_detectorFrames++;
            RoomFrame? room = m_mode.Room.HasFrame ? m_mode.Room.Frame : (RoomFrame?)null;
            var json = RecordJson.DetectorFrame(frame.FrameId, frame.CaptureRealtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                frame.Camera, "device", fits, tracks, room);
            json["inference_ms"] = Math.Round(frame.InferenceMs, 1);
            m_worker.AppendLine(Path.Combine(Dir, "detections.jsonl"), json.ToString(Formatting.None));
        }

        /// <summary>Appends an audit line to the session's events.jsonl.</summary>
        public void LogEvent(JObject line)
        {
            if (IsRecording && m_worker != null)
            {
                m_worker.AppendLine(Path.Combine(Dir, "events.jsonl"), line.ToString(Formatting.None));
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Adds head + both controllers for this rendered frame to poses.csv (buffered).</summary>
        private void AppendPoseRow(float now)
        {
            var head = m_mode.Rig.Head;
            var inv = CultureInfo.InvariantCulture;
            m_poseRows.Append(now.ToString("F4", inv)).Append(',')
                .Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append(',')
                .Append(Time.frameCount).Append(',');
            AppendPose(head != null ? head.position : Vector3.zero, head != null ? head.rotation : Quaternion.identity);
            foreach (var c in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch })
            {
                var a = m_mode.Rig.Anchor(c);
                m_poseRows.Append(',').Append(m_mode.Rig.IsTracked(c) ? 1 : 0).Append(',');
                AppendPose(a != null ? a.position : Vector3.zero, a != null ? a.rotation : Quaternion.identity);
            }

            m_poseRows.Append('\n');
            m_poseRowsTotal++;
        }

        /// <summary>Position and quaternion as CSV.</summary>
        private void AppendPose(Vector3 p, Quaternion q)
        {
            var inv = CultureInfo.InvariantCulture;
            m_poseRows.Append(p.x.ToString("F5", inv)).Append(',').Append(p.y.ToString("F5", inv)).Append(',')
                .Append(p.z.ToString("F5", inv)).Append(',').Append(q.x.ToString("F6", inv)).Append(',')
                .Append(q.y.ToString("F6", inv)).Append(',').Append(q.z.ToString("F6", inv)).Append(',')
                .Append(q.w.ToString("F6", inv));
        }

        /// <summary>Hands the buffered pose rows to the worker (every 0.5 s).</summary>
        private void FlushPoses()
        {
            if (m_poseRows.Length == 0 || m_worker == null)
            {
                return;
            }

            var text = m_poseRows.ToString(0, m_poseRows.Length - 1); // AppendLine adds the final newline
            m_poseRows.Clear();
            m_worker.AppendLine(Path.Combine(Dir, "poses.csv"), text);
        }

        /// <summary>Reuses a frame-sized byte buffer (avoids 5 MB allocations per frame).</summary>
        private byte[] Rent(int length)
        {
            while (m_pool.TryTake(out var b))
            {
                if (b.Length == length)
                {
                    return b;
                }
            }

            return new byte[length];
        }

        /// <summary>Gives a buffer back to the pool.</summary>
        private void Return(byte[] b)
        {
            if (m_pool.Count < 8)
            {
                m_pool.Add(b);
            }
        }

        /// <summary>Updates free space (warning below 2 GB).</summary>
        private void CheckDisk()
        {
            FreeGb = DiskSpace.FreeGb(m_mode.Store.Root);
            if (LowDisk)
            {
                Log.Warn($"ann_rec_low_disk free_gb={FreeGb:F2}");
            }
        }

        /// <summary>Everything needed to interpret the session later: versions, device, room, settings, definitions, conventions.</summary>
        private JObject BuildSessionJson()
        {
            var ctx = m_mode.Context;
            var room = m_mode.CurrentRoom;
            var json = new JObject
            {
                ["schema_version"] = Schema.Version,
                ["session_id"] = SessionId,
                ["start_utc"] = m_startUtc.ToString("o", CultureInfo.InvariantCulture),
                ["app"] = new JObject
                {
                    ["name"] = Application.productName,
                    ["version"] = Application.version,
                    ["unity"] = Application.unityVersion,
                    ["build"] = BuildInfo.Json,
                },
                ["device"] = new JObject
                {
                    ["model"] = SystemInfo.deviceModel,
                    ["os"] = SystemInfo.operatingSystem,
                    ["os_build"] = DiskSpace.AndroidBuildDisplay(),
                    ["gpu"] = SystemInfo.graphicsDeviceName,
                    ["graphics_api"] = SystemInfo.graphicsDeviceType.ToString(),
                },
                ["room"] = room == null
                    ? null
                    : new JObject
                    {
                        ["id"] = room.Id,
                        ["name"] = room.Name,
                        ["anchor_uuid"] = room.AnchorUuid,
                        ["frame_state"] = m_mode.Room.State.ToString(),
                        ["frame_at_start"] = m_mode.Room.HasFrame ? RecordJson.Frame(m_mode.Room.Frame, m_mode.Room.AnchorUuid) : null,
                    },
                ["operator"] = m_mode.Operator,
                ["lighting"] = m_settings.Lighting,
                ["settings"] = new JObject
                {
                    ["frame_rate_hz"] = m_settings.RateHz,
                    ["depth"] = m_settings.Depth,
                    ["detector"] = m_settings.Detector,
                    ["detector_model"] = ctx.DetectorModel,
                    ["camera"] = "left",
                    ["requested_resolution"] = new JArray(ctx.Camera.CurrentResolution.x, ctx.Camera.CurrentResolution.y),
                    ["jpeg_quality"] = 90,
                    ["tip_calibration"] = JToken.FromObject(m_mode.Calibration),
                },
                ["gt_definition"] = GroundTruth.Definition,
                ["gt_definition_version"] = GroundTruth.DefinitionVersion,
                ["box_convention"] = GroundTruth.BoxConvention,
                ["room_frame_definition"] = GroundTruth.RoomFrameDefinition,
                ["conventions"] = new JObject
                {
                    ["world"] = "Unity world, left-handed: x right, y up, z forward; metres; quaternions [x,y,z,w]",
                    ["camera_local"] = "+x right, +y up, +z forward (CameraModel); viewport origin bottom-left",
                    ["image_pixels"] = "JPEG pixel (u, v) top-left origin: u = viewport.x * W, v = (1 - viewport.y) * H",
                    ["projection"] = "sensor = (x/z * fx + cx, y/z * fy + cy); viewport = (sensor - crop.xy) / crop.wh",
                    ["keypoints"] = "0 = centre, 1..8 = UprightBox corners (index-1 bits: 1=+x, 2=top, 4=+z)",
                },
            };
            return json;
        }
    }

    public static class DiskSpace
    {
        /// <summary>Free space of the data partition in GB (Android StatFs; −1 if unknown).</summary>
        public static double FreeGb(string path)
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var statFs = new AndroidJavaObject("android.os.StatFs", path))
                {
                    return statFs.Call<long>("getAvailableBytes") / 1e9;
                }
#else
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return new DriveInfo(root).AvailableFreeSpace / 1e9;
#endif
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Horizon OS build string (android.os.Build.DISPLAY).</summary>
        public static string AndroidBuildDisplay()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var build = new AndroidJavaClass("android.os.Build"))
                {
                    return build.GetStatic<string>("DISPLAY");
                }
            }
            catch (Exception)
            {
                return null;
            }
#else
            return null;
#endif
        }
    }

    /// <summary>Package versions etc. written by BuildScript into Resources/qd3d_buildinfo.json.</summary>
    public static class BuildInfo
    {
        private static JToken s_json;

        public static JToken Json
        {
            get
            {
                if (s_json == null)
                {
                    var asset = Resources.Load<TextAsset>("qd3d_buildinfo");
                    try
                    {
                        s_json = asset != null ? JToken.Parse(asset.text) : new JObject { ["missing"] = true };
                    }
                    catch (Exception)
                    {
                        s_json = new JObject { ["unreadable"] = true };
                    }
                }

                return s_json;
            }
        }
    }
}
