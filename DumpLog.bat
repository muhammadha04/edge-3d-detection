@echo off
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist Builds mkdir Builds
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (echo NO_DEVICE> Builds\device_log.txt & echo Headset not connected over USB. & exit /b 1)
"%ADB%" logcat -d -v time -s Unity CameraAccess MRUK OVRPlugin > Builds\device_log.txt 2>&1
echo Saved Builds\device_log.txt
