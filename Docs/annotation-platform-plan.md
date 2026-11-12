# QD3D Annotate: in-headset ground-truth annotation and recording platform (implementation plan)

**Goal.** A Quest 3 tool that produces **trustworthy, reusable 3D ground truth** for the thesis:
- name and catalogue objects;
- annotate each object's 3D box with a method you choose (manual, marker-assisted or hybrid);
- verify the boxes in passthrough;
- record sessions (camera frames, depth, poses, detector output);
- export everything to a PC tool that overlays, measures and replays.

**First milestone: a 1-chair pilot** that tells us which annotation method is accurate enough.

This document is the spec for the implementing agent. Build on branch `v2.1-yolo-depth` (Unity 6000.2.13f1, MRUK 85, Inference Engine 2.2.1). Keep the existing app working: annotation is a new **mode** in the start menu.

---

## 1. Principles (do not compromise)
1. **Truth independent of the detector.** By default, annotation never shows detector boxes. The "assisted" method exists but is flagged in the data.
2. **Repeatability measured, not assumed.** Every object is annotated **k ≥ 3 times**, blind (previous boxes hidden). The spread is the ground-truth error floor reported in the thesis.
3. **Room frame is reproducible.** Store boxes relative to a **reference marker on the floor** (printed QR code), with a persisted spatial anchor as backup. Never store them only in raw session world coordinates.
4. **Record raw data once, evaluate forever.** Recordings must allow re-running any detector offline (replay) without going back to the room.
5. **Everything is a plugin.** Annotation methods, marker sources and recorders sit behind interfaces, so new ones can be added without touching the UI.

## 2. Annotation methods (user picks per object; all write the same `UprightBox`)

| ID | Method | How | Expected precision | Use |
|---|---|---|---|---|
| **M1 Touch** (default) | Controller-tip touch | Physically touch the object's footprint extremes with the controller tip and press the trigger: front-left, front-right, back-right (projected to the floor). Then touch the highest point. 3 floor points give yaw, width and depth; the top point gives height. | ~0.5–1 cm (controller tracking) | Most objects; best accuracy |
| **M2 Point** | Ray to depth / floor | Ray from the controller hits floor/depth. Click the same 3 corners and the top. | ~1–3 cm at 1–2 m | Fast; large or far objects |
| **M3 Marker** | Printed QR code(s) | Tape a QR code flat on the floor at a known corner of the object, using a printed **corner jig** (an L-shaped sheet with the QR code at a measured offset from the inside corner). Push the jig against the object's corner or leg. The box comes from the QR pose plus tape-measured W×D×H. Optionally use 2 jigs on diagonal corners, which gives yaw and the footprint without a tape. | depends on QR pose accuracy (to be measured in the pilot) | Repeatable, operator-independent |
| **M4 Assisted** | Start from the YOLO + depth box | The detector proposes a box; the user corrects it with the editor. Flagged `assisted=true`. | — | Speed only; never the primary ground truth |

**Common box editor** (after any method):
- **Gizmo:** move along x/z, rotate around y, resize w/d/h with the thumbsticks.
- **Steps:** coarse 1 cm / 1°, fine 2 mm / 0.2° (toggle with the grip).
- **Snapping:** bottom to the floor (on by default); "front" face marked with an arrow and switchable in 90° steps (needed for chairs).

**Controller-tip calibration** (once per controller, stored):
1. Touch the 4 printed corners of the reference QR code on the floor (its pose is known) with the controller tip.
2. Solve the tip offset in the controller frame by least squares.
3. Show the residual. Ship a default offset so M1 works before calibration.

## 3. Markers
- **Primary: MRUK QR code trackables (no extra library).**
  - MRUK treats QR codes as trackables with a 6-DoF pose and a payload (`MRUKTrackable.MarkerPayloadString`).
  - The feature is **experimental**, needs the Spatial Data permission, Scene/Anchor/experimental features enabled, and MRUK/Core ≥ v83 (we have 85).
  - Meta advises QR codes be relatively large and close to the device. Print at **≥ 15 cm**, version ≤ 10.
  - Turn tracking off when not needed (it costs resources).
  - Known OS-update regressions exist: pin the OS version during the study.
- **Payload convention:**
  - `QD3D;REF;room=<roomId>` for the reference marker;
  - `QD3D;JIG;offx=<m>;offz=<m>` for corner jigs;
  - `QD3D;OBJ;id=<objectId>` (optional object tags).
