@echo off
rem Builds swh2map.dll (x64, static CRT) with the newest Visual Studio C++ tools: build.cmd <output folder>
rem Shares the Steam/GOG address map with Coop: Mods\Coop\native\exemap.c + map_gog.h.
setlocal
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo Visual Studio C++ tools not found ^(needed for Mods\MapCoop\swh2map.dll^) & exit /b 1)
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
set "COOP=%~dp0..\..\Coop\native"
set "OBJ=%TEMP%\swh2map"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /O2 /W4 /MT /LD /Brepro /I"%COOP%" /Fo"%OBJ%\\" /Fe"%OUT%\swh2map.dll" "%~dp0swh2map.c" "%COOP%\exemap.c" /link /NOLOGO /Brepro kernel32.lib ws2_32.lib || exit /b 1
del "%OUT%\swh2map.lib" "%OUT%\swh2map.exp" 2>nul
