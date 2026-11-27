@echo off
rem Double-click: builds the loader and all mods and copies them into the game folder(s) (default Steam path, or
rem GAME_DIRS from build.local.cmd). A folder whose game is running is skipped.
rem Another game folder: deploy.cmd "D:\Games\SteamWorld Heist 2"
rem Your mod settings (profile.json), backups and reports are kept. After this, open NE Mod Loader & Manager and click Apply.
setlocal
call "%~dp0build.cmd" deploy %1
