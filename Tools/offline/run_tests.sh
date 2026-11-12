#!/usr/bin/env bash
# Builds and runs the pipeline + annotation tests without Unity or a headset:
#   Tools/offline/run_tests.sh [-v] [--write-fixture]
#   Tools/offline/run_tests.sh replay <session_dir> [replay options]   (see Tools/offline/ReplayRunner.cs)
# Uses mcs/mono from PATH, or the Mono that ships with the Unity editor (set UNITY_EDITOR_DIR to override).
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RT="$ROOT/Assets/QuestDetect3D/Runtime"
ED="$ROOT/Assets/QuestDetect3D/Editor"
OUTDIR="${TMPDIR:-/tmp}/qd3d_offline"
mkdir -p "$OUTDIR"

MONO=mono
NETSTD=""
if command -v mcs >/dev/null 2>&1; then
  MCS=(mcs)
else
  UMONO="${UNITY_EDITOR_DIR:-/c/Program Files/Unity/Hub/Editor/6000.2.13f1/Editor}/Data/MonoBleedingEdge"
  MONO="$UMONO/bin/mono"
  [ -f "$MONO.exe" ] && MONO="$MONO.exe"
  MCS_EXE="$UMONO/lib/mono/4.5/mcs.exe"
  [ -f "$MCS_EXE" ] || { echo "mcs not found (install mono or set UNITY_EDITOR_DIR)"; exit 2; }
  MCS=("$MONO" "$MCS_EXE")
  NETSTD="$UMONO/lib/mono/4.5/Facades/netstandard.dll"
fi

# Native Windows mono needs Windows paths.
P() { if command -v cygpath >/dev/null 2>&1 && [ -e "$1" ]; then cygpath -w "$1"; else echo "$1"; fi; }

# Newtonsoft JSON: the same DLL Unity uses (package cache).
JSON_DLL="$(ls "$ROOT"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll 2>/dev/null | head -1)"
[ -f "$JSON_DLL" ] || { echo "Newtonsoft.Json.dll not found in Library/PackageCache (open the project in Unity once)"; exit 2; }
cp -f "$JSON_DLL" "$OUTDIR/"

REFS=(-r:"$(P "$OUTDIR/Newtonsoft.Json.dll")")
if [ -n "$NETSTD" ]; then REFS+=(-r:"$(P "$NETSTD")"); fi

# Pure sources shared by the tests and the replay (no UnityEngine).
CORE=(
  "$ROOT/Tools/offline/UnityShim.cs"
  "$RT/Core/UprightBox.cs" "$RT/Core/ObjectCategory.cs" "$RT/Core/CameraModel.cs" "$RT/Core/IDepthRaycaster.cs"
  "$RT/Detection/YoloDecode.cs" "$RT/Detection/DetectionTypes.cs"
  "$RT/Geometry/DepthBoxFitter.cs" "$RT/Geometry/BackrestEstimator.cs" "$RT/Geometry/BoxSolver.cs" "$RT/Geometry/SupportPlaneEstimator.cs"
  "$RT/Tracking/ConfidenceModel.cs" "$RT/Tracking/TrackedObject.cs" "$RT/Tracking/ObjectTracker.cs"
  "$RT/Annotation/Data/GroundTruth.cs" "$RT/Annotation/Data/AnnotationModels.cs" "$RT/Annotation/Data/AnnotationStore.cs"
  "$RT"/Annotation/Logic/*.cs
  "$RT/Recording/Png16.cs"
)

compile() { # out.exe sources...
  local out="$1"; shift
  local srcs=()
  for f in "$@"; do srcs+=("$(P "$f")"); done
  "${MCS[@]}" -nologo -langversion:latest -define:QD3D_OFFLINE "${REFS[@]}" -out:"$(P "$out")" "${srcs[@]}"
}

if [ "$1" = "replay" ]; then
  shift
  OUT="$OUTDIR/qd3d_replay.exe"
  compile "$OUT" "${CORE[@]}" "$RT/Recording/RecordJson.cs" "$ROOT/Tools/offline/RecordedDepthRaycaster.cs" "$ROOT/Tools/offline/ReplayRunner.cs"
  "$MONO" "$(P "$OUT")" "$@"
  exit $?
fi

OUT="$OUTDIR/qd3d_offline.exe"
compile "$OUT" "${CORE[@]}" "$ROOT/Tools/offline/Program.cs" \
  "$ED/Tests/SyntheticRoom.cs" "$ED/Tests/PipelineTests.cs" "$ED/Tests/AnnotationTests.cs" "$ED/Tests/AnnotationLogicTests.cs"
cd "$ROOT"
"$MONO" "$(P "$OUT")" "$@"
