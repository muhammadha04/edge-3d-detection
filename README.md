# QuestDetect3D v2.2: on-device 3D object detection with confidence for Meta Quest 3, plus in-headset ground truth (QD3D Annotate)

Runs entirely on the Quest 3, using only its own cameras and depth. Each piece of furniture is detected, measured as a **metric 3D box** (position, heading, width × depth × height in cm), fused across views and drawn in the room with a **confidence % and ± uncertainty**.

Part of the M.Sc. thesis *Real-Time 3D Object Detection for Low Vision Accessibility Using Augmented Reality* (University of Haifa, AmpliLab).

```
Passthrough camera (1280×960, GPU texture)
   └─► YOLO11n @320, FP16, Unity Inference Engine on the Quest GPU ──► 2D boxes (chair, screen)
          └─► Quest Depth API rays inside each box ──► DepthBoxFitter ──► metric upright box + uncertainty
                 └─► multi-view tracker (class voting, confidence) ──► world-locked box, sized by uncertainty
```
MediaPipe Objectron (chair keypoints + least-squares box solver, the v2 pipeline) remains available as a baseline.

## Using the app

**Start menu** (shown at launch; **A** or the left **Menu** button reopens it). Use the thumbstick to select a row and change its value, and **A** to change/start.

| Option | Values |
|---|---|
| Mode | **Detect** (live 3D detection, below) / **Annotate** (QD3D Annotate: ground truth, recording, see below) |
| Detector | **YOLO + depth** (chair, sofa, bench, table, bed, toilet, plant, screen, fridge, bag) / Objectron (chairs) |
| Inference | GPU / CPU (YOLO backend; switch if the GPU misbehaves) |
| Display | Box (inflated and softened while uncertain) / lab-chair 3D scan + debug points |
| Show boxes from | 30–95 % (default 55 % = confirmed objects only) |

Each session starts with an empty room; only the menu settings are remembered.

**Buttons:** **B** shows the debug view (the 2D detection, plus the depth points used: cyan inside the box, yellow rejected). **Y** resets. **X** hides the HUD.

**Before walking around:** run `GuardianOff.bat` to turn the boundary off (`GuardianOff.bat on` turns it back on). Glance at the floor for a second after starting, keep objects 0.5–3.5 m away, and walk around them to raise confidence.

