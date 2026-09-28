# ALM Image Uploader

A portable Windows program that uploads screenshots as attachments to the
**test cases in Test Lab** of **HP ALM / Micro Focus ALM / OpenText ALM**.
It covers every test set under one Test Lab folder in a single run.

No Python and no installation needed: one `.exe` file.

## Windows program (single .exe, recommended)

Current version: **2.0.0** (see `windows/CHANGELOG.md`).

`windows/` holds a native Windows version, `ALM-Image-Uploader.exe` (about 50 KB).
It runs on the .NET Framework 4.x that comes with Windows 10/11, so there is
nothing to install and no Python. It works step by step:

1. Log in (ALM URL, username, password).
2. Choose the domain and project from lists loaded from ALM.
3. Choose the Test Lab folder with **Browse ALM...** (the Test Lab folder tree) and
   the image folder, then **Load**. Images can sit in one flat folder with any names:
   - left: the images, with a preview and a suggested test case (from the file name)
   - right: the test sets and test cases, with how many images each has
   - pick images (Ctrl/Shift+click) and a test case, then **Assign** (or drag the
     images onto the test case, or double-click it); one image can go to several
     test cases; **Accept suggestion** takes the suggested test case
   - tick test sets / test cases (ticking a test set ticks all its test cases) to
     assign the selected images to every ticked test case at once; **Untick all**
     and **Clear all assignments** start over
   - **Upload assigned images** uploads everything not uploaded yet
   - **Download attachments...** saves the attachments of all ticked test cases as
     `<folder>\<test set>\<test case>\<file>`, renamed after the test case
     (e.g. `1.1_Land_Screen_1.png`); loading that folder assigns every file to its
     test case again, ready to upload
   - sort and filter both lists: click an image column title to sort (natural order,
     "1.9" before "1.10"), "Show" filters images by status; the test tree has "Sort"
     and "Show" (with / without images, ticked, waiting for upload, Comments value)
   - the Comments column of each test case is shown as e.g. `[NA]` (grey)
   - **Set field...** changes one field (e.g. the *Comments* selection list to *NA*)
     on all ticked test cases; the values come from the field's ALM list
   The assignments and what was uploaded are saved in `ALM-assignments.txt` in the
   image folder, so the work can continue later without uploading twice. Images that
   already sit in `<test set>\<test case>` folders are assigned automatically.
 It is a C# port of the same logic
(`AlmCore.cs`, `MainForm.cs`) and uses the Windows certificate store and proxy
settings automatically.

Build it on Linux or macOS with Mono (`apt install mono-devel` / `brew install mono`):

```bash
windows/build.sh        # -> windows/ALM-Image-Uploader.exe
```

## Download (Python build via GitHub Actions)

1. Open the repository on GitHub → **Actions** → **Build ALM Image Uploader (Windows)**.
2. Open the latest green run → **Artifacts** → download `ALM-Image-Uploader-windows`.
3. Unzip it. `ALM-Image-Uploader.exe` is the program. Copy it anywhere (Desktop, USB drive).

> Windows SmartScreen may say "Windows protected your PC" because the file is
> not signed. Click **More info → Run anyway**. If your company's antivirus
> blocks it, ask IT to allow the file.

## How it works

Images are placed in folders that follow the Test Lab structure:

```
Images\                          <- "Image folder"
  Sprint 12 Regression\          <- test set name
    Login Test\                  <- test case name
      step1.png
      step2.png
    Logout Test\
      result.png
  Sprint 12 Smoke\
    Login Test\
      step1.png
```

Each image is attached to that test case **inside that test set** (the test
instance). So `Login Test` in two different test sets each gets its own images.

## Steps

1. **ALM login**: ALM URL (e.g. `https://alm.company.com/qcbin`), Domain,
   Project, Username, Password.
2. **Test Lab folder**: the folder path as shown in Test Lab, e.g.
   `Root\Release 1\Sprint 12`, or the folder ID. Every test set in that folder
   and its sub folders is included.
