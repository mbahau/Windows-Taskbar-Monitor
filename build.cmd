@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo Building SysMonitor.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /out:SysMonitor.exe ^
  /win32manifest:app.manifest ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  SysMonitor.cs

if %ERRORLEVEL% neq 0 ( echo BUILD FAILED & exit /b 1 )
echo Done -> SysMonitor.exe
endlocal
