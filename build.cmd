@echo off
rem LevelBand build launcher
rem   build.cmd           full build  -> dist\LevelBand.exe
rem   build.cmd -Lite     lite build  -> dist\LevelBand-lite.exe
rem   build.cmd -All      both
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
