# Branch `twostage` — MediaPipe Two-Stage Chair Detection

This branch implements **MediaPipe Objectron's two-stage chair pipeline** on Quest 3, following the official guide:

- [Objectron solution docs](https://github.com/google-ai-edge/mediapipe/blob/master/docs/solutions/objectron.md)
- Graph: `mediapipe/graphs/object_detection_3d/object_occlusion_tracking.pbtxt`
- Android reference build: `bazel build -c opt --config android_arm64 --define chair=true mediapipe/examples/android/src/java/com/google/mediapipe/apps/objectdetection3d:objectdetection3d`

## Two-stage pipeline (inside MediaPipe graph)

Homuler's `ObjectronGpuSubgraph` (used on Android/Quest GPU) matches Google's mobile two-stage graph:

| Stage | Subgraph / calculator | Output |
|-------|----------------------|--------|
| **1** | `ObjectDetectionOidV4Subgraph` (SSD on Open Images) | 2D crop rects → `multi_box_rects` |
| **2** | `BoxLandmarkSubgraph` + `Lift2DFrameAnnotationTo3DCalculator` (EPnP) | 3D box → `lifted_objects` |

Stage 1 runs only when tracking is lost (`min_tracking_confidence`). Stage 2 runs every frame on the tracked crop.

Unity graph config: `Assets/MediaPipeUnity/Samples/Scenes/Objectron/objectron_gpu.txt`

## Visuals: one box per chair

Two renderers exist; they must **not** overlap on the same chair:

| Visual | Component | Line width | When shown |
|--------|-----------|------------|------------|
| **Thick live box** | `OrientedBBoxDrawer` | 0.12 m | Unlocalized candidates only, while scanning |
| **Thin pinned box** | `ObjectronQuestVisuals` | 0.004 m | After localize; **frozen** until refine |

Both use stage-2 `lifted_objects`. Thick = live preview for **unlocalized** candidates only. Thin = pinned box after stable latch + localize.

## Live detection aligned with box debug

Live scanning now mirrors [`ObjectronBoxDebugManager`](Assets/ObjectronDetection/Scripts/ObjectronBoxDebugManager.cs):

| Behavior | Box debug | Live (twostage) |
|----------|-----------|-----------------|
| Per-frame placement | `PlaceDetailed` once per frame | Same |
| Head-roll compensation | `CompensateHeadRoll = true` | Same during latch |
| Floor snap | On demand | Only when pinning / refining |
| Auto-pin | Manual latch | After **5 stable frames** + **2 s** cooldown between new chairs |

This prevents one chair from being localized many times (duplicate thin boxes) and frees the pipeline to scan the next chair.

## Tiered processing (performance)

| Phase | When | Work |
|-------|------|------|
| **Latch (heavy track)** | Every N frames while scanning | MediaPipe + `PlaceDetailed` (same as box debug) |
| **Localize (pin)** | Stable latch + cooldown | Floor snap + add to localized list |
| **Refine (heavy)** | Frozen chair, better quality only | Replace corners if `ObjectronDetectionQuality` improves |
| **Diagnostics** | Editor / Development builds | `pipeline_compare`, `pipeline stage1_*`, `live_latch` |

### Adaptive inference stride

- 2 frames between inference when scanning with 0–1 localized chairs
- 3 frames when 2+ localized
- 6 frames when all slots full (localized chairs frozen; refine-only)

### Graph object cap

`max_num_objects` set to `unlocalizedSlots + 1` (minimum 1) so MediaPipe stage-2 loop shrinks as chairs are pinned.

### Localize gate (anti-duplicate)

- **Stability:** center must stay within **15 cm** for **5 consecutive** latched frames
- **Cooldown:** **2 s** minimum between pinning new chairs
- **Dedup:** oriented-box overlap + center radius (0.45 m / 0.65 m) — same chair never pinned twice

### Refine cooldown

Per chair: 0.75 s between heavy refine attempts unless live track center moved > 15 cm.

## Better detection score

[`ObjectronDetectionQuality.cs`](Assets/ObjectronDetection/Scripts/ObjectronDetectionQuality.cs) — **lower = better**:

- Size fit vs ~45×45×90 cm reference
- Placement method tier (prefers depth-refined / mask-oriented)
- Scene depth raycast hit
- Stage-1 / stage-2 pixel alignment error
- Large center jump penalty (stability)

Refine gate: `ObjectronDetectionQuality.IsBetterThan(candidate, chair.LastQuality)`.

## Defaults

| Setting | Value | Notes |
|---------|-------|-------|
| `min_detection_confidence` | 0.5 | objectron.md |
| `min_tracking_confidence` | 0.55 | Quest-tuned |
| `max_num_objects` | 3 | start-menu slider up to 20 |
| Model | `object_detection_3d_chair.bytes` | chair |

## Scene

`Assets/ObjectronDetection/Scenes/ObjectronChairDetection.unity`

## Logcat

```bash
adb logcat -s QuestObj3D
```

- `chair_localized` — first pin after stable latch
- `chair_refined` — frozen box updated (quality improved)
- `live_latch` — stability progress toward next pin
- `pipeline stage1_ssd_rects=N stage2_lifted=M` — debug builds only
- `pipeline_compare` — raw vs refined extent (debug builds only)

## Test on device

1. Scan 1 chair: thick while aiming → thin pin → thick gone for that chair.
2. Scan 3 chairs: no stacked thick+thin on same chair.
3. Hold better view on pinned chair: box stays frozen until `chair_refined` in logcat.
4. Max chairs 8–10: check frame rate vs previous build (`infer_ms` in debug logs).

## Branch relationship

```
main (cup)
  └── chair (original heuristics)
        └── twostage (tiered two-stage + frozen localize/refine)
```
