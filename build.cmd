@echo off
rem Builds the loader and every mod into dist\SWH2-ModPack-<ver>\ (+ .zip and Nexus archives in dist\nexus\).
rem   build.cmd                 build only (or build and deploy, if build.local.cmd says AUTO_DEPLOY=1)
rem   build.cmd deploy          build and copy SWH2ModLoader\ + Mods\ into the game folder(s) (default: Steam)
rem   build.cmd deploy "D:\Games\SteamWorld Heist 2"
rem   build.cmd nodeploy        build only, even with AUTO_DEPLOY=1
rem   build.cmd rebuild         rebuild everything (by default unchanged loader/mods are reused from obj\package\)
rem A game folder whose game is running is skipped. Personal defaults (several game folders, e.g. Steam + GOG;
rem restarting an open mod loader so it applies the new mods): copy build.local.example.cmd to build.local.cmd
rem (not committed) and edit it.
setlocal
set AUTO_DEPLOY=
set GAME_DIR=
set GAME_DIRS=
set RESTART_LOADER=
if exist "%~dp0build.local.cmd" call "%~dp0build.local.cmd"

set DEPLOY=
if "%AUTO_DEPLOY%"=="1" set DEPLOY=1
if /i "%~1"=="deploy" set DEPLOY=1
if /i "%~1"=="nodeploy" set DEPLOY=
rem GAME_DIRS: folders separated by ";" (GAME_DIR: older single-folder setting).
set "GAME=%GAME_DIRS%"
if not defined GAME set "GAME=%GAME_DIR%"
if /i "%~1"=="deploy" if not "%~2"=="" set "GAME=%~2"

set ARGS=
if defined DEPLOY set ARGS=-Deploy
if defined DEPLOY if "%RESTART_LOADER%"=="1" set ARGS=%ARGS% -RestartLoader
if defined DEPLOY if defined GAME set ARGS=%ARGS% -Game "%GAME%"
if /i "%~1"=="rebuild" set ARGS=%ARGS% -Rebuild
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package.ps1" %ARGS%
set RC=%ERRORLEVEL%
if %RC% neq 0 (
  echo. & echo BUILD FAILED
) else if "%~1"=="" if not defined DEPLOY (
  explorer "%~dp0dist"
)
rem Keep the window open when started by double-click.
echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RC%
