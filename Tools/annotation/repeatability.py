#!/usr/bin/env python3
"""Ground-truth quality report from the annotation files (no recording needed).

Per object and method (M1 Touch, M2 Point, M4 Assisted):
  n repetitions, spread (centre σ cm, yaw σ °, size σ %) recomputed here with the same algorithm as the app
  (Repeatability.cs, checked against the stored gt_sigma), time per annotation, self-rating, accepted box vs the
  tape-measured W/D/H (Δ cm), verify status, and the agreement between methods (M1 vs M2 centre/yaw difference).
Per room: drift-cross and room-cross checks per session, anchor re-placements (offset of the new cross touch in the
old frame).

Decision rule for the pilot (Docs/annotation-platform-plan.md §9): choose the method with the smallest σ whose box
is within 1 cm of the tape; go to the full dataset when centre σ ≤ 1.5 cm.

    python Tools/annotation/repeatability.py Data/QD3D                 # → Data/QD3D/reports/repeatability.md + .csv
    python Tools/annotation/repeatability.py Data/QD3D --out reports
"""
from __future__ import annotations

import argparse
import csv
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qd3d.geometry import UprightBox, symmetry_period, wrap_pi  # noqa: E402
from qd3d.session import DataRoot  # noqa: E402
from qd3d.stats import robust_box  # noqa: E402

METHODS = ["M1", "M2", "M4"]


def method_rows(root: DataRoot):
    """Per object and method: spread, time, rating, tape Δ, verify; plus method-agreement rows."""
    rows, agreement = [], []
    for room in root.rooms():
        if not room:
            continue
        ann = root.annotations(room["id"])
        for obj_ann in ann.get("objects", []):
            obj = root.objects.get(obj_ann["object_id"], {})
            period = symmetry_period(obj.get("symmetry"))
            tape = obj.get("tape_dims_cm") or {}
            means = {}
            for m in METHODS:
                reps = [r for r in obj_ann.get("repetitions", []) if r.get("method") == m and r.get("accepted", True) and r.get("box")]
                if not reps:
                    continue
                boxes = [UprightBox.from_dict(r["box"]) for r in reps]
                rb = robust_box(boxes, period)
                means[m] = rb["box"]
                stored = (obj_ann.get("accepted_by_method") or {}).get(m, {}).get("gt_sigma", {})
                b = rb["box"]
                row = {
                    "room": room["id"], "object": obj.get("name", obj_ann["object_id"]), "object_id": obj_ann["object_id"],
                    "method": m, "n": rb["n"], "blind": sum(1 for r in reps if r.get("blind")),
                    "centre_sigma_cm": rb["centre_cm"], "yaw_sigma_deg": rb["yaw_deg"], "size_sigma_pct": rb["size_pct"],
                    "stored_centre_sigma_cm": stored.get("centre_cm"),
                    "sigma_matches_app": (abs(stored.get("centre_cm", rb["centre_cm"]) - rb["centre_cm"]) < 0.05) if stored else None,
                    "mean_duration_s": float(np.mean([r.get("duration_s", 0) for r in reps])),
                    "mean_self_rating": float(np.mean([r.get("self_rating", 0) for r in reps])),
                    "W_cm": b.size[0] * 100, "D_cm": b.size[2] * 100, "H_cm": b.size[1] * 100,
                    "dW_tape_cm": b.size[0] * 100 - tape["w"] if tape.get("w") else None,
                    "dD_tape_cm": b.size[2] * 100 - tape["d"] if tape.get("d") else None,
                    "dH_tape_cm": b.size[1] * 100 - tape["h"] if tape.get("h") else None,
                    "verify": obj_ann.get("verify"),
                    "edits_mean": float(np.mean([len((r.get("raw_inputs") or {}).get("edits", [])) for r in reps])),
                    "tip_default_any": any((r.get("raw_inputs") or {}).get("tip_is_default", False) for r in reps),
                }
                rows.append(row)
            for a in METHODS:
                for c in METHODS:
                    if a < c and a in means and c in means:
                        agreement.append({
                            "room": room["id"], "object": obj.get("name", obj_ann["object_id"]), "pair": f"{a} vs {c}",
                            "centre_diff_cm": float(np.linalg.norm(means[a].center - means[c].center) * 100),
                            "yaw_diff_deg": math.degrees(abs(wrap_pi(means[a].yaw - means[c].yaw))),
                            "size_diff_cm": float(np.mean(np.abs(means[a].size - means[c].size)) * 100),
                        })
    return rows, agreement


