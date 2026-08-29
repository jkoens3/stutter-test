@echo off
REM Builds StutterTest.exe using the C# compiler that ships with Windows.
REM No downloads, no SDK, no Visual Studio. Just double-click this once.

setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo.
    echo Couldn't find the built-in C# compiler.
    echo Expected it at:
    echo   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
    echo.
    REM --- Code signing (only runs if the 'sign' tool is installed) ---
REM If you're building from source yourself, this step is skipped
REM automatically and you'll just get an unsigned exe, which works fine.
where sign >nul 2>&1
if %errorlevel%==0 (
    echo.
    echo Signing...
    sign code artifact-signing StutterTest.exe ^
      --artifact-signing-account stuttertest-signing ^
      --artifact-signing-certificate-profile stuttertest ^
      --artifact-signing-endpoint https://cus.codesigning.azure.net/ ^
      --azure-credential-type azure-cli
) else (
    echo.
    echo [Signing skipped - the 'sign' tool isn't installed.]
    echo [The exe works fine unsigned, Windows will just warn about it.]
)

echo.
pause
    exit /b 1
)

if exist StutterTest.exe del StutterTest.exe

echo Building StutterTest.exe...
echo.

"%CSC%" /target:winexe /out:StutterTest.exe /optimize+ ^
  /win32manifest:app.manifest ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Management.dll ^
  /reference:System.Runtime.Serialization.dll ^
  StutterTest.cs Compare.cs Share.cs Calibration.cs

if not exist StutterTest.exe (
    echo.
    echo ******************************************
    echo  BUILD FAILED - see the errors above.
    echo  Nothing was signed. The old exe was removed.
    echo ******************************************
    echo.
    REM --- Code signing (only runs if the 'sign' tool is installed) ---
REM If you're building from source yourself, this step is skipped
REM automatically and you'll just get an unsigned exe, which works fine.
where sign >nul 2>&1
if %errorlevel%==0 (
    echo.
    echo Signing...
    sign code artifact-signing StutterTest.exe ^
      --artifact-signing-account stuttertest-signing ^
      --artifact-signing-certificate-profile stuttertest ^
      --artifact-signing-endpoint https://cus.codesigning.azure.net/ ^
      --azure-credential-type azure-cli
) else (
    echo.
    echo [Signing skipped - the 'sign' tool isn't installed.]
    echo [The exe works fine unsigned, Windows will just warn about it.]
)

echo.
pause
    exit /b 1
)

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
    echo      compare_template.html
    echo.
) else (
    echo.
    echo Build failed. Check the errors above.
    echo.
)
REM --- Code signing (only runs if the 'sign' tool is installed) ---
REM If you're building from source yourself, this step is skipped
REM automatically and you'll just get an unsigned exe, which works fine.
where sign >nul 2>&1
if %errorlevel%==0 (
    echo.
    echo Signing...
    sign code artifact-signing StutterTest.exe ^
      --artifact-signing-account stuttertest-signing ^
      --artifact-signing-certificate-profile stuttertest ^
      --artifact-signing-endpoint https://cus.codesigning.azure.net/ ^
      --azure-credential-type azure-cli
) else (
    echo.
    echo [Signing skipped - the 'sign' tool isn't installed.]
    echo [The exe works fine unsigned, Windows will just warn about it.]
)

echo.
pause
