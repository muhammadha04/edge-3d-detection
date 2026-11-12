@echo off
rem Turn the Quest Guardian (boundary) OFF so you can walk freely in passthrough.
rem   GuardianOff.bat        -> Guardian off
rem   GuardianOff.bat on     -> Guardian back on
rem Needs: developer mode, headset on USB (or wireless adb). Resets after a headset reboot.
rem Safety: you are relying on passthrough only - watch for furniture, stairs and people.
cd /d "%~dp0"
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.2.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist "%ADB%" set "ADB=adb"

"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (
  echo Headset not connected. Plug in the USB cable and allow USB debugging in the headset.
  goto :end
)

if /i "%~1"=="on" (
  set "DISABLE=false"
  set "PAUSE=0"
  echo Turning Guardian back ON...
) else (
  set "DISABLE=true"
  set "PAUSE=1"
  echo Turning Guardian OFF...
)

rem 1) Official Meta test-automation switch (com.oculus.rc).
"%ADB%" shell content call --uri content://com.oculus.rc --method SET_PROPERTY --extra disable_guardian:b:%DISABLE%

rem 2) Developer property that pauses the boundary (works on builds where the call above is ignored).
"%ADB%" shell setprop debug.oculus.guardian_pause %PAUSE%

echo.
echo Current state:
"%ADB%" shell content call --uri content://com.oculus.rc --method GET_PROPERTY
for /f %%v in ('"%ADB%" shell getprop debug.oculus.guardian_pause') do echo debug.oculus.guardian_pause=%%v

:end
if not "%~2"=="nopause" if not "%~1"=="nopause" pause
