"""Hybrid mode, PC side: run MediaPipe Objectron (Chair) only inside each YOLO chair box.

For every frame and every YOLO chair detection in <session>/<yolo jsonl> (default: the pc_onnx*.jsonl file):
  crop = the YOLO box padded ×1.35, at the frame's aspect, at least 35 % of the frame height (same rule as
  HybridFuser.CropForBox in the app), resized to 640×480, Objectron in static-image mode on that crop.
Keypoints are mapped back to full-frame viewport coordinates (origin bottom-left). The keypoint set whose
centre lies inside the YOLO box with 2D IoU ≥ 0.25 is attached to that YOLO detection as "keypoints"/"lifted".

Writes <session>/pc_hybrid_chair.jsonl: the YOLO detections (fit: null) plus keypoints where Objectron found
the chair. Replay: Tools/offline/run_tests.sh replay <session> --detections pc_hybrid_chair.jsonl --mode hybrid
"""
import json, os, sys, time
import cv2
import mediapipe as mp

PAD, MIN_FRAC, CROP_W, CROP_H = 1.35, 0.35, 640, 480


def crop_for_box(box, W, H, aspect, PAD=PAD, MIN_FRAC=MIN_FRAC):
    x0, y0, x1, y1 = box
    w_px, h_px = (x1 - x0) * W * PAD, (y1 - y0) * H * PAD
    if w_px / h_px < aspect:
        w_px = h_px * aspect
    else:
        h_px = w_px / aspect
    if h_px < H * MIN_FRAC:
        h_px = H * MIN_FRAC; w_px = h_px * aspect
    s = min(1.0, W / w_px, H / h_px)
    w, h = w_px * s / W, h_px * s / H
    cx = min(max((x0 + x1) / 2, w / 2), 1 - w / 2)
    cy = min(max((y0 + y1) / 2, h / 2), 1 - h / 2)
    return cx - w / 2, cy - h / 2, w, h


def iou(a, b):
    ix = max(0, min(a[2], b[2]) - max(a[0], b[0])); iy = max(0, min(a[3], b[3]) - max(a[1], b[1]))
    i = ix * iy; u = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - i
    return i / u if u > 0 else 0


def main(session, yolo_name=None, pad=PAD, min_frac=MIN_FRAC, min_conf=0.3, suffix=""):
    if yolo_name is None:
        yolo_name = next(f for f in os.listdir(session) if f.startswith("pc_onnx") and f.endswith(".jsonl"))
    frames = {json.loads(l)["frame_id"]: json.loads(l) for l in open(os.path.join(session, "frames.jsonl"))}
    obj = mp.solutions.objectron.Objectron(static_image_mode=True, max_num_objects=2,
                                           min_detection_confidence=min_conf, model_name="Chair")
    out = os.path.join(session, f"pc_hybrid_chair{suffix}.jsonl")
    n = nd = nk = 0; t0 = time.time()
    with open(out, "w") as w:
        for line in open(os.path.join(session, yolo_name)):
            r = json.loads(line)
            f = frames.get(r["frame_id"])
            img = cv2.imread(os.path.join(session, f["file"])) if f else None
            dets = []
            for d in r["detections"]:
                if d.get("category") != "Chair":
                    continue
                d = dict(d); d["fit"] = None
                nd += 1
                if img is not None:
                    H, W = img.shape[:2]
                    cx, cy, cw, ch = crop_for_box(d["viewport"], W, H, CROP_W / CROP_H, pad, min_frac)
                    # viewport y is bottom-up, image rows top-down
                    r0, r1 = int(round((1 - cy - ch) * H)), int(round((1 - cy) * H))
                    c0, c1 = int(round(cx * W)), int(round((cx + cw) * W))
                    crop = cv2.resize(img[r0:r1, c0:c1], (CROP_W, CROP_H), interpolation=cv2.INTER_AREA)
                    res = obj.process(cv2.cvtColor(crop, cv2.COLOR_BGR2RGB))
                    best = None
                    for o in (res.detected_objects or []):
                        kp = [[cx + lm.x * cw, cy + (1 - lm.y) * ch] for lm in o.landmarks_2d.landmark]
                        xs = [p[0] for p in kp[1:]]; ys = [p[1] for p in kp[1:]]
                        kb = [min(xs), min(ys), max(xs), max(ys)]
                        v = d["viewport"]; c = ((kb[0] + kb[2]) / 2, (kb[1] + kb[3]) / 2)
                        if not (v[0] <= c[0] <= v[2] and v[1] <= c[1] <= v[3]):
                            continue
                        s = iou(kb, v)
                        if s >= 0.25 and (best is None or s > best[0]):
                            best = (s, kp, [[lm.x, lm.y, -lm.z] for lm in o.landmarks_3d.landmark])
                    if best:
                        d["keypoints"], d["lifted"], d["keypoint_iou"] = best[1], best[2], round(best[0], 3)
                        d["crop"] = [cx, cy, cw, ch]
                        nk += 1
                dets.append(d)
            r["detections"] = dets; r["source"] = "pc:hybrid_chair"
            w.write(json.dumps(r) + "\n"); n += 1
    print(json.dumps({"frames": n, "yolo_chairs": nd, "with_keypoints": nk, "seconds": round(time.time() - t0, 1), "out": out}))


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("session"); ap.add_argument("--yolo")
    ap.add_argument("--pad", type=float, default=PAD); ap.add_argument("--min-frac", type=float, default=MIN_FRAC)
    ap.add_argument("--min-conf", type=float, default=0.3); ap.add_argument("--suffix", default="")
    a = ap.parse_args()
    main(a.session, a.yolo, a.pad, a.min_frac, a.min_conf, a.suffix)
