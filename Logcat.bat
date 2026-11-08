@echo off
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist Builds mkdir Builds
"%ADB%" logcat -c
echo Recording Unity logs to Builds\device_log.txt - close this window to stop.
"%ADB%" logcat -s Unity > Builds\device_log.txt
