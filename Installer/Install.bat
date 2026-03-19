@echo off

:: Elevate if not already admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo [Install.bat] Running as administrator.
echo [Install.bat] Batch file location: %~dp0
echo [Install.bat] Looking for: %~dp0_internal\InstallHelper.ps1

if not exist "%~dp0_internal\InstallHelper.ps1" (
    echo ERROR: Could not find _internal\InstallHelper.ps1
    echo The release zip may be incomplete. Please re-download.
    pause
    exit /b 1
)

echo [Install.bat] Found InstallHelper.ps1 - launching...
powershell -ExecutionPolicy Bypass -File "%~dp0_internal\InstallHelper.ps1"
pause
