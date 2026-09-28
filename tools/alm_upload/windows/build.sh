#!/bin/sh
# Builds ALM-Image-Uploader.exe (Windows, .NET Framework 4.x - built into Windows 10/11).
# Works on Linux or macOS with Mono installed (apt install mono-devel / brew install mono).
set -e
cd "$(dirname "$0")"
mcs -target:winexe -optimize+ -langversion:7 \
    -out:ALM-Image-Uploader.exe \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Xml.dll \
    AlmCore.cs MainForm.cs
echo "Built $(pwd)/ALM-Image-Uploader.exe"
