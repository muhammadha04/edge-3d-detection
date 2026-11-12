"""Geometry shared by all QD3D PC tools. Mirrors the C# app exactly:

* World: Unity, LEFT-handed, x right, y up, z forward, metres. Quaternions [x, y, z, w] (Unity convention).
* UprightBox (Runtime/Core/UprightBox.cs): bottom_center, yaw (heading of local +Z = object front, 0 = +Z,
  positive towards +X), size = [width (local X), height (Y), depth (local Z)].
  Forward = (sin yaw, 0, cos yaw), Right = (cos yaw, 0, -sin yaw).
* Keypoints (Runtime/Annotation/Logic/KeypointProjection.cs): 0 = centre, 1..8 = corners, corner index c = k-1
  with bits 1 = +x (right), 2 = top, 4 = +z (front).
* CameraModel (Runtime/Core/CameraModel.cs): camera local +x right, +y up, +z forward;
  sensor = (x/z * fx + cx, y/z * fy + cy); viewport = (sensor - crop.xy) / crop.wh (origin bottom-left);
  JPEG pixel (top-left origin): u = viewport.x * W, v = (1 - viewport.y) * H.
* OpenCV camera frame used in exported datasets: x right, y DOWN, z forward (= camera local with y negated).
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Iterable, Optional, Sequence

import numpy as np

KEYPOINT_NAMES = [
    "center",
    "left_bottom_back", "right_bottom_back", "left_top_back", "right_top_back",
    "left_bottom_front", "right_bottom_front", "left_top_front", "right_top_front",
]
# Box edges as keypoint index pairs (1..8), for drawing and the COCO skeleton.
BOX_EDGES = [(1, 2), (3, 4), (5, 6), (7, 8), (1, 3), (2, 4), (5, 7), (6, 8), (1, 5), (2, 6), (3, 7), (4, 8)]
FRONT_FACE = [5, 6, 8, 7]
# Horizontal flip of the image swaps left/right corners.
FLIP_IDX = [0, 2, 1, 4, 3, 6, 5, 8, 7]


def wrap_pi(a: float) -> float:
    """Angle (radians) wrapped to [−π, π)."""
    return a - 2.0 * math.pi * math.floor((a + math.pi) / (2.0 * math.pi))


def quat_to_matrix(q: Sequence[float]) -> np.ndarray:
    """Rotation matrix of a quaternion [x, y, z, w]; R @ v == Unity's q * v."""
    x, y, z, w = (float(c) for c in q)
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def yaw_matrix(yaw: float) -> np.ndarray:
    """Unity Quaternion.Euler(0, yaw, 0) as a matrix: maps +Z to (sin, 0, cos)."""
    c, s = math.cos(yaw), math.sin(yaw)
    return np.array([[c, 0.0, s], [0.0, 1.0, 0.0], [-s, 0.0, c]])


@dataclass
class UprightBox:
    bottom_center: np.ndarray
    yaw: float  # radians
    size: np.ndarray  # [w, h, d]

    @staticmethod
    def from_dict(d: dict) -> "UprightBox":
        """Box from a JSON record {bottom_center, yaw_deg, size}."""
        return UprightBox(np.asarray(d["bottom_center"], float), math.radians(float(d["yaw_deg"])), np.asarray(d["size"], float))

    def to_dict(self) -> dict:
        """JSON record {bottom_center, yaw_deg, size}."""
        return {"bottom_center": [float(v) for v in self.bottom_center], "yaw_deg": math.degrees(self.yaw),
                "size": [float(v) for v in self.size]}

    @property
    def forward(self) -> np.ndarray:
        """Unit vector of the object front (local +Z)."""
        return np.array([math.sin(self.yaw), 0.0, math.cos(self.yaw)])

    @property
    def right(self) -> np.ndarray:
        """Unit vector of local +X."""
        return np.array([math.cos(self.yaw), 0.0, -math.sin(self.yaw)])

    @property
    def center(self) -> np.ndarray:
        """Box centre (bottom centre + half height)."""
        return self.bottom_center + np.array([0.0, self.size[1] * 0.5, 0.0])

    def corners(self) -> np.ndarray:
        """8×3 corners in UprightBox.GetCorners order (index bits 1 = +x, 2 = top, 4 = +z)."""
        out = np.zeros((8, 3))
        for i in range(8):
            sx = 1 if i & 1 else -1
            top = 1 if i & 2 else 0
            sz = 1 if i & 4 else -1
            out[i] = (self.bottom_center + self.right * (0.5 * self.size[0] * sx) + np.array([0.0, self.size[1] * top, 0.0])
                      + self.forward * (0.5 * self.size[2] * sz))
        return out

    def keypoints(self) -> np.ndarray:
        """9×3: centre + 8 corners."""
        return np.vstack([self.center[None, :], self.corners()])

    def footprint(self) -> np.ndarray:
        """4×2 (x, z) bottom corners counter-clockwise in the xz plane."""
        c = self.corners()
        return c[[0, 1, 5, 4]][:, [0, 2]]


