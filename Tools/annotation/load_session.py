#!/usr/bin/env python3
"""Inspect recorded QD3D sessions (reader API: qd3d.session.Session).

    python Tools/annotation/load_session.py Data/QD3D                 # every session
    python Tools/annotation/load_session.py Data/QD3D/sessions/<id>   # one session
    python Tools/annotation/load_session.py <session> --frame 10      # GT boxes in world / camera frame for frame 10
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.session import Session, find_sessions  # noqa: E402


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--frame", type=int, help="print camera, GT boxes (world + OpenCV camera frame) and depth stats")
    args = ap.parse_args(argv)
    sessions = find_sessions(args.paths)
    if not sessions:
        print("no sessions found", file=sys.stderr)
        return 1
    for path in sessions:
        s = Session(path)
        print(s.summary())
        if args.frame is not None:
            rec = s.frame(args.frame)
            if rec is None:
                print(f"  frame {args.frame} not found")
                continue
            cam = s.camera(rec)
            print(f"  frame {args.frame}: camera pos {np.round(cam.position, 3)}  file {rec['file']}")
            for g in s.gt_boxes(rec):
                if g.box_world is None:
                    print(f"  {g.name}: no room frame")
                    continue
                c_cv = cam.world_to_opencv(g.box_world.center[None, :])[0]
                px, ok = cam.world_to_pixel(g.box_world.keypoints())
                print(f"  {g.name}: centre world {np.round(g.box_world.center, 3)} camera(opencv) {np.round(c_cv, 3)} "
                      f"dist {np.linalg.norm(c_cv):.2f} m  keypoints px {json.dumps(np.round(px[ok], 1).tolist())}")
            pts = s.depth_points(rec, stride=4)
            if len(pts):
                print(f"  depth: {len(pts)} points, y range {pts[:, 1].min():.2f}..{pts[:, 1].max():.2f} m")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
