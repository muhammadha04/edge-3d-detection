"""Tests of the QD3D PC tools on a synthetic session (no headset needed).

    pip install -r Tools/annotation/requirements.txt
    python -m pytest Tools/annotation/tests -q          # or, without pytest:  python Tools/annotation/tests/test_tools.py

The synthetic session (make_synthetic_session.py) uses exactly the headset file formats: rendered frames, analytic
depth PNGs + reprojection matrices, room frame, 3 blind M1 repetitions and device-style detections.
"""
from __future__ import annotations

import json
import math
import shutil
import sys
import tempfile
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))

import evaluate  # noqa: E402
import export_dataset  # noqa: E402
import make_synthetic_session  # noqa: E402
import overlay  # noqa: E402
import repeatability  # noqa: E402
from qd3d.geometry import Camera, UprightBox, iou_3d, yaw_error  # noqa: E402
from qd3d.models import load_plugin  # noqa: E402
from qd3d.session import Session  # noqa: E402
from qd3d.stats import average_precision, robust_box, spearman  # noqa: E402

_ROOT = None


def synthetic_root() -> Path:
    """One synthetic QD3D folder shared by all tests (12 frames, built once)."""
    global _ROOT
    if _ROOT is None:
        _ROOT = Path(tempfile.mkdtemp(prefix="qd3d_test_")) / "QD3D"
        make_synthetic_session.make(_ROOT, n_frames=12, seed=3)
    return _ROOT


def session() -> Session:
    return Session(next((synthetic_root() / "sessions").iterdir()))


def test_projection_matches_csharp_fixture():
    """The C# app (KeypointProjection + CameraModel) and the Python tools project identically."""
    f = json.loads((HERE / "fixtures" / "projection_fixture.json").read_text())
    c = f["camera"]
    cam = Camera(np.array(c["position"]), np.array(c["rotation_xyzw"]), np.array(c["focal"]), np.array(c["principal"]),
                 Camera.crop_from(c["sensor_resolution"], c["resolution"]), tuple(c["resolution"]))
    px, ok = cam.world_to_pixel(UprightBox.from_dict(f["box"]).keypoints())
    assert ok.all()
    assert np.abs(px - np.array(f["keypoints_px"])).max() < 0.01


def test_depth_reprojection_lands_on_scene():
    """Every recorded depth pixel reprojects onto the floor or the box (depth PNG + matrices are self-consistent)."""
    s = session()
    rec = s.frames[0]
    pts = s.depth_points(rec, stride=1)
    box = s.gt_boxes(rec)[0].box_world
    rel = pts - box.bottom_center
    loc = np.stack([rel @ box.right, rel[:, 1], rel @ box.forward], 1)
    on_box = (np.abs(loc[:, 0]) <= box.size[0] / 2 + 0.01) & (np.abs(loc[:, 2]) <= box.size[2] / 2 + 0.01) & \
             (loc[:, 1] > -0.01) & (loc[:, 1] <= box.size[1] + 0.01)
    on_floor = np.abs(pts[:, 1]) < 0.01
    assert len(pts) > 1000
    assert (on_box | on_floor).mean() > 0.99


def test_robust_box_matches_app():
    """Same numbers as AnnotationLogicTests.TestRepeatability in the app."""
    boxes = [UprightBox(np.array([1.0, 0, 2]), math.radians(10), np.array([0.60, 0.90, 0.50])),
             UprightBox(np.array([1.01, 0, 2]), math.radians(11), np.array([0.61, 0.90, 0.50])),
             UprightBox(np.array([0.99, 0, 2]), math.radians(9), np.array([0.59, 0.90, 0.50]))]
    r = robust_box(boxes)
    assert abs(r["centre_cm"] - 1.0) < 1e-3
    assert abs(r["yaw_deg"] - 1.0) < 1e-3
    assert abs(r["size_pct"] - (0.01 / 0.60 * 100) / 3) < 1e-3
    table = [UprightBox(np.zeros(3), 0.0, np.array([1.2, 0.75, 0.8])),
             UprightBox(np.zeros(3), math.pi / 2, np.array([0.8, 0.75, 1.2])),
             UprightBox(np.zeros(3), math.pi, np.array([1.2, 0.75, 0.8]))]
    assert robust_box(table, math.pi / 2)["yaw_deg"] < 1e-6
    assert robust_box(table, math.pi / 2)["size_pct"] < 1e-6


