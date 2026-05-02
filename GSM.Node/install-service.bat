@echo off
REM ============================================================
REM  install-service.bat — install GSM.Node as a Windows service
REM
REM  Fallback for users who don't want to launch GSM.NodeSetup.
REM  GSM.NodeSetup can do this same operation interactively from
REM  its "Service" tab (GUI) or main menu option 5 (CLI).
REM
REM  Must be run from an elevated command prompt.
REM ============================================================
setlocal

REM Resolve the directory this script lives in (with trailing backslash).
set "SCRIPT_DIR=%~dp0"

REM Strip trailing backslash for use inside quoted binPath, otherwise
REM the C runtime parses the closing \" as an escaped quote and
REM swallows subsequent arguments. (Same SteamCMD-era root cause
REM documented in PowerGSM_Reference.md.)
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

set "EXE_PATH=%SCRIPT_DIR%\GSM.Node.exe"

if not exist "%EXE_PATH%" (
    echo ERROR: GSM.Node.exe was not found next to this script.
    echo Expected: "%EXE_PATH%"
    exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: This script must be run as Administrator.
    echo Right-click and choose "Run as administrator", or run from an
    echo elevated command prompt.
    exit /b 1
)

echo Creating service GSMNode...
sc create GSMNode binPath= "\"%EXE_PATH%\"" DisplayName= "PowerGSM Node" start= auto
if errorlevel 1 (
    echo ERROR: sc create failed.
    exit /b 1
)

sc description GSMNode "PowerGSM Node - game server management agent"

echo Starting service GSMNode...
sc start GSMNode
if errorlevel 1 (
    echo Service was created but could not start. Check the configuration
    echo in nodesettings.json and try: sc start GSMNode
    exit /b 1
)

echo.
echo Done. Service GSMNode is installed and running.
echo View status: sc query GSMNode
echo View logs:   Event Viewer -^> Windows Logs -^> Application
endlocal
