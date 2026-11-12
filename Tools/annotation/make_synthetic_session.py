#!/usr/bin/env python3
"""Generate a synthetic QD3D data folder (same formats as the headset) so every PC tool can be tested without a
Quest: a room with a tape-cross frame, one annotated chair-sized box (3 blind M1 repetitions), and a recorded
walk-around session with rendered JPEG frames, analytic depth PNGs + reprojection matrices, poses, device-style
detections and events.

    python Tools/annotation/make_synthetic_session.py out/QD3D [--frames 24] [--seed 1]
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.geometry import Camera, RoomFrame, UprightBox, look_rotation, quat_to_matrix  # noqa: E402
from qd3d.stats import robust_box  # noqa: E402

GT_DEFINITION = ("The tightest UPRIGHT box that sits on the floor, is aligned with the object's front, and contains the "
                 "whole object. Armrests, casters and base tips count. Corners usually float in the air, so EXTREMES are "
                 "marked, not corners.")

ROOM = RoomFrame(np.array([0.4, 0.0, -0.6]), math.radians(20.0))
CHAIR_ROOM = UprightBox(np.array([0.9, 0.0, 1.6]), math.radians(15.0), np.array([0.62, 0.95, 0.58]))
W, H = 640, 480
SENSOR = (864, 864)
FOCAL = (584.1, 584.1)
PRINCIPAL = (435.1, 431.85)
DEPTH_SIZE = 96
NEAR, FAR = 0.1, 20.0


def perspective(fov_deg: float) -> np.ndarray:
    """OpenGL-style projection (near 0.1 m, far 20 m) used as the synthetic depth camera."""
    f = 1.0 / math.tan(math.radians(fov_deg) / 2)
    return np.array([[f, 0, 0, 0], [0, f, 0, 0], [0, 0, -(FAR + NEAR) / (FAR - NEAR), -2 * FAR * NEAR / (FAR - NEAR)], [0, 0, -1, 0]])


def view_matrix(position: np.ndarray, rotation_xyzw: np.ndarray) -> np.ndarray:
    """Unity worldToCameraMatrix: camera looks along −z in view space."""
    R = quat_to_matrix(rotation_xyzw)
    T = np.eye(4)
    T[:3, :3] = R.T
    T[:3, 3] = -R.T @ position
    S = np.diag([1.0, 1.0, -1.0, 1.0])
    return S @ T


def zbuffer_params():
    """Depth linearisation parameters matching perspective(): depth = zb0 / (ndc_z + zb1)."""
    return [-2 * FAR * NEAR / (FAR - NEAR), -(FAR + NEAR) / (FAR - NEAR), 0.0, 0.0]


def ray_scene(o: np.ndarray, d: np.ndarray, box: UprightBox) -> float:
    """Distance to the first hit: floor (y = 0) or the box."""
    best = math.inf
    if d[1] < -1e-6:
        best = -o[1] / d[1]
    # box slab test in box-local coordinates
    lo = np.array([-box.size[0] / 2, 0.0, -box.size[2] / 2])
    hi = np.array([box.size[0] / 2, box.size[1], box.size[2] / 2])
    rel = o - box.bottom_center
    ol = np.array([rel @ box.right, rel[1], rel @ box.forward])
    dl = np.array([d @ box.right, d[1], d @ box.forward])
    t0, t1 = -math.inf, math.inf
    for k in range(3):
        if abs(dl[k]) < 1e-12:
            if ol[k] < lo[k] or ol[k] > hi[k]:
                return best
            continue
        a, b = (lo[k] - ol[k]) / dl[k], (hi[k] - ol[k]) / dl[k]
        t0, t1 = max(t0, min(a, b)), min(t1, max(a, b))
    if t0 <= t1 and t0 > 0:
        best = min(best, t0)
    return best


def render_depth(M: np.ndarray, zb, box: UprightBox) -> np.ndarray:
    """Analytic depth map (floor + box) in uint16 mm, stored exactly like the headset (row 0 = ndc_y −1)."""
    Minv = np.linalg.inv(M)
    out = np.zeros((DEPTH_SIZE, DEPTH_SIZE), np.uint16)
    for row in range(DEPTH_SIZE):
        for col in range(DEPTH_SIZE):
            ndc = np.array([2 * (col + 0.5) / DEPTH_SIZE - 1, 2 * (row + 0.5) / DEPTH_SIZE - 1])
            pn = Minv @ np.array([ndc[0], ndc[1], -1.0, 1.0])
            pf = Minv @ np.array([ndc[0], ndc[1], 1.0, 1.0])
            pn, pf = pn[:3] / pn[3], pf[:3] / pf[3]
            d = pf - pn
            d /= np.linalg.norm(d)
            t = ray_scene(pn, d, box)
            if not math.isfinite(t):
                continue
            p = pn + d * t
            h = M @ np.append(p, 1.0)
            ndc_z = h[2] / h[3]
            lin = zb[0] / (ndc_z + zb[1])
            if lin > 0.95 * FAR:
                continue
            out[row, col] = np.uint16(min(65535, max(0, round(lin * 1000))))
    return out


def render_image(cam: Camera, box: UprightBox) -> np.ndarray:
    """Painter's-algorithm rendering of the floor grid and the coloured box (stands in for a camera frame)."""
    img = np.full((H, W, 3), (90, 90, 90), np.uint8)
    # floor grid (world y = 0) every 0.5 m
    for v in np.arange(-4, 4.01, 0.5):
        for a, b in (([v, 0, -4], [v, 0, 4]), ([-4, 0, v], [4, 0, v])):
            pts = np.linspace(a, b, 40)
            px, ok = cam.world_to_pixel(pts)
            for i in range(len(pts) - 1):
                if ok[i] and ok[i + 1]:
                    cv2.line(img, tuple(px[i].astype(int)), tuple(px[i + 1].astype(int)), (120, 120, 120), 1)
    c = box.corners()
    faces = [([1, 3, 7, 5], (60, 60, 200)), ([0, 4, 6, 2], (60, 160, 60)), ([4, 5, 7, 6], (200, 120, 40)),
             ([0, 2, 3, 1], (40, 200, 200)), ([2, 6, 7, 3], (200, 60, 200))]
    order = sorted(faces, key=lambda f: -np.linalg.norm(cam.world_to_local(c[f[0]].mean(axis=0)[None])[0]))
    for idx, color in order:
        local = cam.world_to_local(c[idx])
        if np.any(local[:, 2] < 0.05):
            continue
        px, _ = cam.world_to_pixel(c[idx])
        cv2.fillConvexPoly(img, np.round(px).astype(np.int32), color)
    return img