@dataclass
class RoomFrame:
    origin: np.ndarray
    yaw: float  # radians

    @staticmethod
    def from_dict(d: Optional[dict]) -> Optional["RoomFrame"]:
        """Room frame from {origin, yaw_deg} (None for an empty record)."""
        if not d:
            return None
        return RoomFrame(np.asarray(d["origin"], float), math.radians(float(d["yaw_deg"])))

    @property
    def rotation(self) -> np.ndarray:
        """Room → world rotation matrix."""
        return yaw_matrix(self.yaw)

    def room_to_world(self, p: np.ndarray) -> np.ndarray:
        """Room-frame points (N×3 or 3) → world."""
        return (self.rotation @ np.asarray(p, float).T).T + self.origin

    def world_to_room(self, p: np.ndarray) -> np.ndarray:
        """World points → room frame."""
        return (self.rotation.T @ (np.asarray(p, float) - self.origin).T).T

    def box_to_world(self, b: UprightBox) -> UprightBox:
        """Room-frame box → world box."""
        return UprightBox(self.room_to_world(b.bottom_center), wrap_pi(b.yaw + self.yaw), b.size.copy())

    def box_to_room(self, b: UprightBox) -> UprightBox:
        """World box → room-frame box."""
        return UprightBox(self.world_to_room(b.bottom_center), wrap_pi(b.yaw - self.yaw), b.size.copy())


@dataclass
class Camera:
    position: np.ndarray
    rotation: np.ndarray  # quaternion xyzw
    focal: np.ndarray
    principal: np.ndarray
    crop: np.ndarray  # x, y, w, h (sensor pixels)
    resolution: tuple  # (W, H)
    _r: np.ndarray = field(default=None, repr=False)

    def __post_init__(self):
        """Caches the rotation matrix."""
        self._r = quat_to_matrix(self.rotation)

    @staticmethod
    def crop_from(sensor_res: Sequence[int], current_res: Sequence[int]) -> np.ndarray:
        """Same as CameraModel.FromIntrinsics."""
        sw, sh = float(sensor_res[0]), float(sensor_res[1])
        if sw <= 0 or sh <= 0:
            return np.array([0.0, 0.0, float(current_res[0]), float(current_res[1])])
        sx, sy = current_res[0] / sw, current_res[1] / sh
        m = max(sx, sy)
        sx, sy = sx / m, sy / m
        return np.array([sw * (1 - sx) * 0.5, sh * (1 - sy) * 0.5, sw * sx, sh * sy])

    @staticmethod
    def from_frame(rec: dict) -> "Camera":
        """From a frames.jsonl record (camera + intrinsics) or a detections.jsonl "camera" object."""
        if "intrinsics" in rec:
            intr = rec["intrinsics"]
            cam = rec["camera"]
            crop = intr.get("sensor_crop") or Camera.crop_from(intr["sensor_resolution"], intr["resolution"])
            return Camera(np.asarray(cam["position"], float), np.asarray(cam["rotation"], float), np.asarray(intr["focal"], float),
                          np.asarray(intr["principal"], float), np.asarray(crop, float), tuple(intr["resolution"]))
        c = rec["camera"] if "camera" in rec else rec
        return Camera(np.asarray(c["position"], float), np.asarray(c["rotation"], float), np.asarray(c["focal"], float),
                      np.asarray(c["principal"], float), np.asarray(c["sensor_crop"], float), tuple(c["resolution"]))

    @property
    def R(self) -> np.ndarray:
        """Camera local → world rotation."""
        return self._r

    def world_to_local(self, p: np.ndarray) -> np.ndarray:
        """World points → camera local (+x right, +y up, +z forward)."""
        return (self._r.T @ (np.atleast_2d(p) - self.position).T).T

    def world_to_opencv(self, p: np.ndarray) -> np.ndarray:
        """World points → OpenCV camera frame (x right, y down, z forward)."""
        local = self.world_to_local(p)
        return local * np.array([1.0, -1.0, 1.0])

    def local_to_viewport(self, local: np.ndarray):
        """Camera-local points → viewport (bottom-left origin) and a 'in front of the camera' mask."""
        local = np.atleast_2d(local)
        z = local[:, 2]
        valid = z >= 0.02
        zz = np.where(valid, z, 1.0)
        sx = local[:, 0] / zz * self.focal[0] + self.principal[0]
        sy = local[:, 1] / zz * self.focal[1] + self.principal[1]
        vp = np.stack([(sx - self.crop[0]) / self.crop[2], (sy - self.crop[1]) / self.crop[3]], axis=1)
        return vp, valid

    def world_to_viewport(self, p: np.ndarray):
        """World points → viewport and validity mask."""
        return self.local_to_viewport(self.world_to_local(p))

    def viewport_to_pixel(self, vp: np.ndarray) -> np.ndarray:
        """Viewport → JPEG pixels (top-left origin)."""
        W, H = self.resolution
        vp = np.atleast_2d(vp)
        return np.stack([vp[:, 0] * W, (1.0 - vp[:, 1]) * H], axis=1)

    def world_to_pixel(self, p: np.ndarray):
        """World points → JPEG pixels and validity mask (the same projection as the app)."""
        vp, valid = self.world_to_viewport(p)
        return self.viewport_to_pixel(vp), valid

    def viewport_to_ray(self, vp: Sequence[float]):
        """Viewport point → world ray (origin, unit direction), like CameraModel.ViewportToRay."""
        d = np.array([(self.crop[0] + self.crop[2] * vp[0] - self.principal[0]) / self.focal[0],
                      (self.crop[1] + self.crop[3] * vp[1] - self.principal[1]) / self.focal[1], 1.0])
        d = self._r @ d
        return self.position.copy(), d / np.linalg.norm(d)


