// Gravity-aligned room frame defined by a physical tape cross on the floor (no markers).
//   origin = cross centre (touched with the calibrated controller tip, so it lies on the floor)
//   +Y     = world up (gravity)
//   +Z     = horizontal direction from the cross centre to a second touched point on one tape arm
//   +X     = Unity convention (left-handed): +X = +Y × +Z rotated, i.e. "right" when looking along +Z
// All ground-truth boxes are stored in this frame. The persisted OVRSpatialAnchor only re-finds the frame in a
// new session; its tilt is ignored (only position and heading are used), so the frame stays gravity-aligned.
// Pure maths (no Unity objects): unit-tested offline.
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public struct RoomFrame
    {
        /// <summary>Minimum horizontal distance between the cross centre and the arm point.</summary>
        public const float MinArmDistance = 0.15f;

        /// <summary>World position of the room origin.</summary>
        public Vector3 Origin;

        /// <summary>World heading of room +Z around +Y, radians (0 = world +Z, positive towards world +X).</summary>
        public float Yaw;

        public bool IsValid;

        public RoomFrame(Vector3 origin, float yaw)
        {
            Origin = origin;
            Yaw = WrapPi(yaw);
            IsValid = true;
        }

        public Quaternion Rotation => Quaternion.Euler(0f, Yaw * Mathf.Rad2Deg, 0f);
        public Vector3 AxisZ => new Vector3(Mathf.Sin(Yaw), 0f, Mathf.Cos(Yaw));
        public Vector3 AxisX => new Vector3(Mathf.Cos(Yaw), 0f, -Mathf.Sin(Yaw));

        /// <summary>Frame from the two touches on the tape cross. Error text when the arm point is too close.</summary>
        public static bool TryFromCrossTouches(Vector3 centre, Vector3 armPoint, out RoomFrame frame, out string error)
        {
            frame = default;
            var d = armPoint - centre;
            d.y = 0f;
            var dist = d.magnitude;
            if (dist < MinArmDistance)
            {
                error = $"arm point only {dist * 100f:F0} cm from the centre (need ≥ {MinArmDistance * 100f:F0} cm)";
                return false;
            }

            error = null;
            frame = new RoomFrame(centre, Mathf.Atan2(d.x, d.z));
            return true;
        }

        /// <summary>Frame from a (possibly slightly tilted) anchor pose: position + heading of its +Z.</summary>
        public static RoomFrame FromPose(Vector3 position, Quaternion rotation)
        {
            var f = rotation * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-8f)
            {
                // Anchor looking straight up/down: fall back to its up vector's heading.
                f = rotation * Vector3.up;
                f.y = 0f;
            }

            return new RoomFrame(position, Mathf.Atan2(f.x, f.z));
        }

        /// <summary>Room-frame point → world point.</summary>
        public Vector3 RoomToWorld(Vector3 p) => Origin + Rotation * p;
        /// <summary>World point → room-frame point.</summary>
        public Vector3 WorldToRoom(Vector3 p) => Quaternion.Inverse(Rotation) * (p - Origin);
        /// <summary>World direction → room-frame direction.</summary>
        public Vector3 DirectionToRoom(Vector3 d) => Quaternion.Inverse(Rotation) * d;
        /// <summary>Room-frame direction → world direction.</summary>
        public Vector3 DirectionToWorld(Vector3 d) => Rotation * d;
        /// <summary>World rotation → room-frame rotation.</summary>
        public Quaternion RotationToRoom(Quaternion q) => Quaternion.Inverse(Rotation) * q;
        /// <summary>Room-frame rotation → world rotation.</summary>
        public Quaternion RotationToWorld(Quaternion q) => Rotation * q;
        /// <summary>World heading → room heading (radians, wrapped to ±π).</summary>
        public float YawToRoom(float worldYaw) => WrapPi(worldYaw - Yaw);
        /// <summary>Room heading → world heading (radians, wrapped to ±π).</summary>
        public float YawToWorld(float roomYaw) => WrapPi(roomYaw + Yaw);

        /// <summary>World box → room-frame box (what is stored).</summary>
        public UprightBox BoxToRoom(UprightBox world)
        {
            return new UprightBox(WorldToRoom(world.BottomCenter), YawToRoom(world.Yaw), world.Size);
        }

        /// <summary>Stored room-frame box → world box (what is drawn).</summary>
        public UprightBox BoxToWorld(UprightBox room)
        {
            return new UprightBox(RoomToWorld(room.BottomCenter), YawToWorld(room.Yaw), room.Size);
        }

        /// <summary>Angle wrapped to [−π, π).</summary>
        public static float WrapPi(float a)
        {
            const float twoPi = 2f * Mathf.PI;
            a = a - twoPi * (float)System.Math.Floor((a + Mathf.PI) / twoPi);
            return a;
        }

        /// <summary>Origin and heading for logs.</summary>
        public override string ToString() =>
            IsValid ? $"origin=({Origin.x:F3},{Origin.y:F3},{Origin.z:F3}) yaw={Yaw * Mathf.Rad2Deg:F2}°" : "invalid";
    }
}
