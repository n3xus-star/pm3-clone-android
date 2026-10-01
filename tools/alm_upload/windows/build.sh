#!/bin/sh
# Builds ALM-Image-Uploader.exe (Windows, .NET Framework 4.5+ - built into Windows 10/11).
# Works on Linux or macOS with Mono installed (apt install mono-devel / brew install mono).
#
# It compiles against Microsoft's official .NET Framework 4.5 reference assemblies
# (NuGet package Microsoft.NETFramework.ReferenceAssemblies.net45), not Mono's own
# libraries: Mono has extra methods that do not exist on Windows and would fail there
# with "Method not found".
set -e
cd "$(dirname "$0")"
REFS=.refs/build/.NETFramework/v4.5
if [ ! -f "$REFS/mscorlib.dll" ]; then
    echo "Downloading .NET Framework 4.5 reference assemblies..."
    mkdir -p .refs
    curl -sSfL -o .refs/net45.nupkg \
        https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net45/1.0.3/microsoft.netframework.referenceassemblies.net45.1.0.3.nupkg
    (cd .refs && unzip -q -o net45.nupkg)
fi
mcs -target:winexe -optimize+ -langversion:7 -nostdlib -noconfig \
    -out:ALM-Image-Uploader.exe \
    -r:"$REFS/mscorlib.dll" -r:"$REFS/System.dll" -r:"$REFS/System.Core.dll" -r:"$REFS/System.Xml.dll" \
    -r:"$REFS/System.Windows.Forms.dll" -r:"$REFS/System.Drawing.dll" -r:"$REFS/System.IO.Compression.dll" \
    AssemblyInfo.cs AlmCore.cs Reports.cs ResultsDialogs.cs MainForm.cs
echo "Built $(pwd)/ALM-Image-Uploader.exe"
