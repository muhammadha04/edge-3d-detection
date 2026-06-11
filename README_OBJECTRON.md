# Quest 3 Passthrough 3D Object Detection (MediaPipe Objectron — Cup)

Unity 6 project for **Meta Quest 3** that runs **native MediaPipe Objectron** (Cup model) on **Passthrough Camera API** frames and draws **world-space 3D wireframe boxes** over detected cups. Inference uses **homuler MediaPipeUnityPlugin** (not Unity Inference Engine / Sentis).

## Requirements

- Unity **6000.0.38f1** or newer
- Meta Quest **3 / 3S**, Horizon OS **v74+**
- USB debugging, Developer Mode
- Physical device (Passthrough Camera API is not supported in XR Simulator)

## Packages

- Meta MRUK (via sample `manifest.json`)
- `com.github.homuler.mediapipe` **v0.11.0** (local package under `Packages/com.github.homuler.mediapipe`)
- `com.unity.ai.inference` kept only so Meta’s sample Sentis scripts compile; **Objectron uses MediaPipe**, and `ObjectronDisableSentis` disables YOLO at runtime

## Quick start

1. Open `QuestObjectron3D` in Unity 6.
2. Run **Meta → Tools → Project Setup Tool** → Fix all / Apply all.
3. Open scene **`Assets/ObjectronDetection/Scenes/ObjectronCupDetection.unity`** — pre-configured with:
   - **Bootstrap** (Streaming Assets loader, CPU inference)
   - **QuestObjectron** (`ObjectronCupDetectionManager`, `ObjectronGraph` Cup + `objectron_cpu.txt`, `PassthroughImageSource`, `TextureFramePool` size 10, `ObjectronSceneReferences`, `ObjectronDisableSentis`)
   - Child **WorldBoundingBoxes** with `OrientedBBoxDrawer`
   - References wired to **PassthroughCameraAccess** and **EnvironmentRaycast** from the Meta prefabs
4. If components look missing, run **QuestObjectron → Configure Objectron Cup Detection Scene**.
5. **File → Build Settings → Android** → build and deploy to Quest 3.
6. Grant **camera** and **spatial** permissions on first launch.
7. Point at a **cup** in good lighting (~0.5–1.5 m).

`ObjectronDisableSentis` disables the legacy Sentis/YOLO path at runtime.

## Logcat

```bash
adb logcat -s QuestObj3D Unity
```

| Tag step | Meaning |
|----------|---------|
| `BOOT` | App / MediaPipe bootstrap |
| `PERM` | Camera / spatial permissions |
| `PCA` | Passthrough camera opened |
| `FRAME` | Periodic frame stats |
| `DETECT` | Objectron inference |
| `POSE` | Per-object pose |
| `WORLD` | World corner placement |
| `VIZ` | Box count drawn |
| `HUD` | Headset debug overlay ready |
| `ERR` | Errors |

## Headset debug HUD

On Quest, a **Screen Space Camera** panel is parented to **OVRCameraRig → CenterEyeAnchor** (falls back to `Camera.main`). It shows live detect state, rotation/flip, world center, extent, distance, viz count, object id, placement, frame id, and PCA resolution.

- **A (right controller)** — cycle `PassthroughImageSource` rotation (0° → 90° → 180° → 270°)
- **B (right controller)** — toggle horizontal flip

Logcat when HUD spawns: `[HUD] ready parent=CenterEyeAnchor`

`OnGUI` debug in `ObjectronDetectionDebug` is disabled on device builds by default; use the headset HUD instead.

## Architecture

```
Passthrough (Building Blocks) → PassthroughCameraAccess → TextureFrame → ObjectronGraph (Cup, native)
  → FrameAnnotation → ObjectronWorldPlacement (PCA ray + MRUK depth) → OrientedBBoxDrawer
```

## Troubleshooting

- **`DllNotFoundException: mediapipe_jni`**: Player Settings → **ARM64** only, IL2CPP; confirm `mediapipe_android.aar` is in the MediaPipe package.
- **No detections**: Use a clear cup, improve lighting; check `DETECT empty` in logcat.
- **Detections only when you yaw the headset ~-90°** (not when changing rotation in the Inspector alone): the PCA buffer is landscape while Objectron expects upright framing. Default is **Rotation270** on `PassthroughImageSource`. On device, press **A** to cycle rotation and **B** to flip. If mug appears only with head yaw, try **Rotation0** + flip combinations (see Meta `PassthroughCameraApiSamples/CameraToWorld`).
- **No pink/green viz in headset** but `[VIZ]` in logcat: visuals live under **`ObjectronVisualsRoot`** (DontDestroyOnLoad) with **Sprites/Default**, ZWrite off, queue 5000. Check `active_in_hierarchy=true` in `[VIZ]` lines.
- **Boxes offset**: Enable spatial permission for MRUK depth raycast; adjust fallback distance in `ObjectronWorldPlacement.cs`.
- **Black screen**: Run Project Setup Tool; enable Passthrough on `[BuildingBlock] Passthrough`.

## References

- [Unity-PassthroughCameraApiSamples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples)
- [homuler/MediaPipeUnityPlugin](https://github.com/homuler/MediaPipeUnityPlugin) (v0.11.0 for Objectron)
- [MediaPipe Objectron (legacy)](https://developers.google.com/mediapipe/solutions/guide#legacy)
