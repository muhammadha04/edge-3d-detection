// User-facing session options chosen in the start menu. Only these preferences persist between launches
// (PlayerPrefs); detected objects never do — every session starts with an empty room.
using UnityEngine;

namespace QuestDetect3D
{
    public enum DisplayMode
    {
        /// <summary>Clean 3D box + label.</summary>
        Box,
        /// <summary>Lab-chair 3D scan fitted into the box + cyan fitted keypoints + label.</summary>
        ScanAndKeypoints,
    }

    public enum DetectorKind
    {
        /// <summary>YOLOv9-t (Inference Engine) 2D boxes + Quest depth → metric 3D boxes, many furniture classes.</summary>
        YoloDepth,
        /// <summary>MediaPipe Objectron chair keypoints + box solver (v2 baseline).</summary>
        Objectron,
    }

    public enum AppMode
    {
        /// <summary>Live 3D detection (the original app).</summary>
        Detect,
        /// <summary>QD3D Annotate: manual ground truth, recording, export.</summary>
        Annotate,
    }

    public enum InferenceBackend
    {
        Gpu,
        Cpu,
    }

    public sealed class AppSettings
    {
        public const float MinConfidenceLow = 0.30f;
        public const float MinConfidenceHigh = 0.95f;
        public const float MinConfidenceStep = 0.05f;
        public const float DefaultMinConfidence = 0.55f;

        private const string KeyMode = "qd3d.displayMode";
        private const string KeyMinConf = "qd3d.minConfidence";
        private const string KeyDetector = "qd3d.detector";
        private const string KeyBackend = "qd3d.backend";
        private const string KeyAppMode = "qd3d.appMode";

        public AppMode AppMode = AppMode.Detect;

        public DetectorKind Detector = DetectorKind.YoloDepth;
        public InferenceBackend Backend = InferenceBackend.Gpu;

        public DisplayMode Mode = DisplayMode.Box;

        /// <summary>A box is drawn only while its track confidence is at least this value.</summary>
        public float MinConfidence = DefaultMinConfidence;

        public bool ShowScan => Mode == DisplayMode.ScanAndKeypoints;

        public static AppSettings Load()
        {
            var s = new AppSettings
            {
                Mode = (DisplayMode)Mathf.Clamp(PlayerPrefs.GetInt(KeyMode, (int)DisplayMode.Box), 0, 1),
                MinConfidence = PlayerPrefs.GetFloat(KeyMinConf, DefaultMinConfidence),
                Detector = (DetectorKind)Mathf.Clamp(PlayerPrefs.GetInt(KeyDetector, 0), 0, 1),
                Backend = (InferenceBackend)Mathf.Clamp(PlayerPrefs.GetInt(KeyBackend, 0), 0, 1),
                AppMode = (AppMode)Mathf.Clamp(PlayerPrefs.GetInt(KeyAppMode, 0), 0, 1),
            };
            s.MinConfidence = Snap(s.MinConfidence);
            return s;
        }

        public void Save()
        {
            PlayerPrefs.SetInt(KeyMode, (int)Mode);
            PlayerPrefs.SetFloat(KeyMinConf, MinConfidence);
            PlayerPrefs.SetInt(KeyDetector, (int)Detector);
            PlayerPrefs.SetInt(KeyBackend, (int)Backend);
            PlayerPrefs.SetInt(KeyAppMode, (int)AppMode);
            PlayerPrefs.Save();
        }

        public void StepMinConfidence(int direction)
        {
            MinConfidence = Snap(MinConfidence + direction * MinConfidenceStep);
        }

        private static float Snap(float v)
        {
            v = Mathf.Clamp(v, MinConfidenceLow, MinConfidenceHigh);
            return Mathf.Round(v / MinConfidenceStep) * MinConfidenceStep;
        }

        public override string ToString() => $"app_mode={AppMode} detector={Detector} backend={Backend} mode={Mode} min_conf={MinConfidence:F2}";
    }
}