def chair_world() -> UprightBox:
    """The synthetic chair in world coordinates."""
    return ROOM.box_to_world(CHAIR_ROOM)


def write_jsonl(path: Path, rows):
    """Writes one JSON object per line."""
    with open(path, "w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(r) + "\n")


def make(root: Path, n_frames: int = 24, seed: int = 1, session_id: str = "20261112_100000_lab") -> Path:
    """Creates a complete QD3D folder (catalog, room, annotations, one session) and returns the session path."""
    rng = np.random.default_rng(seed)
    root.mkdir(parents=True, exist_ok=True)
    for d in ("rooms", "annotations", "photos", "sessions"):
        (root / d).mkdir(exist_ok=True)
    catalog = {"schema_version": 1, "objects": [{
        "id": "chair_01", "name": "Chair_01", "category": "Chair", "tape_dims_cm": {"w": 62.0, "d": 58.0, "h": 95.0},
        "symmetry": "none", "front_definition": "seat front edge", "notes": "synthetic", "photo": None, "created": "2026-11-12T10:00:00.000Z"}]}
    (root / "catalog.json").write_text(json.dumps(catalog, indent=2))
    room = {"schema_version": 1, "id": "lab", "name": "Lab", "anchor_uuid": "00000000-0000-0000-0000-000000000001",
            "drift_reference": [2.5, 0.0, 0.5], "drift_checks": [
                {"timestamp": "2026-11-12T10:00:00Z", "kind": "drift_cross", "when": "session_start", "session_id": session_id,
                 "position": [2.5, 0.0, 0.5], "delta_mm": 0.0},
                {"timestamp": "2026-11-12T10:05:00Z", "kind": "drift_cross", "when": "session_end", "session_id": session_id,
                 "position": [2.503, 0.001, 0.498], "delta_mm": 3.7},
                {"timestamp": "2026-11-12T10:05:10Z", "kind": "room_cross", "when": "session_end", "session_id": session_id,
                 "position": [0.002, 0.0, -0.001], "delta_mm": 2.2}],
            "anchor_history": [], "test_box": None, "notes": "", "created": "2026-11-12T09:00:00Z"}
    (root / "rooms" / "lab.json").write_text(json.dumps(room, indent=2))

    # Three blind M1 repetitions around the true box (σ ≈ 5 mm, 0.5°).
    reps, boxes = [], []
    for i in range(3):
        b = UprightBox(CHAIR_ROOM.bottom_center + np.array([rng.normal(0, 0.005), 0.0, rng.normal(0, 0.005)]),
                       CHAIR_ROOM.yaw + math.radians(rng.normal(0, 0.5)), CHAIR_ROOM.size + rng.normal(0, 0.004, 3))
        boxes.append(b)
        reps.append({"method": "M1", "assisted": False, "repetition": i + 1, "blind": True, "box": {**b.to_dict(), "front_quarter": 0},
                     "raw_inputs": {"touches": [], "edits": [], "floor_y": 0.0, "controller": "right"}, "operator": "synthetic",
                     "timestamp": f"2026-11-12T09:3{i}:00.000Z", "duration_s": 70.0 + 10 * i, "self_rating": 4, "accepted": True,
                     "anchor_uuid": room["anchor_uuid"], "session_id": None})
    rb = robust_box(boxes)
    accepted = {"box": {**rb["box"].to_dict(), "front_quarter": 0},
                "gt_sigma": {"centre_cm": rb["centre_cm"], "yaw_deg": rb["yaw_deg"], "size_pct": rb["size_pct"]},
                "n": 3, "method": "M1", "computed": "2026-11-12T09:33:00.000Z"}
    ann = {"schema_version": 1, "room_id": "lab", "gt_definition": GT_DEFINITION, "objects": [
        {"object_id": "chair_01", "repetitions": reps, "accepted": accepted, "accepted_by_method": {"M1": accepted},
         "verify": "ok", "verify_time": "2026-11-12T09:40:00.000Z"}]}
    (root / "annotations" / "lab.json").write_text(json.dumps(ann, indent=2))

    gt_world = ROOM.box_to_world(rb["box"])
    sdir = root / "sessions" / session_id
    (sdir / "frames").mkdir(parents=True, exist_ok=True)
    (sdir / "depth").mkdir(exist_ok=True)
    frame_json = {"origin": ROOM.origin.tolist(), "yaw_deg": math.degrees(ROOM.yaw), "anchor_uuid": room["anchor_uuid"]}
    session = {
        "schema_version": 1, "session_id": session_id, "start_utc": "2026-11-12T10:00:00Z",
        "app": {"name": "QuestDetect3D", "version": "synthetic", "unity": "6000.2.13f1", "build": {"mruk": "85.0.0"}},
        "device": {"model": "synthetic", "os": "n/a"},
        "room": {"id": "lab", "name": "Lab", "anchor_uuid": room["anchor_uuid"], "frame_state": "Localized", "frame_at_start": frame_json},
        "operator": "synthetic", "lighting": "normal",
        "settings": {"frame_rate_hz": 5, "depth": True, "detector": True, "detector_model": "synthetic", "camera": "left"},
        "gt_definition": GT_DEFINITION, "duration_s": n_frames / 5.0,
        "counts": {"frames_written": n_frames, "depth_written": n_frames, "frames_dropped": 0},
    }
    (sdir / "session.json").write_text(json.dumps(session, indent=2))

    crop = Camera.crop_from(SENSOR, (W, H))
    P = perspective(80.0)
    zb = zbuffer_params()
    frames, depths, dets, poses = [], [], [], ["t_realtime,unix_ms,unity_frame,head_px,head_py,head_pz,head_qx,head_qy,head_qz,head_qw,"
                                               "left_tracked,left_px,left_py,left_pz,left_qx,left_qy,left_qz,left_qw,"
                                               "right_tracked,right_px,right_py,right_pz,right_qx,right_qy,right_qz,right_qw"]
    centre = gt_world.center
    for i in range(n_frames):
        fid = i + 1
        t = 1.0 + i * 0.2
        ang = 2 * math.pi * i / n_frames
        radius = 1.2 + 1.6 * (i % 4) / 3  # 1.2 … 2.8 m: several distance bins
        pos = centre + np.array([radius * math.sin(ang), 1.55 - centre[1], radius * math.cos(ang)])
        q = look_rotation(centre - pos + np.array([0.0, -0.1, 0.0]))
        cam = Camera(pos, q, np.array(FOCAL), np.array(PRINCIPAL), crop, (W, H))
        cv2.imwrite(str(sdir / "frames" / f"{fid:06d}.jpg"), render_image(cam, gt_world), [cv2.IMWRITE_JPEG_QUALITY, 90])
        M = P @ view_matrix(pos, q)
        cv2.imwrite(str(sdir / "depth" / f"{fid:06d}.png"), render_depth(M, zb, gt_world))
        cam_json = {"position": pos.tolist(), "rotation": q.tolist()}
        frames.append({
            "frame_id": fid, "file": f"frames/{fid:06d}.jpg", "t_realtime": t, "pca_timestamp_unix_ms": 1762941600000 + int(t * 1000),
            "camera": cam_json, "intrinsics": {"focal": list(FOCAL), "principal": list(PRINCIPAL), "sensor_resolution": list(SENSOR),
                                               "sensor_crop": crop.tolist(), "resolution": [W, H]},
            "exposure": None, "head": cam_json, "depth_frame": fid, "room_frame": frame_json})
        depths.append({"frame_id": fid, "file": f"depth/{fid:06d}.png", "t_realtime": t, "width": DEPTH_SIZE, "height": DEPTH_SIZE,
                       "reprojection_matrix": M.reshape(-1).tolist(), "zbuffer_params": zb})
        # Device-like detection: 2D box of the projected corners + a noisy depth fit + a confirmed track.
        px, ok = cam.world_to_viewport(gt_world.corners())
        vp = [float(px[:, 0].min()), float(px[:, 1].min()), float(px[:, 0].max()), float(px[:, 1].max())]
        noisy = UprightBox(gt_world.bottom_center + rng.normal(0, 0.02, 3) * np.array([1, 0, 1]),
                           gt_world.yaw + math.radians(rng.normal(0, 3)), gt_world.size * (1 + rng.normal(0, 0.03, 3)))
        conf = float(np.clip(0.9 - 0.08 * (radius - 1.2), 0.3, 0.95))
        dets.append({"frame_id": fid, "source": "device", "t_realtime": t, "unix_ms": 0,
                     "camera": {**cam_json, "focal": list(FOCAL), "principal": list(PRINCIPAL), "sensor_crop": crop.tolist(), "resolution": [W, H]},
                     "detections": [{"label": "chair", "category": "Chair", "class_id": 56, "score": conf, "viewport": vp,
                                     "fit": {"valid": True, "box": noisy.to_dict(), "sigma_pos": 0.02 * radius}}],
                     "tracks": [{"id": 1, "category": "Chair", "state": "Confirmed", "confidence": conf, "box": noisy.to_dict(),
                                 "position_sigma": 0.02 * radius, "yaw_sigma_deg": 3.0, "size_sigma_rel": 0.05, "uncertainty_cm": 4.0, "hits": i + 1}],
                     "room_frame": {"origin": ROOM.origin.tolist(), "yaw_deg": math.degrees(ROOM.yaw)}})
        pose = ",".join(f"{v:.5f}" for v in list(pos) + list(q))
        poses.append(f"{t:.4f},{1762941600000 + int(t * 1000)},{i},{pose},1,{pose},1,{pose}")
    write_jsonl(sdir / "frames.jsonl", frames)
    write_jsonl(sdir / "depth.jsonl", depths)
    write_jsonl(sdir / "detections.jsonl", dets)
    write_jsonl(sdir / "events.jsonl", [{"t": "2026-11-12T10:00:00Z", "type": "recording_start", "session_id": session_id}])
    (sdir / "poses.csv").write_text("\n".join(poses) + "\n")
    return sdir


def main(argv=None) -> int:
    """Command line entry point (see the module docstring)."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out", help="QD3D root to create")
    ap.add_argument("--frames", type=int, default=24)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args(argv)
    print(make(Path(args.out), args.frames, args.seed))
    return 0


if __name__ == "__main__":
    sys.exit(main())
