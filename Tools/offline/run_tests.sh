#!/usr/bin/env bash
# Builds and runs the pipeline tests without Unity or a headset:  Tools/offline/run_tests.sh [-v]
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RT="$ROOT/Assets/QuestDetect3D/Runtime"
OUT="${TMPDIR:-/tmp}/qd3d_offline.exe"
mcs -nologo -langversion:latest -define:QD3D_OFFLINE -out:"$OUT" \
  "$ROOT/Tools/offline/UnityShim.cs" "$ROOT/Tools/offline/Program.cs" \
  "$RT/Core/UprightBox.cs" "$RT/Core/ObjectCategory.cs" "$RT/Core/CameraModel.cs" "$RT/Core/IDepthRaycaster.cs" \
  "$RT/Detection/YoloDecode.cs" "$RT/Detection/DetectionTypes.cs" \
  "$RT/Geometry/DepthBoxFitter.cs" "$RT/Geometry/BackrestEstimator.cs" "$RT/Geometry/BoxSolver.cs" "$RT/Geometry/SupportPlaneEstimator.cs" \
  "$RT/Tracking/ConfidenceModel.cs" "$RT/Tracking/TrackedObject.cs" "$RT/Tracking/ObjectTracker.cs" \
  "$ROOT/Assets/QuestDetect3D/Editor/Tests/SyntheticRoom.cs" "$ROOT/Assets/QuestDetect3D/Editor/Tests/PipelineTests.cs"
mono "$OUT" "$@"
