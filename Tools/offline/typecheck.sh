#!/usr/bin/env bash
# Fast compile check of Assets/QuestDetect3D/Runtime against the Unity + package assemblies (no Unity launch).
# Catches C# errors in seconds; the real build (BuildAndDeploy.bat) stays the reference.
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
UED="${UNITY_EDITOR_DIR:-/c/Program Files/Unity/Hub/Editor/6000.2.13f1/Editor}"
UMONO="$UED/Data/MonoBleedingEdge"
MONO="$UMONO/bin/mono"; [ -f "$MONO.exe" ] && MONO="$MONO.exe"
CSC="$UMONO/lib/mono/4.5/csc.exe"
P() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }
OUT="${TMPDIR:-/tmp}/qd3d_typecheck"; mkdir -p "$OUT"
RSP="$OUT/typecheck.rsp"
{
  echo "-nostdlib+ -nologo -target:library -langversion:9 -nowarn:0618,0414,0649,0169,0219,0067 -define:UNITY_ANDROID -define:UNITY_2021_3_OR_NEWER -out:$(P "$OUT/qd3d_check.dll")"
  echo "-r:\"$(P "$UED/Data/NetStandard/ref/2.1.0/netstandard.dll")\""
  for d in "$UED"/Data/Managed/UnityEngine/UnityEngine*.dll; do echo "-r:\"$(P "$d")\""; done
  for d in "$ROOT"/Library/ScriptAssemblies/*.dll; do
    case "$(basename "$d")" in Assembly-CSharp*|*Editor*|*Tests*) continue;; esac
    echo "-r:\"$(P "$d")\""
  done
  for d in "$ROOT"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll; do echo "-r:\"$(P "$d")\""; done
  find "$ROOT/Library/PackageCache" -path "*mediapipe*" -name "*.dll" -path "*Plugins*" ! -name "mediapipe_c.dll" ! -path "*Android*" ! -path "*iOS*" ! -path "*Linux*" ! -path "*macOS*" 2>/dev/null | while read -r d; do echo "-r:\"$(P "$d")\""; done
  find "$ROOT/Assets/QuestDetect3D/Runtime" "$ROOT/Assets/MediaPipeUnity/Samples" -name "*.cs" ! -path "*/Editor/*" ! -path "*/Tests/*" | while read -r f; do echo "\"$(P "$f")\""; done
} > "$RSP"
"$MONO" "$CSC" -noconfig "@$(P "$RSP")" 2>&1 | grep -v "warning CS" | grep -v "^$" | head -${MAXLINES:-60}