# ---------------------------------------------------------------- 3D IoU, errors

def _polygon_area(poly: np.ndarray) -> float:
    """Area of a simple polygon (shoelace)."""
    if len(poly) < 3:
        return 0.0
    x, y = poly[:, 0], poly[:, 1]
    return 0.5 * abs(float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1))))


def _ensure_ccw(poly: np.ndarray) -> np.ndarray:
    """Same polygon, counter-clockwise."""
    x, y = poly[:, 0], poly[:, 1]
    signed = 0.5 * float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))
    return poly if signed >= 0 else poly[::-1]


def _clip(subject: np.ndarray, clip: np.ndarray) -> np.ndarray:
    """Sutherland–Hodgman: convex polygon intersection (both counter-clockwise)."""
    out = list(subject)
    for i in range(len(clip)):
        a, b = clip[i], clip[(i + 1) % len(clip)]
        inp, out = out, []
        if not inp:
            break

        def inside(p):
            """True when p is left of (inside) the clip edge a→b."""
            return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]) >= -1e-12

        def intersect(p, q):
            """Intersection of segment p→q with the clip edge line."""
            d1 = (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
            d2 = (b[0] - a[0]) * (q[1] - a[1]) - (b[1] - a[1]) * (q[0] - a[0])
            t = d1 / (d1 - d2)
            return p + t * (q - p)

        prev = inp[-1]
        for cur in inp:
            if inside(cur):
                if not inside(prev):
                    out.append(intersect(prev, cur))
                out.append(cur)
            elif inside(prev):
                out.append(intersect(prev, cur))
            prev = cur
    return np.array(out) if out else np.zeros((0, 2))


def iou_3d(a: UprightBox, b: UprightBox) -> float:
    """3D IoU of two upright boxes (rotated footprint polygon intersection × vertical overlap)."""
    pa, pb = _ensure_ccw(a.footprint()), _ensure_ccw(b.footprint())
    inter_area = _polygon_area(_clip(pa, pb))
    ya0, ya1 = a.bottom_center[1], a.bottom_center[1] + a.size[1]
    yb0, yb1 = b.bottom_center[1], b.bottom_center[1] + b.size[1]
    h = max(0.0, min(ya1, yb1) - max(ya0, yb0))
    inter = inter_area * h
    va = float(np.prod(a.size))
    vb = float(np.prod(b.size))
    union = va + vb - inter
    return inter / union if union > 0 else 0.0


def symmetry_period(symmetry: Optional[str]) -> float:
    """Yaw period of a catalog symmetry: none 2π, '2' π, '4' π/2."""
    return {"2": math.pi, "4": math.pi / 2}.get(str(symmetry), 2 * math.pi)


def yaw_error(a: float, b: float, period: float = 2 * math.pi) -> float:
    """Smallest absolute yaw difference (radians) modulo the symmetry period."""
    d = (a - b) % period
    return min(d, period - d)


def align_size(pred: UprightBox, gt: UprightBox) -> np.ndarray:
    """Prediction size expressed on the GT axes: width/depth swap when the yaws differ by ~90° (mod 180°)."""
    d = abs(wrap_pi(pred.yaw - gt.yaw)) % math.pi
    if math.pi / 4 < d < 3 * math.pi / 4:
        return np.array([pred.size[2], pred.size[1], pred.size[0]])
    return pred.size.copy()


# ---------------------------------------------------------------- depth reprojection

def depth_to_world(depth_mm: np.ndarray, M: Sequence[float], zb: Sequence[float], stride: int = 1) -> np.ndarray:
    """Reproject a recorded depth map (uint16 mm, row-major as stored) to world points (N×3).
    ndc_x = 2(col+0.5)/W − 1, ndc_y = 2(row+0.5)/H − 1, ndc_z = zb0/d − zb1, world = M⁻¹[ndc,1] / w."""
    H, W = depth_mm.shape
    rows, cols = np.mgrid[0:H:stride, 0:W:stride]
    d = depth_mm[rows, cols].astype(float) / 1000.0
    ok = d > 0
    rows, cols, d = rows[ok], cols[ok], d[ok]
    ndc = np.stack([2.0 * (cols + 0.5) / W - 1.0, 2.0 * (rows + 0.5) / H - 1.0, zb[0] / d - zb[1], np.ones_like(d)], axis=0)
    Minv = np.linalg.inv(np.asarray(M, float).reshape(4, 4))
    h = Minv @ ndc
    return (h[:3] / h[3]).T


def world_to_depth_pixel(points: np.ndarray, M: Sequence[float], zb: Sequence[float], size: tuple):
    """World points → (col, row) in the depth map and their linear depth (metres) along the depth camera."""
    P = np.asarray(M, float).reshape(4, 4)
    p = np.atleast_2d(points)
    h = P @ np.hstack([p, np.ones((len(p), 1))]).T
    w = h[3]
    valid = w > 1e-6
    w = np.where(valid, w, 1.0)
    ndc = h[:3] / w
    H, W = size
    col = (ndc[0] + 1.0) * 0.5 * W - 0.5
    row = (ndc[1] + 1.0) * 0.5 * H - 0.5
    with np.errstate(divide="ignore", invalid="ignore"):
        lin = zb[0] / (ndc[2] + zb[1])
    return col, row, lin, valid


def clip_segment_near(a: np.ndarray, b: np.ndarray, near: float = 0.03):
    """Clip a segment given in camera-local coordinates against z >= near. Returns None if fully behind."""
    if a[2] < near and b[2] < near:
        return None
    if a[2] < near:
        t = (near - a[2]) / (b[2] - a[2])
        a = a + t * (b - a)
    elif b[2] < near:
        t = (near - b[2]) / (a[2] - b[2])
        b = b + t * (a - b)
    return a, b


def iter_pairs(items: Iterable):
    """All unordered pairs of a sequence."""
    items = list(items)
    for i in range(len(items)):
        for j in range(i + 1, len(items)):
            yield items[i], items[j]


def matrix_to_quat(m: np.ndarray) -> np.ndarray:
    """Rotation matrix (columns = local x, y, z axes in world) → quaternion [x, y, z, w]."""
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w = 0.25 * s
        x = (m[2, 1] - m[1, 2]) / s
        y = (m[0, 2] - m[2, 0]) / s
        z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w = (m[2, 1] - m[1, 2]) / s
        x = 0.25 * s
        y = (m[0, 1] + m[1, 0]) / s
        z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w = (m[0, 2] - m[2, 0]) / s
        x = (m[0, 1] + m[1, 0]) / s
        y = 0.25 * s
        z = (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w = (m[1, 0] - m[0, 1]) / s
        x = (m[0, 2] + m[2, 0]) / s
        y = (m[1, 2] + m[2, 1]) / s
        z = 0.25 * s
    return np.array([x, y, z, w])


def look_rotation(forward: np.ndarray, up=(0.0, 1.0, 0.0)) -> np.ndarray:
    """Unity Quaternion.LookRotation (left-handed): z = forward, x = up × z, y = z × x. Returns [x, y, z, w]."""
    z = np.asarray(forward, float)
    z = z / np.linalg.norm(z)
    x = np.cross(np.asarray(up, float), z)
    x = x / np.linalg.norm(x)
    y = np.cross(z, x)
    return matrix_to_quat(np.stack([x, y, z], axis=1))
