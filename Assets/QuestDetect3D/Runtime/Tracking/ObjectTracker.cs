// Associates per-frame box observations with persistent tracks and manages their lifecycle.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public sealed class ObjectTracker
    {
        private const int MaxTracks = 16;
        private const float TentativeTimeoutSec = 4f;
        private const float LowConfidenceRemoveSec = 4f;
        private const float RemoveConfidence = 0.18f;

        private readonly List<TrackedObject> m_tracks = new List<TrackedObject>();
        private readonly List<int> m_order = new List<int>();

        public IReadOnlyList<TrackedObject> Tracks => m_tracks;

        public event Action<TrackedObject> TrackConfirmed;
        public event Action<TrackedObject> TrackRemoved;

        public int ConfirmedCount
        {
            get
            {
                var n = 0;
                foreach (var t in m_tracks)
                {
                    if (t.State == TrackState.Confirmed)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public void Reset()
        {
            foreach (var t in m_tracks)
            {
                TrackRemoved?.Invoke(t);
            }

            m_tracks.Clear();
            TrackedObject.ResetIds();
        }

        /// <summary>Single-category frame (Objectron path): only tracks of that category's group can miss.</summary>
        public void Process(
            ObjectCategory category,
            List<BoxObservation> observations,
            Func<TrackedObject, bool> isExpectedVisible,
            float now)
        {
            for (var i = 0; i < observations.Count; i++)
            {
                var o = observations[i];
                o.Category = category;
                if (o.YawPeriodDeg <= 0f)
                {
                    o.YawPeriodDeg = 360f;
                }

                observations[i] = o;
            }

            ProcessMulti(observations, isExpectedVisible, now, Categories.Get(category).Group);
        }

        /// <param name="observations">Observations of one camera frame (each carries its own category).</param>
        /// <param name="isExpectedVisible">True when a track should have been detected in this frame.</param>
        /// <param name="onlyGroup">Restrict miss counting to one group (null = every track can miss).</param>
        public void ProcessMulti(
            List<BoxObservation> observations,
            Func<TrackedObject, bool> isExpectedVisible,
            float now,
            CategoryGroup? onlyGroup = null)
        {
            var assigned = new HashSet<TrackedObject>();

            m_order.Clear();
            for (var i = 0; i < observations.Count; i++)
            {
                m_order.Add(i);
            }

            m_order.Sort((a, b) => observations[b].Quality.CompareTo(observations[a].Quality));

            foreach (var index in m_order)
            {
                var obs = observations[index];
                var group = Categories.Get(obs.Category).Group;
                TrackedObject best = null;
                var bestDistance = float.MaxValue;
                foreach (var track in m_tracks)
                {
                    if (track.Group != group || assigned.Contains(track))
                    {
                        continue;
                    }

                    var gate = Mathf.Max(track.AssociationGate(),
                        Mathf.Max(0.6f * Mathf.Max(obs.Box.Size.x, obs.Box.Size.z), 0.07f) + 0.05f);
                    var d = HorizontalDistance(track.Estimate.BottomCenter, obs.Box.BottomCenter);
                    // Depth boxes (yaw mod 90°) jump more between single views (unseen back, heading); also accept
                    // when either centre lies inside the other's footprint (field test: 138 new tracks, 70 merges).
                    var overlaps = obs.YawPeriodDeg > 0f && obs.YawPeriodDeg < 359f &&
                                   (InsideFootprint(obs.Box.BottomCenter, track.Estimate, FootprintMargin) ||
                                    InsideFootprint(track.Estimate.BottomCenter, obs.Box, FootprintMargin));
                    if ((d < gate || overlaps) && d < bestDistance)
                    {
                        best = track;
                        bestDistance = d;
                    }
                }

                if (best == null)
                {
                    if (m_tracks.Count >= MaxTracks || obs.Quality < 0.05f)
                    {
                        continue;
                    }

                    best = new TrackedObject(obs.Category, now, obs.YawPeriodDeg > 0f ? obs.YawPeriodDeg : 360f);
                    m_tracks.Add(best);
                    Log.Info($"track_new id={best.Id} {obs.Category} {obs.Box} q={obs.Quality:F2}");
                }

                assigned.Add(best);
                best.AddObservation(obs, Categories.Get(best.Category), now);
                MaybeConfirm(best, now);
            }

            foreach (var track in m_tracks)
            {
                if (assigned.Contains(track) || (onlyGroup.HasValue && track.Group != onlyGroup.Value))
                {
                    continue;
                }

                if (isExpectedVisible != null && isExpectedVisible(track))
                {
                    track.RegisterMiss(Categories.Get(track.Category), now);
                }
            }

            MergeDuplicates(now);
            Prune(now);
        }

        private void MaybeConfirm(TrackedObject track, float now)
        {
            if (track.State == TrackState.Confirmed)
            {
                return;
            }

            if (track.Confidence >= ConfidenceModel.ConfirmThreshold && track.Hits >= ConfidenceModel.ConfirmMinHits)
            {
                track.State = TrackState.Confirmed;
                track.ConfirmedTime = now;
                Log.Info($"track_confirmed id={track.Id} {track.Category} {track.Estimate} " +
                         $"{track.ConfidenceDetail} ±{track.UncertaintyCm:F1}cm");
                TrackConfirmed?.Invoke(track);
            }
        }

        private void MergeDuplicates(float now)
        {
            for (var i = 0; i < m_tracks.Count; i++)
            {
                for (var j = i + 1; j < m_tracks.Count; j++)
                {
                    var a = m_tracks[i];
                    var b = m_tracks[j];
                    if (a.Group != b.Group)
                    {
                        continue;
                    }

                    var d = HorizontalDistance(a.Estimate.BottomCenter, b.Estimate.BottomCenter);
                    var depthTracks = a.YawPeriod < 6f && b.YawPeriod < 6f;
                    var contained = depthTracks &&
                                    (InsideFootprint(a.Estimate.BottomCenter, b.Estimate, 0f) ||
                                     InsideFootprint(b.Estimate.BottomCenter, a.Estimate, 0f));
                    if (d > 0.5f * Mathf.Min(a.AssociationGate(), b.AssociationGate()) && !contained)
                    {
                        continue;
                    }

                    // Keep the confirmed / older track.
                    var keep = a.State == TrackState.Confirmed || (b.State != TrackState.Confirmed && a.Hits >= b.Hits) ? a : b;
                    var drop = keep == a ? b : a;
                    keep.MergeFrom(drop, Categories.Get(keep.Category), now);
                    m_tracks.Remove(drop);
                    Log.Info($"track_merged keep={keep.Id} drop={drop.Id}");
                    TrackRemoved?.Invoke(drop);
                    MaybeConfirm(keep, now);
                    return;
                }
            }
        }

        private void Prune(float now)
        {
            for (var i = m_tracks.Count - 1; i >= 0; i--)
            {
                var t = m_tracks[i];
                var remove = false;
                if (t.State == TrackState.Tentative)
                {
                    remove = now - t.LastSeenTime > TentativeTimeoutSec;
                }
                else
                {
                    if (t.Confidence < RemoveConfidence)
                    {
                        if (t.LowConfidenceSince < 0f)
                        {
                            t.LowConfidenceSince = now;
                        }

                        remove = now - t.LowConfidenceSince > LowConfidenceRemoveSec;
                    }
                    else
                    {
                        t.LowConfidenceSince = -1f;
                    }
                }

                if (remove)
                {
                    Log.Info($"track_removed id={t.Id} state={t.State} C={t.Confidence:F2} misses={t.Misses:F0}");
                    m_tracks.RemoveAt(i);
                    TrackRemoved?.Invoke(t);
                }
            }
        }

        /// <summary>Margin (m) around a footprint for association; small enough to keep chairs 45 cm apart separate.</summary>
        private const float FootprintMargin = 0.10f;

        /// <summary>Is <paramref name="p"/> (x,z) inside the box footprint grown by <paramref name="margin"/>?</summary>
        public static bool InsideFootprint(Vector3 p, in UprightBox box, float margin)
        {
            var d = p - box.BottomCenter;
            var lx = d.x * box.Right.x + d.z * box.Right.z;
            var lz = d.x * box.Forward.x + d.z * box.Forward.z;
            return Mathf.Abs(lx) <= 0.5f * box.Size.x + margin && Mathf.Abs(lz) <= 0.5f * box.Size.z + margin;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
