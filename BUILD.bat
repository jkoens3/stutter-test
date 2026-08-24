@echo off
REM Builds StutterTest.exe using the C# compiler that ships with Windows.
REM No downloads, no SDK, no Visual Studio. Just double-click this once.

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo.
    echo Couldn't find the built-in C# compiler.
    echo Expected it at:
    echo   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
    echo.
    pause
    exit /b 1
)

echo Building StutterTest.exe...
echo.

"%CSC%" /target:winexe /out:StutterTest.exe /optimize+ ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Management.dll ^
  StutterTest.cs

if exist StutterTest.exe (
    echo.
    echo ==========================================
    echo  Done. StutterTest.exe is ready.
    echo ==========================================
    echo.
    echo  To share it, zip up these files:
    echo      StutterTest.exe
    echo      PresentMon.exe
    echo      report_template.html
    echo.
) else (
    echo.
    echo Build failed. Check the errors above.
    echo.
)
pause
