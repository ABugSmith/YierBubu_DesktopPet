@echo off
setlocal
if not exist "%~dp0app\YierBubu-v4.exe" goto missing
if not exist "%~dp0concept\interaction-poses-v4.png" goto missing
if not exist "%~dp0concept\bubu-single-paw-v1.png" goto missing
start "" /D "%~dp0app" "%~dp0app\YierBubu-v4.exe"
exit /b 0
:missing
echo Required files are missing. Extract the entire ZIP before running start.cmd.
pause
exit /b 1
