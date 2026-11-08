@echo off
setlocal
cd /d "%~dp0"
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Unity.exe"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist Builds mkdir Builds
echo RUNNING> Builds\status.txt
echo.
echo  QuestDetect3D v2 - build and deploy
echo  [1/3] Building APK (first run imports the whole project: 10-30 min, later builds ~3 min)
echo        log: Builds\build.log
"%UNITY%" -batchmode -nographics -projectPath "%~dp0." -buildTarget Android -executeMethod QuestDetect3D.EditorTools.BuildScript.BuildAndroid -logFile "%~dp0Builds\build.log"
if errorlevel 1 goto failed
if not exist Builds\QuestDetect3D.apk goto failed
echo  [2/3] Installing on the headset (USB, developer mode on)
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 goto nodevice
"%ADB%" install -r -g "Builds\QuestDetect3D.apk" > Builds\install.log 2>&1
type Builds\install.log
echo  [3/3] Launching
"%ADB%" logcat -c
"%ADB%" shell am start -n com.amplilab.questdetect3d/com.unity3d.player.UnityPlayerGameActivity >> Builds\install.log 2>&1
echo DEPLOYED>> Builds\status.txt
echo  Recording 40 s of app logs to Builds\device_log.txt (put the headset on now)
ping -n 41 127.0.0.1 > nul
"%ADB%" logcat -d -v time -s Unity CameraAccess MRUK OVRPlugin > Builds\device_log.txt 2>&1
echo LOGGED>> Builds\status.txt
echo.
echo  Done. Put the headset on. Logs: DumpLog.bat (USB) or PullLog.bat (app log file)
if not "%1"=="nopause" pause
exit /b 0
:nodevice
echo NO_DEVICE>> Builds\status.txt
echo  APK built, but no headset on USB. Connect it and run DeployOnly.bat
if not "%1"=="nopause" pause
exit /b 0
:failed
echo BUILD_FAILED>> Builds\status.txt
echo.
echo  BUILD FAILED - see Builds\build.log
if not "%1"=="nopause" pause
exit /b 1
