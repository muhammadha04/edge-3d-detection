@echo off
rem Copies the newest recording/export zip from the Quest (app data folder) to Builds\recordings\
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (echo Headset not connected over USB. & goto :end)
if not exist Builds\recordings mkdir Builds\recordings
set "ROOT=/sdcard/Android/data/com.amplilab.questdetect3d/files"
echo Zips on the headset (newest last):
"%ADB%" shell "ls -lt $(find %ROOT% /sdcard/Download -name '*.zip' 2>/dev/null) 2>/dev/null" > Builds\recordings\headset_zips.txt
type Builds\recordings\headset_zips.txt
for /f "usebackq delims=" %%f in (`"%ADB%" shell "ls -t $(find %ROOT% /sdcard/Download -name '*.zip' 2>/dev/null) 2>/dev/null | head -n 1"`) do set "LATEST=%%f"
if "%LATEST%"=="" (echo No zip found on the headset. & goto :end)
echo Pulling %LATEST%
"%ADB%" pull "%LATEST%" Builds\recordings\
echo Done: Builds\recordings
:end
if not "%~1"=="nopause" pause
