#!/usr/bin/env python3
"""Normalise the lab-chair scan (OBJ) into QuestDetect3D's canonical box frame.

Output (Assets/QuestDetect3D/Resources/LabChairScan.bytes), little-endian:
    b"QDM1", uint32 vertex_count, uint32 triangle_count, float32[N,3] positions, int32[T,3] triangles

Canonical frame = UprightBox local frame:
    * Unity axes (OBJ is right-handed → x is negated and triangle winding flipped)
    * pivot at the centre of the bottom face, y up
    * backrest toward -Z, seat facing +Z (found from the mesh itself: seat-height centroid → top-slab centroid)
    * the chair body's footprint axes aligned with X/Z (min-area rectangle of everything above seat height,
      so the rotated 5-star base doesn't tilt the frame)
    * scaled so the whole mesh spans exactly [-0.5, 0.5] x [0, 1] x [-0.5, 0.5]

At runtime the mesh is then placed with localScale = fitted box size and a quarter-turn rotation chosen by the
Depth-API backrest test, so no hand calibration is needed.

Usage:
    git show origin/scancalibration:Assets/Resources/ScanCalibration/LabChair.obj > lab.obj
    python3 Tools/normalize_scan.py lab.obj Assets/QuestDetect3D/Resources/LabChairScan.bytes
"""
import struct
import sys

import numpy as np


def load_obj(path):
    verts, faces = [], []
    with open(path) as f:
        for line in f:
            if line.startswith("v "):
                verts.append([float(x) for x in line.split()[1:4]])
            elif line.startswith("f "):
                idx = [int(p.split("/")[0]) - 1 for p in line.split()[1:]]
                for k in range(1, len(idx) - 1):  # fan-triangulate polygons
                    faces.append([idx[0], idx[k], idx[k + 1]])
    return np.array(verts, dtype=np.float64), np.array(faces, dtype=np.int64)


def main(src, dst):
    v, f = load_obj(src)
    v[:, 0] *= -1.0            # right-handed OBJ → left-handed Unity
    f = f[:, [0, 2, 1]]        # keep front faces front after the mirror

    y0 = v[:, 1].min()
    t = (v[:, 1] - y0) / (v[:, 1].max() - y0)
    upper = v[t > 0.45][:, [0, 2]]
    seat = v[(t > 0.45) & (t < 0.62)][:, [0, 2]].mean(0)
    back = v[t > 0.80][:, [0, 2]].mean(0)
    back_dir = (back - seat) / np.linalg.norm(back - seat)

    best = None
    for deg in np.arange(0.0, 90.0, 0.5):
        a = np.radians(deg)
        ax1, ax2 = np.array([np.cos(a), np.sin(a)]), np.array([-np.sin(a), np.cos(a)])
        p1, p2 = upper @ ax1, upper @ ax2
        area = np.ptp(p1) * np.ptp(p2)
        if best is None or area < best[0]:
            best = (area, ax1, ax2)
    back_axis = max([best[1], -best[1], best[2], -best[2]], key=lambda c: c @ back_dir)

    fwd = -back_axis
    yaw = np.arctan2(fwd[0], fwd[1])
    right = np.array([np.cos(yaw), -np.sin(yaw)])     # UprightBox.Right for this yaw
    p = np.stack([v[:, [0, 2]] @ right, v[:, 1] - y0, v[:, [0, 2]] @ fwd], axis=1)
    lo, hi = p.min(0), p.max(0)
    print(f"scan extent (m): width {hi[0]-lo[0]:.3f}  height {hi[1]-lo[1]:.3f}  depth {hi[2]-lo[2]:.3f}")
    p[:, 0] -= 0.5 * (lo[0] + hi[0])
    p[:, 2] -= 0.5 * (lo[2] + hi[2])
    p /= (hi - lo)

    with open(dst, "wb") as out:
        out.write(struct.pack("<4sII", b"QDM1", len(p), len(f)))
        out.write(p.astype("<f4").tobytes())
        out.write(f.astype("<i4").tobytes())
    print(f"wrote {dst}: {len(p)} vertices, {len(f)} triangles")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
