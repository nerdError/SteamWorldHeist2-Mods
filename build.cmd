@echo off
rem Builds the loader and every mod into dist\SWH2-ModPack-<ver>\ (+ .zip and Nexus archives in dist\nexus\).
rem   build.cmd                 build only (or build and deploy, if build.local.cmd says AUTO_DEPLOY=1)
rem   build.cmd deploy          build and copy SWH2ModLoader\ + Mods\ into the default Steam game folder
rem   build.cmd deploy "D:\Games\SteamWorld Heist 2"
rem   build.cmd nodeploy        build only, even with AUTO_DEPLOY=1
rem   build.cmd rebuild         rebuild everything (by default unchanged loader/mods are reused from obj\package\)
rem Personal defaults: copy build.local.example.cmd to build.local.cmd (not committed) and edit it.
setlocal
set AUTO_DEPLOY=
set GAME_DIR=
if exist "%~dp0build.local.cmd" call "%~dp0build.local.cmd"

set DEPLOY=
if "%AUTO_DEPLOY%"=="1" set DEPLOY=1
if /i "%~1"=="deploy" set DEPLOY=1
if /i "%~1"=="nodeploy" set DEPLOY=
set "GAME=%GAME_DIR%"
if /i "%~1"=="deploy" if not "%~2"=="" set "GAME=%~2"

if defined DEPLOY (
  tasklist /fi "imagename eq SteamWorld Heist 2.exe" 2>nul | find /i "SteamWorld Heist 2.exe" >nul && (
    echo SteamWorld Heist 2 is running: building only, NOT copying into the game folder. Close the game and run again.
    echo.
    set DEPLOY=
  )
)

set ARGS=
if defined DEPLOY (
  if defined GAME (set ARGS=-Deploy -Game "%GAME%") else (set ARGS=-Deploy)
)
if /i "%~1"=="rebuild" set ARGS=%ARGS% -Rebuild
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package.ps1" %ARGS%
set RC=%ERRORLEVEL%
if %RC% neq 0 (
  echo. & echo BUILD FAILED
) else if defined DEPLOY (
  echo. & echo Copied into the game folder. Open NE Mod Loader ^& Manager and click "Apply to game".
) else if "%~1"=="" (
  explorer "%~dp0dist"
)
rem Keep the window open when started by double-click.
echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RC%
