// Controllers for QD3D Annotate: tip positions (calibrated offsets), button edges, rays into the world.
// The controller "tip" is a fixed point on the controller shell, expressed in the controller anchor frame of the
// OVRCameraRig. It ships with a default offset (so Touch works immediately) and is replaced per controller by the
// pivot calibration (calibration.json).
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class AnnRig
    {
        /// <summary>Default tip offset (controller anchor frame, metres): a point ~5 cm ahead of and 2.5 cm below the
        /// anchor, roughly the front-bottom of the Touch Plus shell. Calibrate for real measurements.</summary>
        public static readonly Vector3 DefaultTipOffset = new Vector3(0f, -0.025f, 0.05f);

        private readonly OVRCameraRig m_rig;
        private readonly Vector3[] m_tip = { DefaultTipOffset, DefaultTipOffset };
        private readonly bool[] m_tipDefault = { true, true };

        public AnnRig(OVRCameraRig rig, Transform head)
        {
            m_rig = rig;
            Head = head;
        }

        public Transform Head { get; }
        public bool IsValid => m_rig != null;

        /// <summary>0 for the left controller, 1 for the right.</summary>
        public static int Index(OVRInput.Controller c) => c == OVRInput.Controller.LTouch ? 0 : 1;
        /// <summary>"left" / "right" (used in the files).</summary>
        public static string Name(OVRInput.Controller c) => c == OVRInput.Controller.LTouch ? "left" : "right";
        /// <summary>"left" → LTouch, anything else → RTouch.</summary>
        public static OVRInput.Controller FromName(string n) => n == "left" ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        /// <summary>OVRCameraRig controller anchor transform.</summary>
        public Transform Anchor(OVRInput.Controller c)
        {
            if (m_rig == null)
            {
                return null;
            }

            return c == OVRInput.Controller.LTouch ? m_rig.leftControllerAnchor : m_rig.rightControllerAnchor;
        }

        /// <summary>Controller connected and its position tracked.</summary>
        public bool IsTracked(OVRInput.Controller c) =>
            OVRInput.GetControllerPositionTracked(c) && OVRInput.IsControllerConnected(c);

        /// <summary>Sets the tip offset (controller frame, metres) and whether it is the uncalibrated default.</summary>
        public void SetTipOffset(OVRInput.Controller c, Vector3 offset, bool isDefault)
        {
            m_tip[Index(c)] = offset;
            m_tipDefault[Index(c)] = isDefault;
        }

        /// <summary>Current tip offset of a controller.</summary>
        public Vector3 TipOffset(OVRInput.Controller c) => m_tip[Index(c)];
        /// <summary>True while the controller still uses the shipped default (calibrate before measuring!).</summary>
        public bool TipIsDefault(OVRInput.Controller c) => m_tipDefault[Index(c)];

        /// <summary>World position of the controller tip (what M1 Touch records).</summary>
        public Vector3 TipWorld(OVRInput.Controller c)
        {
            var a = Anchor(c);
            return a != null ? a.TransformPoint(m_tip[Index(c)]) : Vector3.zero;
        }

        /// <summary>Ray from the controller anchor along its forward axis (M2 Point, face push, M4 pick).</summary>
        public Ray PointerRay(OVRInput.Controller c)
        {
            var a = Anchor(c);
            return a != null ? new Ray(a.position, a.forward) : new Ray(Vector3.zero, Vector3.forward);
        }

        /// <summary>Applies the saved tip calibrations (defaults for controllers without one).</summary>
        public void ApplyCalibration(CalibrationFile file)
        {
            SetTipOffset(OVRInput.Controller.LTouch, DefaultTipOffset, true);
            SetTipOffset(OVRInput.Controller.RTouch, DefaultTipOffset, true);
            if (file?.Tips == null)
            {
                return;
            }

            foreach (var tip in file.Tips)
            {
                if (tip?.Offset != null && tip.Offset.Length == 3 && !tip.IsDefault)
                {
                    SetTipOffset(FromName(tip.Controller), Vec.FromArray(tip.Offset), false);
                }
            }
        }

        // ------------------------------------------------------------------ buttons

        /// <summary>Index trigger pressed this frame.</summary>
        public static bool TriggerDown(OVRInput.Controller c) => OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, c);
        /// <summary>Index trigger held.</summary>
        public static bool TriggerHeld(OVRInput.Controller c) => OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, c);
        /// <summary>Index trigger released this frame.</summary>
        public static bool TriggerUp(OVRInput.Controller c) => OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, c);
        /// <summary>Grip held (fine steps / extend touch / face drag).</summary>
        public static bool GripHeld(OVRInput.Controller c) => OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, c);
        /// <summary>Grip pressed this frame.</summary>
        public static bool GripDown(OVRInput.Controller c) => OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, c);
        /// <summary>Grip released this frame.</summary>
        public static bool GripUp(OVRInput.Controller c) => OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, c);
        /// <summary>Thumbstick position (−1..1).</summary>
        public static Vector2 Stick(OVRInput.Controller c) => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, c);
    }
}
