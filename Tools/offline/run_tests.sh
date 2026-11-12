#!/usr/bin/env bash
# Builds and runs the pipeline + annotation tests without Unity or a headset:  Tools/offline/run_tests.sh [-v]
# Uses mcs/mono from PATH, or the Mono that ships with the Unity editor (set UNITY_EDITOR_DIR to override).
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RT="$ROOT/Assets/QuestDetect3D/Runtime"
ED="$ROOT/Assets/QuestDetect3D/Editor"
OUTDIR="${TMPDIR:-/tmp}/qd3d_offline"
mkdir -p "$OUTDIR"
OUT="$OUTDIR/qd3d_offline.exe"

MONO=mono; NETSTD="Facades/netstandard.dll"
if ! command -v mcs >/dev/null 2>&1; then
  UMONO="${UNITY_EDITOR_DIR:-/c/Program Files/Unity/Hub/Editor/6000.2.13f1/Editor}/Data/MonoBleedingEdge"
  MONO="$UMONO/bin/mono"; [ -f "$MONO.exe" ] && MONO="$MONO.exe"
  MCS_EXE="$UMONO/lib/mono/4.5/mcs.exe"
  [ -f "$MCS_EXE" ] || { echo "mcs not found (install mono or set UNITY_EDITOR_DIR)"; exit 2; }
  mcs() { "$MONO" "$MCS_EXE" "$@"; }
  NETSTD="$UMONO/lib/mono/4.5/Facades/netstandard.dll"
fi

# Newtonsoft JSON: the same DLL Unity uses (package cache).
JSON_DLL="$(ls "$ROOT"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll 2>/dev/null | head -1)"
[ -f "$JSON_DLL" ] || { echo "Newtonsoft.Json.dll not found in Library/PackageCache (open the project in Unity once)"; exit 2; }
cp -f "$JSON_DLL" "$OUTDIR/"

# Native Windows mono needs Windows paths.
P() { if command -v cygpath >/dev/null 2>&1 && [ -e "$1" ]; then cygpath -w "$1"; else echo "$1"; fi; }

mcs -nologo -langversion:latest -define:QD3D_OFFLINE -r:"$(P "$OUTDIR/Newtonsoft.Json.dll")" -r:"$(P "$NETSTD")" -out:"$(P "$OUT")" \
  "$(P "$ROOT/Tools/offline/UnityShim.cs")" "$(P "$ROOT/Tools/offline/Program.cs")" \
  "$(P "$RT/Core/UprightBox.cs")" "$(P "$RT/Core/ObjectCategory.cs")" "$(P "$RT/Core/CameraModel.cs")" "$(P "$RT/Core/IDepthRaycaster.cs")" \
  "$(P "$RT/Detection/YoloDecode.cs")" "$(P "$RT/Detection/DetectionTypes.cs")" \
  "$(P "$RT/Geometry/DepthBoxFitter.cs")" "$(P "$RT/Geometry/BackrestEstimator.cs")" "$(P "$RT/Geometry/BoxSolver.cs")" "$(P "$RT/Geometry/SupportPlaneEstimator.cs")" \
  "$(P "$RT/Tracking/ConfidenceModel.cs")" "$(P "$RT/Tracking/TrackedObject.cs")" "$(P "$RT/Tracking/ObjectTracker.cs")" \
  "$(P "$RT/Annotation/Data/GroundTruth.cs")" "$(P "$RT/Annotation/Data/AnnotationModels.cs")" "$(P "$RT/Annotation/Data/AnnotationStore.cs")" \
  "$(P "$ED/Tests/SyntheticRoom.cs")" "$(P "$ED/Tests/PipelineTests.cs")" "$(P "$ED/Tests/AnnotationTests.cs")"
"$MONO" "$(P "$OUT")" "$@"
