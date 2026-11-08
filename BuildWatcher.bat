@echo off
cd /d "%~dp0"
title QuestDetect3D build watcher
if not exist Builds mkdir Builds
echo.
echo  QuestDetect3D build watcher - leave this window open.
echo  Builds\trigger_build  : rebuild + install + launch
echo  Builds\trigger_deploy : install + launch last APK
echo  Builds\trigger_pull   : pull app log from the headset
echo WATCHING> Builds\watcher.txt
:loop
if exist Builds\trigger_build (
  del Builds\trigger_build
  echo [%time%] build requested
  call "%~dp0BuildAndDeploy.bat" nopause
)
if exist Builds\trigger_deploy (
  del Builds\trigger_deploy
  echo [%time%] deploy requested
  call "%~dp0DeployOnly.bat" nopause
)
if exist Builds\trigger_pull (
  del Builds\trigger_pull
  echo [%time%] log pull requested
  call "%~dp0PullLog.bat" nopause
  echo PULLED %time%> Builds\pull_status.txt
)
ping -n 3 127.0.0.1 > nul
goto loop
