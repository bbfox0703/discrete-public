@echo off
REM Thin wrapper around build.ps1 — accepts the same arguments.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
