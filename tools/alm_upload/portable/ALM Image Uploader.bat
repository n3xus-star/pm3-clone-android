@echo off
rem Starts ALM Image Uploader with the bundled Python (no install needed)
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "PYTHONNOUSERSITE=1"
start "" "%~dp0python\pythonw.exe" "%~dp0app\start.pyw"
