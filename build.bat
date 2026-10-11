@echo off
rem Build BalatroModManager.exe with the .NET Framework compiler that ships with Windows.
rem No Visual Studio / .NET SDK needed.
setlocal
set OUT=%~dp0BalatroModManager.exe
set SRC=%~dp0src
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [x] csc.exe not found - .NET Framework 4.x is required.
  pause
  exit /b 1
)
echo Building with "%CSC%" ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /out:"%OUT%" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:Microsoft.VisualBasic.dll ^
  "%SRC%\*.cs"
if errorlevel 1 (
  echo [x] Build failed.
  pause
  exit /b 1
)
echo [ok] %OUT%
pause
