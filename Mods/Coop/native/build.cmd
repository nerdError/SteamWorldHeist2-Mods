@echo off
rem Builds swh2coop.dll (x64, static CRT) with the newest Visual Studio C++ tools: build.cmd <output folder>
setlocal
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo Visual Studio C++ tools not found ^(needed for Mods\Coop\native\swh2coop.dll^) & exit /b 1)
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /O2 /W4 /MT /LD /Brepro /Fo"%TEMP%\\" /Fe"%OUT%\swh2coop.dll" "%~dp0swh2coop.c" "%~dp0netplay.c" "%~dp0nettcp.c" "%~dp0exemap.c" /link /NOLOGO /Brepro kernel32.lib || exit /b 1
del "%OUT%\swh2coop.lib" "%OUT%\swh2coop.exp" 2>nul
