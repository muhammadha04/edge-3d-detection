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

    public sealed class AppSettings
    {
        public const float MinConfidenceLow = 0.30f;
        public const float MinConfidenceHigh = 0.95f;
        public const float MinConfidenceStep = 0.05f;
        public const float DefaultMinConfidence = 0.55f;

        private const string KeyMode = "qd3d.displayMode";
        private const string KeyMinConf = "qd3d.minConfidence";

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
            };
            s.MinConfidence = Snap(s.MinConfidence);
            return s;
        }

        public void Save()
        {
            PlayerPrefs.SetInt(KeyMode, (int)Mode);
            PlayerPrefs.SetFloat(KeyMinConf, MinConfidence);
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

        public override string ToString() => $"mode={Mode} min_conf={MinConfidence:F2}";
    }
}
