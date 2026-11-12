// 2D detection + Quest depth → metric, gravity-aligned 3D box (the "YOLO + depth" pipeline).
//
//   1. Sample a jittered grid of depth rays inside the 2D box (inset so the box edges, which mostly show
//      background, are avoided).
//   2. Drop support-surface hits (floor / table top under the object).
//   3. Separate the object from the background along the rays: sort hits by range and cut at range gaps;
//      the nearest cluster with a meaningful share of the hits is the object.
//   3b. Wall rejection: a dominant vertical plane in the cluster that ALSO continues just outside the 2D box
//      (probe rays left/right of it) is a wall/cabinet behind the object, not part of it → its points are
//      removed, and the wall caps how far the unseen back of the object may extend.
//   4. Height: bottom = support plane (floor model) or lowest object points; top = robust top of the points,
//      nudged by the 2D box top edge when depth missed thin tops.
//   5. Footprint + yaw: rotate the points' ground footprint through 0–90° and keep the minimum-area robust
//      rectangle. The flatness of that area curve is the yaw uncertainty.
//   6. Completion: a single view sees only the near faces, so the extent along the viewing direction is
//      a lower bound. It is extended AWAY from the camera to the category's typical depth; the amount added
//      ("completion") is reported and raises uncertainty. Multi-view fusion in the tracker removes it.
//   7. Uncertainty (explainable, per term): depth noise σ_d(z) averaged over points, completion, yaw curve.
//
// Pure logic (no Meta types): unit-tested offline against a synthetic room (Tests/Offline).
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D
{
    public struct DepthFitResult
    {
        public bool Valid;
        public string FailReason;
        public UprightBox Box;

        public int Rays;
        public int Hits;
        public int SupportHits;
        public int ObjectPoints;
        /// <summary>Object points / non-support hits (how cleanly the object separated from background).</summary>
        public float ClusterFraction;
        /// <summary>Fraction of object points inside the fitted box (+3 cm).</summary>
        public float InsideFraction;
        /// <summary>Metres added along the viewing direction from the category prior (unseen back).</summary>
        public float Completion;
        public float SigmaDepth;
        public float SigmaPos;
        public float SigmaYawDeg;
        public float SigmaSizeRel;
        public float Distance;
        public bool UsedFloor;
        /// <summary>Points removed as a background wall (0 when no wall was found).</summary>
        public int WallPoints;
        /// <summary>Where the heading came from: "backrest" (chairs: line fit to the top slab) or "footprint".</summary>
        public string YawSource;
        /// <summary>Metres added to the top from the 2D box's top edge (depth misses thin top rails).</summary>
        public float TopCorrection;

        public override string ToString() =>
            Valid
                ? $"{Box} pts={ObjectPoints}/{Hits}/{Rays} sup={SupportHits} clu={ClusterFraction:F2} in={InsideFraction:F2} " +
                  $"compl={Completion * 100f:F0}cm σd={SigmaDepth * 100f:F1}cm σpos={SigmaPos * 100f:F1}cm " +
                  $"σyaw={SigmaYawDeg:F0}° σsize={SigmaSizeRel * 100f:F0}% d={Distance:F2}m floor={UsedFloor} wall={WallPoints} yaw_src={YawSource} top+={TopCorrection * 100f:F0}cm"
                : $"fail({FailReason}) pts={ObjectPoints}/{Hits}/{Rays}";
    }

    public sealed class DepthBoxFitter
    {
        public int Grid = 11;            // rays per side → 121 rays per detection
        public float Inset = 0.08f;      // fraction of the 2D box trimmed left/right (edges show background)
        public float InsetY = 0.03f;     // top/bottom: keep the far edge of table tops (grazing view)
        public float MaxRange = 7f;
        public int MinPoints = 10;

        // Quest stereo depth noise model σ_d(z) = a + b z² (placeholder until measured on device: Phase-1 task).
        public float NoiseA = 0.008f;
        public float NoiseB = 0.0045f;

        private readonly System.Random m_rng;
        private readonly List<Vector3> m_hits = new List<Vector3>(256);
        private readonly List<Vector3> m_hitNormals = new List<Vector3>(256);
        private readonly List<float> m_range = new List<float>(256);
        private readonly List<int> m_order = new List<int>(256);
        private readonly List<float> m_tmp = new List<float>(256);
        private readonly List<float> m_tmp2 = new List<float>(256);
        private readonly float[] m_areaCurve = new float[45];
        private readonly List<Vector3> m_probeHits = new List<Vector3>(16);
        private readonly List<Vector3> m_probeNormals = new List<Vector3>(16);

        public DepthBoxFitter(int seed = 12345)
        {
            m_rng = new System.Random(seed);
        }

        public float NoiseAt(float z) => NoiseA + NoiseB * z * z;

        /// <param name="floorY">Floor height from the floor model, or NaN when unknown.</param>
        /// <param name="objectPoints">Optional output: the object's depth points (for the debug overlay).</param>
        public DepthFitResult Fit(
            Rect viewport,
            in CameraModel cam,
            in CategoryInfo info,
            IDepthRaycaster depth,
            float floorY,
            List<Vector3> objectPoints = null)
        {
            var r = new DepthFitResult();
            objectPoints?.Clear();
            if (depth == null || !depth.IsAvailable)
            {
                r.FailReason = "no depth";
                return r;
            }

            // ---- 1. rays
            m_hits.Clear();
            m_hitNormals.Clear();
            for (var i = 0; i < Grid; i++)
            {
                for (var j = 0; j < Grid; j++)
                {
                    var u = viewport.xMin + viewport.width * (Inset + (1f - 2f * Inset) * (i + (float)m_rng.NextDouble()) / Grid);
                    var v = viewport.yMin + viewport.height * (InsetY + (1f - 2f * InsetY) * (j + (float)m_rng.NextDouble()) / Grid);
                    r.Rays++;
                    if (depth.Raycast(cam.ViewportToRay(new Vector2(u, v)), MaxRange, out var p, out var n))
                    {
                        m_hits.Add(p);
                        m_hitNormals.Add(n);
                    }
                }
            }

            r.Hits = m_hits.Count;
            if (r.Hits < MinPoints)
            {
                r.FailReason = "depth missing";
                return r;
            }

            // ---- 2. support removal
            var hasFloor = !float.IsNaN(floorY);
            var supportY = hasFloor ? floorY : LowestHorizontal();
            m_range.Clear();
            m_order.Clear();
            for (var k = 0; k < m_hits.Count; k++)
            {
                var p = m_hits[k];
                var isSupport = !float.IsNaN(supportY) && p.y < supportY + 0.04f &&
                                (hasFloor || m_hitNormals[k].y > 0.85f);
                if (isSupport)
                {
                    r.SupportHits++;
                    continue;
                }

                m_order.Add(k);
                m_range.Add(Vector3.Distance(cam.Position, p));
            }

            if (m_order.Count < MinPoints)
            {
                r.FailReason = "only support visible";
                return r;
            }

            // ---- 3. range clustering (sort by range, split at gaps, take nearest substantial cluster)
            var idx = new int[m_order.Count];
            for (var k = 0; k < idx.Length; k++)
            {
                idx[k] = k;
            }

            System.Array.Sort(idx, (a, b) => m_range[a].CompareTo(m_range[b]));
            var start = 0;
            var chosenStart = -1;
            var chosenEnd = -1;
            var bestStart = 0;
            var bestEnd = 0;
            var minShare = Mathf.Max(MinPoints * 0.6f, 0.2f * idx.Length);
            for (var k = 1; k <= idx.Length; k++)
            {
                var split = k == idx.Length;
                if (!split)
                {
                    var t0 = m_range[idx[k - 1]];
                    var t1 = m_range[idx[k]];
                    split = t1 - t0 > Mathf.Max(0.12f, 0.06f * t0);
                }

                if (!split)
                {
                    continue;
                }

                var segSize = k - start;
                if (segSize > bestEnd - bestStart)
                {
                    bestStart = start;
                    bestEnd = k;
                }

                if (chosenStart < 0 && segSize >= minShare)
                {
                    chosenStart = start;
                    chosenEnd = k;
                }

                start = k;
            }

            if (chosenStart < 0)
            {
                chosenStart = bestStart;
                chosenEnd = bestEnd;
            }

            var pts = new List<Vector3>(chosenEnd - chosenStart);
            for (var k = chosenStart; k < chosenEnd; k++)
            {
                pts.Add(m_hits[m_order[idx[k]]]);
            }

            // ---- 3b. wall rejection
            var hasWall = FindWall(pts, viewport, cam, depth, out var wallPoint, out var wallNormal);
            if (hasWall)
            {
                var kept = new List<Vector3>(pts.Count);
                foreach (var p in pts)
                {
                    if (PlaneDistanceXZ(p, wallPoint, wallNormal) > WallInlier)
                    {
                        kept.Add(p); // keep only points clearly in front of the wall
                    }
                }

                r.WallPoints = pts.Count - kept.Count;
                pts = kept;
            }

            r.ObjectPoints = pts.Count;
            r.ClusterFraction = pts.Count / (float)m_order.Count;
            if (pts.Count < MinPoints)
            {
                r.FailReason = $"object cluster too small ({pts.Count})";
                return r;
            }

            objectPoints?.AddRange(pts);

            // ---- 4. vertical extent
            m_tmp.Clear();
            foreach (var p in pts)
            {
                m_tmp.Add(p.y);
            }

            m_tmp.Sort();
            var yLow = Percentile(m_tmp, 0.03f);
            var yTop = Percentile(m_tmp, 0.97f);
            float bottom;
            if (hasFloor && (info.Support == SupportSurface.Floor ? yLow - floorY < 0.45f : yLow - floorY < 0.10f))
            {
                bottom = floorY;
                r.UsedFloor = true;
            }
            else if (!float.IsNaN(supportY) && yLow - supportY < 0.10f)
            {
                bottom = supportY;
            }
            else
            {
                bottom = yLow;
            }

            var centroid = Vector3.zero;
            m_tmp2.Clear();
            foreach (var p in pts)
            {
                centroid += p;
                m_tmp2.Add(new Vector2(p.x - cam.Position.x, p.z - cam.Position.z).magnitude);
            }

            centroid /= pts.Count;
            m_tmp2.Sort();
            var silTop = SilhouetteTop(viewport, cam, Percentile(m_tmp2, 0.05f), Percentile(m_tmp2, 0.95f));
            // Depth often misses thin top edges (a chair's backrest rail). Field test 2026-11-12: YOLO+depth chair
            // heights were 66–77 cm vs 75–92 cm from Objectron. For seating, trust the 2D box top more.
            var seating = info.Group == CategoryGroup.Seating;
            var topGap = seating ? 0.04f : 0.15f;
            var topGain = seating ? 0.8f : 0.5f;
            if (!float.IsNaN(silTop) && silTop - yTop > topGap)
            {
                var add = Mathf.Min(topGain * (silTop - yTop), 0.35f);
                r.TopCorrection = add;
                yTop += add;
            }

            var height = Mathf.Clamp(yTop - bottom, 0.08f, 3f);

            // ---- 5. footprint yaw: minimum-area robust rectangle over 0..88° (box symmetry is 90°)
            var footprint = new List<Vector2>(pts.Count);
            foreach (var p in pts)
            {
                if (p.y > bottom + 0.02f)
                {
                    footprint.Add(new Vector2(p.x, p.z));
                }
            }

            if (footprint.Count < MinPoints / 2)
            {
                r.FailReason = "flat object";
                return r;
            }

            var bestK = 0;
            var bestArea = float.MaxValue;
            for (var k = 0; k < m_areaCurve.Length; k++)
            {
                var yaw = k * 2f * Mathf.Deg2Rad;
                RobustExtents(footprint, yaw, out var rLo, out var rHi, out var fLo, out var fHi);
                var area = Mathf.Max(0.02f, rHi - rLo) * Mathf.Max(0.02f, fHi - fLo);
                m_areaCurve[k] = area;
                if (area < bestArea)
                {
                    bestArea = area;
                    bestK = k;
                }
            }

            // Yaw uncertainty: half-width of the set of angles whose area is within 10 % of the minimum.
            var within = 0;
            for (var k = 0; k < m_areaCurve.Length; k++)
            {
                if (m_areaCurve[k] <= 1.10f * bestArea)
                {
                    within++;
                }
            }

            r.SigmaYawDeg = within >= m_areaCurve.Length - 1 ? 26f : Mathf.Max(3f, within * 2f * 0.5f);
            var bestYaw = bestK * 2f * Mathf.Deg2Rad;
            r.YawSource = "footprint";

            // Chairs: footprints are nearly square, so the area curve is flat (σ yaw ≈ 26° in the field test).
            // The backrest is a plane across the chair's width: a line fit to the top slab of points gives the
            // heading far more precisely.
            if (info.Category == ObjectCategory.Chair && TryBackrestYaw(pts, bottom, height, out var backYaw, out var backSigma) &&
                backSigma < r.SigmaYawDeg)
            {
                bestYaw = backYaw;
                r.SigmaYawDeg = backSigma;
                r.YawSource = "backrest";
            }
            RobustExtents(footprint, bestYaw, out var lo0, out var hi0, out var lo1, out var hi1);

            // ---- 6. completion along the viewing direction
            var box = new UprightBox(Vector3.zero, bestYaw, Vector3.zero);
            var view = new Vector2(centroid.x - cam.Position.x, centroid.z - cam.Position.z);
            view = view.sqrMagnitude > 1e-6f ? view.normalized : new Vector2(0f, 1f);
            var right2 = new Vector2(box.Right.x, box.Right.z);
            var fwd2 = new Vector2(box.Forward.x, box.Forward.z);
            var depthIsForward = Mathf.Abs(Vector2.Dot(fwd2, view)) >= Mathf.Abs(Vector2.Dot(right2, view));
            var lateral = depthIsForward ? hi0 - lo0 : hi1 - lo1;
            var observedDepth = depthIsForward ? hi1 - lo1 : hi0 - lo0;
            // Unseen depth: at most the visible width and at most the category's typical short side
            // (a chair seen from the front is about as deep as it is wide; a table or sofa seen along its long
            // side is only as deep as its short side). Multi-view fusion replaces this guess with measurements.
            var expectedDepth = Mathf.Max(info.ShortSide.x, Mathf.Min(lateral, info.TypicalShort));

            var completion = Mathf.Max(0f, expectedDepth - observedDepth);
            if (observedDepth > 0.85f * expectedDepth)
            {
                completion = 0f;
            }

            if (completion > 0f)
            {
                // Extend the far side: the side whose axis direction points away from the camera.
                if (depthIsForward)
                {
                    if (Vector2.Dot(fwd2, view) >= 0f)
                    {
                        hi1 += completion;
                    }
                    else
                    {
                        lo1 -= completion;
                    }
                }
                else
                {
                    if (Vector2.Dot(right2, view) >= 0f)
                    {
                        hi0 += completion;
                    }
                    else
                    {
                        lo0 -= completion;
                    }
                }
            }

            // A wall behind the object caps the completed back: the box may not go through it.
            if (hasWall && completion > 0f)
            {
                for (var iter = 0; iter < 40 && completion > 0f; iter++)
                {
                    var worst = 0f;
                    for (var k = 0; k < 4; k++)
                    {
                        var rr = (k & 1) != 0 ? hi0 : lo0;
                        var ff = (k & 2) != 0 ? hi1 : lo1;
                        var corner = right2 * rr + fwd2 * ff;
                        worst = Mathf.Min(worst, PlaneDistanceXZ(new Vector3(corner.x, 0f, corner.y), wallPoint, wallNormal));
                    }

                    if (worst > -0.01f)
                    {
                        break;
                    }

                    var step = Mathf.Min(completion, 0.02f);
                    completion -= step;
                    if (depthIsForward)
                    {
                        if (Vector2.Dot(fwd2, view) >= 0f) hi1 -= step; else lo1 += step;
                    }
                    else
                    {
                        if (Vector2.Dot(right2, view) >= 0f) hi0 -= step; else lo0 += step;
                    }
                }
            }

            r.Completion = completion;
            var midR = 0.5f * (lo0 + hi0);
            var midF = 0.5f * (lo1 + hi1);
            var centreXZ = right2 * midR + fwd2 * midF;
            var size = new Vector3(Mathf.Max(0.05f, hi0 - lo0), height, Mathf.Max(0.05f, hi1 - lo1));
            r.Box = new UprightBox(new Vector3(centreXZ.x, bottom, centreXZ.y), bestYaw, size);

            // ---- 7. consistency + uncertainty
            var inside = 0;
            var inv = Quaternion.Inverse(r.Box.Rotation);
            foreach (var p in pts)
            {
                var l = inv * (p - r.Box.BottomCenter);
                if (Mathf.Abs(l.x) <= 0.5f * size.x + 0.03f && Mathf.Abs(l.z) <= 0.5f * size.z + 0.03f &&
                    l.y >= -0.03f && l.y <= size.y + 0.03f)
                {
                    inside++;
                }
            }

            r.InsideFraction = inside / (float)pts.Count;
            r.Distance = Vector3.Distance(cam.Position, r.Box.Center);
            r.SigmaDepth = NoiseAt(r.Distance);
            var nEff = Mathf.Max(1f, pts.Count / 8f); // depth noise is spatially correlated: ~8 points per independent sample
            r.SigmaPos = Mathf.Sqrt(r.SigmaDepth * r.SigmaDepth / nEff + 0.35f * completion * 0.35f * completion + 0.015f * 0.015f);
            var maxDim = Mathf.Max(0.1f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            var relNoise = 2f * r.SigmaDepth / maxDim;
            var relCompletion = 0.5f * completion / Mathf.Max(0.1f, expectedDepth);
            r.SigmaSizeRel = Mathf.Sqrt(relNoise * relNoise + relCompletion * relCompletion);
            r.Valid = true;
            return r;
        }

        private const float WallInlier = 0.035f;

        /// <summary>
        /// Heading from the backrest: principal axis of the points in the top 25 % of the object's height
        /// (horizontal plane). Returned in [0, 90°) like the footprint search; σ from the axis elongation.
        /// </summary>
        private static bool TryBackrestYaw(List<Vector3> pts, float bottom, float height, out float yaw, out float sigmaDeg)
        {
            yaw = 0f;
            sigmaDeg = 90f;
            var cutoff = bottom + 0.75f * height;
            float mx = 0f, mz = 0f;
            var n = 0;
            foreach (var p in pts)
            {
                if (p.y >= cutoff)
                {
                    mx += p.x;
                    mz += p.z;
                    n++;
                }
            }

            if (n < 8)
            {
                return false;
            }

            mx /= n;
            mz /= n;
            float sxx = 0f, szz = 0f, sxz = 0f;
            foreach (var p in pts)
            {
                if (p.y < cutoff)
                {
                    continue;
                }

                var dx = p.x - mx;
                var dz = p.z - mz;
                sxx += dx * dx;
                szz += dz * dz;
                sxz += dx * dz;
            }

            var tr = sxx + szz;
            var det = sxx * szz - sxz * sxz;
            var disc = Mathf.Sqrt(Mathf.Max(0f, tr * tr / 4f - det));
            var l1 = tr / 2f + disc;
            var l2 = Mathf.Max(1e-8f, tr / 2f - disc);
            if (l1 / l2 < 4f || Mathf.Sqrt(l1 / n) < 0.08f)
            {
                return false; // not a clear line (≥ 2:1 in std) or too short to be a backrest
            }

            // Principal direction (dx, dz) = backrest across the chair's width = box Right axis = (cos yaw, -sin yaw).
            var angle = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz); // atan2(dz, dx) of the major axis
            yaw = -angle;
            var deg = yaw * Mathf.Rad2Deg % 90f;
            if (deg < 0f)
            {
                deg += 90f;
            }

            yaw = deg * Mathf.Deg2Rad;
            sigmaDeg = Mathf.Clamp(Mathf.Atan(Mathf.Sqrt(l2 / l1)) * Mathf.Rad2Deg / Mathf.Sqrt(n / 4f), 2f, 20f);
            return true;
        }

        /// <summary>Signed horizontal distance of p from a vertical plane (positive on the camera side).</summary>
        private static float PlaneDistanceXZ(Vector3 p, Vector3 planePoint, Vector3 planeNormal) =>
            (p.x - planePoint.x) * planeNormal.x + (p.z - planePoint.z) * planeNormal.z;

        /// <summary>
        /// Background-wall model built from the object's SURROUNDINGS: depth rays just outside the 2D box (left and
        /// right, several heights) hit whatever is behind/beside the object. A hit on a vertical surface (from its
        /// depth normal) proposes a wall plane; it is accepted when other probes agree with it and it explains part
        /// of the object cluster. A backrest or cabinet front is never proposed: it ends at the silhouette.
        /// </summary>
        private bool FindWall(List<Vector3> pts, Rect viewport, in CameraModel cam, IDepthRaycaster depth,
            out Vector3 planePoint, out Vector3 planeNormal)
        {
            planePoint = default;
            planeNormal = default;
            m_probeHits.Clear();
            m_probeNormals.Clear();
            var margin = Mathf.Max(0.015f, 0.05f * viewport.width);
            var lowest = float.MaxValue;
            foreach (var p in pts)
            {
                lowest = Mathf.Min(lowest, p.y);
            }

            foreach (var u in new[] { viewport.xMin - margin, viewport.xMax + margin })
            {
                if (u < 0.01f || u > 0.99f)
                {
                    continue;
                }

                for (var j = 0; j < 5; j++)
                {
                    var v = viewport.yMin + (0.25f + 0.15f * j) * viewport.height;
                    if (depth.Raycast(cam.ViewportToRay(new Vector2(u, v)), MaxRange, out var hp, out var hn) &&
                        Mathf.Abs(hn.y) < 0.5f && hp.y > lowest + 0.05f)
                    {
                        m_probeHits.Add(hp);
                        m_probeNormals.Add(hn);
                    }
                }
            }

            if (m_probeHits.Count < 2)
            {
                return false;
            }

            var bestScore = 0;
            for (var c = 0; c < m_probeHits.Count; c++)
            {
                var n = new Vector3(m_probeNormals[c].x, 0f, m_probeNormals[c].z).normalized;
                if (n.sqrMagnitude < 0.5f)
                {
                    continue;
                }

                var p0 = m_probeHits[c];
                if ((cam.Position.x - p0.x) * n.x + (cam.Position.z - p0.z) * n.z < 0f)
                {
                    n = -n; // face the camera
                }

                var support = 0;
                foreach (var q in m_probeHits)
                {
                    if (Mathf.Abs(PlaneDistanceXZ(q, p0, n)) < 1.5f * WallInlier)
                    {
                        support++;
                    }
                }

                if (support < 2)
                {
                    continue;
                }

                var explained = 0;
                foreach (var p in pts)
                {
                    if (PlaneDistanceXZ(p, p0, n) < WallInlier)
                    {
                        explained++;
                    }
                }

                // A real background wall leaves the object standing in front of it. If removing the plane would
                // delete most of the cluster, the "wall" is something in front of / part of the object: reject.
                var remaining = pts.Count - explained;
                if (remaining < Mathf.Max(MinPoints, 0.35f * pts.Count))
                {
                    continue;
                }

                if (explained > bestScore)
                {
                    bestScore = explained;
                    planePoint = p0;
                    planeNormal = n;
                }
            }

            return bestScore >= Mathf.Max(4, 0.08f * pts.Count);
        }

        /// <summary>
        /// Height of the 2D box's top edge at the object's range (NaN when degenerate). Looking down, the top edge
        /// belongs to the FAR side of the object (back of a table top / backrest), looking up to the NEAR side,
        /// so the matching range is used; otherwise the estimate is biased high.
        /// </summary>
        private static float SilhouetteTop(Rect viewport, in CameraModel cam, float nearRange, float farRange)
        {
            var ray = cam.ViewportToRay(new Vector2(viewport.center.x, viewport.yMax));
            var dirH = new Vector2(ray.direction.x, ray.direction.z);
            var range = ray.direction.y < 0f ? farRange : nearRange;
            if (dirH.sqrMagnitude < 1e-6f)
            {
                return float.NaN;
            }

            var t = range / dirH.magnitude;
            return ray.origin.y + ray.direction.y * t;
        }

        private float LowestHorizontal()
        {
            var y = float.NaN;
            for (var k = 0; k < m_hits.Count; k++)
            {
                if (m_hitNormals[k].y > 0.85f && (float.IsNaN(y) || m_hits[k].y < y))
                {
                    y = m_hits[k].y;
                }
            }

            return y;
        }

        private void RobustExtents(List<Vector2> pts, float yaw, out float rLo, out float rHi, out float fLo, out float fHi)
        {
            var right = new Vector2(Mathf.Cos(yaw), -Mathf.Sin(yaw));
            var fwd = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
            m_tmp.Clear();
            m_tmp2.Clear();
            foreach (var p in pts)
            {
                m_tmp.Add(Vector2.Dot(p, right));
                m_tmp2.Add(Vector2.Dot(p, fwd));
            }

            m_tmp.Sort();
            m_tmp2.Sort();
            rLo = Percentile(m_tmp, 0.03f);
            rHi = Percentile(m_tmp, 0.97f);
            fLo = Percentile(m_tmp2, 0.03f);
            fHi = Percentile(m_tmp2, 0.97f);
        }

        private static float Percentile(List<float> sorted, float q)
        {
            if (sorted.Count == 0)
            {
                return 0f;
            }

            var x = q * (sorted.Count - 1);
            var i = Mathf.FloorToInt(x);
            var j = Mathf.Min(sorted.Count - 1, i + 1);
            return Mathf.Lerp(sorted[i], sorted[j], x - i);
        }
    }
}
