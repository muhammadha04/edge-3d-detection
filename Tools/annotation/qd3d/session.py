"""Reader for QD3D data pulled from the headset (PullSession.bat → Data/QD3D/):

    QD3D/
      catalog.json  calibration.json  events.jsonl
      rooms/<roomId>.json          anchor UUID, drift checks
      annotations/<roomId>.json    ground-truth boxes in the room frame (every repetition + accepted)
      sessions/<id>/               session.json frames/ frames.jsonl depth/ depth.jsonl poses.csv
                                   detections.jsonl events.jsonl

Typical use::

    from qd3d.session import Session
    s = Session("Data/QD3D/sessions/20261112_101500_lab")
    for rec in s.frames:
        img = s.image(rec)                 # BGR uint8 (cv2)
        cam = s.camera(rec)                # qd3d.geometry.Camera
        for gt in s.gt_boxes(rec):         # accepted ground truth, room → world for this frame
            px, ok = cam.world_to_pixel(gt.box_world.keypoints())
        pts = s.depth_points(rec)          # N×3 world points of the matched depth map
"""
from __future__ import annotations

import csv
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterator, List, Optional

import numpy as np

from .geometry import Camera, RoomFrame, UprightBox, depth_to_world


def read_jsonl(path: Path) -> List[dict]:
    if not path.exists():
        return []
    out = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    out.append(json.loads(line))
                except json.JSONDecodeError:
                    pass  # a line cut by a crash: skip it
    return out


def read_json(path: Path, default=None):
    if not path.exists():
        return default
    with open(path, encoding="utf-8") as f:
        return json.load(f)


@dataclass
class GtBox:
    object_id: str
    name: str
    category: str
    symmetry: str
    method: str
    n: int
    sigma: dict
    verify: Optional[str]
    box_room: UprightBox
    box_world: Optional[UprightBox]
    tape_cm: Optional[dict]


class DataRoot:
    """The QD3D folder (catalog, rooms, annotations, sessions)."""

    def __init__(self, root: Path):
        self.root = Path(root)
        self.catalog = read_json(self.root / "catalog.json", {"objects": []})
        self.objects = {o["id"]: o for o in self.catalog.get("objects", [])}

    def room(self, room_id: str) -> Optional[dict]:
        return read_json(self.root / "rooms" / f"{room_id}.json")

    def annotations(self, room_id: str) -> dict:
        return read_json(self.root / "annotations" / f"{room_id}.json", {"objects": []})

    def rooms(self) -> List[dict]:
        return [read_json(p) for p in sorted((self.root / "rooms").glob("*.json"))]

    def sessions(self) -> List[Path]:
        d = self.root / "sessions"
        return sorted(p for p in d.iterdir() if (p / "session.json").exists()) if d.exists() else []

    def accepted_boxes(self, room_id: str, include_redo: bool = False) -> List[GtBox]:
        out = []
        for ann in self.annotations(room_id).get("objects", []):
            acc = ann.get("accepted")
            if not acc or (ann.get("verify") == "redo" and not include_redo):
                continue
            obj = self.objects.get(ann["object_id"], {})
            out.append(GtBox(
                object_id=ann["object_id"], name=obj.get("name", ann["object_id"]), category=obj.get("category", "Chair"),
                symmetry=obj.get("symmetry", "none"), method=acc.get("method"), n=acc.get("n", 0),
                sigma=acc.get("gt_sigma", {}), verify=ann.get("verify"), box_room=UprightBox.from_dict(acc["box"]),
                box_world=None, tape_cm=obj.get("tape_dims_cm")))
        return out


