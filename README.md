# QuestDetect3D (v2): metric 3D object detection with confidence on Meta Quest 3

QuestDetect3D runs on a Quest 3 / 3S. It finds objects in the passthrough camera image, works out each one's **real metric 3D box** (position, heading, width × depth × height in cm), fuses that box across many views, and draws it anchored in the room with a **confidence score and ± uncertainty** that the user can see.

> This branch (`v2`) is a clean rewrite. The original prototype (branches `main`, `chair`, `twostage`, `scancalibration`, `feature/centerpose-chair`) is kept for reference. See [What changed from v1](#6-what-changed-from-v1).

```
 Passthrough camera ─► Objectron (2D box keypoints) ─► metric box fit ─► multi-view fusion ─► anchored box + "Chair 87 % · 48×51×86 cm ±2 cm"
        │                                                   ▲
        └──────── Depth API (floor / table height) ─────────┘
```

---

## 1. What the user sees

**Start menu** (opens at launch, and with **A** or the left **Menu** button during a session). Thumbstick up/down picks a row, left/right changes it, **A** starts:

| Option | Values | Effect |
|---|---|---|
| Display | **Box** / **3D scan + cyan keypoints** | Box: clean 3D box with size and confidence. Scan: the lab-chair 3D scan is fitted inside each box (section 2.9), with the cyan fitted keypoints from the debug view turned on |
| Show boxes from | 30–95 % (step 5, default **55 %**) | A box is drawn only while its confidence is at least this value. At 55 % and above you only see **confirmed** chairs, since confirmation happens at 55 %. Lower values also show candidates |

The two menu settings are remembered on the headset. **Detected objects are not**: every session (app launch, or Start from the menu) begins with an empty room. Spatial anchors are only used inside a session and are never saved.

The detector is set to **chairs only**.

- **Candidate** (pulsing box, label `Chair? 42 %`): seen, but not enough evidence yet.
- **Confirmed** (solid box with corner brackets): label `Chair 87 %`, then `48 × 51 × 86 cm ±2 cm`, then a confidence bar. The colour runs from red (low confidence) through amber to green (high).
- **HUD** (follows your gaze lazily): model rate and latency, floor status, number of objects, last fit error, rejected fits.

| Button | Action |
|---|---|
| A (right) / Menu (left) | Open the start menu (Start begins a new, empty session) |
| B (right) | Keypoint view (on by default in scan mode). Magenta = network keypoints; cyan = fitted box corners; yellow = error between them |
| Y (left) | Reset all objects |
| X (left) | Hide / show HUD |

Tips: glance at the floor for a second at start-up. Keep objects 1–3 m away. Walking around an object raises its confidence.

---

## 2. Pipeline in detail

### 2.1 Capture: `PcaFrameSource`
- Source: `PassthroughCameraAccess` (Meta MRUK), left RGB camera at 1280 × 960, with the real per-device **intrinsics** (focal length, principal point, sensor crop) and the **camera pose at the image timestamp**.
- The image is copied with a non-blocking `AsyncGPUReadback`. Meta documents that blocking copies return the *previous* frame.
- Each image is stored in a `FrameContext` together with its own `CameraModel` (pose + intrinsics). Every later geometric step uses that exact model, never "the current head pose".

### 2.2 Detection: `ObjectronDetector` (MediaPipe Objectron, via the homuler MediaPipeUnityPlugin)
- Two-stage model: an SSD (Open Images) finds a 2D crop, then a landmark network regresses the **9 projected keypoints of the object's 3D bounding box** (centre + 8 corners).
- **One frame in flight.** The next image is submitted only after the previous result arrives, so each result is paired with the exact pose of its image.
- The image is fed **unmirrored and upright**. Keypoints map to viewport as `(x, 1 − y)`.
- Objectron's own "lifted" 3D output is **not used for geometry**: its EPnP lift is only up to scale and assumes a phone camera. It is used for exactly one thing: voting which keypoint axis is "up" and scoring how upright the model thinks the object is.
- Objectron leaves every keypoint `id` at 0, so keypoints are indexed by list order.
- Runs on GPU at ~10–13 Hz, ~50 ms per inference on Quest 3.

### 2.3 Support plane from depth: `SupportPlaneEstimator`, `FloorEstimator`
**Yes, depth is used to localise the boxes**, not just to draw them. The Depth API (`EnvironmentRaycastManager`) answers "where does this ray hit the real world?". We use it in three ways:

1. **Floor model.** A few rays per second are cast down and ahead of the head. Hits on horizontal surfaces (normal·up > 0.9) build a robust running estimate of the floor height (low-percentile cluster + median, ±σ).
2. **Local support surface.** For each detection, rays are cast through the object's 4 bottom-corner keypoints and a few points under its footprint. The lowest consistent cluster of hits is the surface the object stands on (a floor, or a table for cups/cameras).
3. **Occlusion check.** When a tracked object should be visible but isn't detected, a depth ray checks whether something is in front of it before counting a "miss".

For floor objects (chair, shoe), the floor model and the local estimate are fused when they agree within 8 cm. Otherwise the floor model wins, because local rays often hit the object's own legs or seat.

**Why this matters.** A single image can't tell a small near chair from a large far one. Knowing the height of the plane the object stands on removes that ambiguity: the rays through the bottom corners must hit that plane, which fixes the box in metres.

Depth is **not** currently used to measure the object's own surface (e.g. a point cloud inside the box), and boxes are **not occluded** by real objects when rendered. Both are listed under [Next steps](#7-next-steps).

### 2.4 Metric box fit: `BoxSolver`
Unknowns: footprint centre (x, z), heading (yaw), width, depth, height, and support height y₀. Seven parameters; the box is constrained to be upright.

1. **Initialise.**
   - Intersect the 4 bottom-corner keypoint rays with the plane y = y₀. This gives a metric footprint, yaw, width, depth, and the corner handedness (which side is +x).
   - Height comes from matching the top-corner rays with vertical lines through the footprint corners.
2. **Refine** with Levenberg–Marquardt. It minimises the reprojection error of all 9 keypoints through the real PCA camera model:
   - Huber loss with a threshold of 4% of the object's on-screen size, so one bad keypoint can't drag the box.
   - A soft prior keeps y₀ near the depth-measured support height (weight from its σ).
3. **Outputs:**
   - the box (`UprightBox`: bottom centre, yaw, size);
   - RMS reprojection error in px **and** as a fraction of the object's on-screen size;
   - the inlier fraction;
   - the fitted 3D keypoints (cyan in the debug view).

Because every corner keypoint has a fixed identity (front-left-bottom, etc.), yaw is determined without the 90°/180° ambiguity v1 had.

Synthetic test (200 random views, 3 px keypoint noise, ±1 cm floor noise): position error ~1–2 cm, yaw < 2°, size ~1 cm.

### 2.5 Per-frame quality: `ConfidenceModel.ObservationQuality`
Each fit gets a quality q ∈ [0, 1], the product of six terms:

| Term | Meaning | Shape |
|---|---|---|
| reprojection | Does the fitted 3D box re-project onto the network's keypoints? | Gaussian of (RMS ÷ on-screen size), σ = 8 %, × inlier fraction |
| support | How well is the floor/table height measured? | Gaussian of depth σ; × fraction of rays that hit |
| gravity | Does the network's own 3D axis agree the object is upright? | rescaled \|cos(up, world-up)\| |
| size | Is the metric size plausible for the category? | 1 inside the category's range, Gaussian fall-off outside |
| distance | Pixels on target | 1 for 0.45–2.8 m, decays beyond |
| model | Detector score, when provided | clamp |

On device this is typically 0.85–0.95 for a chair at 1–2 m (RMS ≈ 3% of the box's on-screen size).

### 2.6 Multi-view fusion and tracking: `ObjectTracker`, `TrackedObject`
- **Association:** a new fit joins the nearest existing track of the same category within a gate (~60% of the footprint + 2σ). Otherwise it starts a new candidate track.
- **Fusion (per track, up to 40 views):**
  - weights = q × time decay (30 s);
  - outlier rejection on position (2.5 × robust spread) and yaw (35°);
  - weighted mean position and size;
  - circular mean of yaw (modulo 180° for symmetric categories such as cups).
- **Moved objects:** 6 consecutive views that disagree with the fused position drop the old history, and the box re-snaps.
- **Lifecycle:**
  - a candidate is **confirmed** at confidence ≥ 0.55 with ≥ 4 views;
  - a candidate unseen for 4 s is dropped;
  - a confirmed object is removed after 4 s below 0.18 confidence, e.g. when it keeps not being detected where it should be visible.
- Duplicate tracks that converge are merged.

### 2.7 Object confidence
```
C = evidence × quality^0.35 × consistency × agreement × viewpoints × presence
```
| Factor | What it measures |
|---|---|
| evidence | 1 − exp(−Σq / 1.2): how much good evidence exists |
| quality | mean per-frame quality |
| consistency | standard error of the fused position (σ/√n, scale 3.5 cm), yaw (8°) and size (7%) |
| agreement | (inlier fraction)^1.5: do the views agree? |
| viewpoints | 0.85 → 1.0 as the views span 25° around the object |
| presence | 1 / (1 + 0.15 × misses): seen when it should be? |

**±cm** = √(σ_pos²/n + 2·σ_floor² + 0.8 cm²). That's the statistical error of the fused centre plus the effect of floor-height error, which slides the box along the viewing ray.

In simulation with Objectron-like noise, a chair confirms at ~57%, rises to ~86% as you move around it, and falls to ~10% within a few seconds once it stops being seen. Clean data reaches ~98%.

> **Important:** C is a principled but *heuristic* score, **not yet a calibrated probability**. To make "90%" mean "right 90% of the time", record (C, measured error) pairs against tape-measured ground truth and fit a calibration curve (isotonic / Platt). See [Next steps](#7-next-steps).

### 2.8 Rendering: `BoxVisual`, `HudPanel`, `DebugOverlay`
- **Box:** a procedural mesh (no LineRenderers) with thin edges, bold corner brackets, faint faces and a brighter footprint. The shader is a custom unlit transparent shader with single-pass-instanced stereo.
- **Smoothing:** displayed pose and size go through a **One-Euro filter** (Casiez et al., 2012), which is stable when still and fast when the object really moves. Label text updates at 4 Hz.
- **Anchoring:** each confirmed object gets an **`OVRSpatialAnchor`**, so its box stays locked to the room if head tracking re-localises.
- **Label:** billboarded, scaled with distance, with a confidence bar.
- **Visibility threshold:** a track keeps being tracked and fused below the menu's "Show boxes from" value; only its drawing is hidden. 3 % hysteresis stops boxes flickering at the threshold.

### 2.9 Fitting the lab-chair scan to a detection (no hand calibration)
The old prototype placed the scan with a saved calibration (offset, rotation, scale), and that never lined up with the model's box. v2 removes the calibration completely, by making the scan and the box share one frame and measuring the one thing the box can't tell us.

1. **Offline normalisation** (`Tools/normalize_scan.py` → `Resources/LabChairScan.bytes`). The OBJ is converted to Unity axes. Then:
   - its **backrest direction** is found from the geometry itself (the seat-height centroid towards the centroid of the top 20 %);
   - the body's footprint axes are found with a minimum-area rectangle over everything above seat height, so the rotated 5-star base doesn't skew it;
   - the mesh is moved so the bottom-centre is the pivot and scaled to exactly a **unit box**, with the backrest at −Z.
   
   The scan is now in the **same canonical frame as `UprightBox`**.
2. **Size and position come straight from the fitted box.** The scan is a child of the box visual, so it sits on the measured support plane, with `localScale = box width × height × depth`. The scan therefore always fills exactly the box the solver produced, and it follows the One-Euro smoothing with it.
3. **Which way the chair faces comes from depth, not from the network** (`BackrestEstimator`). Objectron's labelling of the box axes isn't reliable enough to tell front from back. In scan mode, each visible chair gets a few depth rays per frame, aimed at random points in the **top slab of the box (83–97 % of its height)**, where only the backrest exists. Hits that land in that slab vote for one of the four box sides (−Z, −X, +Z, +X). This works from any viewpoint: from the front, the rays pass over the seat and stop on the backrest; from behind, they hit its back. A side is accepted after ≥ 24 hits once it beats the runner-up by 1.35×. It is only replaced by a side that is 1.8× stronger. The votes reset when the chair is moved. The scan is rotated by that many quarter turns, with x/z size swapped for 90°, and fades in. Until then the label reads "aligning scan… look at the backrest".

The log records each decision as `backrest id=… scores=[…] ratio=… side=…`.

---

## 3. Code map

```
Assets/QuestDetect3D/
  Runtime/
    Detect3DApp.cs               orchestrator: menu → capture → detect → fit → track → render, input, HUD, heartbeat log
    Core/        CameraModel (PCA pinhole, mirrors MRUK maths), UprightBox, ObjectCategory (+ size priors),
                 OneEuroFilter, AppSettings (menu options), Log (logcat + on-headset log file)
    Detection/   DetectionTypes (detector-agnostic KeypointDetection), PcaFrameSource, PcaImageSource,
                 ObjectronDetector
    Geometry/    BoxSolver (init + Levenberg–Marquardt), SupportPlaneEstimator + FloorEstimator (Depth API),
                 BackrestEstimator (Depth API: which box side holds the backrest)
    Tracking/    ConfidenceModel, TrackedObject (fusion), ObjectTracker (association, lifecycle)
    Visuals/     BoxVisual (box / scan), SessionMenu (start menu), ScanModel (loads the canonical scan),
                 HudPanel, DebugOverlay, MeshUtil
  Resources/     LabChairScan.bytes (canonical unit-box scan, built by Tools/normalize_scan.py)
  Editor/        SceneBuilder (builds the scene from Meta's MultiObjectDetection template), BuildScript (batch build)
  Shaders/       QD3D_UnlitTransparent.shader
Tools/normalize_scan.py          OBJ → canonical scan asset (see 2.9)
```
Everything downstream of detection only consumes `KeypointDetection` (9 box keypoints in viewport coordinates + an optional up-axis hint). **Swapping the model** (CenterPose, a custom network) means writing one new detector class.

---

## 4. Build and run (Windows)

Requirements:
- Unity **6000.2.13f1** with Android Build Support.
- Quest 3/3S on Horizon OS v74+, developer mode, USB.
- `Packages/com.github.homuler.mediapipe-0.11.0.tgz`, the [MediaPipeUnityPlugin v0.11.0 release tarball](https://github.com/homuler/MediaPipeUnityPlugin/releases/tag/v0.11.0) (~500 MB, not in git). Put it in `Packages/`.

| Script | What it does |
|---|---|
| `BuildAndDeploy.bat` | Builds `Builds/QuestDetect3D.apk` (batch mode), installs it with permissions granted (`-g`), launches it, saves 40 s of logcat |
| `DeployOnly.bat` | Reinstalls and launches the last APK |
| `PullLog.bat` | Copies the app's own session log (`Builds/app_log.txt`, plus the previous session) |
| `DumpLog.bat` | Saves the Unity logcat to `Builds/device_log.txt` |
| `BuildWatcher.bat` | Leave it running; creating `Builds/trigger_build` / `trigger_deploy` / `trigger_pull` runs the matching step |

The first build imports the project (10–30 min); later builds take ~1 min. In the Editor, **QuestDetect3D → Rebuild Scene / Build APK** does the same.

Package name: `com.amplilab.questdetect3d`.

### Logs
Every app line starts with `[QD3D]`. A `status` line is written every 3 s with detector Hz and ms, detections, fits ok/failed with the last rejection reason, floor, and tracks. There is a `fit` line per second (box, reprojection error, layout, support, quality breakdown) and `track_new / track_confirmed / track_relocated / track_removed` events with the confidence breakdown.

---

## 5. Measured on device (8 Nov 2026, Quest 3, office with chairs)

| | |
|---|---|
| Objectron rate / latency | 10–13 Hz / ~50 ms |
| Frames with a detection while looking at chairs | ~98% |
| Successful metric fits | 99–100% of detections (remaining failures: feet out of view) |
| Per-frame reprojection error | 2.5–3.7% of on-screen box size |
| Typical chair boxes | 46–55 × 55–59 × 79–88 cm, ±2–3 cm |
| Confidence at confirmation → after moving around | ~57–79% → up to ~85–90% |
| Simultaneous chairs | 4 confirmed at once |

---

## 6. What changed from v1

| v1 problem | v2 |
|---|---|
| Image fed mirrored, 3D converted with a Y+Z flip, so 3D was rotated 180° about the view axis and **yaw was mirrored** | Unmirrored image, one correct convention `(x, y, −z)`; lifted 3D used only to vote the up axis |
| Objectron 3D is up to scale and uses phone intrinsics (needed ~2× fudge factors) | Box solved in metres from 2D keypoints + real PCA intrinsics + depth-measured support plane |
| Box centred on a depth hit on the front surface (or behind the chair) | Footprint from bottom-corner rays on the support plane; LM over all 9 keypoints |
| Yaw from "the longer edge": random 90°/180° flips; mesh orientation unusable | Yaw from labelled corners |
| Floor snap rewrote the box centre and rejected boxes > 8 cm off | Support height is a fit parameter with a depth prior |
| FIFO pose queue paired results with the wrong (older) frame | One frame in flight; each result carries its own camera model |
| "Head-roll compensation" injected an error equal to the roll | True camera pose |
| Box frozen on the first frame | Robust multi-view fusion, moved-object handling |
| No anchors, LineRenderer wireframes, ~70 scripts, 5 scenes | Spatial anchors, procedural box mesh + One-Euro smoothing, 18 scripts, 1 scene |

---

## 7. Next steps

1. **Calibrate confidence**: log (C, ground-truth error) and fit a calibration curve so the % is a real probability.
2. **Depth inside the box**: sample the depth map inside the fitted footprint to verify and refine extents. This is also an extra confidence term.
3. **Occlusion-aware rendering** with the environment depth texture.
4. **Better/more general models**: any 3D-box keypoint model plugs into `KeypointDetection`. Options:
   - CenterPose (GPU);
   - a custom Objectron-style head for more classes;
   - class-agnostic "2D detector + depth point-cloud box" for arbitrary objects.
5. Load any scanned chair (not only the lab chair): run `normalize_scan.py` on another OBJ.

## Credits
- MediaPipe Objectron (Google) via [homuler/MediaPipeUnityPlugin](https://github.com/homuler/MediaPipeUnityPlugin).
- Meta [Passthrough Camera API samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples) and MRUK.
- One-Euro filter: Casiez, Roussel & Vogel, CHI 2012.
