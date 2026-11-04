@echo off
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo [build] vswhere.exe not found. Install Visual Studio 2022 Build Tools.
  exit /b 1
)

set "MSBUILD="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
  if not defined MSBUILD set "MSBUILD=%%i"
)
if not defined MSBUILD (
  echo [build] MSBuild not found.
  exit /b 1
)

if not exist "%ROOT%tools\refasm\.NETFramework\v4.8\mscorlib.dll" (
  echo [build] .NET Framework 4.8 reference assemblies missing.
  echo [build] Run: powershell -ExecutionPolicy Bypass -File "%ROOT%tools\fetch-refasm.ps1"
  exit /b 1
)

echo [build] MSBuild: %MSBUILD%
echo [build] Configuration: %CONFIG%
"%MSBUILD%" "%ROOT%src\IWar3Key.sln" /m /nologo /v:m /p:Configuration=%CONFIG% /p:Platform=x86
if errorlevel 1 exit /b %errorlevel%

echo [build] Output: %ROOT%bin\%CONFIG%\
exit /b 0
