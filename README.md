# Edge 3D Detection (Quest Objectron)

Quest 3 mixed-reality app that runs **MediaPipe Objectron** on **Meta Passthrough Camera** frames and draws **world-space 3D bounding boxes** over real objects (cup/mug). Built for debugging placement, camera pose, and head-tilt compensation.

## What it does

- **Passthrough MR background** — see the real room through the headset
- **3D cup detection** — Objectron model on live PCA camera frames
- **World-space wireframe boxes** — compare model output to real objects
- **Box Debug mode** — snapshot-style workflow with labeled X/Y/Z edges and localize/clear controls
- **Head-roll compensation** — boxes stay upright on the mug when your head is tilted

## Requirements

- Unity **6000.0.38f1** or newer
- Meta Quest **3 / 3S**, Horizon OS **v74+**
- USB debugging, Developer Mode
- Physical device (Passthrough Camera API is not supported in XR Simulator)

## Quick start

1. Clone this repo and install the MediaPipe package (not in git — binaries exceed GitHub limits):

   ```powershell
   .\install_mediapipe.ps1
   ```

2. Open this project in Unity 6.
3. Run **Meta → Tools → Project Setup Tool** → Fix all / Apply all.
4. **File → Build Settings → Android** → build and deploy to Quest 3.
5. Grant **camera** and **spatial** permissions on first launch.
6. Main menu → **Quest Objectron / Tools**:
   - **ObjectronCupDetection** — continuous detection + pin (B) / clear (A)
   - **Box Debug (snapshot)** — labeled debug box + Capture & Detect

## Architecture

```
Passthrough (MR) → PassthroughCameraAccess
  → MediaPipe Objectron (Cup)
  → ObjectronWorldPlacement (PCA pose + roll compensation + optional MRUK depth)
  → ObjectronQuestVisuals / ObjectronLabeledBoxVisuals
```

### Camera pose and head tilt

Objectron outputs 3D pose in **camera space** (relative to the frame that was captured). To place boxes in the room:

1. **Frame–pose pairing** — each submitted frame is queued with `PassthroughCameraAccess.GetCameraPose()` at capture time; the same pose is used when the detection result arrives (fixes async timing drift).
2. **Roll compensation** (`CompensateHeadRoll`, default **on**) — removes headset roll when mapping camera-local coordinates to world space, then aligns the box so its bottom face normal points toward gravity.

When your head is upright, model X/Y/Z map cleanly to world edges. When you tilt your head, the model output is still tilted in image space; roll compensation re-orients the box so it sits on the mug in the environment.

## Logcat

```bash
adb logcat -s QuestObj3D Unity
```

| Tag | Meaning |
|-----|---------|
| `BOOT` | App / MediaPipe bootstrap |
| `PCA` | Passthrough camera opened |
| `DETECT` | Objectron inference / box debug |
| `WORLD` | World corner placement |
| `ERR` | Errors |

Box Debug latch lines include `roll=` (head roll degrees) and `level_roll=True` when compensation is active.

## Quest controls (Box Debug)

| Input | Action |
|-------|--------|
| **Capture & Detect** (UI) or **B** | Show latched labeled box |
| **Localize Box** | Pin box in room space |
| **Clear Box** or **A** | Remove box |
| **Start (☰)** | Return to main menu |

## Key scripts

| Script | Role |
|--------|------|
| `ObjectronCupDetectionManager.cs` | Continuous detection pipeline |
| `ObjectronBoxDebugManager.cs` | Box Debug scene (latch + show) |
| `ObjectronWorldPlacement.cs` | Camera → world corner placement |
| `ObjectronWorldOrientation.cs` | Roll compensation + gravity align |
| `ObjectronFramePoseQueue.cs` | Frame/pose pairing for async inference |
| `ObjectronLabeledBoxVisuals.cs` | Labeled wireframe (Box Debug) |

## Packages

- Meta MRUK, Meta OpenXR, Passthrough Camera API samples (forked base)
- `com.github.homuler.mediapipe` v0.11.0 (local package under `Packages/`)

## Troubleshooting

- **No detections** — Point at a clear cup ~0.5–1.5 m, improve lighting.
- **Box tilted with head** — Ensure `CompensateHeadRoll` is enabled on placement options; rebuild after pull.
- **Box offset** — Grant spatial permission for MRUK depth raycast.
- **Detection only at odd head angles** — Adjust `PassthroughImageSource` rotation (see `README_OBJECTRON.md`).

## License

Based on [Meta Unity Passthrough Camera API Samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples) and [MediaPipe Unity Plugin](https://github.com/homuler/MediaPipeUnityPlugin). See respective licenses in those projects.
