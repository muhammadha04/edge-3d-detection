@echo off
rem TuneHybrid.bat - tries Hybrid fusion settings on every session (leave-one-session-out) and calibrates confidence.
rem Takes ~10-20 min. Result: Data\tune_hybrid\report.md
setlocal
cd /d "%~dp0"
set "BASH=bash"
if exist "%ProgramFiles%\Git\bin\bash.exe" set "BASH=%ProgramFiles%\Git\bin\bash.exe"
set "PY=python"
where python >nul 2>nul || set "PY=py -3"
%PY% Tools\annotation\tune_hybrid.py Data\QD3D --bash "%BASH%" %*
echo.
echo  Done: Data\tune_hybrid\report.md
pause
