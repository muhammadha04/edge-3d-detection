"""Tune the Hybrid fusion settings and calibrate its confidence, leave-one-session-out.

For every setting combination, replays pc_hybrid_chair.jsonl of every session (real fitter + HybridFuser + tracker,
Tools/offline/run_tests.sh replay ... --kp-yaw-weight Y --kp-weight K --shape-agree A) and scores the tracks with
evaluate.py. Selection is honest: for each session, the setting is chosen on the OTHER sessions and its numbers are
reported on the held-out one. The final setting is the best on all sessions.

Objective (lower is better, per matched track box): median heading error (deg) + median centre error (cm)
+ median size error (cm) - 20 * AP@0.25 (so recall cannot be traded away).

Confidence: isotonic map track confidence -> P(IoU >= 0.25), fitted leave-one-session-out; ECE before / after.

Usage: python Tools/annotation/tune_hybrid.py Data/QD3D [--bash <bash.exe>] [--quick]
Output: Data/tune_hybrid/report.md, results.csv, calibration.json
"""
import argparse, csv, itertools, json, os, statistics as st, subprocess, sys
from collections import defaultdict

GRID = {"kp_yaw_weight": [0.0, 0.5, 1.0], "kp_weight": [0.35, 0.7], "shape_agree": [25, 35]}
QUICK = {"kp_yaw_weight": [0.0, 1.0], "kp_weight": [0.35, 0.7], "shape_agree": [25]}


def cfg_name(c):
    return "y%.2f_k%.2f_a%d" % (c["kp_yaw_weight"], c["kp_weight"], c["shape_agree"])


def replay(bash, session, c, out):
    cmd = [bash, "Tools/offline/run_tests.sh", "replay", session, "--detections", "pc_hybrid_chair.jsonl",
           "--kp-yaw-weight", str(c["kp_yaw_weight"]), "--kp-weight", str(c["kp_weight"]),
           "--shape-agree", str(c["shape_agree"]), "--out", out]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-800:], r.stderr[-800:])
        raise SystemExit("replay failed: " + " ".join(cmd))


def ap25(rows):
    """Simple per-session AP proxy from matched pairs: fraction of GT-frames matched with IoU >= 0.25."""
    return sum(1 for r in rows if r["iou"] >= 0.25) / max(1, len(rows))


def metrics(rows):
    if not rows:
        return None
    return {"n": len(rows), "yaw": st.median(r["yaw"] for r in rows), "centre": st.median(r["centre"] for r in rows),
            "size": st.median(r["size"] for r in rows), "iou": st.median(r["iou"] for r in rows), "hit25": ap25(rows)}


def objective(m):
    return m["yaw"] + m["centre"] + m["size"] - 20 * m["hit25"]


def isotonic(xs, ys):
    """Pool-adjacent-violators; returns sorted breakpoints (x, y)."""
    pts = sorted(zip(xs, ys))
    blocks = [[x, x, y, 1] for x, y in pts]  # xmin, xmax, mean, weight
    out = []
    for b in blocks:
        out.append(b)
        while len(out) > 1 and out[-2][2] > out[-1][2]:
            a, c = out[-2], out.pop()
            w = a[3] + c[3]
            a[2] = (a[2] * a[3] + c[2] * c[3]) / w
            a[1], a[3] = c[1], w
    return [((b[0] + b[1]) / 2, b[2]) for b in out]


