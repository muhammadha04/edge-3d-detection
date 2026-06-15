# CenterPose Chair (Quest 3)

This branch replaces MediaPipe Objectron with **NVIDIA CenterPose** (Isaac ROS `deployable_dla34` chair ONNX) for category-level chair pose on Quest 3 passthrough.

## Quick start (after automated setup)

1. Open project in **Unity 6000.2.13f1**.
2. If `Packages/com.github.homuler.mediapipe` is missing, run from repo root:
   ```powershell
   .\install_mediapipe.ps1
   ```
3. Open **`Assets/ObjectronDetection/Scenes/CenterPoseChairDetection.unity`** (or launch app → **CenterPose Chair (recommended)**).
4. **File → Build Settings → Android → Build And Run** to Quest 3 (USB, Developer Mode on).

Model assets on disk:
- `Assets/ObjectronDetection/CenterPose/Models/chair.sentis` (runtime, generated from ONNX)
- `chair.onnx` is gitignored; copy via `scripts/copy_centerpose_chair_model.ps1` if you need to reconvert.

Re-run full editor setup (copy ONNX + Sentis + scene wiring):
- Menu: **QuestObjectron → CenterPose → Run Full Setup**
- Or batchmode: `-executeMethod QuestObjectron.CenterPose.Editor.CenterPoseChairDetectionSetup.FullSetupBatch`


```
PassthroughCameraAccess → CenterPose preprocess (512×512)
  → Unity Sentis (chair.sentis) → 8 corner keypoints + obj_scale
  → ObjectronWorldPlacement (2D raycast + MRUK depth)
  → ObjectronQuestVisuals (world-space 3D wireframe boxes)
```

## One-time setup (Unity Editor)

1. Open the project on branch `feature/centerpose-chair`.
2. **QuestObjectron → CenterPose → Copy Chair ONNX From Pose Estimation App**  
   (defaults to `../pose_estimation_app/models/centerpose/chair.onnx`)
3. **QuestObjectron → CenterPose → Convert Chair ONNX To Sentis**
4. Add Meta **Passthrough Camera Access** + **Environment Raycast** prefabs from `PassthroughCameraApiSamples/MultiObjectDetection` (same as Objectron cup scene).
5. **QuestObjectron → CenterPose → Configure CenterPose Chair Detection Scene**  
   Or create empty: **Create CenterPose Chair Scene (Empty)** then add PCA prefabs.
6. Build to Quest 3. Grant camera + scene permissions.

## Runtime

- Point at chairs; up to **3** detections (score ≥ 0.3) show green 3D boxes anchored in the room via scene raycast.
- **Y (left controller)** clears boxes and rescans.
- If boxes are missing or flipped, use the same rotation / flip tuning as Objectron (`PassthroughImageSource` defaults: Rotation0 + horizontal flip).

## Model source

NGC: `tao/centerpose/deployable_dla34/chair.onnx` (same as `pose_estimation_app`).

## Branch vs main

| Main (Objectron) | This branch (CenterPose) |
|------------------|--------------------------|
| MediaPipe TFLite Objectron (Cup) | Sentis CenterPose (Chair) |
| One-shot cup localize (max 3) | Continuous chair tracking (max 3) |
| Requires MediaPipe Bootstrap | No MediaPipe graph |

## PowerShell (optional, outside Unity)

```powershell
.\scripts\copy_centerpose_chair_model.ps1
```

Copies ONNX into `Assets/ObjectronDetection/CenterPose/Models/chair.onnx` for step 3 above.