3. **Image folder**: where the screenshots are kept on the laptop.
4. Press **Create image folders**. The program creates one folder per test set
   and test case, with the exact names from ALM. Put the screenshots into them.
5. Press **Check**. The list shows every test set and test case with the number
   of images found:
   - orange `no images`: that test case has no screenshot yet
   - red `Images NOT matched`: images in the wrong place, with the reason
6. Press **Upload**.

Images that are already attached to the test case (same file name) are skipped,
so running Upload again is safe.

### Options

- **After upload, move images to**: moves every uploaded image to another
  folder (it keeps the same test set\test case layout), so the image folder only
  holds what is still pending.
- **Keep watching**: after the upload, the program keeps running and uploads any
  new image placed in the folders automatically. Press **Stop** to finish.
- **Skip SSL certificate check**: only if the connection fails because of the
  company certificate.

Settings (not the password) are saved in `alm_uploader_settings.json` next to
the `.exe`.

### Special cases

| Case | What to do |
|---|---|
| The same test is in one test set twice | The folders are named `[1]Login Test` and `[2]Login Test` (Create image folders does this) |
| Two test sets have the same name in different sub folders | Their folders follow the sub folder path, e.g. `Images\Smoke\Regression\...` (Create image folders does this) |
| Test name has `: / \ * ? " < > \|` | These become `_` in the folder name and still match |
| Screenshot in a sub folder of a test case folder | Allowed, e.g. `Login Test\Run 2\x.png` |
| Screenshot directly in the test set folder | Allowed if the file name starts with the test name, e.g. `Login Test_1.png` |

## Command-line version

`alm_upload.exe` (in the same download) or `python alm_upload.py` does the same
from a command prompt, for scripts or the Windows Task Scheduler:

```bat
set ALM_URL=https://alm.company.com/qcbin
set ALM_DOMAIN=DEFAULT
set ALM_PROJECT=MyProject
set ALM_USER=myuser
set ALM_PASSWORD=...

rem list test sets / test cases
alm_upload.exe --lab-folder "Root\Release 1\Sprint 12" --list
rem create the image folders
alm_upload.exe --lab-folder "Root\Release 1\Sprint 12" --folder C:\Images --make-folders
rem preview, then upload
alm_upload.exe --lab-folder "Root\Release 1\Sprint 12" --folder C:\Images --dry-run
alm_upload.exe --lab-folder "Root\Release 1\Sprint 12" --folder C:\Images --move-to C:\Images_done
rem keep watching
alm_upload.exe --lab-folder "Root\Release 1\Sprint 12" --folder C:\Images --watch --interval 10
```

Other modes: one test set (`--test-set "Name"` or `--test-set-id 101`) and
other entities (`--entity defects --id 123 a.png`). See `alm_upload.exe --help`.

## Portable zip (no GitHub needed)

`portable/build_portable.py` builds `ALM-Image-Uploader-portable.zip` on any
OS (Linux too). It bundles Windows Python 3.12 with Tk from conda-forge, the
program and a double-click launcher (`ALM Image Uploader.bat`). Unzip it on the
Windows PC and run the `.bat` file. Nothing gets installed.

```bash
pip install zstandard
python portable/build_portable.py --out dist
```

## Building the .exe yourself

The GitHub Actions workflow `.github/workflows/build-alm-uploader.yml` builds it
on every change under `tools/alm_upload/`. If that workflow is not in the
repository yet, create it on GitHub (**Add file → Create new file**, path
`.github/workflows/build-alm-uploader.yml`) and paste the content of
`tools/alm_upload/build-alm-uploader.yml`. To build it on a Windows PC that has Python:

```bat
pip install pyinstaller requests truststore
pyinstaller --onefile --windowed --name ALM-Image-Uploader --hidden-import truststore alm_upload_gui.py
```
