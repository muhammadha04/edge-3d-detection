# Edge 3D Detection (Quest Objectron)

Quest 3 mixed-reality app that runs **MediaPipe Objectron** on **Meta Passthrough Camera** frames and draws **world-space 3D bounding boxes** over real cups/mugs. The project focuses on stable environment-anchored placement (not head-locked overlays), head-tilt compensation, and repeatable scan sessions.

---

## What it does

| Mode | Scene | Behavior |
|------|-------|----------|
| **ObjectronCupDetection** | `ObjectronCupDetection.unity` | Auto-scan up to 3 cups; each cup gets one environment-localized green wireframe box |
| **Box Debug (snapshot)** | `ObjectronBoxDebug.unity` | Continuous inference + manual Capture / Localize / Clear with labeled X/Y/Z edges |

Both modes:
- Use passthrough MR (see the real room)
- Pair each camera frame with the PCA pose captured at submit time
- Compensate headset roll when mapping model output to world space
- Tear down fully when returning to the main menu (☰ Start)

---

## Requirements

- **Unity Hub** → Unity **6000.0.38f1** or newer (project tested on **6000.2.13f1**)
- **Meta Quest 3 / 3S**, Horizon OS **v74+**
- Quest **Developer Mode** + USB debugging enabled
- **Windows** PC for `install_mediapipe.ps1` (or extract the MediaPipe `.tgz` manually on macOS/Linux)
- Physical headset — Passthrough Camera API does **not** work in XR Simulator

---

## Clone and setup (from scratch)

### 1. Clone the repository

```bash
git clone https://github.com/muhammadha04/edge-3d-detection.git
cd edge-3d-detection
```

> The Unity project root is the repo root (`QuestObjectron3D` layout). Open this folder in Unity Hub.

### 2. Install MediaPipe (required — not in git)

Native MediaPipe binaries and the Unity package exceed GitHub’s file size limits. After clone, run:

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
| **MediaPipe native libs** | Same package | `mediapipe_android.aar`, JNI — why step 2 is mandatory |
| **Meta MRUK / Passthrough samples** | In repo under `Assets/PassthroughCameraApiSamples/` | Submodule of Meta’s PCA sample project |
| **Sentis YOLO model** | Meta sample (optional) | Disabled at runtime by `ObjectronDisableSentis`; Objectron does **not** use Sentis |

You do **not** download a separate Objectron `.tflite` — it ships with the homuler package and is referenced by `ObjectronGraph` in the scene.

### 4. Open in Unity

1. Unity Hub → **Add** → select the cloned folder.
2. Use editor **6000.0.38f1+** (install **Android Build Support** + **OpenJDK** + **NDK**).
3. First open may take several minutes (Library import).

### 5. Meta project setup

1. **Meta → Tools → Project Setup Tool** → **Fix all** / **Apply all** (passthrough, OpenXR, etc.).
2. Optional: **QuestObjectron → Configure Objectron Cup Detection Scene** if scene references look broken.

### 6. Build and run on Quest

1. **File → Build Settings** → **Android** → **Switch Platform**.
2. Connect Quest via USB → enable debugging on headset.
3. **Build And Run** (or Build APK, then `adb install`).
4. On first launch, grant **camera** and **spatial** permissions.
5. Main menu → **Quest Objectron / Tools**:
   - **ObjectronCupDetection** — auto cup scan
   - **Box Debug (snapshot)** — manual capture/localize

### 7. Logcat (optional)

```bash
adb logcat -s QuestObj3D Unity
```

### What is *not* in git

- `Packages/com.github.homuler.mediapipe/` — run `install_mediapipe.ps1`
- `Library/`, `Temp/`, `Logs/`, `UserSettings/` — Unity cache
- `*.apk` — local builds

---

## End-to-end pipeline (start → box in the room)

High-level flow shared by both scenes:

