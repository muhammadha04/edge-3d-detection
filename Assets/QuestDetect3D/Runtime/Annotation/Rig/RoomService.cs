// Re-finds a room's frame in the current session from its persisted OVRSpatialAnchor, and creates anchors when
// the room cross is (re-)placed. Never creates a frame silently: if the anchor cannot be loaded or localised the
// frame stays unavailable and Status says why.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public enum RoomFrameState
    {
        NoRoom,
        NotPlaced,
        Localizing,
        Localized,
        Failed,
    }

    public sealed class RoomService
    {
        private const double LocalizeTimeoutSec = 15.0;

        private OVRSpatialAnchor m_anchor;
        private int m_generation;

        public RoomFrameState State { get; private set; } = RoomFrameState.NoRoom;
        public string Status { get; private set; } = "no room selected";
        public string AnchorUuid { get; private set; }
        public RoomFrame Frame { get; private set; }
        public bool HasFrame => State == RoomFrameState.Localized && Frame.IsValid;

        /// <summary>Called on the main thread when the state changes (UI refresh, audit).</summary>
        public event Action<RoomFrameState, string> Changed;

        public void Clear()
        {
            m_generation++;
            DestroyAnchor();
            Frame = default;
            AnchorUuid = null;
            Set(RoomFrameState.NoRoom, "no room selected");
        }

        /// <summary>Per frame: follow the anchor (the system may refine its pose).</summary>
        public void Update()
        {
            if (State == RoomFrameState.Localized && m_anchor != null && m_anchor.Localized)
            {
                var t = m_anchor.transform;
                Frame = RoomFrame.FromPose(t.position, t.rotation);
            }
        }

        public async void Load(RoomFile room)
        {
            var generation = ++m_generation;
            DestroyAnchor();
            Frame = default;
            AnchorUuid = room?.AnchorUuid;
            if (room == null)
            {
                Set(RoomFrameState.NoRoom, "no room selected");
                return;
            }

            if (string.IsNullOrEmpty(room.AnchorUuid))
            {
                Set(RoomFrameState.NotPlaced, "room cross not placed yet: Room → Place room cross");
                return;
            }

            if (!Guid.TryParse(room.AnchorUuid, out var uuid))
            {
                Set(RoomFrameState.Failed, $"room file has an invalid anchor UUID '{room.AnchorUuid}'");
                return;
            }

            Set(RoomFrameState.Localizing, "loading the room anchor…");
            try
            {
                var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
                var load = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound);
                if (generation != m_generation)
                {
                    return;
                }

                if (!load.Success || unbound.Count == 0)
                {
                    Set(RoomFrameState.Failed,
                        $"ROOM ANCHOR NOT FOUND ({Short(room.AnchorUuid)}, status {load.Status}). The room frame is NOT available. " +
                        "Are you in the right room? Press Retry, or re-place the cross on the SAME tape cross.");
                    return;
                }

                var anchor = unbound[0];
                Set(RoomFrameState.Localizing, "localising the room anchor (look around the room cross)…");
                var localized = anchor.Localized || await anchor.LocalizeAsync(LocalizeTimeoutSec);
                if (generation != m_generation)
                {
                    return;
                }

                if (!localized)
                {
                    Set(RoomFrameState.Failed,
                        $"ROOM ANCHOR FOUND BUT NOT LOCALISED within {LocalizeTimeoutSec:F0} s ({Short(room.AnchorUuid)}). " +
                        "The room frame is NOT available. Walk around the room cross and press Retry.");
                    return;
                }

                var go = new GameObject("QD3D RoomAnchor " + Short(room.AnchorUuid));
                m_anchor = go.AddComponent<OVRSpatialAnchor>();
                anchor.BindTo(m_anchor);
                if (anchor.TryGetPose(out var pose))
                {
                    go.transform.SetPositionAndRotation(pose.position, pose.rotation);
                }

                Frame = RoomFrame.FromPose(go.transform.position, go.transform.rotation);
                Set(RoomFrameState.Localized, $"room frame localised ({Short(room.AnchorUuid)})");
            }
            catch (Exception e)
            {
                if (generation == m_generation)
                {
                    Set(RoomFrameState.Failed, "anchor load error: " + e.Message);
                }
            }
        }

        /// <summary>Create and persist a new anchor at the frame defined by the touched cross.</summary>
        public async void CreateAnchor(RoomFrame frame, Action<string, string> done)
        {
            var generation = ++m_generation;
            DestroyAnchor();
            Set(RoomFrameState.Localizing, "creating the room anchor…");
            try
            {
                var go = new GameObject("QD3D RoomAnchor (new)");
                go.transform.SetPositionAndRotation(frame.Origin, frame.Rotation);
                var anchor = go.AddComponent<OVRSpatialAnchor>();
                var created = await anchor.WhenCreatedAsync();
                if (!created)
                {
                    UnityEngine.Object.Destroy(go);
                    Set(RoomFrameState.Failed, "could not create the spatial anchor (is anchor support enabled?)");
                    done?.Invoke(null, "anchor creation failed");
                    return;
                }

                var save = await anchor.SaveAnchorAsync();
                if (!save.Success)
                {
                    UnityEngine.Object.Destroy(go);
                    Set(RoomFrameState.Failed, $"could not save the spatial anchor (status {save.Status})");
                    done?.Invoke(null, $"anchor save failed: {save.Status}");
                    return;
                }

                if (generation != m_generation)
                {
                    UnityEngine.Object.Destroy(go);
                    return;
                }

                m_anchor = anchor;
                go.name = "QD3D RoomAnchor " + Short(anchor.Uuid.ToString());
                AnchorUuid = anchor.Uuid.ToString();
                Frame = RoomFrame.FromPose(go.transform.position, go.transform.rotation);
                Set(RoomFrameState.Localized, $"room anchor created and saved ({Short(AnchorUuid)})");
                done?.Invoke(AnchorUuid, null);
            }
            catch (Exception e)
            {
                Set(RoomFrameState.Failed, "anchor creation error: " + e.Message);
                done?.Invoke(null, e.Message);
            }
        }

        private void DestroyAnchor()
        {
            if (m_anchor != null)
            {
                UnityEngine.Object.Destroy(m_anchor.gameObject);
                m_anchor = null;
            }
        }

        private void Set(RoomFrameState state, string status)
        {
            State = state;
            Status = status;
            Log.Info($"ann_room_frame state={state} uuid={AnchorUuid ?? "none"} frame={Frame} status=\"{status}\"");
            Changed?.Invoke(state, status);
        }

        public static string Short(string uuid) => string.IsNullOrEmpty(uuid) ? "none" : uuid.Length > 8 ? uuid.Substring(0, 8) : uuid;
    }
}
