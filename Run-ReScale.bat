@echo off
cd /d "%~dp0"
set "EXE=%~dp0ReScale.exe"
if not exist "%EXE%" set "EXE=%~dp0publish\ReScale.exe"
if not exist "%EXE%" (
    echo Error: ReScale.exe not found!
    pause
    exit /b 1
)
echo Starting ReScale with Administrator privileges...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%EXE%' -Verb RunAs"
