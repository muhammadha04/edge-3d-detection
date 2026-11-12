#!/usr/bin/env python3
"""Compare detector output with the in-headset ground truth.

Inputs: one or more recorded sessions, and any detections.jsonl-style files in them:
  detections.jsonl         the device (YOLO + depth fits + tracker, recorded live with "Detector: ON")
  pc_*.jsonl               run_models.py (2D only, unless a plugin returns 3D boxes)
  replay_*.jsonl           Tools/offline/run_tests.sh replay (real fitter + tracker on recorded depth)

Three evaluation units per file:
  fits    per-frame 3D boxes from the depth fitter          (score = detector score, sigma = fit sigma_pos)
  tracks  tracker estimates in view                          (score = track confidence, sigma = position_sigma)
  2d      2D boxes vs the projected ground-truth box         (score = detector score)

Metrics:
  centre error (3D, cm), yaw error (symmetry-aware with the catalog symmetry, and modulo 90° = box shape only),
  size error (cm, after aligning width/depth), 3D IoU, AP@0.25 / AP@0.5 (3D) and AP2D@0.5, all stratified by camera
  distance (0–1, 1–2, 2–3, 3+ m); confidence vs error: Spearman(confidence, centre error), Spearman(sigma, centre
  error), reliability diagram + ECE with "correct" = IoU ≥ 0.25.

Outputs (default <session>/eval/, or --out): report.md, summary.csv, pairs.csv, plots/*.png

    python Tools/annotation/evaluate.py Data/QD3D/sessions/<id>
    python Tools/annotation/evaluate.py Data/QD3D --out Data/eval            # every session together
    python Tools/annotation/evaluate.py <session> --detections detections.jsonl --detections replay_detections.jsonl

How much data: per object ≥ 3 blind M1 repetitions (gt_sigma), and per condition ≥ 2 walk-arounds of 2 min at 5 Hz
(~600 frames) so every distance bin holds ≥ 50 matched frames; report bins with fewer as "n/a".
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import sys
from collections import defaultdict
from pathlib import Path
from typing import Dict, List, Optional

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.geometry import Camera, RoomFrame, UprightBox, align_size, iou_3d, symmetry_period, yaw_error  # noqa: E402
from qd3d.session import Session, find_sessions  # noqa: E402
from qd3d.stats import average_precision, reliability, spearman  # noqa: E402

DIST_BINS = [(0.0, 1.0), (1.0, 2.0), (2.0, 3.0), (3.0, 99.0)]
MATCH_IOU = 0.05      # a 3D prediction is paired with a GT box (for errors) above this IoU …
MATCH_CENTRE = 0.6    # … or when its centre is closer than this (m)
MIN_BIN = 20          # fewer samples than this → "n/a" in the report
MAX_GT_DISTANCE = 5.0


def bin_name(d: float) -> str:
    for lo, hi in DIST_BINS:
        if lo <= d < hi:
            return f"{lo:.0f}-{hi:.0f} m" if hi < 99 else f"{lo:.0f}+ m"
    return "?"


def gt_in_view(cam: Camera, box: UprightBox) -> bool:
    """GT counts for a frame when its centre is in front of the camera, inside the image and within 5 m."""
    vp, ok = cam.world_to_viewport(box.center[None])
    d = float(np.linalg.norm(box.center - cam.position))
    return bool(ok[0]) and 0 <= vp[0, 0] <= 1 and 0 <= vp[0, 1] <= 1 and d < MAX_GT_DISTANCE


def gt_bbox_viewport(cam: Camera, box: UprightBox) -> Optional[List[float]]:
    """Viewport rect [xmin, ymin, xmax, ymax] of the projected GT corners (clipped to 0..1), None if behind."""
    local = cam.world_to_local(box.corners())
    front = local[:, 2] >= 0.02
    if not front.any():
        return None
    vp, _ = cam.local_to_viewport(local[front])
    lo, hi = np.clip(vp.min(axis=0), 0, 1), np.clip(vp.max(axis=0), 0, 1)
    if hi[0] - lo[0] < 1e-3 or hi[1] - lo[1] < 1e-3:
        return None
    return [float(lo[0]), float(lo[1]), float(hi[0]), float(hi[1])]


def iou_2d(a, b) -> float:
    iw = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    ih = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = iw * ih
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def same_category(pred: str, gt: str) -> bool:
    return str(pred).lower() == str(gt).lower()


def frame_room(sess: Session, line: dict) -> Optional[RoomFrame]:
    if line.get("room_frame"):
        return RoomFrame.from_dict(line["room_frame"])
    rec = sess.frame(line.get("frame_id")) if str(line.get("source", "")).startswith("pc:") else sess.nearest_frame(line.get("t_realtime", 0))
    return sess.room_frame(rec)


def evaluate_file(sess: Session, path: Path) -> Dict[str, list]:
    """Per-unit lists of predictions {score, sigma, tp25, tp50, iou, frame, ...} and GT counts per distance bin."""
    lines = sess.detections(str(path))
    preds: Dict[str, list] = {"fits": [], "tracks": [], "2d": []}
    gt_count: Dict[str, Dict[str, int]] = {u: defaultdict(int) for u in preds}
    pairs: List[dict] = []
    for line in lines:
        cam = Camera.from_frame(line)
        room = frame_room(sess, line)
        if room is None:
            continue
        gts = []
        for g in sess.root.accepted_boxes(sess.room_id):
            bw = room.box_to_world(g.box_room)
            if gt_in_view(cam, bw):
                gts.append((g, bw, float(np.linalg.norm(bw.center - cam.position))))
        for unit in preds:
            for _, _, d in gts:
                gt_count[unit][bin_name(d)] += 1
                gt_count[unit]["all"] += 1

        units = {"fits": [], "tracks": [], "2d": []}
        for det in line.get("detections", []):
            units["2d"].append({"category": det.get("category"), "score": float(det.get("score", 0)), "viewport": det.get("viewport")})
            fit = det.get("fit") or {}
            box = fit.get("box") or det.get("box")
            if box and fit.get("valid", True):
                units["fits"].append({"category": det.get("category"), "score": float(det.get("score", 0)),
                                      "sigma": fit.get("sigma_pos"), "box": UprightBox.from_dict(box)})
        for t in line.get("tracks", []):
            b = UprightBox.from_dict(t["box"])
            if gt_in_view(cam, b):
                units["tracks"].append({"category": t.get("category"), "score": float(t.get("confidence", 0)),
                                        "sigma": t.get("position_sigma"), "box": b, "state": t.get("state"), "track_id": t.get("id")})

        for unit, items in units.items():
            items.sort(key=lambda p: -p["score"])
            used = set()
            for p in items:
                best, best_iou, best_d = None, 0.0, math.inf
                for k, (g, bw, dist) in enumerate(gts):
                    if not same_category(p["category"], g.category):
                        continue
                    if unit == "2d":
                        gb = gt_bbox_viewport(cam, bw)
                        v = iou_2d(p["viewport"], gb) if gb and p["viewport"] else 0.0
                        cd = 0.0
                    else:
                        v = iou_3d(p["box"], bw)
                        cd = float(np.linalg.norm(p["box"].center - bw.center))
                    if k not in used and (v > best_iou or (v == best_iou and cd < best_d)):
                        best, best_iou, best_d = k, v, cd
                rec = {"unit": unit, "frame_id": line.get("frame_id"), "score": p["score"], "sigma": p.get("sigma"),
                       "iou": best_iou, "tp25": False, "tp50": False, "distance": None}
                if best is not None and (best_iou >= MATCH_IOU or (unit != "2d" and best_d < MATCH_CENTRE)):
                    g, bw, dist = gts[best]
                    rec["distance"] = dist
                    rec["tp25"] = best_iou >= 0.25
                    rec["tp50"] = best_iou >= 0.5
                    if rec["tp25"]:
                        used.add(best)
                    if unit != "2d":
                        b = p["box"]
                        period = symmetry_period(g.symmetry)
                        size = align_size(b, bw)
                        err = {
                            "session": sess.id, "source": path.name, "unit": unit, "frame_id": line.get("frame_id"),
                            "object_id": g.object_id, "distance": dist, "score": p["score"], "sigma": p.get("sigma"),
                            "iou": best_iou, "centre_cm": best_d * 100,
                            "centre_xz_cm": float(np.linalg.norm((b.center - bw.center)[[0, 2]]) * 100),
                            "yaw_deg": math.degrees(yaw_error(b.yaw, bw.yaw, period)),
                            "yaw90_deg": math.degrees(yaw_error(b.yaw, bw.yaw, math.pi / 2)),
                            "size_w_cm": (size[0] - bw.size[0]) * 100, "size_h_cm": (size[1] - bw.size[1]) * 100,
                            "size_d_cm": (size[2] - bw.size[2]) * 100,
                            "size_abs_cm": float(np.mean(np.abs(size - bw.size)) * 100),
                            "gt_sigma_cm": g.sigma.get("centre_cm"),
                        }
                        pairs.append(err)
                        rec["centre_cm"] = err["centre_cm"]
                preds[unit].append(rec)
    return {"preds": preds, "gt": gt_count, "pairs": pairs}


def summarise(name: str, result: Dict, plots: Optional[Path]) -> List[dict]:
    rows = []
    for unit, preds in result["preds"].items():
        gt = result["gt"][unit]
        if not preds and not gt.get("all"):
            continue
        row = {"source": name, "unit": unit, "frames_gt": gt.get("all", 0), "predictions": len(preds)}
        scores = [p["score"] for p in preds]
        if unit == "2d":
            row["AP2D@0.5"] = average_precision(scores, [p["tp50"] for p in preds], gt.get("all", 0))
        else:
            row["AP@0.25"] = average_precision(scores, [p["tp25"] for p in preds], gt.get("all", 0))
            row["AP@0.5"] = average_precision(scores, [p["tp50"] for p in preds], gt.get("all", 0))
            for lo, hi in DIST_BINS:
                b = bin_name(lo + 1e-3)
                sel = [p for p in preds if p.get("distance") is not None and bin_name(p["distance"]) == b]
                n_gt = gt.get(b, 0)
                row[f"AP@0.25 {b}"] = average_precision([p["score"] for p in sel], [p["tp25"] for p in sel], n_gt) if n_gt >= MIN_BIN else float("nan")
            pairs = [p for p in result["pairs"] if p["unit"] == unit]
            if pairs:
                for k in ("centre_cm", "yaw_deg", "yaw90_deg", "size_abs_cm", "iou"):
                    v = np.array([p[k] for p in pairs], float)
                    row[f"{k} median"] = float(np.median(v))
                    row[f"{k} p90"] = float(np.percentile(v, 90))
                for lo, hi in DIST_BINS:
                    b = bin_name(lo + 1e-3)
                    v = np.array([p["centre_cm"] for p in pairs if bin_name(p["distance"]) == b], float)
                    row[f"centre_cm median {b}"] = float(np.median(v)) if len(v) >= MIN_BIN else float("nan")
                row["spearman(conf, centre err)"] = spearman([p["score"] for p in pairs], [p["centre_cm"] for p in pairs])
                sig = [(p["sigma"], p["centre_cm"]) for p in pairs if p["sigma"] is not None]
                row["spearman(sigma, centre err)"] = spearman([s for s, _ in sig], [e for _, e in sig]) if sig else float("nan")
            rel = reliability(scores, [p["tp25"] for p in preds])
            row["ECE (IoU>=0.25)"] = rel["ece"]
            if plots is not None and preds:
                plot(name, unit, preds, pairs, rel, plots)
        rows.append(row)
    return rows


def plot(name: str, unit: str, preds: list, pairs: list, rel: dict, out: Path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    out.mkdir(parents=True, exist_ok=True)
    stem = f"{Path(name).stem}_{unit}"
    fig, ax = plt.subplots(1, 3, figsize=(15, 4.2))
    bins = [b for b in rel["bins"] if b["n"] > 0]
    ax[0].plot([0, 1], [0, 1], "k--", lw=1)
    ax[0].bar([b["conf"] for b in bins], [b["acc"] for b in bins], width=0.08, alpha=0.7)
    ax[0].set(title=f"Reliability (ECE {rel['ece']:.3f})", xlabel="confidence", ylabel="fraction IoU ≥ 0.25", xlim=(0, 1), ylim=(0, 1))
    if pairs:
        ax[1].scatter([p["distance"] for p in pairs], [p["centre_cm"] for p in pairs], s=6, alpha=0.5)
        ax[1].set(title="Centre error vs distance", xlabel="distance (m)", ylabel="centre error (cm)")
        ax[2].scatter([p["score"] for p in pairs], [p["centre_cm"] for p in pairs], s=6, alpha=0.5)
        ax[2].set(title="Confidence vs error", xlabel="confidence", ylabel="centre error (cm)")
    fig.suptitle(f"{name} · {unit}")
    fig.tight_layout()
    fig.savefig(out / f"{stem}.png", dpi=110)
    plt.close(fig)


def fmt(v) -> str:
    if isinstance(v, float):
        return "n/a" if math.isnan(v) else f"{v:.3f}" if abs(v) < 10 else f"{v:.1f}"
    return str(v)


def run(session_paths: List[Path], detection_names: List[str], out: Path, plots: bool = True) -> List[dict]:
    out.mkdir(parents=True, exist_ok=True)
    merged: Dict[str, Dict] = {}
    all_pairs: List[dict] = []
    for sp in session_paths:
        sess = Session(sp)
        names = detection_names or sorted({p.name for p in sp.glob("*.jsonl")} - {"frames.jsonl", "depth.jsonl", "events.jsonl"})
        for name in names:
            path = sp / name
            if not path.exists():
                continue
            r = evaluate_file(sess, path)
            m = merged.setdefault(name, {"preds": {u: [] for u in r["preds"]}, "gt": {u: defaultdict(int) for u in r["gt"]}, "pairs": []})
            for u in r["preds"]:
                m["preds"][u].extend(r["preds"][u])
                for k, v in r["gt"][u].items():
                    m["gt"][u][k] += v
            m["pairs"].extend(r["pairs"])
            all_pairs.extend(r["pairs"])
    rows = []
    for name, r in merged.items():
        rows.extend(summarise(name, r, out / "plots" if plots else None))
    keys = []
    for r in rows:
        keys += [k for k in r if k not in keys]
    with open(out / "summary.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=keys)
        w.writeheader()
        w.writerows(rows)
    if all_pairs:
        with open(out / "pairs.csv", "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(all_pairs[0].keys()))
            w.writeheader()
            w.writerows(all_pairs)
    md = [f"# QD3D evaluation", "", f"Sessions: {', '.join(p.name for p in session_paths)}", "",
          "Ground truth = accepted in-headset boxes (verify ≠ redo). 'n/a' = fewer than "
          f"{MIN_BIN} samples. Units: fits = per-frame depth boxes, tracks = tracker estimates in view, 2d = image boxes.", ""]
    main_keys = ["source", "unit", "frames_gt", "predictions", "AP@0.25", "AP@0.5", "AP2D@0.5", "centre_cm median", "centre_cm p90",
                 "yaw_deg median", "yaw90_deg median", "size_abs_cm median", "iou median", "spearman(conf, centre err)",
                 "spearman(sigma, centre err)", "ECE (IoU>=0.25)"]
    md.append("| " + " | ".join(main_keys) + " |")
    md.append("|" + "---|" * len(main_keys))
    for r in rows:
        md.append("| " + " | ".join(fmt(r.get(k, "")) for k in main_keys) + " |")
    md += ["", "## By distance", ""]
    dist_keys = ["source", "unit"] + [f"AP@0.25 {bin_name(lo + 1e-3)}" for lo, _ in DIST_BINS] + [f"centre_cm median {bin_name(lo + 1e-3)}" for lo, _ in DIST_BINS]
    md.append("| " + " | ".join(dist_keys) + " |")
    md.append("|" + "---|" * len(dist_keys))
    for r in rows:
        if r["unit"] != "2d":
            md.append("| " + " | ".join(fmt(r.get(k, "")) for k in dist_keys) + " |")
    if plots:
        md += ["", "## Plots", ""] + [f"![{p.stem}](plots/{p.name})" for p in sorted((out / "plots").glob("*.png"))]
    (out / "report.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    return rows


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("sessions", nargs="+")
    ap.add_argument("--detections", action="append", default=[], help="file name inside each session (repeatable; default: all *.jsonl outputs)")
    ap.add_argument("--out", help="output folder (default <session>/eval for one session, else ./eval)")
    ap.add_argument("--no-plots", action="store_true")
    args = ap.parse_args(argv)
    sessions = find_sessions(args.sessions)
    if not sessions:
        ap.error("no sessions found")
    out = Path(args.out) if args.out else (sessions[0] / "eval" if len(sessions) == 1 else Path("eval"))
    rows = run(sessions, args.detections, out, not args.no_plots)
    for r in rows:
        print(f"{r['source']:<40} {r['unit']:<6} " + "  ".join(f"{k}={fmt(r[k])}" for k in ("AP@0.25", "AP@0.5", "AP2D@0.5", "centre_cm median") if k in r))
    print(f"report: {out / 'report.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
