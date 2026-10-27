@echo off
cd /d "%~dp0"
if exist "MadokaPet.exe" (
  start "" "%~dp0MadokaPet.exe"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 (pause) else (start "" "%~dp0MadokaPet.exe")
)