- **Pose averaging:** average the QR pose over ≥ 60 tracked frames; report the position σ and yaw σ.
- **Optional secondary source:** AprilTag 36h11 from PCA frames (CPU readback, which we already have) with a native/C# AprilTag detector. Behind the same `IMarkerSource` interface, only if the MRUK QR accuracy turns out insufficient in the pilot.
- **Printables to generate** (PDF, A4, with a 10 cm scale bar to verify print size):
  - reference QR code;
  - 4 corner jigs (L-shape, QR code at a known offset, with cut and alignment lines);
  - 1 drift-check QR code.

## 4. Room frame and drift
- **Room frame:**
  - origin = reference QR code centre on the floor;
  - axes = QR code axes, made gravity-aligned (y up);
  - all ground-truth boxes are stored in this frame.
- **On load:** detect the reference QR code, which recreates the frame in the current session. Fall back to the persisted `OVRSpatialAnchor`.
- **Drift check:**
  - a second QR code ≥ 2 m away;
  - log its pose in the room frame at session start and end, and every time it is seen;
  - drift = change of that pose. Report it per session.

## 5. Data model (JSON, `persistentDataPath/QD3D/`)
```
QD3D/
  catalog.json                 objects (persist across sessions)
  rooms/<roomId>.json          reference QR payload, anchor UUID, notes
  annotations/<roomId>.json    all ground-truth boxes (room frame) + history
  sessions/<sessionId>/
    session.json               app/OS/MRUK versions, room, operator, lighting, settings
    frames/000123.jpg          PCA RGB (left; optional right), JPEG q90
    depth/000123.png           16-bit depth (mm), Quest depth texture readback, + depth_meta.jsonl (pose, fov/proj)
    frames.jsonl               per frame: timestamp, camera pose, intrinsics, exposure, head pose, frame id
    head.csv                   72 Hz head + controller poses
    detections.jsonl           YOLO/Objectron detections, fits, tracker states per frame
    markers.jsonl              QR detections (payload, pose, tracked)
    events.jsonl               annotation actions, drift checks, user notes
```
**Object (catalog):**
- `id`, `name` (entered with the system keyboard), `category` (dropdown of ObjectCategory plus custom);
- `measured_dims_cm` {w, d, h} from a tape, `symmetry` (none / 2 / 4-fold), `front_definition` (text);
- `photo` (PCA frame), `notes`, `created`.

**Annotation:**
- `object_id`, `room_id`, `method` (M1–M4), `assisted`, `repetition` (1…k), `blind` (bool);
- `box` {`bottom_center`, `yaw_deg`, `size`} in the room frame, and `front_quarter`;
- `raw_inputs` (touched points, QR poses, editor deltas), `operator`, `timestamp`, `duration_s`, `self_rating` (1–5).

**Versioning:** keep every repetition. The "accepted" ground truth = robust mean of the accepted repetitions; its spread is stored as `gt_sigma`.

## 6. App UI (new "Annotate" mode)
- **Tech:**
  - Meta Interaction SDK ray + poke on world-space uGUI panels;
  - system virtual keyboard for names;
  - this replaces the thumbstick-only menu for this mode (the detection mode stays as is).
- **Screens:**
  1. **Room:** create or select a room, then scan the reference QR code (shows the frame axes). Shows drift-check status.
  2. **Catalog:** add an object (name, category, dims, symmetry, photo), edit, delete. List with annotation counts per object.
  3. **Annotate:**
     - pick an object, pick a method (M1–M4), follow on-screen steps, edit, confirm;
     - "repeat blind" toggle hides earlier boxes;
     - a counter shows k/3.
  4. **Verify:**
     - all ground-truth boxes overlaid (colour by object, thickness by `gt_sigma`);
     - walk around and mark each box OK / redo;
     - optional "detector vs ground truth" view, showing the live error in cm.
  5. **Record:**
     - start/stop, frame rate (2/5/10 Hz), depth on/off, right camera on/off, detector on/off;
     - shows MB used and dropped frames.
  6. **Export:** zip a session/room, and show the `adb pull` path (plus `PullSession.bat`).
- **HUD while annotating:** current step, tracking quality, the QR code's σ, live box dimensions vs tape-measured dimensions (difference in cm).

