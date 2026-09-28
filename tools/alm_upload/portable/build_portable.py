#!/usr/bin/env python3
r"""
Build the portable Windows package of ALM Image Uploader on any OS.

Result: ALM-Image-Uploader-portable.zip, which unzips to

    ALM-Image-Uploader\
        ALM Image Uploader.bat     <- double-click this
        alm_upload-cmd.bat         <- command-line version
        BACA SAYA.txt
        app\                       alm_upload.py, alm_upload_gui.py, start.pyw
        python\                    Windows CPython 3.12 + Tcl/Tk (conda-forge)

Nothing is installed on the Windows PC and no Python is needed there.
Windows Python comes from conda-forge; requests/truststore come from PyPI wheels.
This script only downloads and unpacks files, it never runs Windows binaries.

Needs: pip, and the 'zstandard' package (pip install zstandard).

    python build_portable.py [--out DIR]
"""

import argparse
import glob
import http.client
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
import zipfile

import zstandard

CONDA = "https://conda.anaconda.org/conda-forge"
PYTHON_VERSION = "3.12"
WHEELS = ["requests", "truststore"]
# DLLs the Python extension modules need, copied next to python.exe
LIB_BIN_DLLS = ["ffi-8.dll", "libbz2.dll", "libcrypto-3-x64.dll", "libexpat.dll", "liblzma.dll",
                "libssl-3-x64.dll", "sqlite3.dll", "tcl86t.dll", "tk86t.dll", "zlib1.dll"]
DROP = ["Lib/test", "Lib/idlelib", "Lib/ensurepip", "Lib/lib2to3", "Lib/turtledemo",
        "Lib/tkinter/test", "Lib/pydoc_data", "Lib/venv", "DLLs/_ctypes_test.pyd",
        "vcamp140.dll", "Library/lib/tk8.6/demos"]
HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.dirname(HERE)

README = """ALM Image Uploader (portable)
=============================

Tak perlu install apa-apa, tak perlu Python.

1. Unzip folder ini ke mana-mana (contoh: Desktop atau D:\\Tools).
   Jangan jalankan terus dari dalam fail zip.
2. Double-click "ALM Image Uploader.bat".
3. Isi login ALM, Test Lab folder (contoh Root\\Release 1\\Sprint 12)
   dan Image folder.
4. Tekan "Create image folders" -> letak screenshot dalam folder
   <test set>\\<test case>.
5. Tekan "Check" untuk semak, kemudian "Upload".

Kalau program tak keluar: lihat fail app\\start_error.log.
Kalau Windows tunjuk "Windows protected your PC": More info -> Run anyway.

Versi command line: buka Command Prompt dalam folder ini, taip
    alm_upload-cmd.bat --help
"""


def fetch(url, tries=4):
    for n in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=120) as r:
                return r.read()
        except (OSError, http.client.HTTPException):
            if n == tries - 1:
                raise
            time.sleep(2 ** (n + 1))


def resolve_conda_packages():
    """Newest python 3.12 for win-64 plus its whole dependency tree."""
    index = {}
    for sub in ("win-64", "noarch"):
        data = json.loads(fetch("{}/{}/current_repodata.json".format(CONDA, sub)))
        for fn, meta in {**data.get("packages", {}), **data.get("packages.conda", {})}.items():
            meta.update(fn=fn, sub=sub)
            index.setdefault(meta["name"], []).append(meta)

    def best(name):
        cands = index[name]
        if name == "python":
            cands = [c for c in cands if c["version"].startswith(PYTHON_VERSION + ".")
                     and "cpython" in c["build"]
                     and not any(d.startswith("libpython") for d in c["depends"])]
        return max(cands, key=lambda c: (c.get("timestamp", 0)))

    todo, chosen = ["python"], {}
    while todo:
        name = todo.pop()
        if name in chosen:
            continue
        chosen[name] = best(name)
        todo += [d.split()[0] for d in chosen[name]["depends"] if not d.startswith("__")]
    return chosen.values()


def extract_conda(blob, dest):
    z = zipfile.ZipFile(io.BytesIO(blob))
    for n in z.namelist():
        if n.startswith("pkg-") and n.endswith(".tar.zst"):
            stream = zstandard.ZstdDecompressor().stream_reader(io.BytesIO(z.read(n)))
            with tarfile.open(fileobj=stream, mode="r|") as t:
                t.extractall(dest, filter="tar")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=".", help="Folder for the zip (default: current folder)")
    out = os.path.abspath(ap.parse_args().out)

    work = tempfile.mkdtemp(prefix="almportable-")
    try:
        conda_root = os.path.join(work, "conda")
        for pkg in resolve_conda_packages():
            print("conda-forge:", pkg["fn"])
            extract_conda(fetch("{}/{}/{}".format(CONDA, pkg["sub"], pkg["fn"])), conda_root)

        app_root = os.path.join(work, "ALM-Image-Uploader")
        py = os.path.join(app_root, "python")
        os.makedirs(py)
        for f in os.listdir(conda_root):
            if f.lower().endswith((".exe", ".dll")):
                shutil.copy2(os.path.join(conda_root, f), py)
        for d in ("DLLs", "Lib"):
            shutil.copytree(os.path.join(conda_root, d), os.path.join(py, d))
        for dll in LIB_BIN_DLLS:
            shutil.copy2(os.path.join(conda_root, "Library", "bin", dll), py)
        for d in ("tcl8", "tcl8.6", "tk8.6"):
            shutil.copytree(os.path.join(conda_root, "Library", "lib", d),
                            os.path.join(py, "Library", "lib", d))
        for rel in DROP:
            p = os.path.join(py, rel)
            if os.path.isdir(p):
                shutil.rmtree(p)
            elif os.path.exists(p):
                os.remove(p)
        for p in glob.glob(os.path.join(py, "**", "__pycache__"), recursive=True):
            shutil.rmtree(p, ignore_errors=True)
        for p in glob.glob(os.path.join(py, "**", "*.pdb"), recursive=True):
            os.remove(p)

        wheels = os.path.join(work, "wheels")
        subprocess.check_call([
            sys.executable, "-m", "pip", "download", "-q", "--only-binary=:all:",
            "--platform", "win_amd64", "--python-version", PYTHON_VERSION,
            "--implementation", "cp", "-d", wheels] + WHEELS)
        site = os.path.join(py, "Lib", "site-packages")
        for whl in glob.glob(os.path.join(wheels, "*.whl")):
            print("wheel:", os.path.basename(whl))
            zipfile.ZipFile(whl).extractall(site)

        app = os.path.join(app_root, "app")
        os.makedirs(app)
        for f in ("alm_upload.py", "alm_upload_gui.py"):
            shutil.copy2(os.path.join(SRC, f), app)
        shutil.copy2(os.path.join(HERE, "start.pyw"), app)
        for f in ("ALM Image Uploader.bat", "alm_upload-cmd.bat"):
            shutil.copy2(os.path.join(HERE, f), app_root)
        with open(os.path.join(app_root, "BACA SAYA.txt"), "w", newline="\r\n", encoding="utf-8") as fh:
            fh.write(README)

        os.makedirs(out, exist_ok=True)
        zip_path = os.path.join(out, "ALM-Image-Uploader-portable.zip")
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
            for root, _dirs, files in os.walk(app_root):
                for f in files:
                    full = os.path.join(root, f)
                    z.write(full, os.path.relpath(full, work))
        print("Built", zip_path, "({:.1f} MB)".format(os.path.getsize(zip_path) / 1e6))
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
