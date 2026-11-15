using UnityEngine;

namespace QuestDetect3D
{
    /// <summary>Keypoint evidence for one object: a BoxSolver fit of Objectron keypoints found inside a YOLO box.</summary>
    public struct KeypointEvidence
    {
        public UprightBox Box;
        /// <summary>ObservationQuality of the keypoint fit, [0,1].</summary>
        public float Quality;
        public float Time;
    }

    /// <summary>Result of fusing a YOLO+depth fit with (optional) keypoint evidence, checked against depth.</summary>
    public struct HybridResult
    {
        public UprightBox Box;
        public bool HasFront;
        /// <summary>Front direction (yaw, radians, full 360°) from the keypoints, snapped to the depth box's quadrant.</summary>
        public float FrontYaw;
        /// <summary>Depth checks, each in [0,1]: bottom on the floor/support, no free space seen through the box, depth vs keypoint agreement.</summary>
        public float Support;
        public float FreeSpace;
        public float Agreement;
        /// <summary>Combined verification score in [0,1]; multiplies the observation quality by (0.4 + 0.6·V).</summary>
        public float Verification;
        public int FreeSpaceRays;
        public int FreeSpaceViolations;

        public override string ToString() =>
            $"hybrid front={(HasFront ? $"{FrontYaw * Mathf.Rad2Deg:F0}°" : "-")} sup={Support:F2} free={FreeSpace:F2} " +
            $"agree={Agreement:F2} V={Verification:F2} ({FreeSpaceViolations}/{FreeSpaceRays} see-through)";
    }

    /// <summary>
    /// Hybrid detector logic (pure, shared by the app and the offline replay):
    ///   YOLO finds the object (recall) → DepthBoxFitter gives a metric box (position, shape, height, yaw mod 90°);
    ///   Objectron keypoints, run on a crop around the YOLO box, give the front (heading) and a second box;
    ///   depth verifies the result (support, free-space violations, agreement) and scales its confidence.
    /// </summary>
    public sealed class HybridFuser
    {
        /// <summary>Keypoint yaw must agree with the depth box modulo 90° within this to give the front.</summary>
        public float ShapeAgreeDeg = 25f;
        /// <summary>Keypoint centre must be this close to the depth box centre (m, plus 0.5 × footprint).</summary>
        public float CentreGate = 0.20f;
        /// <summary>Weight of the keypoint box in the fused centre / footprint size (0 = depth only).</summary>
        public float KeypointWeight = 0.35f;
        /// <summary>How far the heading moves from the depth yaw to the keypoint yaw (× quality × agreement).</summary>
        public float YawWeight = 0f;

        private readonly System.Random m_rng = new System.Random(11);

        /// <summary>Crop (viewport rect) around a 2D box: padded, with the given pixel aspect, shifted inside the image.</summary>
        public static Rect CropForBox(Rect box, Vector2Int image, float aspect, float pad = 1.35f, float minFraction = 0.35f)
        {
            var wPx = box.width * image.x * pad;
            var hPx = box.height * image.y * pad;
            if (wPx / hPx < aspect)
            {
                wPx = hPx * aspect;
            }
            else
            {
                hPx = wPx / aspect;
            }

            // Not smaller than a fraction of the frame (tiny crops starve Objectron of context), not larger than it.
            var minH = image.y * minFraction;
            if (hPx < minH)
            {
                hPx = minH;
                wPx = hPx * aspect;
            }

            var scale = Mathf.Min(1f, Mathf.Min(image.x / wPx, image.y / hPx));
            wPx *= scale;
            hPx *= scale;
            var w = wPx / image.x;
            var h = hPx / image.y;
            var cx = Mathf.Clamp(box.center.x, w * 0.5f, 1f - w * 0.5f);
            var cy = Mathf.Clamp(box.center.y, h * 0.5f, 1f - h * 0.5f);
            return new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        }

        /// <summary>Crop viewport → full-frame viewport.</summary>
        public static Vector2 FromCrop(Rect crop, Vector2 v) => new Vector2(crop.x + v.x * crop.width, crop.y + v.y * crop.height);

        /// <summary>2D bounding box of the visible keypoints (full-frame viewport).</summary>
        public static Rect KeypointBounds(KeypointDetection det)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (var i = 1; i < KeypointDetection.KeypointCount; i++)
            {
                if (!det.Has[i])
                {
                    continue;
                }

                var p = det.Viewport[i];
                x0 = Mathf.Min(x0, p.x);
                y0 = Mathf.Min(y0, p.y);
                x1 = Mathf.Max(x1, p.x);
                y1 = Mathf.Max(y1, p.y);
            }

