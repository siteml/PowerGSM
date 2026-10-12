@echo off
rem Double-clickable wrapper: hops to the repo root, runs the organizer, stays open.
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\organize-plans.ps1"
echo.
pause
