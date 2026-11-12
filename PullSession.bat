@echo off
rem ============================================================================================================
rem  PullSession.bat - copy everything QD3D Annotate recorded on the headset to this PC.
rem
rem  What it copies: the whole QD3D folder of the app (catalog, rooms, annotations, calibration, photos, events,
rem  every recorded session with its frames/depth/poses/detections, and exported zips) into  Data\QD3D\
rem  (Data\ is git-ignored). Run it as often as you like: adb pull only adds/overwrites, it never deletes here.
rem
rem  Usage:   PullSession.bat            (double-click, or from a terminal)
rem           PullSession.bat nopause    (no "press a key" at the end, for scripts)
rem  Needs:   headset on USB with developer mode on and this PC allowed (USB debugging prompt in the headset).
rem  Size:    ~75 MB per minute of recording at 5 Hz with depth; a 2-minute walk-around is ~150 MB.
rem  Then:    AnalyseSessions.bat  (overlay, dataset, PC models, replay, evaluation, repeatability)
rem ============================================================================================================
setlocal
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
set "SRC=/sdcard/Android/data/com.amplilab.questdetect3d/files/QD3D"
if not exist Data mkdir Data
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (
  echo  Headset not connected over USB ^(or USB debugging not allowed in the headset^).
  echo NO_DEVICE> Data\pull_status.txt
  if not "%1"=="nopause" pause
  exit /b 1
)
echo  Pulling %SRC%  -^>  %CD%\Data\QD3D   (this can take a few minutes for long recordings)
"%ADB%" pull "%SRC%" Data
if errorlevel 1 (
  echo PULL_FAILED> Data\pull_status.txt
  echo  adb pull failed. Has the app run Annotate mode at least once on this headset?
  if not "%1"=="nopause" pause
  exit /b 1
)
echo PULLED> Data\pull_status.txt
echo.
echo  Done: Data\QD3D   (sessions in Data\QD3D\sessions)
echo  Next: AnalyseSessions.bat
if not "%1"=="nopause" pause
exit /b 0