class Session:
    def __init__(self, path, data_root: Optional[Path] = None):
        self.path = Path(path)
        self.info = read_json(self.path / "session.json", {})
        self.frames = read_jsonl(self.path / "frames.jsonl")
        self.frames.sort(key=lambda r: r["frame_id"])
        self._frame_by_id = {r["frame_id"]: r for r in self.frames}
        self.depth_records = {r["frame_id"]: r for r in read_jsonl(self.path / "depth.jsonl")}
        self.root = DataRoot(data_root or self.path.parent.parent)
        room = self.info.get("room") or {}
        self.room_id = room.get("id")
        self._gt = self.root.accepted_boxes(self.room_id) if self.room_id else []
        self._start_frame = RoomFrame.from_dict(room.get("frame_at_start"))

    @property
    def id(self) -> str:
        return self.info.get("session_id", self.path.name)

    def frame(self, frame_id: int) -> Optional[dict]:
        return self._frame_by_id.get(frame_id)

    def image(self, rec: dict):
        import cv2
        return cv2.imread(str(self.path / rec["file"]), cv2.IMREAD_COLOR)

    def image_path(self, rec: dict) -> Path:
        return self.path / rec["file"]

    @staticmethod
    def camera(rec: dict) -> Camera:
        return Camera.from_frame(rec)

    def room_frame(self, rec: Optional[dict] = None) -> Optional[RoomFrame]:
        """Room frame valid at this frame (per-frame anchor pose), else the one at session start."""
        if rec is not None and rec.get("room_frame"):
            return RoomFrame.from_dict(rec["room_frame"])
        return self._start_frame

    def gt_boxes(self, rec: Optional[dict] = None) -> List[GtBox]:
        frame = self.room_frame(rec)
        out = []
        for g in self._gt:
            out.append(GtBox(**{**g.__dict__, "box_world": frame.box_to_world(g.box_room) if frame else None}))
        return out

    # ------------------------------------------------------------ depth

    def depth(self, frame_id: int):
        """(depth_mm uint16 H×W, depth record) or (None, None)."""
        rec = self.depth_records.get(frame_id)
        if rec is None:
            return None, None
        import cv2
        img = cv2.imread(str(self.path / rec["file"]), cv2.IMREAD_UNCHANGED)
        return img, rec

    def depth_points(self, rec_or_id, stride: int = 2) -> np.ndarray:
        fid = rec_or_id["frame_id"] if isinstance(rec_or_id, dict) else rec_or_id
        d, r = self.depth(fid)
        if d is None:
            return np.zeros((0, 3))
        return depth_to_world(d, r["reprojection_matrix"], r["zbuffer_params"], stride)

    # ------------------------------------------------------------ other streams

    def detections(self, name: str = "detections.jsonl") -> List[dict]:
        p = Path(name)
        return read_jsonl(p if p.is_absolute() or p.exists() else self.path / name)

    def events(self) -> List[dict]:
        return read_jsonl(self.path / "events.jsonl")

    def poses(self) -> Dict[str, np.ndarray]:
        path = self.path / "poses.csv"
        if not path.exists():
            return {}
        with open(path, encoding="utf-8") as f:
            rows = list(csv.reader(f))
        header, body = rows[0], [r for r in rows[1:] if len(r) == len(rows[0])]
        arr = np.array(body, dtype=float) if body else np.zeros((0, len(header)))
        return {h: arr[:, i] for i, h in enumerate(header)}

    def nearest_frame(self, t_realtime: float) -> Optional[dict]:
        if not self.frames:
            return None
        return min(self.frames, key=lambda r: abs(r["t_realtime"] - t_realtime))

    def summary(self) -> str:
        counts = self.info.get("counts", {})
        frame = self._start_frame
        lines = [
            f"session  {self.id}",
            f"app      {self.info.get('app', {}).get('version')}  device {self.info.get('device', {}).get('model')}",
            f"room     {self.room_id}  frame {'ok' if frame else 'MISSING'}  operator {self.info.get('operator')}  lighting {self.info.get('lighting')}",
            f"frames   {len(self.frames)}  depth {len(self.depth_records)}  dropped {counts.get('frames_dropped', '?')}  "
            f"duration {self.info.get('duration_s', '?')} s",
            f"settings {json.dumps(self.info.get('settings', {}).get('frame_rate_hz'))} Hz, depth {self.info.get('settings', {}).get('depth')}, "
            f"detector {self.info.get('settings', {}).get('detector')}",
            f"gt boxes {len(self._gt)}: " + ", ".join(f"{g.name}({g.method} n={g.n})" for g in self._gt),
        ]
        return "\n".join(lines)


def find_sessions(paths: List[str]) -> List[Path]:
    """Expand arguments: a session dir, a sessions/ dir, or a QD3D root."""
    out: List[Path] = []
    for p in map(Path, paths):
        if (p / "session.json").exists():
            out.append(p)
        elif (p / "sessions").exists():
            out.extend(DataRoot(p).sessions())
        elif p.exists():
            out.extend(sorted(q for q in p.iterdir() if (q / "session.json").exists()))
    return out


def distance(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.linalg.norm(np.asarray(a) - np.asarray(b)))


def angle_deg(rad: float) -> float:
    return rad * 180.0 / math.pi
