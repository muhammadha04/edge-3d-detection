@echo off
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (echo Headset not connected over USB. & exit /b 1)
"%ADB%" install -r -g "Builds\QuestDetect3D.apk" > Builds\install.log 2>&1
type Builds\install.log
"%ADB%" shell am start -n com.amplilab.questdetect3d/com.unity3d.player.UnityPlayerGameActivity
if not "%1"=="nopause" pause
