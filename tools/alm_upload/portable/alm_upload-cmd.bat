@echo off
rem Command-line version: alm_upload-cmd.bat --help
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "PYTHONNOUSERSITE=1"
"%~dp0python\python.exe" "%~dp0app\alm_upload.py" %*
