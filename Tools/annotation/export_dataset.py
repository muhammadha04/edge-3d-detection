#!/usr/bin/env python3
"""Auto-label every recorded frame from the ground truth (annotations in the room frame).

For every frame and every accepted ground-truth object:
  * 2D box (pixels, clipped to the image) of the projected 8 corners,
  * the 9 keypoints (0 = centre, 1..8 = corners in UprightBox order: index-1 bits 1 = +x right, 2 = top, 4 = +z front),
  * per-keypoint visibility: 0 = outside the image / behind the camera, 1 = inside but occluded according to the
    recorded depth (depth closer than the keypoint by > 5 cm + 3 %), 2 = visible (or no depth to tell),
  * distance camera → box centre, the metric box in the OpenCV camera frame (x right, y down, z forward).

Writes, split by SESSION (never by frame):
  (a) coco/<split>.json          COCO keypoints (category per object category; keypoints 9 × [x, y, v])
  (b) yolo/images|labels/<split> YOLO-pose txt: class cx cy w h  x1 y1 v1 … x9 y9 v9 (normalised) + dataset.yaml
  (c) raw.jsonl                  everything above per (frame, object), incl. world/room/camera boxes

    python Tools/annotation/export_dataset.py Data/QD3D --out Data/dataset
    python Tools/annotation/export_dataset.py Data/QD3D --out Data/dataset --val <session_id> --test <session_id>
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import shutil
import sys
from pathlib import Path
from typing import Dict, List

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.geometry import BOX_EDGES, FLIP_IDX, KEYPOINT_NAMES, Camera, UprightBox, world_to_depth_pixel  # noqa: E402
from qd3d.session import Session, find_sessions  # noqa: E402

OCCLUSION_ABS = 0.05
OCCLUSION_REL = 0.03
MIN_BOX_PX = 8


def keypoint_visibility(sess: Session, rec: dict, cam: Camera, kps: np.ndarray, depth_cache: Dict):
    """(pixels 9×2, visibility 9, occlusion flags list) for the 9 world keypoints."""
    W, H = cam.resolution
    px, in_front = cam.world_to_pixel(kps)
    inside = in_front & (px[:, 0] >= 0) & (px[:, 0] < W) & (px[:, 1] >= 0) & (px[:, 1] < H)
    vis = np.where(inside, 2, 0)
    occl = ["outside"] * len(kps)
    fid = rec.get("depth_frame")
    depth, drec = (None, None)
    if fid is not None:
        if fid not in depth_cache:
            depth_cache.clear()
            depth_cache[fid] = sess.depth(fid)
        depth, drec = depth_cache[fid]
    for i in range(len(kps)):
        if not inside[i]:
            continue
        if depth is None:
            occl[i] = "unknown"
            continue
        col, row, lin, ok = world_to_depth_pixel(kps[i], drec["reprojection_matrix"], drec["zbuffer_params"], depth.shape)
        c, r = int(round(float(col[0]))), int(round(float(row[0])))
        if not ok[0] or not (0 <= r < depth.shape[0] and 0 <= c < depth.shape[1]) or depth[r, c] == 0:
            occl[i] = "unknown"
            continue
        measured = depth[r, c] / 1000.0
        if measured < float(lin[0]) - (OCCLUSION_ABS + OCCLUSION_REL * float(lin[0])):
            vis[i] = 1
            occl[i] = "occluded"
        else:
            occl[i] = "visible"
    return px, vis, occl


def box_in_camera(cam: Camera, box: UprightBox) -> dict:
    """Metric box in the OpenCV camera frame: centre, rotation (columns = box right/up/forward), size."""
    c = cam.world_to_opencv(box.center[None])[0]
    R_world = np.stack([box.right, np.array([0.0, 1.0, 0.0]), box.forward], axis=1)
    flip = np.diag([1.0, -1.0, 1.0])
    R_cv = flip @ cam.R.T @ R_world
    return {"center": c.tolist(), "rotation": R_cv.tolist(), "size": box.size.tolist(),
            "convention": "OpenCV camera frame (x right, y down, z forward); rotation columns = box right, up, front"}


def assign_splits(session_ids: List[str], val: List[str], test: List[str], ratios=(0.7, 0.15, 0.15)) -> Dict[str, str]:
    """train/val/test per SESSION: explicit --val/--test ids, else a stable hash (70/15/15); ≥ 3 sessions → every split non-empty."""
    out = {}
    for sid in session_ids:
        if sid in test:
            out[sid] = "test"
        elif sid in val:
            out[sid] = "val"
        elif val or test:
            out[sid] = "train"
        else:
            h = int(hashlib.sha1(sid.encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
            out[sid] = "train" if h < ratios[0] else "val" if h < ratios[0] + ratios[1] else "test"
    if not val and not test and len(session_ids) >= 3 and set(out.values()) != {"train", "val", "test"}:
        # Few sessions: make sure every split gets at least one, deterministically.
        ordered = sorted(session_ids)
        out = {sid: "train" for sid in ordered}
        out[ordered[-1]] = "test"
        out[ordered[-2]] = "val"
    return out


def export(paths: List[str], out_dir: Path, val: List[str] = (), test: List[str] = (), every: int = 1, copy_images: bool = True,
           min_visible: int = 1) -> dict:
    """Writes coco/<split>.json, yolo/ (images, labels, dataset.yaml), raw.jsonl and export_summary.json. Returns counts."""
    sessions = [Session(p) for p in find_sessions(paths)]
    if not sessions:
        raise SystemExit("no sessions found")
    splits = assign_splits([s.id for s in sessions], list(val), list(test))
    out_dir.mkdir(parents=True, exist_ok=True)
    categories: Dict[str, int] = {}
    coco = {sp: {"images": [], "annotations": [], "categories": []} for sp in ("train", "val", "test")}
    raw = open(out_dir / "raw.jsonl", "w", encoding="utf-8")
    stats = {"frames": 0, "objects": 0, "sessions": {}, "splits": splits}
    image_id = ann_id = 0
    for sess in sessions:
        split = splits[sess.id]
        img_dir = out_dir / "yolo" / "images" / split
        lbl_dir = out_dir / "yolo" / "labels" / split
        img_dir.mkdir(parents=True, exist_ok=True)
        lbl_dir.mkdir(parents=True, exist_ok=True)
        depth_cache: Dict = {}
        n_frames = n_obj = 0
        for k, rec in enumerate(sess.frames):
            if k % every:
                continue
            frame = sess.room_frame(rec)
            if frame is None:
                continue
            cam = sess.camera(rec)
            W, H = cam.resolution
            labels, frame_anns = [], []
            for g in sess.gt_boxes(rec):
                box = g.box_world
                kps = box.keypoints()
                px, vis, occl = keypoint_visibility(sess, rec, cam, kps, depth_cache)
                local = cam.world_to_local(kps)
                if np.all(local[:, 2] < 0.02):
                    continue
                front = local[:, 2] >= 0.02
                cpx = px[front]
                x0, y0 = np.clip(cpx.min(axis=0), 0, [W, H])
                x1, y1 = np.clip(cpx.max(axis=0), 0, [W, H])
                if x1 - x0 < MIN_BOX_PX or y1 - y0 < MIN_BOX_PX or int((vis > 0).sum()) < min_visible:
                    continue
                cat = g.category
                if cat not in categories:
                    categories[cat] = len(categories)
                dist = float(np.linalg.norm(box.center - cam.position))
                kp_list = []
                for (u, v), vv in zip(px, vis):
                    kp_list += [float(u) if vv else 0.0, float(v) if vv else 0.0, int(vv)]
                ann = {"id": ann_id, "image_id": image_id, "category_id": categories[cat], "bbox": [float(x0), float(y0), float(x1 - x0), float(y1 - y0)],
                       "area": float((x1 - x0) * (y1 - y0)), "iscrowd": 0, "keypoints": kp_list, "num_keypoints": int((vis > 0).sum()),
                       "object_id": g.object_id, "distance": dist, "box3d_camera": box_in_camera(cam, box)}
                frame_anns.append(ann)
                ann_id += 1
                cls = categories[cat]
                line = [cls, (x0 + x1) / 2 / W, (y0 + y1) / 2 / H, (x1 - x0) / W, (y1 - y0) / H]
                for (u, v), vv in zip(px, vis):
                    line += [float(np.clip(u / W, 0, 1)) if vv else 0.0, float(np.clip(v / H, 0, 1)) if vv else 0.0, int(vv)]
                labels.append(" ".join(f"{x:.6f}" if isinstance(x, float) else str(x) for x in line))
                raw.write(json.dumps({
                    "session": sess.id, "split": split, "frame_id": rec["frame_id"], "image": str(sess.image_path(rec)),
                    "object_id": g.object_id, "name": g.name, "category": cat, "gt_method": g.method, "gt_n": g.n, "gt_sigma": g.sigma,
                    "bbox_xyxy": [float(x0), float(y0), float(x1), float(y1)], "keypoints_px": px.tolist(), "visibility": vis.tolist(),
                    "occlusion": occl, "distance": dist, "box_world": box.to_dict(), "box_room": g.box_room.to_dict(),
                    "box_camera": box_in_camera(cam, box), "camera": {"position": cam.position.tolist(), "rotation": cam.rotation.tolist(),
                                                                       "focal": cam.focal.tolist(), "principal": cam.principal.tolist(),
                                                                       "crop": cam.crop.tolist(), "resolution": list(cam.resolution)},
                }) + "\n")
            if not frame_anns:
                continue
            name = f"{sess.id}_{rec['frame_id']:06d}.jpg"
            src = sess.image_path(rec)
            if copy_images:
                shutil.copyfile(src, img_dir / name)
            (lbl_dir / (Path(name).stem + ".txt")).write_text("\n".join(labels) + "\n")
            coco[split]["images"].append({"id": image_id, "file_name": str(src) if not copy_images else f"yolo/images/{split}/{name}",
                                          "width": W, "height": H, "session": sess.id, "frame_id": rec["frame_id"]})
            coco[split]["annotations"].extend(frame_anns)
            image_id += 1
            n_frames += 1
            n_obj += len(frame_anns)
        stats["sessions"][sess.id] = {"split": split, "frames": n_frames, "objects": n_obj}
        stats["frames"] += n_frames
        stats["objects"] += n_obj
    raw.close()
    cats = [{"id": i, "name": name, "supercategory": "furniture", "keypoints": KEYPOINT_NAMES,
             "skeleton": [[a + 1, b + 1] for a, b in BOX_EDGES]} for name, i in sorted(categories.items(), key=lambda kv: kv[1])]
    (out_dir / "coco").mkdir(exist_ok=True)
    for sp, data in coco.items():
        data["categories"] = cats
        data["info"] = {"description": "QD3D auto-labelled from in-headset ground truth", "keypoint_order": KEYPOINT_NAMES,
                        "visibility": "0 outside/behind, 1 occluded (recorded depth), 2 visible or unknown"}
        (out_dir / "coco" / f"{sp}.json").write_text(json.dumps(data))
    names = {i: name for name, i in categories.items()}
    yaml = ["# QD3D YOLO-pose dataset (export_dataset.py)", f"path: {(out_dir / 'yolo').resolve().as_posix()}",
            "train: images/train", "val: images/val", "test: images/test", "kpt_shape: [9, 3]", f"flip_idx: {FLIP_IDX}", "names:"]
    yaml += [f"  {i}: {names[i]}" for i in sorted(names)]
    (out_dir / "yolo" / "dataset.yaml").write_text("\n".join(yaml) + "\n")
    (out_dir / "export_summary.json").write_text(json.dumps(stats, indent=2))
    return stats


def main(argv=None) -> int:
    """Command line entry point (see the module docstring)."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help="QD3D root(s) or session folders")
    ap.add_argument("--out", required=True)
    ap.add_argument("--val", action="append", default=[], help="session id for the validation split (repeatable)")
    ap.add_argument("--test", action="append", default=[], help="session id for the test split (repeatable)")
    ap.add_argument("--every", type=int, default=1, help="use every Nth frame")
    ap.add_argument("--no-copy", action="store_true", help="do not copy images (COCO file_name points to the session)")
    args = ap.parse_args(argv)
    stats = export(args.paths, Path(args.out), args.val, args.test, args.every, not args.no_copy)
    print(json.dumps({k: v for k, v in stats.items() if k != "splits"}, indent=1))
    print("splits:", json.dumps(stats["splits"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