```
Boot / permissions
  → MediaPipe Bootstrap (GPU)
  → PassthroughCameraAccess + PassthroughImageSource
  → ObjectronGraph (Cup model, async)
  → [per frame] capture pose + copy texture → inference
  → FrameAnnotation (2D keypoints + 3D translation/rotation/scale)
  → ObjectronWorldPlacement.PlaceDetailed()
  → orientation constraints (roll / gravity / upright-on-table)
  → world-space 9-point corner array
  → wireframe visuals (QuestVisuals or LabeledBoxVisuals)
```

### Step-by-step (one inference frame)

1. **Capture** — `PassthroughCameraAccess.GetCameraPose()` is read and enqueued in `ObjectronFramePoseQueue` *before* the texture is submitted to MediaPipe. This fixes async drift (using the pose at callback time would make boxes follow the head).

2. **Inference** — Every 2nd frame (`InferenceEveryNFrames = 2`), the PCA image is copied into a `TextureFrame` and sent to Objectron (Cup). MediaPipe returns a `FrameAnnotation` with up to 3 `ObjectAnnotation` entries (translation, rotation 3×3, scale, keypoints, object id).

3. **Main-thread process** — The pending annotation is dequeued with the **paired** camera pose (`DequeueOrCurrent`). Processing is throttled to ≥ 0.2 s between runs (`DetectionProcessMinInterval`) to limit log/UI spam.

4. **Raw placement** — `ObjectronWorldPlacement.TryPlaceAnnotation()` picks the first working method:
   - **Keypoint3D** — 3D keypoints transformed camera → world
   - **Keypoint2DRaycast** — 2D keypoints → viewport ray → MRUK scene raycast (or 0.75 m fallback)
   - **TranslationBox** — model translation + rotation matrix + half-extents → 8 corners

   Camera-local points are converted with:

   ```
   worldPoint = placementPose.position + placementPose.rotation * cameraLocalMeters
   ```

   where `placementPose = GetRollCompensatedPose(cameraPose)` when `CompensateHeadRoll` is on (head roll removed; pitch/yaw kept).

5. **Depth refinement** (when 2D keypoints yield a viewport rect) — optional path in `PlaceOne()`:
   - Raycast at mask center for distance
   - **ModelOrientedMaskBox** — model rotation + depth-scaled extents
   - **DepthRefinedBox** — scaled oriented box
   - **TableSnappedBox** — optional MRUK table snap (`EnableTableSnap`)
   - **MaskAlignedBox** — camera-aligned fallback (**disabled in cup mode** — it billboards toward the viewer)

6. **Post-placement constraints** (in `PlaceDetailed`, cup mode always on):

   | Step | Function | What it does |
   |------|----------|--------------|
   | Roll / gravity | `TryAlignBoxToGravity` | Rotates box so the bottom face normal points **down** (−Y) |
   | Upright on table | `TryConstrainUprightOnTable` | Rebuilds box with world +Y vertical; **yaw** from the longer horizontal model edge; no camera billboard |
   | Smoothing | `Smooth(objectId)` | Exponential blend (α = 0.35) per tracked object id |

7. **Corner layout** — 9 points: index `0` = center, `1–8` = corners. Edge lengths for metrics:
   - X edge: `|corners[2] − corners[1]|`
   - Y edge: `|corners[3] − corners[1]|`
   - Z edge: `|corners[5] − corners[1]|`

