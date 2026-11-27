@echo off
rem Builds and runs the offline tests: test.c (input lock, includes swh2coop.c with fake SDL functions) and
rem net_test.c (netplay recorder on a fake exe image, includes netplay.c).
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "OBJ=%TEMP%\swh2coop_test"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /W4 /MT /Fo"%OBJ%\\" /Fe"%TEMP%\swh2coop_test.exe" "%~dp0test.c" "%~dp0netplay.c" "%~dp0nettcp.c" "%~dp0exemap.c" /link /NOLOGO >nul || exit /b 1
cl /nologo /W4 /MT /Fo"%OBJ%\\" /Fe"%TEMP%\swh2net_test.exe" "%~dp0net_test.c" /link /NOLOGO >nul || exit /b 1
"%TEMP%\swh2coop_test.exe" || exit /b 1
"%TEMP%\swh2net_test.exe"
