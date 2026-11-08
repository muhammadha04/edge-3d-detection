// Detector-agnostic data passed from a 3D-box keypoint model to the geometry stage.
// Any future model (CenterPose, a custom network, ...) only has to fill KeypointDetection.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    /// <summary>Everything captured together with one camera image.</summary>
    public sealed class FrameContext
    {
        public int FrameId;
        public CameraModel Camera;
        public float CaptureRealtime;
    }

    /// <summary>
    /// Assigns the three bits of a keypoint index (k = index - 1) to box roles.
    /// Box keypoint convention (MediaPipe Objectron / Objectron dataset): index 0 = centre,
    /// indices 1..8 = corners, with each object axis encoded by one bit of (index - 1).
    /// </summary>
    public struct BoxLayout
    {
        public int UpBit;
        public int FrontBit;
        public int SideBit;
        /// <summary>+1 when the object "up" axis points to world up.</summary>
        public int UpSign;
        /// <summary>+1 / -1: whether the object side axis equals box local +X or -X. 0 = unknown.</summary>
        public int SideHandedness;

        public static BoxLayout MediaPipeDefault => new BoxLayout
        {
            // MediaPipe box.cc: bit 4 = x, bit 2 = y (up), bit 1 = z (front).
            UpBit = 2,
            FrontBit = 1,
            SideBit = 4,
            UpSign = 1,
            SideHandedness = 0,
        };

        public static int Sign(int bit, int index) => ((index - 1) & bit) != 0 ? 1 : -1;

        public bool IsTop(int index) => UpSign * Sign(UpBit, index) > 0;
        public int FrontSign(int index) => Sign(FrontBit, index);
        public int SideSign(int index) => Sign(SideBit, index);

        public override string ToString() =>
            $"up=bit{UpBit}{(UpSign > 0 ? "+" : "-")} front=bit{FrontBit} side=bit{SideBit} chi={SideHandedness}";
    }

    public sealed class KeypointDetection
    {
        public const int KeypointCount = 9;

        public ObjectCategory Category;
        public int ModelObjectId;

        /// <summary>Viewport coordinates (bottom-left origin) of keypoints 0..8.</summary>
        public readonly Vector2[] Viewport = new Vector2[KeypointCount];
        public readonly bool[] Has = new bool[KeypointCount];
        /// <summary>Per-keypoint weight (lower for keypoints the model marks hidden).</summary>
        public readonly float[] Weight = new float[KeypointCount];

        public BoxLayout Layout = BoxLayout.MediaPipeDefault;

        /// <summary>|cos| between the model's up axis and world up (NaN when unknown).</summary>
        public float ModelVerticality = float.NaN;

        /// <summary>Horizontal world direction of the model's front axis (zero when unknown).</summary>
        public Vector3 ModelFrontWorld;

        /// <summary>Horizontal world direction of the model's side axis (zero when unknown).</summary>
        public Vector3 ModelSideWorld;

        /// <summary>Model-reported score / visibility in [0, 1] (1 when not provided).</summary>
        public float ModelScore = 1f;

        public int ValidKeypointCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < KeypointCount; i++)
                {
                    if (Has[i])
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    public sealed class DetectionFrame
    {
        public FrameContext Context;
        public readonly List<KeypointDetection> Detections = new List<KeypointDetection>();
        public float InferenceMs;
    }

    /// <summary>Common surface of every 3D box keypoint detector.</summary>
    public interface IBoxKeypointDetector
    {
        bool IsReady { get; }
        bool IsBusy { get; }
        string ModelName { get; }
        ObjectCategory Category { get; }
        event Action<DetectionFrame> FrameProcessed;
    }
}
