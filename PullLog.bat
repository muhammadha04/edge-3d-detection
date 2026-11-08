@echo off
rem Pulls the app's own session log (works for sessions run without the USB cable).
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist Builds mkdir Builds
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (echo NO_DEVICE> Builds\app_log.txt & echo Headset not connected over USB. & exit /b 1)
"%ADB%" pull /sdcard/Android/data/com.amplilab.questdetect3d/files/qd3d_log.txt Builds\app_log.txt
"%ADB%" pull /sdcard/Android/data/com.amplilab.questdetect3d/files/qd3d_log.txt.prev Builds\app_log_prev.txt
echo Saved Builds\app_log.txt