8. **Cup mode: localize or refine** — See [Cup detection rules](#objectroncupdetection-rules) below.

9. **Visualize** — `ObjectronQuestVisuals.Localize()` draws 12 world-space `LineRenderer` edges parented under `ObjectronVisualsRoot` (world-locked, not parented to the camera).

### Head-tilt math (roll compensation)

MediaPipe outputs pose in the **tilted camera image frame**. When the user tilts their head:

- `RemoveCameraRoll(cameraRotation)` projects camera forward onto the horizontal plane and builds `Quaternion.LookRotation(flatForward, Vector3.up)`.
- All camera-local model vectors use this leveled pose for world mapping.
- `TryAlignBoxToGravity` then corrects residual tilt so the mug sits on the table plane.

### Upright-on-table math

Assumes the mug sits on a **flat horizontal surface** (only yaw varies):

1. Find which box axis is most aligned with world up → vertical half-extent.
2. Of the two horizontal axes, use the **longer** one for yaw (handle + cylinder ≈ 11 cm axis).
3. Project that axis onto the XZ plane → `flatYaw`.
4. `worldRot = LookRotation(flatYaw, Vector3.up)`.
5. Rebuild corners: `corner[i] = center + worldRot * (unitCorner[i] ⊙ halfExtents)`.

---

## ObjectronCupDetection rules

### Scan behavior

1. Scene starts **scanning** — inference runs continuously.
2. **New cup** — center not within **15 cm** of any already-localized cup → box is **frozen** in world space immediately.
3. **Same cup** — center within 15 cm → no second box; optional **size refinement** only (see below).
4. Stops adding new cups at **3** (`MaxLocalizedCups` = MediaPipe `maxNumObjects`).
5. **No 2D overlay** — `ObjectronPassthroughOverlay` disabled; no screen-space boxes.

### Size refinement (`ObjectronCupSizeFit`)

Reference mug extents (handle on left): **11 × 10 × 8 cm** (X × Y × Z).

For each detection, edge lengths are sorted (rotation-invariant) and scored:

```
score = Σ_axis ((detected_a − ref_a) / ref_a)²
```

Lower is better. An existing localized cup is **updated** only if the new score improves by at least **5%** (`IsBetterFit`, `minRelativeImprovement = 0.05`). Position/orientation update together with the new corner array.

### Cup mode placement flags (forced in code)

Set in `ObjectronCupDetectionManager.Awake()`:

| Option | Value | Reason |
|--------|-------|--------|
| `CompensateHeadRoll` | `true` | Level camera pose for world mapping |
| `ConstrainUprightOnTable` | `true` | Horizontal bottom, yaw-only rotation |
| `DisableMaskAlignedFallback` | `true` | Never use camera-facing mask box |
| `UseMaskWhenBadOrientation` | `false` | Don’t swap to mask on bad orientation |
| `EnableTableSnap` | `true` | MRUK raycast snap when available |
| `MirrorInferenceHorizontal` | from `PassthroughImageSource.isHorizontallyFlipped` | Align with PCA flip |

### MediaPipe tuning (inspector / scene)

| Parameter | Default | Meaning |
|-----------|---------|---------|
| `m_minDetectionConfidence` | `0.35` | Objectron detection threshold |
| `m_minTrackingConfidence` | `0.55` | Objectron tracking threshold |
| `m_runningMode` | `Async` | Non-blocking inference |
| `InferenceEveryNFrames` | `2` | Run model every 2nd frame |
| `DetectionProcessMinInterval` | `0.2` s | Min time between main-thread processes |
| `SameCupCenterRadiusM` | `0.15` m | Duplicate cup dedup radius |
| `MaxLocalizedCups` | `3` | Max simultaneous localized mugs |

---

## Box Debug mode

Continuous inference latches the latest valid placement into memory. User workflow:

1. Point at mug — live inference updates internal latch (`LatchDetection`).
2. **Capture & Detect** (UI) or **B** — show labeled wireframe from latched corners.
3. **Localize Box** — pin in world space.
4. **Clear** or **A** — remove box.
5. **☰ Start** — return to menu (full cleanup).

Box Debug uses the same placement pipeline but does **not** force cup-mode restrictions (mask fallback remains available in options). It uses `ObjectronLabeledBoxVisuals` for axis labels.

---

## Session lifecycle (main menu restart)

Both **ObjectronCupDetection** and **Box Debug** reset to zero when leaving or re-entering:

| When | What runs |
|------|-----------|
| **☰ Start** (in scene) | `ObjectronSessionCleanup.LeaveObjectronScene()` → stop graph, clear cups/boxes, destroy `DontDestroyOnLoad` visual roots |
| **Menu button** (enter scene) | `BeginFreshSession()` before `LoadScene` |
| **Scene Awake** | `BeginFreshSession()` + manager `ShutdownForSceneExit` flag cleared |

`ShutdownForSceneExit()` stops the inference coroutine, unsubscribes MediaPipe callbacks, calls `objectronGraph.Stop()`, clears localized state, and nulls `ImageSourceProvider.ImageSource`.

---

## Architecture diagram

```
Passthrough MR
  └─ PassthroughCameraAccess (pose + ViewportPointToRay)
       └─ PassthroughImageSource → TextureFramePool
            └─ ObjectronGraph (Cup, async)
                 └─ FrameAnnotation
                      └─ ObjectronWorldPlacement
                           ├─ camera-local pose (MediaPipe frame fixes)
                           ├─ optional depth / MRUK refinement
                           ├─ TryAlignBoxToGravity
                           └─ TryConstrainUprightOnTable (cup mode)
                                └─ ObjectronQuestVisuals / ObjectronLabeledBoxVisuals
```

---

## Quest controls

### ObjectronCupDetection

| Input | Action |
|-------|--------|
| **A** (right) | Reset scan — clear all localized cups |
| **☰ Start** | Main menu + full session cleanup |

### Box Debug

| Input | Action |
|-------|--------|
| **Capture & Detect** (UI) or **B** | Show latched labeled box |
| **Localize Box** | Pin box in room space |
| **Clear Box** or **A** | Remove box |
| **☰ Start** | Main menu + full session cleanup |

---

## Logcat

```bash
adb logcat -s QuestObj3D Unity
```

| Tag | Meaning |
|-----|---------|
| `BOOT` | Bootstrap, session cleanup, placement options |
| `DETECT` | Inference, `cup_localized`, `cup_size_refined`, `cup_scan_reset` |
| `WORLD` | Placement method per object id |
| `VIZ` | Wireframe counts |
| `ERR` | Errors |

Useful strings: `cup_localized`, `cup_size_refined`, `cup_scan_reset`, `objectron_session_cleanup`, `cup_detection_shutdown`, `box_debug_shutdown`.

---

## Key scripts

| Script | Role |
|--------|------|
| `ObjectronCupDetectionManager.cs` | Cup scan, localize, size refinement, session shutdown |
| `ObjectronBoxDebugManager.cs` | Box Debug latch + capture/localize |
| `ObjectronWorldPlacement.cs` | Camera → world corners, depth refinement |
| `ObjectronWorldOrientation.cs` | Roll compensation, gravity align, upright-on-table |
| `ObjectronFramePoseQueue.cs` | Frame/pose pairing for async inference |
| `ObjectronCupSizeFit.cs` | Reference mug size scoring |
| `ObjectronSessionCleanup.cs` | Menu exit / fresh session |
| `ObjectronQuestVisuals.cs` | Green wireframes (cup mode) |
| `ObjectronLabeledBoxVisuals.cs` | Labeled wireframe (Box Debug) |
| `ObjectronLocalizedCupState.cs` | Runtime cup track (not Unity-serialized) |

---

## Packages

- Meta MRUK, Meta OpenXR, Passthrough Camera API samples
- `com.github.homuler.mediapipe` v0.11.0 (local package under `Packages/`)

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| No detections | Cup ~0.5–1.5 m, good lighting; logcat `DETECT empty` |
| Box tilted with head | `CompensateHeadRoll` + `ConstrainUprightOnTable` enabled (cup mode defaults) |
| Box faces you when walking around | Rebuild with cup mode (mask aligned disabled); boxes must be localized, not live-tracked |
| Box offset in depth | Grant spatial permission for MRUK raycast |
| Boxes persist after menu | Ensure latest build with `ObjectronSessionCleanup` |
| Detection only at odd angles | Adjust `PassthroughImageSource` rotation (see `README_OBJECTRON.md`) |
| Build error `LocalizedCup` serialization | Runtime state uses `ObjectronLocalizedCupState` + `[NonSerialized]` lists |

---

## License

Based on [Meta Unity Passthrough Camera API Samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples) and [MediaPipe Unity Plugin](https://github.com/homuler/MediaPipeUnityPlugin). See respective licenses in those projects.