## How a box is made (`Runtime/Geometry/DepthBoxFitter.cs`)
1. **Depth rays:** an 11×11 jittered grid of depth rays inside the YOLO box.
2. **Support removal:** floor and table-top hits are dropped, using the floor model from `FloorEstimator`.
3. **Object/background separation:** hits are sorted by range and split at gaps; the nearest cluster that holds a substantial share of the hits is the object.
4. **Wall rejection:** probe rays just outside the 2D box find walls behind the object. Points on or behind a wall are removed, and the wall also caps the box's back.
5. **Height:** bottom = floor (or lowest points); top = robust top of the points. The 2D box's top edge corrects for thin tops that depth misses.
6. **Yaw:** the minimum-area footprint rectangle. How flat the area-versus-angle curve is gives the yaw uncertainty.
7. **Completion:** one view only sees the front of an object, so the depth axis is extended away from the camera, by at most min(visible width, the category's typical depth). Multi-view fusion then replaces this guess.
8. **Uncertainty:** depth noise σ(z) = 0.8 cm + 0.45 cm·z², averaged over the points, plus the amount of completion. Each term is logged.

**Tracking** (`Runtime/Tracking`): association within a category group (seating, table, …). The label is a weighted vote, so chair/bench confusion settles. Yaw is fused modulo 90° with an x/z size swap. **Confidence** = evidence × fit quality × cross-view consistency × agreement × view diversity × presence; a box is confirmed at ≥ 55 % with ≥ 4 hits.

## Field results
`Docs/field-test-2026-11-12.md`:
- detection latency **67–107 ms** at 7–9 Hz (YOLO11n@320), down from ~700 ms (YOLOv9-t@640);
- Objectron and YOLO + depth agree on chair positions within 2–9 cm at 1–3 m;
- three fixes in 2.1.1: duplicate tracks, chair height, chair heading from the backrest.

## Model choice (measured)
**Current:** YOLO11n (Ultralytics, AGPL-3.0), exported at **320×320**, FP16, chairs and screens only (`YoloDecode.Allowed`). On GPU the whole network is scheduled at once and the three outputs are read back together. On the test photos it beats YOLOv9-t@640 (chair 0.95 vs 0.79; the chair that YOLOv9 called "bench" is now "chair" 0.93).

**Earlier comparison (YOLOv9-t@640):**
The pretrained YOLOv9-t (MIT licence, from Meta's PCA samples) is exported by `Editor/YoloModelExport.cs` to **FP16** weights. Meta's prebuilt **uint8** file loses a lot of class confidence. Self-test against onnxruntime FP32 (`Docs/selftest-2026-11-12.txt`):

| Photo / class | ONNX FP32 | Meta uint8 | Ours FP16 |
|---|---|---|---|
| chair (close) | 0.97 | 0.91 | 0.97 |
| chair (room) | 0.79 | 0.45 | 0.79 |
| tv | 0.85 | missed | 0.85 |
| chair seen as "bench" | 0.75 | missed | 0.75 |

The FP16 model is 4.3 MB. Its boxes match FP32 with IoU ≥ 0.999.

## Testing without a headset
- **Offline (mono, or the Mono inside the Unity editor on Windows):** `Tools/offline/run_tests.sh` compiles the real pipeline and annotation code against a minimal UnityEngine stand-in (`UnityShim.cs`). `Tools/offline/typecheck.sh` compiles all runtime code against the Unity assemblies in about 30 s.
- **PC tools:** `python Tools/annotation/tests/test_tools.py` (or pytest) runs the tools on a synthetic session (`make_synthetic_session.py`), including a keypoint-projection fixture shared with the C# tests.
- **In Unity:** `QuestDetect3D → Run Self-Test`, which also runs automatically before every batch build and writes `Builds/selftest.txt`.
- **Synthetic room** (`Editor/Tests/SyntheticRoom.cs`): chairs, a table and a sofa built from parts, floor and walls. It uses the real Quest camera intrinsics (f = 865.7 px) and Quest-like depth: 320 px angular resolution, σ(z) noise, dropouts, flying pixels and noisy normals.

| Test | Result |
|---|---|
| Single view, 72 viewpoints, 1.3–2.8 m | centre error 3.6 cm (median), height 2.3 cm, yaw 3° |
| Single view, floor not yet known | centre error 5.8 cm |
| Chair 5 cm in front of a wall | centre error 3.4 cm (20 cm before wall rejection) |
| 20 s walk-around: 2 chairs, table, sofa; 15 % missed detections, 20 % chair→bench mislabels | all 4 confirmed with the correct label, centre error 2–8 cm, size error 7–16 %, 0 false / 0 duplicate boxes |
| Backrest side used to orient the scan | 5 / 5 correct |
| YOLO FP16 in Unity vs onnxruntime | 4 / 4 match |

## Ground-truth annotation: QD3D Annotate (Mode: Annotate)
Manual, marker-free ground truth and recordings for the thesis. **One-page guide: [`Docs/annotation-quickstart.md`](Docs/annotation-quickstart.md)** (spec: [`Docs/annotation-platform-plan.md`](Docs/annotation-platform-plan.md); QR / marker parts not implemented).

- **Room frame:** a persisted `OVRSpatialAnchor` placed by touching a taped floor cross (centre, then one arm). Boxes are stored in this gravity-aligned frame. If the anchor cannot be localised the app says so and never creates a new frame silently. Drift is checked with a second cross and by re-touching the room cross.
- **Tip calibration:** pivot calibration (rotate the controller around a pen dot; least squares; residual in mm). A default offset ships.
- **Methods:**
  - **M1 Touch:** front reference + extremes with the controller tip.
  - **M2 Point:** the same with a ray on depth or the floor.
  - **M4 Assisted:** starts from the live YOLO + depth box; flagged and never the primary ground truth.
- **Box editor:** face push/pull, coarse/fine thumbstick steps, floor snap, front arrow in 90° steps. Live checks: translucent walls, depth points outside the box in red, plumb lines, box vs tape Δ.
- **Blind repeats:** k ≥ 3 per object. Accepted box = per-coordinate median; `gt_sigma` (centre cm, yaw °, size %); every repetition and every action is kept (`events.jsonl` audit trail).
- **Recorder:** JPEG frames (2/5/10 Hz) with pose and intrinsics, 16-bit depth PNG + reprojection matrices, 72 Hz poses, detector output, events. Encoding runs on a worker thread.
- **PC (Windows):** `PullSession.bat`, then `AnalyseSessions.bat`, which runs `load_session.py`, `repeatability.py`, `overlay.py`, `run_models.py`, the C# replay of the real fitter + tracker on recorded depth, `evaluate.py` and `export_dataset.py`. All in `Tools/annotation` (Python 3.10+, `requirements.txt`).

## Build
Run `BuildWatcher.bat` (it watches `Builds/trigger_*`) or `BuildAndDeploy.bat`. The build exports the FP16 model, runs the self-test, rebuilds `Scenes/Detect3D.unity` and produces `Builds/QuestDetect3D.apk` (`com.amplilab.questdetect3d`, 2.2.8). Logs are written to `qd3d_log.txt` on the headset; pull them with `PullLog.bat`. They include `yolo_fit` lines showing every term of each fit.

Requirements: Unity 6000.2.13f1, Meta MRUK 85, Meta Interaction SDK 85, Newtonsoft JSON 3.2.1, Unity Inference Engine 2.2.1, and the homuler MediaPipe tarball in `Packages/` (Objectron baseline only; not in git).

## Code map
```
Runtime/Detect3DApp.cs            menu → capture → detect → fit → track → render; HUD; logs
Runtime/Detection/                YoloDetector (Inference Engine), YoloDecode (NMS), ObjectronDetector, PCA frame source
Runtime/Geometry/                 DepthBoxFitter, BoxSolver (Objectron), Floor/SupportPlane, BackrestEstimator, IDepthRaycaster
Runtime/Tracking/                 TrackedObject, ObjectTracker, ConfidenceModel
Runtime/Visuals/                  BoxVisual (uncertainty-scaled box, scan), SessionMenu, HudPanel, DebugOverlay
Runtime/Annotation/               QD3D Annotate: AnnotationMode (operator guide in its header), Data/ (JSON model + store),
                                  Logic/ (room frame, touch box, editor ops, pivot calibration, repeatability – unit-tested),
                                  Rig/ (controllers, anchors, keyboard), UI/ (screens), Visuals/, Session/ (live checks)
Runtime/Recording/                SessionRecorder, DepthCapture (+ Resources/QD3D_DepthCopy.compute), Png16, RecorderWorker
Editor/                           SceneBuilder, BuildScript, YoloModelExport, SelfTest, Tests/ (synthetic room + annotation tests)
Tools/offline/                    mono test runner, typecheck, replay fitter (RecordedDepthRaycaster + ReplayRunner)
Tools/annotation/                 PC tools (Python): session reader, overlay, dataset export, model runner, evaluation, repeatability
PullSession.bat, AnalyseSessions.bat   Windows: pull data from the headset, run the whole PC analysis
```

## Next steps
1. **On-device check:** measure the YOLO rate on GPU vs CPU, and compare real fits against the synthetic results.
2. **Measure Quest depth noise** (wall and board at 0.5–4 m) to replace the σ(z) placeholder.
3. **Real ground-truth set:** collect it with QD3D Annotate (pilot first), then fit a confidence calibration with `evaluate.py` (reliability diagram, ECE).
4. **CenterPose spike:** a pretrained keypoint model whose heatmaps give per-corner uncertainty. Its deformable convolutions must be converted first.
5. **Custom model:** YOLO backbone plus 9-keypoint and variance heads, trained on Unity-generated Quest-like data. Its uncertainty would propagate through the solver to the box covariance.
6. **User study tools:** the uncertainty level-of-detail cue and a Wizard-of-Oz latency mode (RQ2 and RQ3).

Licences: YOLOv9-t weights MIT (Meta samples). Objectron Apache-2.0.
