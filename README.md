# Edge 3D Detection (Quest Objectron)

Quest 3 mixed-reality app that runs **MediaPipe Objectron (Chair)** on **Meta Passthrough Camera** frames and draws **world-space 3D bounding boxes** over real chairs. The project focuses on stable environment-anchored placement (not head-locked overlays), head-tilt compensation, and repeatable scan sessions.

> **Branch `chair`:** Cup detection is replaced with chair detection. Use `main` for the cup/mug build.

---

## What it does

| Mode | Scene | Behavior |
|------|-------|----------|
| **ObjectronChairDetection** | `ObjectronChairDetection.unity` | Auto-scan up to 3 chairs; each chair gets one environment-localized green wireframe box |
| **Box Debug (snapshot)** | `ObjectronBoxDebug.unity` | Continuous inference + manual Capture / Localize / Clear with labeled X/Y/Z edges |
| **Scan Calibration** | `ObjectronScanCalibration.unity` | Align `lab-chair.obj` scan to a live detection box; save relative pose for mesh overlay |

Both modes:
- Use passthrough MR (see the real room)
- Pair each camera frame with the PCA pose captured at submit time
- Compensate headset roll when mapping model output to world space
- Tear down fully when returning to the main menu (â˜° Start)

---

## Requirements

- **Unity Hub** â†’ Unity **6000.0.38f1** or newer (project tested on **6000.2.13f1**)
- **Meta Quest 3 / 3S**, Horizon OS **v74+**
- Quest **Developer Mode** + USB debugging enabled
- **Windows** PC for `install_mediapipe.ps1` (or extract the MediaPipe `.tgz` manually on macOS/Linux)
- Physical headset â€” Passthrough Camera API does **not** work in XR Simulator

---

## Clone and setup (from scratch)

### 1. Clone the repository

```bash
git clone https://github.com/muhammadha04/edge-3d-detection.git
cd edge-3d-detection
```

> The Unity project root is the repo root (`QuestObjectron3D` layout). Open this folder in Unity Hub.

### 2. Install MediaPipe (required â€” not in git)

Native MediaPipe binaries and the Unity package exceed GitHubâ€™s file size limits. After clone, run:

```powershell
.\install_mediapipe.ps1
```

This script:

