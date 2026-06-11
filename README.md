# Edge 3D Detection (Quest Objectron)

Quest 3 mixed-reality app that runs **MediaPipe Objectron** on **Meta Passthrough Camera** frames and draws **world-space 3D bounding boxes** over real objects (cup/mug). Built for debugging placement, camera pose, and head-tilt compensation.

## What it does

- **Passthrough MR background** — see the real room through the headset
- **One-shot cup localization** — each visible cup gets a stable 3D box anchored in the room (up to 3)
- **Box Debug mode** — snapshot-style workflow with labeled X/Y/Z edges and localize/clear controls
- **Head-roll compensation** — boxes stay upright on the mug when your head is tilted
- **No 2D overlay in cup mode** — only environment-localized 3D wireframes (no screen-space boxes following the head)

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
   - **ObjectronCupDetection** — scan room; each cup localized once with a stable 3D box
   - **Box Debug (snapshot)** — labeled debug box + Capture & Detect

## ObjectronCupDetection flow

1. Scene starts **scanning** — inference runs continuously.
2. When a **new cup** is seen (not within ~15 cm of an already localized cup), its 3D box is **frozen in world space** immediately (same placement path as Box Debug: frame-pose pairing + roll compensation).
3. Repeat until **3 cups** are localized (MediaPipe `maxNumObjects` limit).
4. Already localized cups are **never updated** in this build.
5. Press **A (right controller)** to **reset** — clear all boxes and scan again.

Future work: optional “better detection” gate to replace a localized cup only when a higher-quality detection arrives.

## Architecture

```
Passthrough (MR) → PassthroughCameraAccess
  → MediaPipe Objectron (Cup)
  → ObjectronWorldPlacement (PCA pose + roll compensation + optional MRUK depth)
  → ObjectronQuestVisuals (environment-localized wireframes)
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
| `DETECT` | Objectron inference / cup localize |
| `WORLD` | World corner placement |
| `ERR` | Errors |

Look for `cup_localized` and `cup_scan_reset` in cup mode.

## Quest controls

### ObjectronCupDetection

| Input | Action |
|-------|--------|
| **A** (right) | Reset scan — clear all localized cups and start over |

### Box Debug

| Input | Action |
|-------|--------|
| **Capture & Detect** (UI) or **B** | Show latched labeled box |
| **Localize Box** | Pin box in room space |
| **Clear Box** or **A** | Remove box |
| **Start (☰)** | Return to main menu |

## Key scripts

| Script | Role |
|--------|------|
| `ObjectronCupDetectionManager.cs` | One-shot multi-cup scan + environment localize |
| `ObjectronBoxDebugManager.cs` | Box Debug scene (latch + show) |
| `ObjectronWorldPlacement.cs` | Camera → world corner placement |
| `ObjectronWorldOrientation.cs` | Roll compensation + gravity align |
| `ObjectronFramePoseQueue.cs` | Frame/pose pairing for async inference |
| `ObjectronQuestVisuals.cs` | Green wireframe boxes (cup mode) |
| `ObjectronLabeledBoxVisuals.cs` | Labeled wireframe (Box Debug) |

## Packages

- Meta MRUK, Meta OpenXR, Passthrough Camera API samples (forked base)
- `com.github.homuler.mediapipe` v0.11.0 (local package under `Packages/`)

## Troubleshooting

- **No detections** — Point at a clear cup ~0.5–1.5 m, improve lighting.
- **Box tilted with head** — Ensure `CompensateHeadRoll` is enabled on placement options; rebuild after pull.
- **Box offset** — Grant spatial permission for MRUK depth raycast.
- **Boxes follow head** — Rebuild with latest cup mode (boxes must be localized once, not updated every frame).
- **Detection only at odd head angles** — Adjust `PassthroughImageSource` rotation (see `README_OBJECTRON.md`).

## License

Based on [Meta Unity Passthrough Camera API Samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples) and [MediaPipe Unity Plugin](https://github.com/homuler/MediaPipeUnityPlugin). See respective licenses in those projects.
