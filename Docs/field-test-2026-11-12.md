# Field test 2026-11-12: Objectron vs YOLO11n + depth (Quest 3, ~3 min, room with many chairs)

Source: `Builds/app_log.txt` (app 2.1.0, YOLO11n@320 FP16 on GPUCompute; Objectron on its own GPU graph).

## Measurements
| | Objectron | YOLO11n@320 + depth |
|---|---|---|
| Detection latency (frame → result) | 40–67 ms | **67–107 ms** (YOLOv9-t@640 with per-frame layer splitting: ~700 ms) |
| Detection rate | 8.4–10.6 Hz | 7.0–9.4 Hz |
| Fits OK / failed | 755 / 0 | 2330 / 43 (mostly "only support visible" when close to or above a chair) |
| Confirmed objects | 12 chairs | 13 (chairs + 1 screen at 0.85 m height) |
| Tracks created / merged / removed (~45 s vs ~95 s) | 34 / 0 / 17 | **138 / 70 / 41** |
| Chair heights | 75–100 cm | **66–77 cm** (too short) |
| Typical σ heading per frame | — | **26°** (maximum: square footprints) |
| Confidence at confirmation | 0.56–0.65 | 0.55–0.59 (limited by the consistency term, 0.64–0.81) |

**Cross-method agreement** (two independent methods, same chairs; floor-plane centre):

| Chair | Objectron | YOLO + depth | Difference |
|---|---|---|---|
| A | (0.78, 1.19) | (0.80, 1.20) | 2 cm |
| B | (−0.39, 1.93) | (−0.35, 1.86) | 8 cm |
| C | (1.12, −0.29) | (1.20, −0.34) | 9 cm |
| D | (4.46, 4.00) | (4.29, 4.08) | 19 cm (≈4.5 m away) |

## Problems found → fixes (app 2.1.1)
| # | Problem | Cause | Fix | Where |
|---|---|---|---|---|
| 1 | Duplicate tracks: 138 created, 70 merged | Single-view depth boxes jump (unseen back completion, 90° heading ambiguity) by more than the distance gate | Depth-box observations also associate when either centre lies inside the other's footprint (+10 cm). Duplicate depth tracks merge when one centre is inside the other's footprint. The 10 cm margin keeps chairs ≥ 45 cm apart separate. | `ObjectTracker.ProcessMulti`, `MergeDuplicates`, `InsideFootprint` |
| 2 | Chairs too short | Depth misses the thin backrest top rail; the 2D-box-top correction only kicked in above a 15 cm gap, at 50 % | For seating: correct when the gap is > 4 cm, at 80 %, capped at 35 cm. Logged as `top+=`. | `DepthBoxFitter` (height step) |
| 3 | Heading σ ≈ 26° on chairs | Nearly square footprint → flat minimum-area curve | Chairs: heading from a line fit (PCA) to the top 25 % of points (the backrest spans the chair's width). Used when it is a clear line (≥ 2:1) and more certain than the footprint. Logged as `yaw_src=backrest\|footprint`. | `DepthBoxFitter.TryBackrestYaw` |

## Offline check (synthetic room, 21/21 tests pass)
| | Before | After |
|---|---|---|
| Single-view heading error (median) | 3.0° | **1.4°** |
| Single-view height error (median) | 2.3 cm | **1.6 cm** |
| Walk-around | 4/4 objects correct, 0 duplicates | unchanged |
| Tracks created in walk-around | 5 for 4 objects | 5 |

The synthetic room does not reproduce the real churn (its single views are cleaner), and its full-slab backrest has no thin top rail. Fixes 1 and 2 therefore **must be verified on the headset**.

## What to check in the next log
- `grep -c track_new` and `grep -c track_merged`: tracks created should drop well below 138 per ~95 s (ideally close to the number of objects seen, plus a few).
- `yolo_fit … yaw_src=backrest` on chairs, with σyaw well below 26°.
- `top+=` on chairs, and confirmed chair heights in the 75–95 cm range.
- Confidence at confirmation above 0.6 (from a better consistency term).
