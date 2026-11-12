@echo off
rem ============================================================================================================
rem  AnalyseSessions.bat - the Windows side of QD3D Annotate, in one click.
rem
rem  Runs, for the data pulled by PullSession.bat (Data\QD3D):
rem    1. load_session.py     summary of every session (frames, depth, dropped, GT boxes)
rem    2. repeatability.py    ground-truth spread per object/method, box vs tape, drift   -> Data\QD3D\reports\
rem    3. overlay.py          MP4 per session with GT boxes + device detections + depth   -> <session>\overlay.mp4
rem    4. run_models.py       the app's YOLO (FP32 ONNX) on every recorded frame           -> <session>\pc_*.jsonl
rem    5. replay (C#, mono)   real DepthBoxFitter + tracker on recorded depth, for device and PC detections
rem    6. evaluate.py         device vs PC replay vs ground truth (AP, errors, calibration) -> Data\eval\report.md
rem    7. export_dataset.py   auto-labelled COCO / YOLO-pose / JSONL dataset, split by session -> Data\dataset\
rem
rem  Usage:   AnalyseSessions.bat               all steps on Data\QD3D
rem           AnalyseSessions.bat nopause       same, no "press a key" (scripts)
rem           set QD3D_DATA=<QD3D folder> / set QD3D_OUT=<output folder> to analyse another copy.
rem  Needs:   Python 3.10+ with Tools\annotation\requirements.txt  (pip install -r Tools\annotation\requirements.txt)
rem           Git for Windows (bash) for step 5; Unity 6000.2.13f1 installed (its Mono compiles the replay).
rem  Recommended amount of data for the thesis numbers: >= 3 blind M1 repetitions per object, >= 2 walk-arounds of
rem  2 min per object and lighting condition, at least 3 sessions so train/val/test each get one.
rem ============================================================================================================
setlocal
cd /d "%~dp0"
set "DATA=Data\QD3D"
set "OUT=Data"
if not "%QD3D_DATA%"=="" set "DATA=%QD3D_DATA%"
if not "%QD3D_OUT%"=="" set "OUT=%QD3D_OUT%"
set "BASH=bash"
if exist "%ProgramFiles%\Git\bin\bash.exe" set "BASH=%ProgramFiles%\Git\bin\bash.exe"
set "PY=python"
where python >nul 2>nul || set "PY=py -3"
if not exist "%DATA%\sessions" (
  echo  No data in %DATA%. Run PullSession.bat first.
  if not "%1"=="nopause" pause
  exit /b 1
)
echo [1/7] Sessions
%PY% Tools\annotation\load_session.py "%DATA%" || goto failed
echo [2/7] Ground-truth repeatability
%PY% Tools\annotation\repeatability.py "%DATA%" || goto failed
echo [3/7] Alignment overlay videos
%PY% Tools\annotation\overlay.py "%DATA%" --detections detections.jsonl --depth || goto failed
echo [4/7] PC YOLO on recorded frames
%PY% Tools\annotation\run_models.py "%DATA%" || goto failed
echo [5/7] Replay of the depth fitter + tracker (C#)
for /d %%S in ("%DATA%\sessions\*") do (
  if exist "%%S\detections.jsonl" "%BASH%" Tools/offline/run_tests.sh replay "%%S" --detections detections.jsonl
  for %%F in ("%%S\pc_*.jsonl") do "%BASH%" Tools/offline/run_tests.sh replay "%%S" --detections "%%~nxF"
)
echo [6/7] Evaluation against the ground truth
%PY% Tools\annotation\evaluate.py "%DATA%" --out "%OUT%\eval" || goto failed
echo [7/7] Dataset export
%PY% Tools\annotation\export_dataset.py "%DATA%" --out "%OUT%\dataset" || goto failed
echo.
echo  Done.  Reports: %DATA%\reports\repeatability.md  and  %OUT%\eval\report.md
echo         Videos:  %DATA%\sessions\*\overlay.mp4        Dataset: %OUT%\dataset
if not "%1"=="nopause" pause
exit /b 0
:failed
echo  A step failed (see above).
if not "%1"=="nopause" pause
exit /b 1
