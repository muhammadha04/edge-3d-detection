#!/usr/bin/env python3
"""Alignment QA: MP4 of the recorded frames with the ground-truth boxes projected (and optionally detections and
the recorded depth). If the green boxes sit on the objects in every frame, camera poses, intrinsics, the room
frame and the annotations are consistent.

    python Tools/annotation/overlay.py Data/QD3D/sessions/<id>                       # → <id>/overlay.mp4
    python Tools/annotation/overlay.py <session> --detections detections.jsonl --depth --out qa.mp4
    python Tools/annotation/overlay.py <session> --png 5                            # also every 5th frame as PNG

Colours: green = accepted ground truth (front face brighter, yellow arrow = front), magenta = detector 3D fits,
orange = detector 2D boxes, cyan = tracks, depth = blue (near) → red (far) dots.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.geometry import BOX_EDGES, FRONT_FACE, Camera, UprightBox, clip_segment_near  # noqa: E402
from qd3d.session import Session, find_sessions  # noqa: E402


def draw_box(img, cam: Camera, box: UprightBox, color, thickness=2, label=None, front=True):
    kp = box.keypoints()
    local = cam.world_to_local(kp)
    for a, b in BOX_EDGES:
        seg = clip_segment_near(local[a], local[b])
        if seg is None:
            continue
        vp, _ = cam.local_to_viewport(np.array(seg))
        px = cam.viewport_to_pixel(vp)
        if not np.all(np.isfinite(px)):
            continue
        is_front = a in FRONT_FACE and b in FRONT_FACE
        cv2.line(img, tuple(np.round(px[0]).astype(int)), tuple(np.round(px[1]).astype(int)), color,
                 thickness + (1 if is_front and front else 0), cv2.LINE_AA)
    if front:
        tip = box.bottom_center + box.forward * (0.5 * box.size[2] + 0.25)
        seg = clip_segment_near(cam.world_to_local(box.bottom_center[None])[0], cam.world_to_local(tip[None])[0])
        if seg is not None:
            px = cam.viewport_to_pixel(cam.local_to_viewport(np.array(seg))[0])
            cv2.arrowedLine(img, tuple(np.round(px[0]).astype(int)), tuple(np.round(px[1]).astype(int)), (0, 220, 255), 2,
                            cv2.LINE_AA, tipLength=0.15)
    if label:
        c_local = cam.world_to_local(box.center[None])[0]
        if c_local[2] > 0.05:
            px = cam.viewport_to_pixel(cam.local_to_viewport(c_local[None])[0])[0]
            cv2.putText(img, label, (int(px[0]) - 40, int(px[1])), cv2.FONT_HERSHEY_SIMPLEX, 0.6, color, 2, cv2.LINE_AA)


def draw_depth(img, cam: Camera, points: np.ndarray):
    if len(points) == 0:
        return
    local = cam.world_to_local(points)
    vp, ok = cam.local_to_viewport(local)
    px = cam.viewport_to_pixel(vp)
    H, W = img.shape[:2]
    z = local[:, 2]
    ok &= (px[:, 0] >= 0) & (px[:, 0] < W) & (px[:, 1] >= 0) & (px[:, 1] < H)
    zn = np.clip((z - 0.3) / 4.0, 0, 1)
    for (u, v), t in zip(px[ok].astype(int), zn[ok]):
        cv2.circle(img, (u, v), 2, (int(255 * (1 - t)), 60, int(255 * t)), -1)


def draw_detections(img, cam: Camera, det_frame: dict):
    H, W = img.shape[:2]
    for d in det_frame.get("detections", []):
        x0, y0, x1, y1 = d["viewport"]
        p0 = (int(x0 * W), int((1 - y1) * H))
        p1 = (int(x1 * W), int((1 - y0) * H))
        cv2.rectangle(img, p0, p1, (0, 140, 255), 1)
        cv2.putText(img, f"{d['label']} {d['score']:.2f}", (p0[0], max(12, p0[1] - 4)), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 140, 255), 1)
        fit = d.get("fit") or {}
        if fit.get("valid") and "box" in fit:
            draw_box(img, cam, UprightBox.from_dict(fit["box"]), (255, 0, 255), 1, front=False)
    for t in det_frame.get("tracks", []):
        if t.get("state") == "Confirmed":
            draw_box(img, cam, UprightBox.from_dict(t["box"]), (255, 255, 0), 1, f"#{t['id']} {t['confidence']:.2f}", front=False)


def run(session_path: Path, out: Path, detections: str = None, depth: bool = False, png_every: int = 0, max_frames: int = 0,
        fps: float = 0) -> dict:
    s = Session(session_path)
    det_by_frame = {}
    if detections:
        for d in s.detections(detections):
            det_by_frame.setdefault(d["frame_id"], d)
    rate = fps or s.info.get("settings", {}).get("frame_rate_hz", 5)
    writer = None
    stats = {"frames": 0, "gt_projected": 0, "missing_images": 0}
    frames = s.frames[:max_frames] if max_frames else s.frames
    png_dir = out.with_suffix("") if png_every else None
    if png_dir:
        png_dir.mkdir(parents=True, exist_ok=True)
    for i, rec in enumerate(frames):
        img = s.image(rec)
        if img is None:
            stats["missing_images"] += 1
            continue
        cam = s.camera(rec)
        if depth:
            draw_depth(img, cam, s.depth_points(rec, stride=3))
        for g in s.gt_boxes(rec):
            if g.box_world is None:
                continue
            draw_box(img, cam, g.box_world, (60, 255, 60), 2, f"{g.name} ({g.method} n={g.n})")
            stats["gt_projected"] += 1
        if rec["frame_id"] in det_by_frame:
            draw_detections(img, cam, det_by_frame[rec["frame_id"]])
        frame_text = f"{s.id}  frame {rec['frame_id']}  t={rec['t_realtime']:.2f}s" + ("" if s.room_frame(rec) else "  NO ROOM FRAME")
        cv2.putText(img, frame_text, (10, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2, cv2.LINE_AA)
        if writer is None:
            h, w = img.shape[:2]
            out.parent.mkdir(parents=True, exist_ok=True)
            writer = cv2.VideoWriter(str(out), cv2.VideoWriter_fourcc(*"mp4v"), float(rate), (w, h))
        writer.write(img)
        if png_dir and i % png_every == 0:
            cv2.imwrite(str(png_dir / f"{rec['frame_id']:06d}.png"), img)
        stats["frames"] += 1
    if writer is not None:
        writer.release()
    return stats


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("sessions", nargs="+")
    ap.add_argument("--out", help="output MP4 (default <session>/overlay.mp4)")
    ap.add_argument("--detections", help="detections.jsonl (device, run_models.py or replay) to draw as well")
    ap.add_argument("--depth", action="store_true", help="draw the recorded depth reprojected into the image")
    ap.add_argument("--png", type=int, default=0, help="also save every Nth frame as PNG")
    ap.add_argument("--max-frames", type=int, default=0)
    ap.add_argument("--fps", type=float, default=0)
    args = ap.parse_args(argv)
    for path in find_sessions(args.sessions):
        out = Path(args.out) if args.out else path / "overlay.mp4"
        stats = run(path, out, args.detections, args.depth, args.png, args.max_frames, args.fps)
        print(f"{path.name}: {stats['frames']} frames, {stats['gt_projected']} GT boxes drawn -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
