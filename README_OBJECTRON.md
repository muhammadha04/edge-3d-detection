# Quest 3 Passthrough 3D Object Detection (MediaPipe Objectron — Chair)

Unity 6 project for **Meta Quest 3** that runs **native MediaPipe Objectron** (Chair model) on **Passthrough Camera API** frames and draws **world-space 3D wireframe boxes** over detected chairs. Inference uses **homuler MediaPipeUnityPlugin** (not Unity Inference Engine / Sentis).

> **Branch `chair`:** This branch detects chairs instead of cups. Use `main` for cup/mug detection.

For the full technical pipeline (placement math, parameters, chair rules, session cleanup), see **[README.md](README.md)**.

## Requirements

- Unity **6000.0.38f1** or newer
- Meta Quest **3 / 3S**, Horizon OS **v74+**
- USB debugging, Developer Mode
- Physical device (Passthrough Camera API is not supported in XR Simulator)

## Packages

- Meta MRUK (via sample `manifest.json`)
- `com.github.homuler.mediapipe` **v0.11.0** (local package under `Packages/com.github.homuler.mediapipe`)
- `com.unity.ai.inference` kept only so Meta's sample Sentis scripts compile; **Objectron uses MediaPipe**, and `ObjectronDisableSentis` disables YOLO at runtime

## Quick start

1. Open `QuestObjectron3D` in Unity 6.
2. Run **Meta → Tools → Project Setup Tool** → Fix all / Apply all.
3. Ensure `Assets/StreamingAssets/object_detection_3d_chair.bytes` exists (copied from MediaPipe package resources).
4. Open scene **`Assets/ObjectronDetection/Scenes/ObjectronChairDetection.unity`** — pre-configured with:
   - **Bootstrap** (Streaming Assets loader, GPU inference on Android)
   - **QuestObjectron** (`ObjectronChairDetectionManager`, `ObjectronGraph` Chair, `PassthroughImageSource`, `TextureFramePool`, `ObjectronSceneReferences`, `ObjectronDisableSentis`)
   - Child **WorldBoundingBoxes** with `OrientedBBoxDrawer`
   - References wired to **PassthroughCameraAccess** and **EnvironmentRaycast** from the Meta prefabs
5. If components look missing, run **QuestObjectron → Configure Objectron Chair Detection Scene**.
6. **File → Build Settings → Android** → build and deploy to Quest 3.
7. Grant **camera** and **spatial** permissions on first launch.
8. Point at **chairs** (~1–3 m). Each chair is localized **once** with a stable 3D box (max 3). Press **Y (left controller)** to reset and scan again.

`ObjectronDisableSentis` disables the legacy Sentis/YOLO path at runtime.

### Chair detection behavior

- **3D only** — no 2D screen overlay in chair scan mode.
- **One shot per chair** — after localization, that chair's box stays fixed in the room until reset.
- **Dedup** — detections within ~50 cm of an existing box are treated as the same chair.
- **Upright on floor** — box is constrained upright; bottom face must snap to the floor via Meta Scene API (`EnvironmentRaycastManager`). Detections without a valid floor hit are rejected.
- **Size refinement** — optional improvement against reference dimensions ~45×45×90 cm.

### Permissions

Grant **camera** and **spatial (scene)** permissions on first launch. Floor snap requires scene mesh raycasts (`USE_SCENE`).
