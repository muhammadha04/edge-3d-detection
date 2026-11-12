"""Statistics shared by the PC tools. robust_box() mirrors Runtime/Annotation/Logic/Repeatability.cs exactly
(per-coordinate median after aligning yaw modulo the symmetry period; sample std (n−1) as gt_sigma)."""
from __future__ import annotations

import math
from typing import Dict, List, Sequence

import numpy as np

from .geometry import UprightBox, wrap_pi


def align_yaw(box: UprightBox, reference_yaw: float, period: float) -> UprightBox:
    """Box re-expressed with its yaw closest to the reference modulo the symmetry period (W/D swap on odd quarter turns)."""
    d = wrap_pi(box.yaw - reference_yaw)
    k = int(round(d / period))  # Mathf.RoundToInt uses banker's rounding; |d/period| = 0.5 only at exact ±180° for period 2π
    if k == 0:
        return box
    removed = k * period
    quarters = int(round(removed / (math.pi / 2)))
    size = box.size.copy()
    if abs(quarters) % 2 == 1:
        size = np.array([size[2], size[1], size[0]])
    return UprightBox(box.bottom_center.copy(), wrap_pi(box.yaw - removed), size)


def robust_box(boxes: Sequence[UprightBox], period: float = 2 * math.pi) -> Dict:
    """Robust mean (median) + gt_sigma of repetitions — identical to Repeatability.cs in the app."""
    n = len(boxes)
    if n == 0:
        return {"n": 0}
    ref = boxes[0].yaw
    aligned = [align_yaw(b, ref, period) for b in boxes]
    yaws = np.array([ref + wrap_pi(a.yaw - ref) for a in aligned])
    bc = np.array([a.bottom_center for a in aligned])
    sz = np.array([a.size for a in aligned])
    mean = UprightBox(np.median(bc, axis=0), wrap_pi(float(np.median(yaws))), np.median(sz, axis=0))
    out = {"n": n, "box": mean, "centre_cm": 0.0, "yaw_deg": 0.0, "size_pct": 0.0}
    if n < 2:
        return out
    centres = np.array([a.center for a in aligned])
    out["centre_cm"] = float(math.sqrt(((centres - mean.center) ** 2).sum() / (n - 1)) * 100)
    mean_yaw = ref + wrap_pi(mean.yaw - ref)
    out["yaw_deg"] = float(math.degrees(math.sqrt(((yaws - mean_yaw) ** 2).sum() / (n - 1))))
    m = sz.mean(axis=0)
    std = np.sqrt(((sz - m) ** 2).sum(axis=0) / (n - 1))
    out["size_pct"] = float(np.mean(np.where(m > 1e-6, std / np.maximum(m, 1e-9) * 100, 0)))
    return out


def spearman(x: Sequence[float], y: Sequence[float]) -> float:
    """Spearman rank correlation (NaN for < 3 values or constant input)."""
    x, y = np.asarray(x, float), np.asarray(y, float)
    if len(x) < 3 or np.all(x == x[0]) or np.all(y == y[0]):
        return float("nan")

    def rank(a):
        """Ranks with ties averaged."""
        order = a.argsort()
        r = np.empty(len(a))
        r[order] = np.arange(len(a))
        # average ties
        for v in np.unique(a):
            idx = np.where(a == v)[0]
            if len(idx) > 1:
                r[idx] = r[idx].mean()
        return r

    rx, ry = rank(x), rank(y)
    return float(np.corrcoef(rx, ry)[0, 1])


def average_precision(scores: Sequence[float], tp: Sequence[bool], n_gt: int) -> float:
    """All-point interpolated AP (VOC 2010+)."""
    if n_gt == 0:
        return float("nan")
    if len(scores) == 0:
        return 0.0
    order = np.argsort(-np.asarray(scores, float), kind="stable")
    tp = np.asarray(tp, bool)[order]
    ctp = np.cumsum(tp)
    cfp = np.cumsum(~tp)
    recall = ctp / n_gt
    precision = ctp / np.maximum(ctp + cfp, 1e-9)
    mrec = np.concatenate([[0.0], recall, [1.0]])
    mpre = np.concatenate([[0.0], precision, [0.0]])
    for i in range(len(mpre) - 2, -1, -1):
        mpre[i] = max(mpre[i], mpre[i + 1])
    idx = np.where(mrec[1:] != mrec[:-1])[0]
    return float(np.sum((mrec[idx + 1] - mrec[idx]) * mpre[idx + 1]))


def reliability(conf: Sequence[float], correct: Sequence[bool], bins: int = 10) -> Dict:
    """Reliability diagram bins and expected calibration error (ECE)."""
    conf = np.asarray(conf, float)
    correct = np.asarray(correct, float)
    edges = np.linspace(0, 1, bins + 1)
    rows: List[Dict] = []
    ece = 0.0
    n = len(conf)
    for i in range(bins):
        sel = (conf >= edges[i]) & ((conf < edges[i + 1]) if i < bins - 1 else (conf <= edges[i + 1]))
        if not sel.any():
            rows.append({"lo": edges[i], "hi": edges[i + 1], "n": 0, "conf": float("nan"), "acc": float("nan")})
            continue
        c, a = conf[sel].mean(), correct[sel].mean()
        ece += sel.sum() / max(n, 1) * abs(a - c)
        rows.append({"lo": edges[i], "hi": edges[i + 1], "n": int(sel.sum()), "conf": float(c), "acc": float(a)})
    return {"bins": rows, "ece": float(ece) if n else float("nan")}
