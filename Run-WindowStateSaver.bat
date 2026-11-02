@echo off
cd /d "%~dp0"
echo Starting Window State Saver with Administrator privileges...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%~dp0publish\WindowStateSaver.exe' -Verb RunAs"