            return x1 > x0 ? Rect.MinMaxRect(x0, y0, x1, y1) : default;
        }

        /// <summary>Keypoints belong to the YOLO box: centre inside it and 2D IoU ≥ minIou.</summary>
        public static bool BelongsTo(Rect keypointBounds, Rect yoloBox, float minIou = 0.25f)
        {
            if (keypointBounds.width <= 0f || !(keypointBounds.center.x >= yoloBox.xMin && keypointBounds.center.x <= yoloBox.xMax && keypointBounds.center.y >= yoloBox.yMin && keypointBounds.center.y <= yoloBox.yMax))
            {
                return false;
            }

            var ix = Mathf.Max(0f, Mathf.Min(keypointBounds.xMax, yoloBox.xMax) - Mathf.Max(keypointBounds.xMin, yoloBox.xMin));
            var iy = Mathf.Max(0f, Mathf.Min(keypointBounds.yMax, yoloBox.yMax) - Mathf.Max(keypointBounds.yMin, yoloBox.yMin));
            var inter = ix * iy;
            var union = keypointBounds.width * keypointBounds.height + yoloBox.width * yoloBox.height - inter;
            return union > 0f && inter / union >= minIou;
        }

        /// <summary>The keypoint box describes the same object as the depth box (centre gate).</summary>
        public bool SameObject(in UprightBox depthBox, in UprightBox kpBox)
        {
            var d = Horizontal(depthBox.BottomCenter - kpBox.BottomCenter).magnitude;
            return d < CentreGate + 0.5f * Mathf.Max(depthBox.Size.x, depthBox.Size.z);
        }

