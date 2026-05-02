@echo off
REM ============================================================
REM  uninstall-service.bat — remove the GSMNode Windows service
REM
REM  Must be run from an elevated command prompt.
REM ============================================================
setlocal

net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: This script must be run as Administrator.
    exit /b 1
)

echo Stopping service GSMNode (ignored if already stopped)...
sc stop GSMNode

echo Deleting service GSMNode...
sc delete GSMNode
if errorlevel 1 (
    echo ERROR: sc delete failed. The service may not exist.
    exit /b 1
)

echo.
echo Done. Service GSMNode removed.
endlocal
