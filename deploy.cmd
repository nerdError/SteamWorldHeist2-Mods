@echo off
rem Double-click: builds the loader and all mods and copies them into the game folder (default Steam path).
rem Another game folder: deploy.cmd "D:\Games\SteamWorld Heist 2"
rem Your mod settings (profile.json), backups and reports are kept. After this, open SWH2 Mod Loader and click Apply.
setlocal
tasklist /fi "imagename eq SteamWorld Heist 2.exe" 2>nul | find /i "SteamWorld Heist 2.exe" >nul
if %ERRORLEVEL% equ 0 (
  echo SteamWorld Heist 2 is running. Close the game first, then run this again.
  pause
  exit /b 1
)
call "%~dp0build.cmd" deploy %1