        public HybridResult Fuse(in DepthFitResult fit, KeypointEvidence? keypoints, in CameraModel cam, IDepthRaycaster depth, float floorY)
        {
            var box = fit.Box;
            var r = new HybridResult { Agreement = 0.5f };

            if (keypoints.HasValue && SameObject(box, keypoints.Value.Box))
            {
                var kp = keypoints.Value.Box;
                // Depth box shape is defined modulo 90°: find the quadrant k with yaw + k·90° closest to the keypoint front.
                var bestK = 0;
                var bestD = float.MaxValue;
                for (var k = 0; k < 4; k++)
                {
                    var d = Mathf.Abs(Mathf.DeltaAngle(kp.Yaw * Mathf.Rad2Deg, box.Yaw * Mathf.Rad2Deg + 90f * k));
                    if (d < bestD)
                    {
                        bestD = d;
                        bestK = k;
                    }
                }

                var yawAgree = Mathf.Clamp01(1f - bestD / ShapeAgreeDeg);
                var iou = BevIoU(box, kp);
                r.Agreement = Mathf.Clamp01(0.5f * yawAgree + 0.5f * Mathf.Clamp01(iou / 0.5f));
                if (bestD < ShapeAgreeDeg)
                {
                    r.HasFront = true;
                    r.FrontYaw = box.Yaw + bestK * 0.5f * Mathf.PI;

                    // Re-express the depth box in the front frame, then blend centre and footprint with the keypoint box
                    // (keypoints see the front/back extent that the depth fit has to complete).
                    var size = (bestK & 1) == 1 ? new Vector3(box.Size.z, box.Size.y, box.Size.x) : box.Size;
                    var w = KeypointWeight * Mathf.Clamp01(keypoints.Value.Quality / 0.6f) * r.Agreement;
                    var centre = Vector3.Lerp(box.BottomCenter, new Vector3(kp.BottomCenter.x, box.BottomCenter.y, kp.BottomCenter.z), w);
                    var kpSize = AlignedTo(kp, r.FrontYaw);
                    size.x = Mathf.Lerp(size.x, kpSize.x, w);
                    size.z = Mathf.Lerp(size.z, kpSize.z, w);
                    // Height: depth (floor-anchored) unless the keypoints see a clearly taller object (thin top rails).
                    if (kpSize.y > size.y + 0.04f)
                    {
                        size.y = Mathf.Lerp(size.y, kpSize.y, 0.5f * w / Mathf.Max(1e-3f, KeypointWeight));
                    }

                    // Heading: depth gives the shape axis, keypoints refine it (they see the front edge directly).
                    var wy = YawWeight * Mathf.Clamp01(keypoints.Value.Quality / 0.6f) * r.Agreement;
                    r.FrontYaw += wy * Mathf.DeltaAngle(r.FrontYaw * Mathf.Rad2Deg, kp.Yaw * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    box = new UprightBox(centre, r.FrontYaw, size);
                }
            }

            r.Box = box;

            // Support: bottom on the floor (or the support surface the fitter used).
            r.Support = fit.UsedFloor || float.IsNaN(floorY)
                ? 1f
                : Mathf.Clamp01(1f - (Mathf.Abs(box.BottomCenter.y - floorY) - 0.05f) / 0.25f);

            // Free space: rays aimed at the box's solid core (seat/backrest region) that pass clearly behind the box
            // mean the box claims space the depth sensor sees as empty.
            FreeSpaceCheck(box, cam, depth, ref r);
            var ratio = r.FreeSpaceRays > 0 ? r.FreeSpaceViolations / (float)r.FreeSpaceRays : 0f;
            r.FreeSpace = r.FreeSpaceRays >= 4 ? Mathf.Clamp01(1f - (ratio - 0.35f) / 0.45f) : 0.7f;

            r.Verification = Mathf.Clamp01(0.35f * r.Support + 0.40f * r.FreeSpace + 0.25f * r.Agreement);
            return r;
        }

        private void FreeSpaceCheck(in UprightBox box, in CameraModel cam, IDepthRaycaster depth, ref HybridResult r)
        {
            if (depth == null || !depth.IsAvailable)
            {
                return;
            }

            var right = box.Right;
            var fwd = box.Forward;
            for (var i = 0; i < 16; i++)
            {
                var a = (float)m_rng.NextDouble() - 0.5f;
                var b = (float)m_rng.NextDouble() - 0.5f;
                var h = 0.3f + 0.45f * (float)m_rng.NextDouble();
                var target = box.BottomCenter + right * (a * 0.5f * box.Size.x) + fwd * (b * 0.5f * box.Size.z) + Vector3.up * (h * box.Size.y);
                var dir = target - cam.Position;
                var dist = dir.magnitude;
                if (dist < 0.3f || !cam.WorldToViewport(target, out var vp) || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f)
                {
                    continue;
                }

                if (!depth.Raycast(new Ray(cam.Position, dir / dist), dist + 3f, out var hit, out _))
                {
                    continue;
                }

                r.FreeSpaceRays++;
                // Behind the whole box along this ray → the ray crossed the box without hitting anything.
                var exit = dist + 0.5f * Mathf.Max(box.Size.x, box.Size.z) + 0.10f;
                if (Vector3.Distance(cam.Position, hit) > exit)
                {
                    r.FreeSpaceViolations++;
                }
            }
        }

        private static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>Size of box b expressed in the frame of yaw (swap width/depth when b is rotated by ~90°).</summary>
        private static Vector3 AlignedTo(in UprightBox b, float yaw)
        {
            var d = Mathf.Abs(Mathf.DeltaAngle(b.Yaw * Mathf.Rad2Deg, yaw * Mathf.Rad2Deg));
            return d > 45f && d < 135f ? new Vector3(b.Size.z, b.Size.y, b.Size.x) : b.Size;
        }

        /// <summary>Bird's-eye IoU, sampled (boxes are small; 400 samples is plenty for a score).</summary>
        public static float BevIoU(in UprightBox a, in UprightBox b)
        {
            const int n = 20;
            var inter = 0;
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    var p = a.BottomCenter + a.Right * (((i + 0.5f) / n - 0.5f) * a.Size.x) + a.Forward * (((j + 0.5f) / n - 0.5f) * a.Size.z);
                    var q = p - b.BottomCenter;
                    if (Mathf.Abs(Vector3.Dot(q, b.Right)) <= 0.5f * b.Size.x && Mathf.Abs(Vector3.Dot(q, b.Forward)) <= 0.5f * b.Size.z)
                    {
                        inter++;
                    }
                }
            }

            var areaA = a.Size.x * a.Size.z;
            var areaB = b.Size.x * b.Size.z;
            var interArea = areaA * inter / (n * n);
            var union = areaA + areaB - interArea;
            return union > 1e-6f ? interArea / union : 0f;
        }
    }
}
