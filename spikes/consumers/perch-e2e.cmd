@echo off
rem Runs perch-e2e.ps1 (same name, same folder) under Windows PowerShell. Arguments are passed through.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dpn0.ps1" %*
exit /b %errorlevel%