## 7. Code structure (Unity)
```
Runtime/Annotation/
  AnnotationMode.cs            mode controller (enter/exit, screens)
  Catalog.cs, RoomFrame.cs     data + persistence (JSON via JsonUtility/Newtonsoft)
  Methods/IAnnotationMethod.cs (Begin, Step(input), Result → UprightBox + raw inputs)
  Methods/TouchMethod.cs, PointMethod.cs, MarkerJigMethod.cs, AssistedMethod.cs
  BoxEditor.cs                 gizmo + thumbstick editing
  TipCalibration.cs
  Markers/IMarkerSource.cs, MrukQrSource.cs, (AprilTagSource.cs later)
  DriftMonitor.cs
Runtime/Recording/
  SessionRecorder.cs           frames (AsyncGPUReadback → JPEG on a worker thread), poses, events
  DepthRecorder.cs             EnvironmentDepth texture → 16-bit PNG + reprojection metadata
  DetectionLogger.cs           hooks Detect3DApp outputs
Editor/Printables/             generate QR/jig PDFs (or Tools/printables.py)
Tools/annotation/ (Python)
  load_session.py              reader for the format above
  overlay.py                   draw ground-truth boxes (and detections) on recorded frames → MP4
  repeatability.py             per-object/method spread → table + plots
  evaluate.py                  centre / yaw / size error, 3D IoU, AP@0.25/0.5; confidence calibration (ECE, reliability)
Tools/offline/RecordedDepthRaycaster.cs   IDepthRaycaster over recorded depth → replay DepthBoxFitter on real data
```
Reuse: `UprightBox`, `CameraModel`, `IDepthRaycaster`, `Log`, `SessionMenu` (add an "Annotate" entry), `BoxVisual` (ground-truth colour style).

## 8. Implementation milestones (each must build and be testable on device)
| # | Deliverable | Acceptance |
|---|---|---|
| A0 | Mode skeleton, data model, JSON persistence, start-menu entry | Create a room and an object; they survive an app restart |
| A1 | Catalog UI with keyboard + photo | Name an object "Chair_01", category Chair, dims, photo |
| A2 | Reference QR code + room frame + drift monitor | Frame axes shown on the QR code; drift logged; boxes reappear after a restart |
| A3 | M1 Touch + tip calibration + box editor + blind repeat | 3 repetitions of Chair_01 stored; spread shown in cm |
| A4 | Verify screen | Overlays align when walking around; OK/redo saved |
| A5 | Recorder + export + `PullSession.bat` + `overlay.py` | 2-min walk-around → MP4 with ground-truth boxes on recorded frames |
| A6 | M3 marker jigs + printables; M2 Point | Same chair annotated with M1, M2, M3 (×3 each) |
| A7 | `repeatability.py`, `evaluate.py`, `RecordedDepthRaycaster` replay | Pilot report generated (section 9) |
| A8 | M4 Assisted, AprilTag source (only if needed) | — |

## 9. The 1-chair pilot (what you do with A0–A7)
1. Print the reference QR code (≥ 15 cm), a drift QR code and 2 corner jigs. Check the printed size with the scale bar.
2. Tape the reference QR code flat on the floor near the chair, and the drift QR code ≥ 2 m away.
3. Measure the chair with a tape (W, D, H, footprint extremes) and take a photo.
4. Create room "Lab" and object "Chair_01".
5. Calibrate the controller tip.
6. Annotate blind: **M1 ×3, M2 ×3, M3 ×3**. Also annotate after moving the chair once (move test).
7. Verify: walk around and mark OK/redo.
8. Record two 2-min walk-arounds (normal and dim light) with the detector on.
9. Export, then run `repeatability.py` and `evaluate.py`.

**Pilot report must contain:**
- **Per method:** centre σ, yaw σ, size σ, time per annotation.
- **Agreement between methods:** M1 vs M3 centre and yaw difference.
- **Measured dimensions vs annotated dimensions.**
- **QR pose σ** and **drift per session.**
- **YOLO + depth error vs ground truth** on the recordings: centre, yaw, size, 3D IoU; confidence vs error.

**Decision rule:**
- Choose the method with the smallest σ that stays within 1 cm of tape-measured size.
- If centre σ is ≤ 1.5 cm, proceed to the full dataset (15–25 objects, 2–3 rooms, 1 held out).
- If not, add the AprilTag source or phone-LiDAR validation.

## 10. Practicalities
- **Settings:** pin OS/MRUK versions, keep PCA at 1280×960 on the left camera, disable auto-update, Guardian off (`GuardianOff.bat`).
- **Privacy:** no people in frame (or blur them before sharing); recordings stay on lab machines.
- **Storage:** about 45 MB/min of RGB at 5 Hz plus about 60 MB/min of depth. Warn below 2 GB free.
- **Performance:** keep the detector optional during recording; JPEG encoding on a worker thread; never block the main thread.
- **Logs:** every annotation and edit goes to `events.jsonl` (an audit trail for the thesis).
