"""Honest confidence calibration for the tracker boxes the app shows (false positives included).

Every Nth frame of a replay file, every track (Tentative or Confirmed, as drawn by the app) is one sample:
correct = 3D IoU >= 0.25 with some ground-truth box of that room. Isotonic map confidence -> P(correct),
fitted leave-one-session-out; reports ECE before/after, AUROC, and precision/recall-of-shown-boxes per threshold.

Usage: python Tools/annotation/calibrate_confidence.py Data/QD3D [--replay replay_pc_hybrid_chair.jsonl] [--every 5]
Output: Data/calibration/<replay name>/report.md, calibration.json
"""
import argparse, glob, json, os, sys
sys.path.insert(0, os.path.dirname(__file__))
from qd3d.geometry import UprightBox, iou_3d
from tune_hybrid import isotonic, apply_iso, ece


def auroc(scores, labels):
    pos = [s for s, l in zip(scores, labels) if l]; neg = [s for s, l in zip(scores, labels) if not l]
    if not pos or not neg:
        return float("nan")
    allv = sorted((s, l) for s, l in zip(scores, labels))
    rank, r = {}, 1
    i = 0
    while i < len(allv):  # average ranks for ties
        j = i
        while j < len(allv) and allv[j][0] == allv[i][0]:
            j += 1
        for k in range(i, j):
            rank.setdefault(allv[k][0], (r + r + (j - i) - 1) / 2)
        r += j - i; i = j
    return (sum(rank[s] for s in pos) - len(pos) * (len(pos) + 1) / 2) / (len(pos) * len(neg))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data"); ap.add_argument("--replay", default="replay_pc_hybrid_chair.jsonl")
    ap.add_argument("--every", type=int, default=5); ap.add_argument("--out", default=os.path.join("Data", "calibration"))
    a = ap.parse_args()
    gt = {}
    for f in glob.glob(os.path.join(a.data, "annotations", "*.json")):
        d = json.load(open(f))
        gt[d["room_id"]] = [UprightBox.from_dict(o["accepted"]["box"]) for o in d["objects"] if o.get("accepted")]
    samples = {}  # session -> [(conf, correct)]
    for s in sorted(glob.glob(os.path.join(a.data, "sessions", "*"))):
        p = os.path.join(s, a.replay)
        if not os.path.exists(p):
            continue
        sess = json.load(open(os.path.join(s, "session.json")))
        room = sess["room"].get("room_id") or sess["room"].get("id") or os.path.basename(s).split("_", 2)[2]
        G = gt.get(room, [])
        rows = []
        for i, l in enumerate(open(p)):
            if i % a.every:
                continue
            for t in json.loads(l).get("tracks") or []:
                if t.get("category") != "Chair" or not t.get("box_room"):
                    continue
                b = UprightBox.from_dict(t["box_room"])
                best = max((iou_3d(b, g) for g in G), default=0.0)
                rows.append((float(t["confidence"]), 1.0 if best >= 0.25 else 0.0))
        samples[os.path.basename(s)] = rows
    names = [k for k, v in samples.items() if v]
    raw, cal, lab = [], [], []
    for test in names:
        train = [r for n in names if n != test for r in samples[n]]
        table = isotonic([c for c, _ in train], [y for _, y in train])
        for c, y in samples[test]:
            raw.append(c); cal.append(apply_iso(table, c)); lab.append(y)
    full = [r for n in names for r in samples[n]]
    table = isotonic([c for c, _ in full], [y for _, y in full])
    points = [[round(x / 20, 2), round(apply_iso(table, x / 20), 3)] for x in range(21)]
    out = os.path.join(a.out, os.path.splitext(a.replay)[0]); os.makedirs(out, exist_ok=True)
    lines = [f"# Confidence calibration: {a.replay}", "",
             f"{len(full)} track samples (every {a.every}th frame, all shown tracks), {sum(lab)/max(1,len(lab)):.0%} correct (IoU>=0.25)", "",
             f"Leave-one-session-out: ECE {ece(raw, lab):.3f} raw -> {ece(cal, lab):.3f} calibrated; AUROC {auroc(raw, lab):.3f}", "",
             "| min confidence shown | boxes shown | correct among shown | correct boxes kept |", "|---|---|---|---|"]
    tot_ok = sum(lab)
    for th in [0.0, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8]:
        sel = [y for c, y in zip(raw, lab) if c >= th]
        if sel:
            lines.append(f"| {th:.1f} | {len(sel)/len(raw):.0%} | {sum(sel)/len(sel):.0%} | {sum(sel)/max(1,tot_ok):.0%} |")
    lines += ["", "Calibration map (raw confidence -> P(correct)): " + ", ".join(f"{x}:{y}" for x, y in points)]
    open(os.path.join(out, "report.md"), "w").write("\n".join(lines) + "\n")
    json.dump({"replay": a.replay, "points": points, "ece_raw": ece(raw, lab), "ece_cal": ece(cal, lab),
               "auroc": auroc(raw, lab)}, open(os.path.join(out, "calibration.json"), "w"), indent=2)
    print("\n".join(lines))


if __name__ == "__main__":
    main()
