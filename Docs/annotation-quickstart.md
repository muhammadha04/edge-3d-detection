# QD3D Annotate quickstart (one page)

Ground-truth 3D boxes and recordings, made in the headset and analysed on the PC. **Nothing is printed and no markers are used.**

**Ground-truth box:** the tightest **upright** box that sits on the floor, is aligned with the object's front, and contains the whole object. Armrests, casters and base tips count. Corners usually float in the air, so you mark **extremes**, not corners.

## 1. Prepare the room (10 min)
1. With masking tape, tape a **room cross** on the floor (two strips about 30 cm long, crossing at right angles) near the objects. It defines the room frame. Never move it.
2. Tape a **drift cross** 2 m or more away. It measures drift.
3. Make a small pen dot on a table, for the tip calibration.
4. For each object, tape-measure **W** (left–right), **D** (front–back) and **H** (floor to top) to the extremes, in cm. Decide what counts as the front (e.g. "seat front edge").
5. Plug the headset in, run `GuardianOff.bat`, then start the app (or run `BuildAndDeploy.bat`).

## 2. In the headset
Start menu → **Mode: Annotate** → **Start annotating**. Trigger = click. Left **Menu** brings the panel in front of you; **X** hides it.

| Step | Screen | What you do | Check |
|---|---|---|---|
| 1 | Home | **Operator name** | — |
| 2 | Calibrate tip | Pick the controller → **Start** → put the tip cross on the dot, **hold the trigger** and tilt the controller in all directions for about 5 s → release → **Save** | Residual **< 3 mm** (repeat otherwise). Tips turn green. |
| 3 | Room | **New room** → **Place room cross**: tip on the cross centre (trigger), then on one arm ≥ 30 cm out (trigger) → **Save room frame** | Axes on the cross. Status "localised". |
| 4 | Room | **Drift check**: touch the drift-cross centre. The first touch sets the reference. **Place test box** on a tape mark. | — |
| 5 | Catalog | **Add object** → name, category, symmetry, front, notes; **Tape dims**; **Photo** | — |
| 6 | Annotate | Pick the object, **M1 Touch**, **Blind ON** → **Start**. Touch 2 points on the front edge, standing in front of it. Then touch the **front-, back-, left-, right- and top-most** points (any height; grip + trigger adds a point). | The box grows. The white plumb line shows the floor point. |
| 7 | Editor | Walk around. **Red dots** mean the object pokes out: point at that face, **hold grip** and pull it out. Right stick: move / rotate / size per mode (hold grip for 2 mm / 0.2° steps). Left stick ↕: height. Check the yellow **front arrow**. | No red dots. HUD Δ vs tape within ±1 cm. |
| 8 | — | **Confirm** → rate 1–5 → **Repeat** (step a few metres away first). Do this 3 times. | "3/3", spread shown. |
| 9 | Verify | Walk around and mark **OK** / **Redo** per object. | Boxes stay on the objects from every side. |
| 10 | Record | 5 Hz, Depth ON, Detector ON, lighting → **Drift check** (session_start) → **Start** → walk slowly around the objects for 2 min at 0.7–3 m → **Stop** → **Drift check** (session_end) | Dropped frames about 0. Free space above 2 GB. |
| 11 | Export | Optional zip (single file) | Path shown |

**Restart check (once per room):** quit and restart the app. The test box must reappear on its tape mark. If the panel says **ROOM ANCHOR NOT FOUND / NOT LOCALISED**, walk around the cross and press **Retry**. Re-place the cross only on the **same** tape cross.

**How much data**
- **Pilot:** 1 chair, M1 ×3 and M2 ×3 blind, 2 recordings (normal and dim light).
- **Dataset:** 15–25 objects over 2–3 rooms; at least 3 blind M1 repetitions per object; at least 3 recordings of 2 min per room (at least one per lighting condition); at most about 6 objects per recording. Keep 1 room for testing only.
- **Go-ahead:** centre σ ≤ 1.5 cm, box within 1 cm of the tape, drift < 1–2 cm.

## 3. On the PC (Windows)
```bash
pip install -r Tools/annotation/requirements.txt
```
```bash
PullSession.bat
```
```bash
AnalyseSessions.bat
```
`AnalyseSessions.bat` runs every tool in order. Each can also be run on its own:

| Tool | Output |
|---|---|
| `python Tools/annotation/load_session.py Data/QD3D` | summary per session |
| `python Tools/annotation/repeatability.py Data/QD3D` | `Data/QD3D/reports/repeatability.md` (+ CSV): σ per object/method, Δ tape, method agreement, drift |
| `python Tools/annotation/overlay.py Data/QD3D --detections detections.jsonl --depth` | `<session>/overlay.mp4`: green GT boxes must sit on the objects; depth dots on the surfaces |
| `python Tools/annotation/run_models.py Data/QD3D [--model x.onnx \| --plugin my.py:Model]` | `<session>/pc_*.jsonl` (device schema). `--selftest` checks against the app's model results. |
| `bash Tools/offline/run_tests.sh replay <session> --detections <file>` | `<session>/replay_<file>.jsonl`: the app's real DepthBoxFitter + tracker run on the recorded depth |
| `python Tools/annotation/evaluate.py Data/QD3D --out Data/eval` | `Data/eval/report.md`, `summary.csv`, `pairs.csv`, `plots/`: AP@0.25/0.5, centre/yaw/size error, 3D IoU, by distance, Spearman, ECE |
| `python Tools/annotation/export_dataset.py Data/QD3D --out Data/dataset [--val <id> --test <id>]` | `Data/dataset/coco/*.json`, `yolo/` (YOLO-pose, 9 keypoints, `dataset.yaml`), `raw.jsonl`; split by session |

**Where everything lives**
- **Headset:** `/sdcard/Android/data/com.amplilab.questdetect3d/files/QD3D/`
- **PC:** `Data/QD3D/` (git-ignored)
  - `catalog.json`, `calibration.json`, `events.jsonl` (audit trail)
  - `rooms/`, `annotations/`, `photos/`
  - `sessions/<id>/`: `session.json`, `frames/`, `frames.jsonl`, `depth/`, `depth.jsonl`, `poses.csv`, `detections.jsonl`, `events.jsonl`

**Tests without a headset:**
- `python Tools/annotation/tests/test_tools.py` (or `pytest Tools/annotation/tests`) uses a synthetic session.
- `bash Tools/offline/run_tests.sh` runs the C# logic.
- `Builds/selftest.txt` is written before every build.

**Problems**
- **Tip residual above 3 mm:** keep the tip still on the dot and tilt more.
- **Red dots that never go away:** a wall or clutter is touching the object. Check that it is not part of the object.
- **Keyboard does not appear:** the on-panel keyboard opens after 2 s.
- **Logs:** run `PullLog.bat nopause`, then look at `Builds/app_log.txt` (`ann_` lines).