def drift_rows(root: DataRoot):
    """Drift / room-cross checks and anchor re-placements of every room."""
    rows, anchors = [], []
    for room in root.rooms():
        if not room:
            continue
        for d in room.get("drift_checks", []):
            rows.append({"room": room["id"], "session": d.get("session_id"), "kind": d.get("kind"), "when": d.get("when"),
                         "delta_mm": d.get("delta_mm"), "timestamp": d.get("timestamp")})
        for a in room.get("anchor_history", []):
            off = a.get("previous_frame_offset_mm")
            anchors.append({"room": room["id"], "uuid": a.get("uuid"), "created": a.get("created"), "reason": a.get("reason"),
                            "offset_in_old_frame_mm": float(np.linalg.norm(off)) if off else None,
                            "yaw_vs_old_frame_deg": a.get("previous_frame_yaw_deg") if off else None})
    return rows, anchors


def fmt(v):
    """Value formatting for the Markdown tables."""
    if v is None:
        return "—"
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, float):
        return f"{v:.2f}"
    return str(v)


def table(rows, keys):
    """Markdown table of the given columns."""
    if not rows:
        return ["(none)"]
    out = ["| " + " | ".join(keys) + " |", "|" + "---|" * len(keys)]
    out += ["| " + " | ".join(fmt(r.get(k)) for k in keys) + " |" for r in rows]
    return out


def write_csv(path: Path, rows):
    """Rows → CSV (nothing when empty)."""
    if not rows:
        return
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)


def run(root_path: Path, out: Path):
    """Writes repeatability.md / .csv, method_agreement.csv and drift.csv. Returns the rows."""
    root = DataRoot(root_path)
    rows, agreement = method_rows(root)
    drift, anchors = drift_rows(root)
    out.mkdir(parents=True, exist_ok=True)
    write_csv(out / "repeatability.csv", rows)
    write_csv(out / "method_agreement.csv", agreement)
    write_csv(out / "drift.csv", drift)
    md = ["# QD3D ground-truth repeatability", "",
          "σ = sample standard deviation over the blind repetitions (n − 1). Δ tape = accepted box − tape measure.", "",
          "## Per object and method", ""]
    md += table(rows, ["room", "object", "method", "n", "blind", "centre_sigma_cm", "yaw_sigma_deg", "size_sigma_pct", "sigma_matches_app",
                       "mean_duration_s", "mean_self_rating", "dW_tape_cm", "dD_tape_cm", "dH_tape_cm", "verify", "tip_default_any"])
    md += ["", "## Agreement between methods", ""] + table(agreement, ["room", "object", "pair", "centre_diff_cm", "yaw_diff_deg", "size_diff_cm"])
    md += ["", "## Drift checks", ""] + table(drift, ["room", "session", "kind", "when", "delta_mm", "timestamp"])
    if drift:
        by_kind = {}
        for d in drift:
            if d["delta_mm"] is not None:
                by_kind.setdefault(d["kind"], []).append(d["delta_mm"])
        md += [""] + [f"* {k}: n = {len(v)}, mean {np.mean(v):.1f} mm, max {np.max(v):.1f} mm" for k, v in by_kind.items()]
    md += ["", "## Anchor history", ""] + table(anchors, ["room", "uuid", "created", "reason", "offset_in_old_frame_mm", "yaw_vs_old_frame_deg"])
    best = [r for r in rows if r["method"] != "M4" and r["n"] >= 3]
    if best:
        md += ["", "## Decision rule", ""]
        for r in best:
            tape_ok = all(abs(v) <= 1.0 for v in (r["dW_tape_cm"], r["dD_tape_cm"], r["dH_tape_cm"]) if v is not None)
            md.append(f"* {r['object']} {r['method']}: centre σ {r['centre_sigma_cm']:.2f} cm "
                      f"({'≤' if r['centre_sigma_cm'] <= 1.5 else '>'} 1.5 cm), tape within 1 cm: {'yes' if tape_ok else 'no'}")
    (out / "repeatability.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    return rows, agreement, drift


def main(argv=None) -> int:
    """Command line entry point (see the module docstring)."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root", help="QD3D folder (Data/QD3D)")
    ap.add_argument("--out", help="output folder (default <root>/reports)")
    args = ap.parse_args(argv)
    root = Path(args.root)
    out = Path(args.out) if args.out else root / "reports"
    rows, agreement, drift = run(root, out)
    for r in rows:
        print(f"{r['object']:<16} {r['method']}  n={r['n']}  centre σ {r['centre_sigma_cm']:.2f} cm  yaw σ {r['yaw_sigma_deg']:.2f} deg  "
              f"size σ {r['size_sigma_pct']:.2f} %  dW/dD/dH {fmt(r['dW_tape_cm'])}/{fmt(r['dD_tape_cm'])}/{fmt(r['dH_tape_cm'])} cm")
    print(f"report: {out / 'repeatability.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