def test_iou_and_errors():
    a = UprightBox(np.zeros(3), 0.3, np.array([1.0, 1.0, 1.0]))
    assert abs(iou_3d(a, a) - 1.0) < 1e-9
    b = UprightBox(np.array([0.5, 0, 0]), 0.0, np.array([1.0, 1.0, 1.0]))
    c = UprightBox(np.zeros(3), 0.0, np.array([1.0, 1.0, 1.0]))
    assert abs(iou_3d(b, c) - 1 / 3) < 1e-9  # half overlap: 0.5 / 1.5
    assert abs(math.degrees(yaw_error(math.radians(359), math.radians(1))) - 2) < 1e-9
    assert abs(math.degrees(yaw_error(math.radians(91), 0, math.pi / 2)) - 1) < 1e-9
    assert average_precision([0.9, 0.8], [True, True], 2) == 1.0
    assert abs(spearman([1, 2, 3, 4], [10, 20, 30, 40]) - 1) < 1e-9


def test_export_dataset(tmp_path=None):
    out = Path(tmp_path or tempfile.mkdtemp()) / "dataset"
    stats = export_dataset.export([str(synthetic_root())], out)
    assert stats["objects"] == 12
    split = stats["splits"][session().id]
    coco = json.loads((out / "coco" / f"{split}.json").read_text())
    ann = coco["annotations"][0]
    assert len(ann["keypoints"]) == 27
    assert coco["categories"][0]["keypoints"][0] == "center"
    # The box centre is inside the object: the recorded depth must mark it occluded (v = 1).
    assert all(a["keypoints"][2] == 1 for a in coco["annotations"])
    labels = list((out / "yolo" / "labels" / split).glob("*.txt"))
    assert len(labels) == 12 and len(labels[0].read_text().split()) == 5 + 27


def test_evaluate_device_detections(tmp_path=None):
    out = Path(tmp_path or tempfile.mkdtemp()) / "eval"
    rows = evaluate.run([session().path], ["detections.jsonl"], out, plots=False)
    fits = next(r for r in rows if r["unit"] == "fits")
    assert fits["AP@0.25"] == 1.0
    assert fits["centre_cm median"] < 5.0  # synthetic noise: 2 cm per axis
    assert (out / "report.md").exists() and (out / "pairs.csv").exists()


def test_repeatability_report(tmp_path=None):
    out = Path(tmp_path or tempfile.mkdtemp()) / "rep"
    rows, agreement, drift = repeatability.run(synthetic_root(), out)
    assert rows[0]["n"] == 3 and rows[0]["sigma_matches_app"] is True
    assert any(d["kind"] == "drift_cross" for d in drift)


def test_overlay_video(tmp_path=None):
    out = Path(tmp_path or tempfile.mkdtemp()) / "qa.mp4"
    stats = overlay.run(session().path, out, "detections.jsonl", depth=True, max_frames=4)
    assert stats["frames"] == 4 and stats["gt_projected"] == 4 and out.stat().st_size > 0


def test_plugin_interface(tmp_path=None):
    d = Path(tmp_path or tempfile.mkdtemp())
    (d / "dummy_model.py").write_text(
        "class Dummy:\n    def __init__(self, score=0.5):\n        self.name = 'dummy'\n        self.score = score\n"
        "    def run(self, image, frame):\n        return [{'label': 'chair', 'category': 'Chair', 'class_id': 56, 'score': self.score,"
        " 'viewport': [0.1, 0.1, 0.5, 0.5]}]\n")
    plugin = load_plugin(f"{d / 'dummy_model.py'}:Dummy", '{"score": 0.7}')
    assert plugin.run(None, {})[0]["score"] == 0.7


def test_pc_yolo_matches_device_reference():
    """run_models.py reproduces the app's YOLO11n results on its test photos (Editor/TestImages/expected.txt)."""
    try:
        import onnxruntime  # noqa: F401
    except ImportError:
        return
    import run_models
    from qd3d.models import DEFAULT_MODEL, YoloOnnx
    if not DEFAULT_MODEL.exists():
        return
    assert run_models.selftest(YoloOnnx()) == 0


if __name__ == "__main__":
    failed = 0
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
                print(f"PASS {name}")
            except Exception as e:  # noqa: BLE001
                failed += 1
                print(f"FAIL {name}: {e!r}")
    if _ROOT is not None:
        shutil.rmtree(_ROOT.parent, ignore_errors=True)
    print("ALL PASSED" if failed == 0 else f"{failed} FAILED")
    sys.exit(1 if failed else 0)
