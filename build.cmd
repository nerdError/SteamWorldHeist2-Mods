@echo off
rem Builds the loader and every mod into dist\SWH2-ModPack-<ver>\ (+ .zip and Nexus archives in dist\nexus\).
rem   build.cmd                 build only
rem   build.cmd deploy          build and copy SWH2ModLoader\ + Mods\ into the default Steam game folder
rem   build.cmd deploy "D:\Games\SteamWorld Heist 2"
setlocal
set ARGS=
if /i "%~1"=="deploy" (
  if "%~2"=="" (set ARGS=-Deploy) else (set ARGS=-Deploy -Game "%~2")
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package.ps1" %ARGS%
set RC=%ERRORLEVEL%
if %RC% neq 0 (echo. & echo BUILD FAILED) else (if "%~1"=="" explorer "%~dp0dist")
rem Keep the window open when started by double-click.
echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RC%