def apply_iso(table, x):
    if x <= table[0][0]:
        return table[0][1]
    for (x0, y0), (x1, y1) in zip(table, table[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / max(1e-9, x1 - x0)
    return table[-1][1]


def ece(probs, correct, bins=10):
    tot, e = len(probs), 0.0
    for b in range(bins):
        idx = [i for i, p in enumerate(probs) if b / bins <= p < (b + 1) / bins or (b == bins - 1 and p == 1.0)]
        if idx:
            e += len(idx) / tot * abs(sum(probs[i] for i in idx) / len(idx) - sum(correct[i] for i in idx) / len(idx))
    return e


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data")
    ap.add_argument("--bash", default="bash")
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--out", default=os.path.join("Data", "tune_hybrid"))
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    sessions = sorted(os.path.join(a.data, "sessions", s) for s in os.listdir(os.path.join(a.data, "sessions"))
                      if os.path.exists(os.path.join(a.data, "sessions", s, "pc_hybrid_chair.jsonl")))
    grid = QUICK if a.quick else GRID
    cfgs = [dict(zip(grid, v)) for v in itertools.product(*grid.values())]
    print(f"{len(cfgs)} settings x {len(sessions)} sessions")
    names = []
    for i, c in enumerate(cfgs):
        n = cfg_name(c)
        names.append(n)
        for s in sessions:
            replay(a.bash, s, c, os.path.join(s, f"replay_tune_{n}.jsonl"))
        print(f"  [{i + 1}/{len(cfgs)}] {n}")
    ev = os.path.join(a.out, "eval")
    cmd = [sys.executable, "Tools/annotation/evaluate.py", *sessions, "--out", ev, "--no-plots"]
    for n in names:
        cmd += ["--detections", f"replay_tune_{n}.jsonl"]
    subprocess.run(cmd, check=True, capture_output=True)

    # matched track boxes per (setting, session)
    data = defaultdict(list)
    for r in csv.DictReader(open(os.path.join(ev, "pairs.csv"))):
        if r["unit"] != "tracks" or not r["source"].startswith("replay_tune_"):
            continue
        try:
            row = {"yaw": float(r["yaw_deg"]), "centre": float(r["centre_cm"]), "size": float(r["size_abs_cm"]),
                   "iou": float(r["iou"]), "score": float(r["score"])}
        except ValueError:
            continue
        data[(r["source"][12:-6], os.path.basename(r["session"]))].append(row)
    snames = [os.path.basename(s) for s in sessions]

    # leave-one-session-out selection
    lines, held = [], []
    for test in snames:
        train = [s for s in snames if s != test]
        best = min(names, key=lambda n: objective(metrics(sum((data[(n, s)] for s in train), [])) or
                                                  {"yaw": 1e9, "centre": 0, "size": 0, "hit25": 0}))
        base = metrics(data[(names[0], test)]) if (names[0], test) in data else None
        m = metrics(data[(best, test)])
        held.append((test, best, m, base))
    final = min(names, key=lambda n: objective(metrics(sum((data[(n, s)] for s in snames), []))))

    with open(os.path.join(a.out, "results.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["setting", "session", "n", "yaw_deg", "centre_cm", "size_cm", "iou", "hit25"])
        for n in names:
            for s in snames + ["ALL"]:
                m = metrics(data[(n, s)] if s != "ALL" else sum((data[(n, x)] for x in snames), []))
                if m:
                    w.writerow([n, s, m["n"], round(m["yaw"], 2), round(m["centre"], 2), round(m["size"], 2),
                                round(m["iou"], 3), round(m["hit25"], 3)])

    # confidence calibration on the final setting, leave-one-session-out
    raw_p, cal_p, corr = [], [], []
    for test in snames:
        train = sum((data[(final, s)] for s in snames if s != test), [])
        if not train:
            continue
        table = isotonic([r["score"] for r in train], [1.0 if r["iou"] >= 0.25 else 0.0 for r in train])
        for r in data[(final, test)]:
            raw_p.append(r["score"])
            cal_p.append(apply_iso(table, r["score"]))
            corr.append(1.0 if r["iou"] >= 0.25 else 0.0)
    full = sum((data[(final, s)] for s in snames), [])
    table = isotonic([r["score"] for r in full], [1.0 if r["iou"] >= 0.25 else 0.0 for r in full])
    # compact: 11 points at confidence 0, 0.1, ..., 1
    compact = [[round(x / 10, 2), round(apply_iso(table, x / 10), 3)] for x in range(11)]
    json.dump({"setting": final, "calibration_points": compact, "ece_raw": ece(raw_p, corr),
               "ece_calibrated": ece(cal_p, corr)}, open(os.path.join(a.out, "calibration.json"), "w"), indent=2)

    fm = lambda m: "-" if not m else f"heading {m['yaw']:.1f}°, centre {m['centre']:.1f} cm, size {m['size']:.1f} cm, IoU {m['iou']:.2f}, hit@0.25 {m['hit25']:.2f}"
    rep = ["# Hybrid tuning (leave-one-session-out)", "",
           f"Settings tried: {len(names)} ({', '.join(grid)}). Baseline = current code ({names[0]}).", "",
           "| held-out session | chosen on the others | held-out result | baseline on same session |", "|---|---|---|---|"]
    for test, best, m, base in held:
        rep.append(f"| {test} | {best} | {fm(m)} | {fm(base)} |")
    rep += ["", f"**Final setting (best on all sessions): {final}**", "",
            "All sessions: " + fm(metrics(full)), "Baseline all: " + fm(metrics(sum((data[(names[0], s)] for s in snames), []))), "",
            f"Confidence calibration (isotonic, leave-one-session-out, over boxes matched to a GT chair; false positives not included): ECE {ece(raw_p, corr):.3f} -> {ece(cal_p, corr):.3f}",
            "Calibration points (confidence -> P(IoU>=0.25)): " + ", ".join(f"{x}:{y}" for x, y in compact)]
    open(os.path.join(a.out, "report.md"), "w").write("\n".join(rep) + "\n")
    print("\n".join(rep))


if __name__ == "__main__":
    main()
