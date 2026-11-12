// Record (rate, depth, detector, lighting, start/stop, live status) and Export (zip + adb pull path).
using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QuestDetect3D.Recording;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class RecordScreen : IAnnTickScreen
    {
        private static readonly RecordSettings s_settings = new RecordSettings();
        private readonly AnnotationMode m_mode;
        private UnityEngine.UI.Text m_status;
        private string m_error;
        private bool m_wasRecording;

        public RecordScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Record";

        /// <summary>Rate 2/5/10 Hz, depth, detector, lighting, live status, Start/Stop, Drift check.</summary>
        public void Build(AnnPanel p)
        {
            var rec = m_mode.Recorder;
            var busy = rec.IsRecording || rec.IsFinishing;
            m_wasRecording = rec.IsRecording;
            var s = s_settings;
            p.BeginRow();
            foreach (var hz in RecordSettings.Rates)
            {
                var r = hz;
                p.Button($"{hz} Hz", () =>
                {
                    s.RateHz = r;
                    m_mode.Refresh();
                }, !busy, s.RateHz == hz ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 54);
            }

            p.EndRow();
            p.BeginRow();
            p.Button(s.Depth ? "Depth: ON" : "Depth: OFF", () =>
            {
                s.Depth = !s.Depth;
                m_mode.Refresh();
            }, !busy, s.Depth ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 54);
            p.Button(s.Detector ? "Detector: ON" : "Detector: OFF", () =>
            {
                s.Detector = !s.Detector;
                m_mode.Refresh();
            }, !busy, s.Detector ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 54);
            p.Button("Light: " + s.Lighting, () =>
            {
                var i = Array.IndexOf(RecordSettings.Lightings, s.Lighting);
                s.Lighting = RecordSettings.Lightings[(i + 1) % RecordSettings.Lightings.Length];
                m_mode.Refresh();
            }, !busy, null, 54);
            p.EndRow();
            if (!m_mode.Room.HasFrame)
            {
                p.Label("No room frame: frames are recorded in world coordinates only (no ground-truth alignment).", 22, AnnPanel.Warning);
            }

            m_status = p.Label(StatusText(), 26, AnnPanel.Accent);
            if (!string.IsNullOrEmpty(m_error))
            {
                p.Label(m_error, 24, new Color(1f, 0.45f, 0.45f));
            }

            p.BeginRow();
            if (rec.IsRecording)
            {
                p.Button("■ Stop", () =>
                {
                    rec.Stop();
                    m_mode.Audit("recording_stop", new JObject { ["session_id"] = rec.SessionId, ["frames"] = rec.FramesWritten, ["dropped"] = rec.Dropped });
                    m_mode.Refresh();
                }, true, AnnPanel.DangerButton);
            }
            else
            {
                p.Button(rec.IsFinishing ? "Finishing…" : "● Start", Start, !rec.IsFinishing, AnnPanel.PrimaryButton);
            }

            p.Button("Drift check", () => m_mode.Show(new TouchCheckScreen(m_mode, "drift_cross")), m_mode.Room.HasFrame);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
            p.Label("Do a drift check before Start and after Stop. Walk slowly around the object at 0.7–3 m.", 22, AnnPanel.DimText);
        }

        /// <summary>Starts a session with the chosen settings (detector tracks reset when the detector is on).</summary>
        private void Start()
        {
            m_error = null;
            if (!m_mode.Recorder.Start(s_settings, out var error))
            {
                m_error = "Cannot start: " + error;
            }
            else
            {
                if (s_settings.Detector)
                {
                    m_mode.Context.ResetDetector?.Invoke();
                }

                m_mode.Audit("recording_start", new JObject
                {
                    ["session_id"] = m_mode.Recorder.SessionId,
                    ["rate_hz"] = s_settings.RateHz,
                    ["depth"] = s_settings.Depth,
                    ["detector"] = s_settings.Detector,
                    ["lighting"] = s_settings.Lighting,
                });
            }

            m_mode.Refresh();
        }

        /// <summary>Updates the live status line (MB, frames, dropped, free space).</summary>
        public void Tick()
        {
            if (m_status != null)
            {
                m_status.text = StatusText();
            }

            if (m_mode.Recorder.IsRecording != m_wasRecording)
            {
                m_mode.Refresh();
            }
        }

        /// <summary>Status while recording / finishing / idle.</summary>
        private string StatusText()
        {
            var rec = m_mode.Recorder;
            if (rec.IsRecording)
            {
                var free = rec.FreeGb >= 0 ? $"{rec.FreeGb:F1} GB free" : "free space ?";
                var warn = rec.LowDisk ? "  <color=#FF6B6B>LOW DISK (< 2 GB)</color>" : "";
                var det = rec.Settings.Detector ? $"  detector frames {rec.DetectorFrames} ({m_mode.Context.DetectorStatus?.Invoke()})" : "";
                var err = rec.WorkerErrors > 0 ? $"\n<color=#FF6B6B>write errors {rec.WorkerErrors}: {rec.WorkerLastError}</color>" : "";
                if (rec.Settings.Depth && rec.DepthWritten == 0 && rec.Elapsed > 2f)
                {
                    err += $"\n<color=#FF6B6B>DEPTH NOT RECORDED: {rec.DepthError}</color>";
                }
                return $"<color=#FF6B6B>● REC</color> {rec.Elapsed:F0} s   {rec.Bytes / 1e6:F0} MB   frames {rec.FramesWritten}   " +
                       $"depth {rec.DepthWritten}   dropped {rec.Dropped}\n{free}{warn}{det}{err}";
            }

            if (rec.IsFinishing)
            {
                return "Writing the last frames…";
            }

            return string.IsNullOrEmpty(rec.LastSummary) ? "Not recording." : "Last: " + rec.LastSummary;
        }
    }

    public sealed class ExportScreen : IAnnScreen
    {
        private readonly AnnotationMode m_mode;
        private string m_status = "";
        private bool m_busy;

        public ExportScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Export";

        /// <summary>Data folder, counts, Export zip.</summary>
        public void Build(AnnPanel p)
        {
            var root = m_mode.Store.Root;
            var sessions = Directory.Exists(m_mode.Store.SessionsDir) ? Directory.GetDirectories(m_mode.Store.SessionsDir).Length : 0;
            p.Label("Everything lives in:", 24, AnnPanel.DimText);
            p.Label(root, 22);
            p.Label($"{m_mode.Store.ListRooms().Count} room(s), {m_mode.Catalog.Objects.Count} object(s), {sessions} session(s).", 24);
            p.Label("On the PC run PullSession.bat (adb pull of the whole QD3D folder into Data/QD3D). " +
                    "The zip below is optional (one file for manual copying).", 22, AnnPanel.DimText);
            if (!string.IsNullOrEmpty(m_status))
            {
                p.Label(m_status, 24, AnnPanel.Accent);
            }

            p.BeginRow();
            p.Button(m_busy ? "Zipping…" : "Export zip", Zip, !m_busy && !m_mode.Recorder.IsRecording, AnnPanel.PrimaryButton);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
            if (m_mode.Recorder.IsRecording)
            {
                p.Label("Stop the recording before exporting.", 22, AnnPanel.Warning);
            }
        }

        /// <summary>Zips the whole QD3D folder (except exports/) on a worker thread into exports/qd3d_&lt;time&gt;.zip.</summary>
        private void Zip()
        {
            m_busy = true;
            m_status = "Zipping (runs in the background)…";
            m_mode.Refresh();
            var root = m_mode.Store.Root;
            var exports = m_mode.Store.ExportsDir;
            var name = $"qd3d_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
            m_mode.Audit("export_start", new JObject { ["file"] = name });
            Task.Run(() =>
            {
                string result;
                try
                {
                    Directory.CreateDirectory(exports);
                    var zipPath = Path.Combine(exports, name);
                    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                        {
                            if (file.StartsWith(exports, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var entry = file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
                            var level = file.EndsWith(".jpg") || file.EndsWith(".png") ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Fastest;
                            zip.CreateEntryFromFile(file, "QD3D/" + entry, level);
                        }
                    }

                    result = $"Exported: {zipPath} ({new FileInfo(zipPath).Length / 1e6:F0} MB)\nadb pull \"{zipPath}\"";
                }
                catch (Exception e)
                {
                    result = "Export failed: " + e.Message;
                }

                m_mode.RunOnMain(() =>
                {
                    m_busy = false;
                    m_status = result;
                    m_mode.Audit("export_done", new JObject { ["result"] = result });
                    Log.Info("ann_export " + result.Replace('\n', ' '));
                    m_mode.Refresh();
                });
            });
        }
    }
}