1. Downloads [`com.github.homuler.mediapipe-0.11.0.tgz`](https://github.com/homuler/MediaPipeUnityPlugin/releases/download/v0.11.0/com.github.homuler.mediapipe-0.11.0.tgz) (~500 MB) from the [MediaPipeUnityPlugin releases](https://github.com/homuler/MediaPipeUnityPlugin/releases/tag/v0.11.0)
2. Extracts it to `Packages/com.github.homuler.mediapipe/`

**Manual install (if you skip the script):**

```powershell
# From repo root
mkdir Packages -Force
Invoke-WebRequest -Uri "https://github.com/homuler/MediaPipeUnityPlugin/releases/download/v0.11.0/com.github.homuler.mediapipe-0.11.0.tgz" -OutFile "Packages/com.github.homuler.mediapipe-0.11.0.tgz"
tar -xzf Packages/com.github.homuler.mediapipe-0.11.0.tgz -C Packages
Move-Item Packages/package Packages/com.github.homuler.mediapipe
```

### 3. Where the Objectron models come from

| Asset | Source | Notes |
|-------|--------|--------|
| **Objectron Cup model** | Inside `com.github.homuler.mediapipe` | Cup category graph + weights; loaded at runtime by the scene **Bootstrap** from StreamingAssets (e.g. `objectron_cpu.txt` / GPU graph) |
| **MediaPipe native libs** | Same package | `mediapipe_android.aar`, JNI â€” why step 2 is mandatory |
| **Meta MRUK / Passthrough samples** | In repo under `Assets/PassthroughCameraApiSamples/` | Submodule of Metaâ€™s PCA sample project |
| **Sentis YOLO model** | Meta sample (optional) | Disabled at runtime by `ObjectronDisableSentis`; Objectron does **not** use Sentis |

You do **not** download a separate Objectron `.tflite` â€” it ships with the homuler package and is referenced by `ObjectronGraph` in the scene.

### 4. Open in Unity

1. Unity Hub â†’ **Add** â†’ select the cloned folder.
2. Use editor **6000.0.38f1+** (install **Android Build Support** + **OpenJDK** + **NDK**).
3. First open may take several minutes (Library import).

### 5. Meta project setup

1. **Meta â†’ Tools â†’ Project Setup Tool** â†’ **Fix all** / **Apply all** (passthrough, OpenXR, etc.).
2. Optional: **QuestObjectron â†’ Configure Objectron Cup Detection Scene** if scene references look broken.

### 6. Build and run on Quest

1. **File â†’ Build Settings** â†’ **Android** â†’ **Switch Platform**.
2. Connect Quest via USB â†’ enable debugging on headset.
3. **Build And Run** (or Build APK, then `adb install`).
4. On first launch, grant **camera** and **spatial** permissions.
5. Main menu â†’ **Quest Objectron / Tools**:
   - **Chair Detection (auto scan)** â€” auto chair scan
   - **Box Debug (snapshot)** â€” manual capture/localize

### 7. Logcat (optional)

```bash
adb logcat -s QuestObj3D Unity
```

### What is *not* in git

- `Packages/com.github.homuler.mediapipe/` â€” run `install_mediapipe.ps1`
- `Library/`, `Temp/`, `Logs/`, `UserSettings/` â€” Unity cache
- `*.apk` â€” local builds

---

## End-to-end pipeline (start â†’ box in the room)

High-level flow shared by both scenes:

```
Boot / permissions
  â†’ MediaPipe Bootstrap (GPU)
  â†’ PassthroughCameraAccess + PassthroughImageSource
  â†’ ObjectronGraph (Cup model, async)
  â†’ [per frame] capture pose + copy texture â†’ inference
  â†’ FrameAnnotation (2D keypoints + 3D translation/rotation/scale)
  â†’ ObjectronWorldPlacement.PlaceDetailed()
  â†’ orientation constraints (roll / gravity / upright-on-table)
  â†’ world-space 9-point corner array
  â†’ wireframe visuals (QuestVisuals or LabeledBoxVisuals)
```

### Step-by-step (one inference frame)

1. **Capture** â€” `PassthroughCameraAccess.GetCameraPose()` is read and enqueued in `ObjectronFramePoseQueue` *before* the texture is submitted to MediaPipe. This fixes async drift (using the pose at callback time would make boxes follow the head).

2. **Inference** â€” Every 2nd frame (`InferenceEveryNFrames = 2`), the PCA image is copied into a `TextureFrame` and sent to Objectron (Cup). MediaPipe returns a `FrameAnnotation` with up to 3 `ObjectAnnotation` entries (translation, rotation 3Ã—3, scale, keypoints, object id).

3. **Main-thread process** â€” The pending annotation is dequeued with the **paired** camera pose (`DequeueOrCurrent`). Processing is throttled to â‰¥ 0.2 s between runs (`DetectionProcessMinInterval`) to limit log/UI spam.

4. **Raw placement** â€” `ObjectronWorldPlacement.TryPlaceAnnotation()` picks the first working method:
   - **Keypoint3D** â€” 3D keypoints transformed camera â†’ world
   - **Keypoint2DRaycast** â€” 2D keypoints â†’ viewport ray â†’ MRUK scene raycast (or 0.75 m fallback)
   - **TranslationBox** â€” model translation + rotation matrix + half-extents â†’ 8 corners

   Camera-local points are converted with:

   ```
   worldPoint = placementPose.position + placementPose.rotation * cameraLocalMeters
   ```

   where `placementPose = GetRollCompensatedPose(cameraPose)` when `CompensateHeadRoll` is on (head roll removed; pitch/yaw kept).

5. **Depth refinement** (when 2D keypoints yield a viewport rect) â€” optional path in `PlaceOne()`:
   - Raycast at mask center for distance
   - **ModelOrientedMaskBox** â€” model rotation + depth-scaled extents
   - **DepthRefinedBox** â€” scaled oriented box
   - **TableSnappedBox** â€” optional MRUK table snap (`EnableTableSnap`)
   - **MaskAlignedBox** â€” camera-aligned fallback (**disabled in cup mode** â€” it billboards toward the viewer)

6. **Post-placement constraints** (in `PlaceDetailed`, cup mode always on):

   | Step | Function | What it does |
   |------|----------|--------------|
   | Roll / gravity | `TryAlignBoxToGravity` | Rotates box so the bottom face normal points **down** (âˆ’Y) |
   | Upright on table | `TryConstrainUprightOnTable` | Rebuilds box with world +Y vertical; **yaw** from the longer horizontal model edge; no camera billboard |
   | Smoothing | `Smooth(objectId)` | Exponential blend (Î± = 0.35) per tracked object id |

7. **Corner layout** â€” 9 points: index `0` = center, `1â€“8` = corners. Edge lengths for metrics:
   - X edge: `|corners[2] âˆ’ corners[1]|`
   - Y edge: `|corners[3] âˆ’ corners[1]|`
   - Z edge: `|corners[5] âˆ’ corners[1]|`

8. **Cup mode: localize or refine** â€” See [Cup detection rules](#objectroncupdetection-rules) below.

9. **Visualize** â€” `ObjectronQuestVisuals.Localize()` draws 12 world-space `LineRenderer` edges parented under `ObjectronVisualsRoot` (world-locked, not parented to the camera).

### Head-tilt math (roll compensation)

MediaPipe outputs pose in the **tilted camera image frame**. When the user tilts their head:

- `RemoveCameraRoll(cameraRotation)` projects camera forward onto the horizontal plane and builds `Quaternion.LookRotation(flatForward, Vector3.up)`.
- All camera-local model vectors use this leveled pose for world mapping.
- `TryAlignBoxToGravity` then corrects residual tilt so the mug sits on the table plane.

### Upright-on-table math

Assumes the mug sits on a **flat horizontal surface** (only yaw varies):

1. Find which box axis is most aligned with world up â†’ vertical half-extent.
2. Of the two horizontal axes, use the **longer** one for yaw (handle + cylinder â‰ˆ 11 cm axis).
3. Project that axis onto the XZ plane â†’ `flatYaw`.
4. `worldRot = LookRotation(flatYaw, Vector3.up)`.
5. Rebuild corners: `corner[i] = center + worldRot * (unitCorner[i] âŠ™ halfExtents)`.

---

## ObjectronChairDetection rules

### Scan behavior

1. Scene starts **scanning** â€” inference runs continuously.
2. **New chair** â€” center not within **50 cm** of any already-localized chair â†’ box is **frozen** in world space immediately.
3. **Same chair** â€” center within 50 cm â†’ no second box; optional **size refinement** only (see below).
4. Stops adding new chairs at **3** (`MaxLocalizedChairs` = MediaPipe `maxNumObjects`).
5. **No 2D overlay** — `ObjectronPassthroughOverlay` disabled; no screen-space boxes.
6. **Floor required** — a chair is only localized if its box bottom snaps to the Meta Scene API floor (see below).

### Floor snap (Meta Scene API)

Chairs must sit **upright on the floor**. After Objectron placement:

1. `TryConstrainUprightOnTable` — rebuilds the box with world +Y vertical and yaw from the model (no camera billboard).
2. `ObjectronFloorPlaneSnap.TrySnapBoxToFloor` — casts rays **downward** through the bottom face corners using Meta **`EnvironmentRaycastManager`** (Scene mesh / `USE_SCENE` permission).
3. If floor snap fails (no spatial permission, too few ray hits, or lift out of range), that detection is **skipped** — no wireframe is placed.

Logcat: `floor_snap` (success) or `floor_snap_skip` / `placement=floor_snap_failed` (rejected).

**Requires spatial permission** (`com.oculus.permission.USE_SCENE`) — grant when prompted on first launch alongside camera access.

### Size refinement (`ObjectronChairSizeFit`)

Reference chair extents (typical dining chair): **~45 Ã— 45 Ã— 90 cm** (sorted at runtime).

For each detection, edge lengths are sorted (rotation-invariant) and scored:

```
score = Î£_axis ((detected_a âˆ’ ref_a) / ref_a)Â²
```

Lower is better. An existing localized chair is **updated** only if the new score improves by at least **5%** (`IsBetterFit`, `minRelativeImprovement = 0.05`). Position/orientation update together with the new corner array.

### Chair mode placement flags

Set in `ObjectronChairDetectionManager.Awake()`:

| Option | Value | Reason |
|--------|-------|--------|
| `CompensateHeadRoll` | `true` | Level camera pose for world mapping |
| `ConstrainUprightOnTable` | `true` | Chair upright on horizontal floor plane |
| `EnableFloorSnap` | `true` | Snap box bottom to Scene API floor (required to localize) |
| `DisableMaskAlignedFallback` | `true` | No camera-facing billboard boxes |
| `MirrorInferenceHorizontal` | from `PassthroughImageSource.isHorizontallyFlipped` | Align with PCA flip |

### MediaPipe tuning (inspector / scene)

| Parameter | Default | Meaning |
|-----------|---------|---------|
| `m_minDetectionConfidence` | `0.35` | Objectron detection threshold |
| `m_minTrackingConfidence` | `0.55` | Objectron tracking threshold |
| `m_runningMode` | `Async` | Non-blocking inference |
| `InferenceEveryNFrames` | `2` | Run model every 2nd frame |
| `DetectionProcessMinInterval` | `0.2` s | Min time between main-thread processes |
| `SameChairCenterRadiusM` | `0.5` m | Duplicate chair dedup radius |
| `MaxLocalizedChairs` | `3` | Max simultaneous localized chairs |

---

## Box Debug mode

Continuous inference latches the latest valid placement into memory. User workflow:

1. Point at mug â€” live inference updates internal latch (`LatchDetection`).
2. **Capture & Detect** (UI) or **B** â€” show labeled wireframe from latched corners.
3. **Localize Box** â€” pin in world space.
4. **Clear** or **A** â€” remove box.
5. **â˜° Start** â€” return to menu (full cleanup).

Box Debug uses the same placement pipeline but does **not** force cup-mode restrictions (mask fallback remains available in options). It uses `ObjectronLabeledBoxVisuals` for axis labels.

---

## Session lifecycle (main menu restart)

Both **ObjectronChairDetection** and **Box Debug** reset to zero when leaving or re-entering:

| When | What runs |
|------|-----------|
| **â˜° Start** (in scene) | `ObjectronSessionCleanup.LeaveObjectronScene()` â†’ stop graph, clear cups/boxes, destroy `DontDestroyOnLoad` visual roots |
| **Menu button** (enter scene) | `BeginFreshSession()` before `LoadScene` |
| **Scene Awake** | `BeginFreshSession()` + manager `ShutdownForSceneExit` flag cleared |

`ShutdownForSceneExit()` stops the inference coroutine, unsubscribes MediaPipe callbacks, calls `objectronGraph.Stop()`, clears localized state, and nulls `ImageSourceProvider.ImageSource`.

---

## Architecture diagram

```
Passthrough MR
  â””â”€ PassthroughCameraAccess (pose + ViewportPointToRay)
       â””â”€ PassthroughImageSource â†’ TextureFramePool
            └─ ObjectronGraph (Chair, async)
                 └─ FrameAnnotation
                      └─ ObjectronWorldPlacement
                           ├─ camera-local pose (MediaPipe frame fixes)
                           ├─ optional depth / MRUK refinement
                           ├─ TryAlignBoxToGravity
                           ├─ TryConstrainUprightOnTable (chair upright)
                           └─ ObjectronFloorPlaneSnap (Meta Scene API floor)
                                └─ ObjectronQuestVisuals / ObjectronLabeledBoxVisuals
```

---

## Quest controls

### ObjectronChairDetection

| Input | Action |
|-------|--------|
| **Y** (left) | Reset scan â€” clear all localized chairs |
| **â˜° Start** | Main menu + full session cleanup |

### Box Debug

| Input | Action |
|-------|--------|
| **Capture & Detect** (UI) or **B** | Show latched labeled box |
| **Localize Box** | Pin box in room space |
| **Clear Box** or **A** | Remove box |
| **â˜° Start** | Main menu + full session cleanup |

### Scan Calibration (align `lab-chair.obj`)

| Input | Action |
|-------|--------|
| **B** (right) | Show latched detection box |
| **A** (right) | Clear detection box |
| **Trigger** (right, tap) | Spawn scan mesh at aim point (once) |
| **Grip** (right, hold) | Grab and move scan |
| **Trigger** (right, hold) | Rotate scan with controller |
| **Both grips** (hold) | Scale scan (pinch/stretch) |
| **Y** (left) | Freeze scan in place |
| **X** (left) | Save calibration JSON to device + logcat (**after** freeze) |
| **UI panel** (right) | Same actions via raycast + trigger click |
| **â˜° Start** | Main menu + full session cleanup |

Saved on Quest: `Application.persistentDataPath/scan_calibration_chair.json`  
Filter logcat: `SCAN_CALIBRATION_JSON`

---

## Lab chair mesh placement (calibration reference)

Captured **2026-06-12** from `BOX_PROJ_DEBUG` (`DepthRefinedBox`, object id **4873**).  
Full snapshot: [`Assets/ObjectronDetection/Calibration/lab-chair-detection-snapshot-2026-06-12.json`](Assets/ObjectronDetection/Calibration/lab-chair-detection-snapshot-2026-06-12.json)

### Detection box (world)

| Field | Value |
|-------|-------|
| Center (m) | `(-0.52, 0.815, 0.334)` |
| Final edge lengths (m) | **X=0.571, Y=0.427, Z=0.265** |
| Raw edge lengths (m) | `(0.267, 0.248, 0.137)` |
| Mask frustum (m) | `(0.619, 0.591, 0.162)` |
| Camera euler (deg) | `(5, 354, 1)` |

### Model rotation (MediaPipe camera space)

| Field | Value |
|-------|-------|
| Rotation euler (deg) | **(312.4, 326.6, 288.5)** |
| Rounded for Unity | `(312, 327, 288)` |
| Model full extents (m) | `(0.280, 0.212, 0.162)` |
| Model half extents (m) | `(0.140, 0.106, 0.081)` |
| Model up (world) | `(0.88, 0.20, 0.43)` |

### Depth refinement scale (apply to mesh vs Objectron model)

Per-axis scale factors from depth refinement (`SCALE` line in logcat):

| Axis | Scale |
|------|-------|
| **X** | **2.140** |
| **Y** | **1.719** |
| **Z** | **1.929** |
| **Average (uniform)** | **1.929** |

Rounded (`MODEL_SCALED_CAM_PLANE`): `(2.14, 1.72, 1.93)`

### How to use for every detection

1. **Bundled defaults:** `Assets/Resources/ScanCalibration/default_chair_calibration.json` (from your Scan Calibration save). Loaded automatically at runtime.
2. **Device override:** If you save again on Quest (`Left X`), the file on device overrides bundled defaults until reinstall.
3. **Chair Detection:** Each localized chair gets a `lab-chair` mesh via `ObjectronScanMeshVisuals` using `scanToDetection*` relative transform (position, rotation, scale).
4. **Scan Calibration spawn:** Right trigger uses calibrated placement on the latched box first (then controller-aim fallback).

Apply math (`ObjectronScanCalibrationRecord.TryApplyMeshPlacement`):

- **Scale:** uses your tuned `calibratedMeshLocalScale` (~**1.19** from calibration), NOT `detectionBoxSize × scaleRatio` (that produced giant meshes).
- Optional distance tweak: scales slightly if MediaPipe model scale differs from reference.
- **Rotation / position:** `detectionBoxRotation × scanToDetectionRotation` and box-local offset from calibration save.

If rotation/position look wrong after rebuild, **recalibrate once** (B → trigger → adjust → Left Y → Left X) and pull the new JSON — an early save may have captured spawn-at-aim position instead of your final alignment.

---

## Logcat

```bash
adb logcat -s QuestObj3D Unity
```

| Tag | Meaning |
|-----|---------|
| `BOOT` | Bootstrap, session cleanup, placement options |
| `DETECT` | Inference, `chair_localized`, `chair_size_refined`, `chair_scan_reset` |
| `WORLD` | Placement method per object id |
| `VIZ` | Wireframe counts |
| `BOX_PROJ_DEBUG` | Depth-refined box scale/rotation debug |
| `DBG` | Scan calibration JSON (`SCAN_CALIBRATION_JSON`) |
| `ERR` | Errors |

Useful strings: `chair_localized`, `chair_size_refined`, `chair_scan_reset`, `objectron_session_cleanup`, `chair_detection_shutdown`, `box_debug_shutdown`, `SCAN_CALIBRATION_JSON`.

---

## Key scripts

| Script | Role |
|--------|------|
| `ObjectronChairDetectionManager.cs` | Chair scan, localize, size refinement, session shutdown |
| `ObjectronBoxDebugManager.cs` | Box Debug latch + capture/localize |
| `ObjectronWorldPlacement.cs` | Camera â†’ world corners, depth refinement |
| `ObjectronWorldOrientation.cs` | Roll compensation, gravity align, upright-on-table |
| `ObjectronFramePoseQueue.cs` | Frame/pose pairing for async inference |
| `ObjectronChairSizeFit.cs` | Reference chair size scoring |
| `ObjectronFloorPlaneSnap.cs` | Meta Scene API floor raycast + vertical snap |
| `ObjectronSessionCleanup.cs` | Menu exit / fresh session |
| `ObjectronQuestVisuals.cs` | Green wireframes (cup mode) |
| `ObjectronLabeledBoxVisuals.cs` | Labeled wireframe (Box Debug) |
| `ObjectronScanCalibrationManager.cs` | Scan align + save calibration |
| `ObjectronScanCalibrationData.cs` | Calibration record + `ApplyToDetection()` |
| `ObjectronScanManipulator.cs` | VR grab move / rotate / scale for scan mesh |
| `ObjectronLocalizedCupState.cs` | Runtime cup track (not Unity-serialized) |

---

## Packages

- Meta MRUK, Meta OpenXR, Passthrough Camera API samples
- `com.github.homuler.mediapipe` v0.11.0 (local package under `Packages/`)

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| No detections | Cup ~0.5â€“1.5 m, good lighting; logcat `DETECT empty` |
| Box tilted with head | `CompensateHeadRoll` + `ConstrainUprightOnTable` enabled (cup mode defaults) |
| Box faces you when walking around | Rebuild with cup mode (mask aligned disabled); boxes must be localized, not live-tracked |
| Box offset in depth | Grant spatial permission for MRUK raycast |
| Boxes persist after menu | Ensure latest build with `ObjectronSessionCleanup` |
| Detection only at odd angles | Adjust `PassthroughImageSource` rotation (see `README_OBJECTRON.md`) |
| Build error `LocalizedCup` serialization | Runtime state uses `ObjectronLocalizedCupState` + `[NonSerialized]` lists |

---

## License

Based on [Meta Unity Passthrough Camera API Samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples) and [MediaPipe Unity Plugin](https://github.com/homuler/MediaPipeUnityPlugin). See respective licenses in those projects.
