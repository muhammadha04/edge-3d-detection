@echo off
rem CompareModels.bat - YOLO+depth vs Objectron vs Hybrid on every recorded session, scored against the ground truth.
rem Needs the pc_*.jsonl files next to each session (pc_onnx_*: AnalyseSessions.bat step 4;
rem pc_objectron_chair / pc_hybrid_chair: Tools\annotation\run_objectron.py / run_objectron_crops.py).
rem Output: Data\eval_all\report.md and summary.csv
setlocal
cd /d "%~dp0"
set "BASH=bash"
if exist "%ProgramFiles%\Git\bin\bash.exe" set "BASH=%ProgramFiles%\Git\bin\bash.exe"
set "PY=python"
where python >nul 2>nul || set "PY=py -3"
for /d %%S in ("Data\QD3D\sessions\*") do (
  echo == %%~nxS
  for %%F in ("%%S\pc_onnx_*.jsonl" "%%S\pc_objectron_chair.jsonl" "%%S\pc_hybrid_chair.jsonl") do (
    if exist "%%F" "%BASH%" Tools/offline/run_tests.sh replay "%%S" --detections "%%~nxF"
  )
)
%PY% Tools\annotation\evaluate.py Data\QD3D --out Data\eval_all --no-plots
echo.
echo  Done: Data\eval_all\report.md
if not "%1"=="nopause" pause
