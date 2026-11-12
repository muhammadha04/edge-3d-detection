#!/usr/bin/env python3
"""Run detectors on recorded frames on the PC and write detections.jsonl in the SAME schema as the device
(viewport boxes, origin bottom-left; camera of the frame), ready for replay_fitter / evaluate.py.

    python Tools/annotation/run_models.py Data/QD3D/sessions/<id>                       # app model: YOLO11n@320 (FP32 ONNX)
    python Tools/annotation/run_models.py <session> --model Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/yolov9onnx.onnx
    python Tools/annotation/run_models.py <session> --all-classes                       # every mapped category, not only chair/screen
    python Tools/annotation/run_models.py <session> --plugin my_model.py:MyModel --plugin-arg '{"weights": "x.onnx"}'
    python Tools/annotation/run_models.py --selftest                                    # check preprocessing against expected.txt

Output: <session>/pc_<model>.jsonl (or --out).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.models import COCO_TO_CATEGORY, ROOT, YoloOnnx, load_plugin  # noqa: E402
from qd3d.session import Session, find_sessions  # noqa: E402


def run_session(model, session_path: Path, out: Path, every: int = 1) -> dict:
    """Runs the model on every (or every Nth) recorded frame and writes one detections line per frame."""
    s = Session(session_path)
    n = n_det = 0
    t0 = time.time()
    with open(out, "w", encoding="utf-8") as f:
        for k, rec in enumerate(s.frames):
            if k % every:
                continue
            img = s.image(rec)
            if img is None:
                continue
            dets = model.run(img, rec)
            for d in dets:
                d.setdefault("fit", None)
            cam = s.camera(rec)
            line = {
                "frame_id": rec["frame_id"], "source": f"pc:{model.name}", "t_realtime": rec["t_realtime"],
                "unix_ms": rec.get("pca_timestamp_unix_ms"),
                "camera": {"position": cam.position.tolist(), "rotation": cam.rotation.tolist(), "focal": cam.focal.tolist(),
                           "principal": cam.principal.tolist(), "sensor_crop": cam.crop.tolist(), "resolution": list(cam.resolution)},
                "detections": dets, "tracks": [],
            }
            if rec.get("room_frame"):
                line["room_frame"] = {"origin": rec["room_frame"]["origin"], "yaw_deg": rec["room_frame"]["yaw_deg"]}
            f.write(json.dumps(line) + "\n")
            n += 1
            n_det += len(dets)
    return {"frames": n, "detections": n_det, "seconds": round(time.time() - t0, 1), "out": str(out)}


def selftest(model: YoloOnnx) -> int:
    """Run on the app's test photos and compare with Editor/TestImages/expected.txt (onnxruntime FP32 reference)."""
    folder = ROOT / "Assets/QuestDetect3D/Editor/TestImages"
    expected = {}
    for line in (folder / "expected.txt").read_text().splitlines():
        if line.startswith("#") or not line.strip():
            continue
        img, label, score, *vp = line.split("|")
        expected.setdefault(img, []).append((label, float(score), [float(v) for v in vp]))
    failures = 0
    for name, exp in expected.items():
        dets = model.run(cv2.imread(str(folder / f"{name}.jpg")))
        for label, score, vp in exp:
            match = [d for d in dets if d["label"] == label]
            if not match:
                print(f"FAIL {name}/{label}: not detected")
                failures += 1
                continue
            d = max(match, key=lambda d: d["score"])
            err = max(abs(a - b) for a, b in zip(d["viewport"], vp))
            ok = abs(d["score"] - score) < 0.02 and err < 0.01
            failures += 0 if ok else 1
            print(f"{'PASS' if ok else 'FAIL'} {name}/{label}: score {d['score']:.3f} (expected {score:.3f}), viewport err {err:.4f}")
    print("SELFTEST OK" if failures == 0 else f"SELFTEST FAILED ({failures})")
    return 0 if failures == 0 else 1


def main(argv=None) -> int:
    """Command line entry point (see the module docstring)."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("sessions", nargs="*")
    ap.add_argument("--model", help="ONNX model (default: the app's yolo11n_320.onnx)")
    ap.add_argument("--labels", help="class names file (default: the app's COCO list)")
    ap.add_argument("--score", type=float, default=0.25)
    ap.add_argument("--all-classes", action="store_true", help="keep every mapped furniture category (device keeps chair + screen)")
    ap.add_argument("--plugin", help="custom model: file.py:Class or module:Class")
    ap.add_argument("--plugin-arg", help="JSON dict passed to the plugin constructor")
    ap.add_argument("--every", type=int, default=1)
    ap.add_argument("--out", help="output file (single session)")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)
    if args.plugin:
        model = load_plugin(args.plugin, args.plugin_arg)
    else:
        model = YoloOnnx(args.model, args.labels, args.score, allowed=set(COCO_TO_CATEGORY.values()) if args.all_classes else None)
    if args.selftest:
        return selftest(model)
    sessions = find_sessions(args.sessions)
    if not sessions:
        ap.error("no sessions")
    for path in sessions:
        safe = model.name.replace(":", "_").replace("@", "_").replace("/", "_")
        out = Path(args.out) if args.out and len(sessions) == 1 else path / f"pc_{safe}.jsonl"
        print(json.dumps({"session": path.name, **run_session(model, path, out, args.every)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
