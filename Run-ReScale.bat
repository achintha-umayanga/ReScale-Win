@echo off
cd /d "%~dp0"
echo Starting ReScale with Administrator privileges...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%~dp0publish\ReScale.exe' -Verb RunAs"

