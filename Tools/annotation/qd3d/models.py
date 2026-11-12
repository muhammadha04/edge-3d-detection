"""Model plugins for run_models.py.

A plugin is any class with
    name: str
    def run(self, image_bgr: np.ndarray, frame: dict) -> list[dict]
returning detections in the device schema (detections.jsonl):
    {"label": str, "category": str, "class_id": int, "score": float,
     "viewport": [xmin, ymin, xmax, ymax]   # 0..1, origin BOTTOM-left (like the app)
     optional: "keypoints_px": [[u, v], ...], "keypoint_scores": [...], "box": {...world box...},
               "sigma_pos": float, "uncertainty": {...}}
Load your own with  --plugin path/to/file.py:ClassName  (constructor receives the parsed --plugin-arg JSON dict).
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from typing import Dict, List, Optional, Sequence

import cv2
import numpy as np

# Mirrors ObjectCategory.cs: Categories.TryFromCocoLabel + CategoryInfo.Group
COCO_TO_CATEGORY = {
    "chair": "Chair", "sofa": "Sofa", "couch": "Sofa", "bench": "Bench", "diningtable": "Table", "dining table": "Table",
    "bed": "Bed", "toilet": "Toilet", "pottedplant": "Plant", "potted plant": "Plant", "tvmonitor": "Screen", "tv": "Screen",
    "refrigerator": "Fridge", "suitcase": "Luggage", "backpack": "Luggage",
}
CATEGORY_GROUP = {
    "Chair": "Seating", "Sofa": "Seating", "Bench": "Seating", "Table": "Table", "Bed": "Bed", "Cup": "Small",
    "Sneaker": "Small", "Camera": "Small", "Toilet": "Fixture", "Fridge": "Fixture", "Plant": "Plant", "Screen": "Screen",
    "Luggage": "Luggage",
}
# YoloDecode.Allowed: the app tracks chairs and screens only.
DEVICE_ALLOWED = {"Chair", "Screen"}

ROOT = Path(__file__).resolve().parents[3]
DEFAULT_MODEL = ROOT / "Assets/QuestDetect3D/Models/yolo11n_320.onnx"
YOLOV9_MODEL = ROOT / "Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/yolov9onnx.onnx"
DEFAULT_LABELS = ROOT / "Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/SentisYoloClasses.txt"


def _iou_xyxy(a, b) -> float:
    """IoU of two [x1, y1, x2, y2] boxes."""
    iw = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    ih = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = iw * ih
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


class YoloOnnx:
    """Ultralytics-style YOLO ONNX (raw output 1×(4+C)×N: cx, cy, w, h, class scores) with the app's pre/post-processing:
    the whole frame stretched (bilinear) to the model input, RGB, /255, NCHW; boxes in input pixels with a TOP-left
    origin → viewport v = 1 − y; score ≥ 0.25; only categories the app tracks; greedy NMS 0.55 within a category
    group; ≤ 12 detections; boxes thinner than 2 % dropped. Same as YoloModelExport + YoloDecode on the device."""

    def __init__(self, model: Optional[str] = None, labels: Optional[str] = None, score: float = 0.25, iou: float = 0.55,
                 max_det: int = 12, allowed: Optional[Sequence[str]] = None, providers: Optional[List[str]] = None):
        """Loads the ONNX model (input size read from it) and the class names."""
        import onnxruntime as ort
        self.model_path = Path(model) if model else DEFAULT_MODEL
        self.session = ort.InferenceSession(str(self.model_path), providers=providers or ["CPUExecutionProvider"])
        inp = self.session.get_inputs()[0]
        self.input_name = inp.name
        self.input_h, self.input_w = int(inp.shape[2]), int(inp.shape[3])
        self.labels = [l.strip() for l in Path(labels or DEFAULT_LABELS).read_text().replace("\r", "").split("\n") if l.strip()]
        self.score, self.iou, self.max_det = score, iou, max_det
        self.allowed = set(allowed) if allowed is not None else DEVICE_ALLOWED
        self.name = f"onnx:{self.model_path.stem}@{self.input_w}"

    def preprocess(self, image_bgr: np.ndarray) -> np.ndarray:
        """BGR frame → 1×3×H×W float RGB in 0..1, stretched bilinearly to the model input (like the app)."""
        rgb = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2RGB)
        resized = cv2.resize(rgb, (self.input_w, self.input_h), interpolation=cv2.INTER_LINEAR)
        return resized.astype(np.float32).transpose(2, 0, 1)[None] / 255.0

    def raw(self, image_bgr: np.ndarray) -> np.ndarray:
        """Raw network output as (4 + classes) × anchors."""
        out = self.session.run(None, {self.input_name: self.preprocess(image_bgr)})[0][0]
        if out.shape[0] > out.shape[1]:  # (N, 4+C) layout
            out = out.T
        return out  # (4+C, N)

    def run(self, image_bgr: np.ndarray, frame: Optional[dict] = None) -> List[Dict]:
        """Detections of one frame in the device schema (viewport boxes, bottom-left origin)."""
        out = self.raw(image_bgr)
        boxes, cls_scores = out[:4].T, out[4:].T
        cls = cls_scores.argmax(axis=1)
        scores = cls_scores[np.arange(len(cls)), cls]
        cand = []
        for i in np.where(scores >= self.score)[0]:
            label = self.labels[cls[i]] if cls[i] < len(self.labels) else str(cls[i])
            cat = COCO_TO_CATEGORY.get(label)
            if cat is None or cat not in self.allowed:
                continue
            cx, cy, w, h = boxes[i]
            cand.append((float(scores[i]), int(cls[i]), label, cat, [cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2]))
        cand.sort(key=lambda c: -c[0])
        out_dets, suppressed = [], [False] * len(cand)
        for a in range(len(cand)):
            if suppressed[a] or len(out_dets) >= self.max_det:
                continue
            s, c, label, cat, xyxy = cand[a]
            x1, y1, x2, y2 = (np.clip(xyxy[0] / self.input_w, 0, 1), np.clip(xyxy[1] / self.input_h, 0, 1),
                              np.clip(xyxy[2] / self.input_w, 0, 1), np.clip(xyxy[3] / self.input_h, 0, 1))
            vp = [float(min(x1, x2)), float(1 - max(y1, y2)), float(max(x1, x2)), float(1 - min(y1, y2))]
            if vp[2] - vp[0] < 0.02 or vp[3] - vp[1] < 0.02:
                continue
            out_dets.append({"label": label, "category": cat, "class_id": c, "score": s, "viewport": vp})
            for b in range(a + 1, len(cand)):
                if not suppressed[b] and CATEGORY_GROUP.get(cand[b][3]) == CATEGORY_GROUP.get(cat) and _iou_xyxy(xyxy, cand[b][4]) > self.iou:
                    suppressed[b] = True
        return out_dets


def load_plugin(spec: str, args: Optional[str] = None):
    """'path/to/file.py:ClassName' or 'package.module:ClassName'."""
    target, _, cls_name = spec.rpartition(":")
    kwargs = json.loads(args) if args else {}
    if target.endswith(".py"):
        mod_spec = importlib.util.spec_from_file_location(Path(target).stem, target)
        module = importlib.util.module_from_spec(mod_spec)
        mod_spec.loader.exec_module(module)
    else:
        module = importlib.import_module(target)
    plugin = getattr(module, cls_name)(**kwargs)
    if not hasattr(plugin, "run"):
        raise TypeError(f"{spec}: plugin has no run(image_bgr, frame) method")
    plugin.name = getattr(plugin, "name", cls_name)
    return plugin
